using System;
using System.Diagnostics;
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

        private Task<int> Run(string args, IEncodeObserver observer, CancellationToken ct) =>
            AjiProcess.RunAsync(Paths, args, observer.Stdout, observer.Stderr, observer.Started, ct);
    }
}
