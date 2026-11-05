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
}
