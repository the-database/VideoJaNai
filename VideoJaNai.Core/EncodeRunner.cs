using System;
using System.Collections.Generic;
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
        // an encode is starting: progress starts over
        void EncodeStarting(string input, string output);
        void Stdout(string line);
        void Stderr(string line);
        void Started(Process process) { }
    }

    // One run of a job over inputs, as the GUI and the command line both do it: the conf is written
    // once (Prepare), then each video is encoded by one aji_encode. aji_encode prepares the engines
    // before the first frame (TensorRT builds one on first use for a model and size, which can take
    // minutes, and loads it from its cache after that), reporting it as PROGRESS phase=build_engine
    // lines (pct=0, then pct=100) ahead of the encode's own. Damaged spots in the source come as
    // DAMAGE lines (see DamageSpot), collected into the result.
    public sealed class EncodeRunner(AjiPaths paths)
    {
        public AjiPaths Paths { get; } = paths;

        public void Prepare(EncodeJob job) => AjiConf.Write(job, Paths);

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
        public async Task<EncodeResult> EncodeToOutputAsync(EncodeJob job, string input, string output, IEncodeObserver observer,
                                                            string? previewPath = null, CancellationToken ct = default)
        {
            var watch = new RunWatch(observer);
            var result = await PartialOutput.WriteAsync(output, temp => EncodeAsync(job, input, temp, watch, previewPath, ct));
            if (!result.Written)
            {
                result = result with { EngineFailed = watch.Progress.PreparingEngine && watch.Progress.Percent < 100 };
            }
            var damage = watch.Damage;
            if (damage.Count > 0)
            {
                // --on-damage fail: aji_encode stopped at the (first) damaged spot
                result = result with
                {
                    Damage = damage,
                    StoppedOnDamage = !job.ContinueOnDamage && !result.Written && result.ExitCode != 0 && !result.EngineFailed,
                };
            }
            return result;
        }

        private Task<int> Run(string args, IEncodeObserver observer, CancellationToken ct) =>
            AjiProcess.RunAsync(Paths, args, observer.Stdout, observer.Stderr, observer.Started, ct);

        // The observer, with its own read of the output lines: whether aji_encode stopped while it
        // was still preparing the engines, and the damaged spots it reported.
        private sealed class RunWatch(IEncodeObserver inner) : IEncodeObserver
        {
            private readonly List<DamageSpot> _damage = [];

            public AjiProgress Progress { get; } = new();

            public IReadOnlyList<DamageSpot> Damage
            {
                get
                {
                    lock (_damage)
                    {
                        return [.. _damage];
                    }
                }
            }

            public void Command(string text) => inner.Command(text);
            public void EncodeStarting(string input, string output) => inner.EncodeStarting(input, output);
            public void Stderr(string line) => inner.Stderr(line);
            public void Started(Process process) => inner.Started(process);

            public void Stdout(string line)
            {
                Progress.Handle(line);
                if (DamageSpot.Parse(line) is DamageSpot spot)
                {
                    lock (_damage)
                    {
                        _damage.Add(spot);
                    }
                }
                inner.Stdout(line);
            }
        }
    }

    // An encode's exit code, whether its output was written, whether aji_encode stopped while
    // preparing the engines (a failed TensorRT engine build, say), and the damaged spots it
    // reported in the source (StoppedOnDamage: it stopped at one, --on-damage fail).
    public readonly record struct EncodeResult(int ExitCode, bool Written, bool EngineFailed = false)
    {
        private readonly IReadOnlyList<DamageSpot>? _damage;

        public IReadOnlyList<DamageSpot> Damage
        {
            get => _damage ?? [];
            init => _damage = value;
        }

        public bool StoppedOnDamage { get; init; }

        // why the output wasn't written
        public string Failure => ExitCode == 0 ? "aji_encode wrote no output"
                               : EngineFailed ? $"the engine could not be prepared (exit code {ExitCode})"
                               : StoppedOnDamage ? $"damaged video at {Damage[0].Where}"
                               : $"aji_encode failed (exit code {ExitCode})";

        // "Finished with 3 damaged frames (12:34, 12:35, 41:02)" for a written video with damaged
        // spots (--on-damage continue), else null
        public string? FinishedWithDamage => Written && Damage.Count > 0 ? DamageText.Finished(Damage) : null;
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
