using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoJaNai.Core
{
    // What a run reports while aji_encode works. Lines arrive on aji_encode's reader threads.
    public interface IEncodeObserver
    {
        // the command about to run, as the console shows it ("Upscaling with command: ...")
        void Command(string text);
        // an encode (not an engine prebuild) is starting: progress starts over
        void EncodeStarting(string input, string output);
        void Stdout(string line);
        void Stderr(string line);
        void Started(Process process) { }
    }

    // One run of a job over inputs, as the GUI and the command line both do it: the conf is written
    // once (Prepare), then per video the TensorRT engine is prebuilt (TensorRT builds engines on first
    // use, which can take minutes, so it gets its own step; TensorRT-RTX builds in about a second and
    // DirectML needs none) and the video is encoded.
    public sealed class EncodeRunner(AjiPaths paths)
    {
        public AjiPaths Paths { get; } = paths;

        public void Prepare(EncodeJob job) => AjiConf.Write(job, Paths);

        public static bool NeedsEnginePrebuild(EncodeJob job) => job.Backend == Backend.TensorRt;

        // 0 when there is nothing to build; otherwise aji_encode --build-only's exit code.
        public async Task<int> BuildEngineAsync(EncodeJob job, string input, IEncodeObserver observer, CancellationToken ct = default)
        {
            if (!NeedsEnginePrebuild(job))
            {
                return 0;
            }
            var args = AjiArgs.Build(job, Paths, input, null, AjiMode.BuildOnly);
            var engine = job.Backend == Backend.TensorRtRtx ? "TensorRT-RTX" : "TensorRT";
            observer.Command($"Generating {engine} engine with command: {Paths.AjiEncodePath} {args}");
            return await Run(args, observer, ct);
        }

        public async Task<int> EncodeAsync(EncodeJob job, string input, string output, IEncodeObserver observer,
                                           string? previewPath = null, CancellationToken ct = default)
        {
            var args = AjiArgs.Build(job, Paths, input, output, AjiMode.Encode, previewPath);
            observer.Command($"Upscaling with command: {Paths.AjiEncodePath} {args}");
            observer.EncodeStarting(input, output);
            return await Run(args, observer, ct);
        }

        // EncodeAsync into the output's partial file, which becomes the output only when the encode
        // succeeds (see PartialOutput).
        public Task<EncodeResult> EncodeToOutputAsync(EncodeJob job, string input, string output, IEncodeObserver observer,
                                                      string? previewPath = null, CancellationToken ct = default) =>
            PartialOutput.WriteAsync(output, temp => EncodeAsync(job, input, temp, observer, previewPath, ct));

        private Task<int> Run(string args, IEncodeObserver observer, CancellationToken ct) =>
            AjiProcess.RunAsync(Paths, args, observer.Stdout, observer.Stderr, observer.Started, ct);
    }

    // An encode's exit code and whether its output was written.
    public readonly record struct EncodeResult(int ExitCode, bool Written)
    {
        // why the output wasn't written
        public string Failure => ExitCode == 0 ? "aji_encode wrote no output" : $"aji_encode failed (exit code {ExitCode})";
    }

    // An encode writes a temporary file next to its output, which becomes the output only when
    // aji_encode exits 0 having written it, so a stopped or failed encode never leaves a half-written
    // file (which skip-existing would then keep) and never replaces an output that was there.
    public static class PartialOutput
    {
        // ep01-animejanai.mkv -> ep01-animejanai.videojanai-partial.mkv (the extension still picks
        // the container)
        public static string PathFor(string output) =>
            Path.Join(Path.GetDirectoryName(output), Path.GetFileNameWithoutExtension(output) + BatchPlanning.PartialMarker + Path.GetExtension(output));

        // write(the partial path) -> its exit code; the partial file is moved to output on success
        // and deleted otherwise (also when write throws, cancelled included). Moving it can throw
        // (the output open elsewhere, say).
        public static async Task<EncodeResult> WriteAsync(string output, Func<string, Task<int>> write)
        {
            var temp = PathFor(output);
            try
            {
                var rc = await write(temp);
                if (rc == 0 && File.Exists(temp))
                {
                    File.Move(temp, output, overwrite: true);
                    return new EncodeResult(rc, true);
                }
                return new EncodeResult(rc, false);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // a leftover temporary file is harmless
            }
        }
    }
}
