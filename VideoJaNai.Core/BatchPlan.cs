using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoJaNai.Core
{
    // Which videos a run upscales and where each one goes.
    public static class BatchPlan
    {
        // What folder input picks up (anything libav reads works as a single video).
        public static readonly IReadOnlyList<string> VideoExtensions = [
            ".mkv", ".mp4", ".m4v", ".mov", ".webm", ".avi", ".wmv", ".asf", ".flv", ".f4v",
            ".mpg", ".mpeg", ".m2v", ".vob", ".ts", ".m2ts", ".mts", ".3gp", ".ogv", ".dv",
            ".rm", ".rmvb", ".divx", ".y4m"];

        public const string FilenameToken = "%filename%";
        public const string DefaultFilename = "%filename%-animejanai.mkv";

        public static bool IsVideo(string path) =>
            VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        // The output for a video: the filename pattern with %filename% as the video's name (no
        // extension), in the output folder.
        public static string OutputPath(string outputFolder, string filenamePattern, string inputPath) =>
            Path.Join(Path.GetFullPath(outputFolder),
                      filenamePattern.Replace(FilenameToken, Path.GetFileNameWithoutExtension(inputPath)));

        // The GUI's output folder when none is set: the video's folder, or "<folder> animejanai".
        public static string DefaultOutputFolderForFile(string inputFile) =>
            Directory.GetParent(Path.GetFullPath(inputFile))?.ToString() ?? "";

        public static string DefaultOutputFolderForFolder(string inputFolder) =>
            $"{inputFolder.TrimEnd('/', '\\')} animejanai";

        // The videos of a folder, in name order: its own (recursive: and its subfolders', each with
        // its folder relative to `folder`, which the output keeps).
        public static IEnumerable<(string Path, string RelativeFolder)> FolderVideos(string folder, bool recursive)
        {
            var root = Path.GetFullPath(folder);
            return Directory.EnumerateFiles(root, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(IsVideo)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(f => (f, Path.GetRelativePath(root, Path.GetDirectoryName(f)!) is var rel && rel == "." ? "" : rel));
        }
    }

    public enum PlanAction { Encode, SkipExisting }

    public sealed record PlannedVideo(string Input, string Output, PlanAction Action);

    // The videos a run takes, or why it can't run (Problem: a missing input, two videos writing
    // one file, a video written over itself).
    public sealed record PlanResult(List<PlannedVideo> Videos, string? Problem);

    public static class BatchPlanning
    {
        // The command line's temporary output (see the CLI's Run): never an input.
        public const string PartialMarker = ".videojanai-partial";

        // Inputs -> videos, as the GUI's folder mode and the command line both plan them: a file as
        // is; a folder's videos with its subfolders, which the output keeps under the output folder.
        // The output folder defaults to the GUI's (next to the file; "<folder> animejanai"). An
        // output folder inside an input folder isn't scanned (earlier results). Outputs that exist
        // are skipped (skipExisting) or redone. outputMayBeFile: with one file, an output folder
        // that names a video file (and isn't a folder) is that file (the command line's -o).
        public static PlanResult Plan(IReadOnlyList<string> inputs, string? outputFolder, string filename, bool skipExisting,
                                      bool outputMayBeFile = false)
        {
            var videos = new List<PlannedVideo>();
            foreach (var raw in inputs)
            {
                var input = Path.GetFullPath(raw);
                if (File.Exists(input))
                {
                    var output = outputMayBeFile && outputFolder != null && inputs.Count == 1 &&
                                 BatchPlan.IsVideo(outputFolder) && !Directory.Exists(outputFolder)
                        ? Path.GetFullPath(outputFolder)
                        : BatchPlan.OutputPath(outputFolder ?? BatchPlan.DefaultOutputFolderForFile(input), filename, input);
                    videos.Add(new PlannedVideo(input, output, Action(output)));
                }
                else if (Directory.Exists(input))
                {
                    var rootFull = Path.GetFullPath(outputFolder ?? BatchPlan.DefaultOutputFolderForFolder(input));
                    foreach (var (video, rel) in BatchPlan.FolderVideos(input, recursive: true))
                    {
                        if (IsUnder(video, rootFull) || Path.GetFileName(video).Contains(PartialMarker, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        var output = BatchPlan.OutputPath(Path.Join(rootFull, rel), filename, video);
                        videos.Add(new PlannedVideo(video, output, Action(output)));
                    }
                }
                else
                {
                    return new PlanResult(videos, $"not found: {raw}");
                }
            }
            var clash = videos.GroupBy(v => v.Output, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .FirstOrDefault(g => g.Count() > 1);
            if (clash != null)
            {
                return new PlanResult(videos, $"{string.Join(" and ", clash.Select(v => v.Input))} would both be written to {clash.Key}; use an output file name that tells them apart");
            }
            var same = videos.FirstOrDefault(v => string.Equals(v.Input, v.Output, StringComparison.OrdinalIgnoreCase));
            if (same != null)
            {
                return new PlanResult(videos, $"{same.Input} would be written over itself; use another output file name or folder");
            }
            return new PlanResult(videos, null);

            PlanAction Action(string output) => skipExisting && File.Exists(output) ? PlanAction.SkipExisting : PlanAction.Encode;
        }

        private static bool IsUnder(string path, string folder)
        {
            var rel = Path.GetRelativePath(folder, path);
            return rel != path && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
        }
    }
}
