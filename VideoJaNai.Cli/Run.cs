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
    // A planned run: the conf once, then per video the encode (which prepares the engines first),
    // into a temporary file next to the output that becomes the output when aji_encode succeeds
    // (PartialOutput), so a stopped or failed encode never leaves a file that skip-existing would keep.
    public static class Run
    {
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
                    var observer = new Observer(report, plan.Job.AllModels.Any());
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(v.Output)!);
                        var result = await runner.EncodeToOutputAsync(plan.Job, v.Input, v.Output, observer, null, ct);
                        if (result.Written)
                        {
                            written++;
                            report.VideoDone(n, plan.Videos.Count, v, took.Elapsed);
                        }
                        else
                        {
                            failed++;
                            report.VideoError(n, plan.Videos.Count, v, result.Failure, observer.Tail);
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
                }
            }
            finally
            {
                TryDelete(plan.Install.Paths.ConfPath);
            }
            report.Done(written, skipped, failed, start.Elapsed);
            return failed > 0 ? 1 : 0;
        }

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
        private sealed class Observer(Report report, bool upscales) : IEncodeObserver
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
                else if (upscales && line.Contains("no chain active for", StringComparison.Ordinal))
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
