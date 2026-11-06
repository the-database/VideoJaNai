using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoJaNai.Core
{
    // Running aji_encode: its environment, the encoder probe, and a run with its output lines.
    public static class AjiProcess
    {
        // Linux: libaji_trt.so's RUNPATH points at system TensorRT/CUDA paths; the bundled runtime
        // next to it is found through the loader path (inherited by trtexec too). With the shared pack
        // store (aji-runtime.json) the TensorRT runtime is the store's pack: aji_encode links cudart
        // itself, so the pack's folder goes on the loader path (Linux) or PATH (Windows, where
        // aji_encode delay-loads cudart). libaji loads the rest by full path and gives trtexec its
        // builder kernels. Null on Windows without the store.
        public static Dictionary<string, string>? Environment(string inferenceDirectory)
        {
            var trt = RuntimePacks.Read(inferenceDirectory)?.Trt;
            if (OperatingSystem.IsWindows())
            {
                if (trt is null)
                {
                    return null;
                }
                var path = System.Environment.GetEnvironmentVariable("PATH");
                return new Dictionary<string, string>
                {
                    ["PATH"] = string.IsNullOrEmpty(path) ? trt : $"{trt};{path}",
                };
            }
            var ldPath = System.Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            var dirs = trt is null ? inferenceDirectory : $"{inferenceDirectory}:{trt}";
            return new Dictionary<string, string>
            {
                ["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(ldPath) ? dirs : $"{dirs}:{ldPath}",
            };
        }

        // `aji_encode --probe-encoders`: its output when it ran and is a probe, else null (no
        // aji_encode, or one without the probe, which prints its usage and exits 2).
        public static async Task<string?> ProbeAsync(AjiPaths paths, TimeSpan? timeout = null)
        {
            if (!File.Exists(paths.AjiEncodePath))
            {
                return null;
            }
            try
            {
                using var process = new Process();
                process.StartInfo.FileName = paths.AjiEncodePath;
                process.StartInfo.Arguments = "--probe-encoders";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.WorkingDirectory = paths.InferenceDirectory;
                foreach (var (k, v) in Environment(paths.InferenceDirectory) ?? [])
                {
                    process.StartInfo.Environment[k] = v;
                }
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
                await process.WaitForExitAsync(cts.Token);
                var output = await stdout;
                return process.ExitCode == 0 && EncoderProbe.Parse(output) != null ? output : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"encoder probe failed: {ex.Message}");
                return null;
            }
        }

        // Runs aji_encode with `arguments`, each stdout / stderr line (non-empty) to its callback,
        // and returns the exit code. onStarted gets the process right after it starts (the GUI
        // puts it in its kill-on-close job). Cancelling kills it (and the trtexec it may run).
        public static async Task<int> RunAsync(AjiPaths paths, string arguments, Action<string> onStdout, Action<string> onStderr,
                                               Action<Process>? onStarted = null, CancellationToken ct = default)
        {
            using var process = new Process();
            process.StartInfo.FileName = paths.AjiEncodePath;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WorkingDirectory = paths.InferenceDirectory;
            foreach (var (k, v) in Environment(paths.InferenceDirectory) ?? [])
            {
                process.StartInfo.Environment[k] = v;
            }
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    onStderr(e.Data);
                }
            };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    onStdout(e.Data);
                }
            };
            process.Start();
            onStarted?.Invoke(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { /* already exited */ }
                // until it's gone, so the caller can delete the file it was writing
                try { process.WaitForExit(10000); } catch { /* already exited */ }
                throw;
            }
            // the last lines: WaitForExitAsync returns once both streams hit EOF
            return process.ExitCode;
        }
    }

    // aji_encode's "PROGRESS phase=<p> frame=<n> total=<n> fps=<f> pct=<n>" lines (stdout), as a
    // progress value, phase and text. aji_encode always reports fps=0, so the processing rate is
    // measured here from the frame counter, smoothed, since the lines come in 32-frame bursts.
    public sealed class AjiProgress
    {
        private static readonly CultureInfo English = CultureInfo.InvariantCulture;
        private readonly ETACalculator _eta = new(10, 5.0);
        private long _lastFpsFrame = -1;
        private DateTime _lastFpsTime;

        public string Phase { get; private set; } = string.Empty;
        public double Percent { get; private set; }
        public long Frame { get; private set; }
        public long TotalFrames { get; private set; }
        public double Fps { get; private set; }
        public TimeSpan? Remaining => _eta.ETAIsAvailable && Percent is > 0 and < 100 ? _eta.ETR : null;

        // "12.3% - 45.6 fps - ETA 00:12:34", as the GUI's progress bar shows it
        public string Text { get; private set; } = string.Empty;

        public void Reset()
        {
            _eta.Reset();
            _lastFpsFrame = -1;
            _lastFpsTime = default;
            Fps = 0;
            Percent = 0;
            Frame = 0;
            TotalFrames = 0;
            Text = string.Empty;
            Phase = string.Empty;
        }

        public static bool IsProgressLine(string line) => line.StartsWith("PROGRESS", StringComparison.Ordinal);

        // False for a line that isn't a PROGRESS line. `now` is for tests.
        public bool Handle(string line, DateTime? now = null)
        {
            if (!IsProgressLine(line))
            {
                return false;
            }

            var phaseMatch = Regex.Match(line, @"phase=(\w+)");
            if (phaseMatch.Success)
            {
                Phase = phaseMatch.Groups[1].Value;
            }
            var totalMatch = Regex.Match(line, @"total=([0-9]+)");
            if (totalMatch.Success && long.TryParse(totalMatch.Groups[1].Value, NumberStyles.Integer, English, out var total))
            {
                TotalFrames = total;
            }

            // frame=0 lines (build_engine) are skipped, so fps only shows once encoding starts.
            var frameMatch = Regex.Match(line, @"frame=([0-9]+)");
            if (frameMatch.Success && long.TryParse(frameMatch.Groups[1].Value, NumberStyles.Integer, English, out var frame) && frame > 0)
            {
                Frame = frame;
                var t = now ?? DateTime.UtcNow;
                if (_lastFpsFrame >= 0 && frame > _lastFpsFrame)
                {
                    var seconds = (t - _lastFpsTime).TotalSeconds;
                    if (seconds > 0)
                    {
                        var inst = (frame - _lastFpsFrame) / seconds;
                        Fps = Fps <= 0 ? inst : (0.5 * Fps) + (0.5 * inst);
                    }
                }
                _lastFpsFrame = frame;
                _lastFpsTime = t;
            }

            var pctMatch = Regex.Match(line, @"pct=([0-9]+(?:\.[0-9]+)?)");
            if (pctMatch.Success && double.TryParse(pctMatch.Groups[1].Value, NumberStyles.Float, English, out var pct))
            {
                Percent = pct;
                var fpsText = Fps > 0 ? string.Create(English, $" - {Fps:0.0} fps") : "";
                if (pct is > 0 and < 100)
                {
                    _eta.Update((float)(pct / 100.0));
                    Text = _eta.ETAIsAvailable
                        ? string.Create(English, $"{pct:0.0}%{fpsText} - ETA {_eta.ETR:hh\\:mm\\:ss}")
                        : string.Create(English, $"{pct:0.0}%{fpsText}");
                }
                else
                {
                    Text = string.Create(English, $"{pct:0.0}%");
                }
            }
            return true;
        }
    }
}
