using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoJaNai.Core;

namespace VideoJaNai.Cli
{
    // A planned run: the conf once, then per video an engine prebuild (TensorRT) and the encode,
    // into a temporary file next to the output that becomes the output when aji_encode succeeds,
    // so a stopped or failed encode never leaves a file that skip-existing would keep.
    public static class Run
    {
        public const string PartialMarker = ".videojanai-partial";

        public static async Task<int> ExecuteAsync(RunPlan plan, Report report, CancellationToken ct)
        {
            var start = Stopwatch.StartNew();
            report.Info($"videojanai: {plan.Heading}");
            foreach (var note in plan.Notes)
            {
                report.Note(note);
            }
            var toEncode = plan.Videos.Count(v => v.Action == PlanAction.Encode);
            report.Plan(plan.Videos.Count, toEncode, plan.Videos.Count - toEncode);

            var runner = new EncodeRunner(plan.Install.Paths);
            if (toEncode > 0)
            {
                runner.Prepare(plan.Job);
            }
            int written = 0, skipped = 0, failed = 0;
            try
            {
                for (var i = 0; i < plan.Videos.Count; i++)
                {
                    var v = plan.Videos[i];
                    var n = i + 1;
                    if (v.Action == PlanAction.SkipExisting)
                    {
                        skipped++;
                        report.VideoSkip(n, plan.Videos.Count, v);
                        continue;
                    }
                    ct.ThrowIfCancellationRequested();
                    report.VideoStart(n, plan.Videos.Count, v);
                    var took = Stopwatch.StartNew();
                    var observer = new Observer(report);
                    var temp = PartialPath(v.Output);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(v.Output)!);
                        var rc = await runner.BuildEngineAsync(plan.Job, v.Input, observer, ct);
                        if (rc == 0)
                        {
                            rc = await runner.EncodeAsync(plan.Job, v.Input, temp, observer, null, ct);
                        }
                        if (rc == 0 && File.Exists(temp))
                        {
                            File.Move(temp, v.Output, overwrite: true);
                            written++;
                            report.VideoDone(n, plan.Videos.Count, v, took.Elapsed);
                        }
                        else
                        {
                            failed++;
                            report.VideoError(n, plan.Videos.Count, v, Observer.Failure(rc), observer.Tail);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                        failed++;
                        report.VideoError(n, plan.Videos.Count, v, e.Message, observer.Tail);
                    }
                    finally
                    {
                        TryDelete(temp);
                    }
                }
            }
            finally
            {
                TryDelete(plan.Install.Paths.ConfPath);
            }
            report.Done(written, skipped, failed, start.Elapsed);
            return failed > 0 ? 1 : 0;
        }

        // ep01-animejanai.mkv -> ep01-animejanai.videojanai-partial.mkv (the extension still picks
        // the container)
        public static string PartialPath(string output) =>
            Path.Join(Path.GetDirectoryName(output), Path.GetFileNameWithoutExtension(output) + PartialMarker + Path.GetExtension(output));

        static void TryDelete(string path)
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

        // aji_encode's lines -> the report: PROGRESS to the bar (or passed through), the log kept
        // (its last lines explain a failure), fallbacks and passthrough as notices.
        private sealed class Observer(Report report) : IEncodeObserver
        {
            private readonly AjiProgress _progress = new();
            private readonly Queue<string> _tail = new();
            private readonly object _lock = new();

            public IReadOnlyList<string> Tail
            {
                get
                {
                    lock (_lock)
                    {
                        return [.. _tail];
                    }
                }
            }

            public static string Failure(int rc) =>
                rc == 0 ? "aji_encode wrote no output" : $"aji_encode failed (exit code {rc})";

            public void Command(string text) => report.AjiLog(text);

            public void EncodeStarting(string input, string output) => _progress.Reset();

            public void Stdout(string line)
            {
                if (_progress.Handle(line))
                {
                    report.Progress(line, _progress);
                }
                else if (!line.StartsWith("PREVIEW", StringComparison.Ordinal))
                {
                    Log(line);
                }
            }

            public void Stderr(string line)
            {
                Log(line);
                if (BackendFallback.FromAjiLog(line) is string fallback)
                {
                    report.Fallback(fallback);
                }
                else if (line.Contains("no chain active for", StringComparison.Ordinal))
                {
                    var at = line.IndexOf("no chain active for", StringComparison.Ordinal);
                    report.Note($"{line[at..]}: no chain covers this size, so the video is re-encoded without upscaling");
                }
            }

            private void Log(string line)
            {
                lock (_lock)
                {
                    _tail.Enqueue(line);
                    while (_tail.Count > 12)
                    {
                        _tail.Dequeue();
                    }
                }
                report.AjiLog(line);
            }
        }
    }
}
