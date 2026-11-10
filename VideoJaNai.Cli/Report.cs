using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using VideoJaNai.Core;

namespace VideoJaNai.Cli
{
    // What a run reports, in one of two modes (as mangajanai):
    // - Human (the default): a progress bar with %, frames, fps and ETA on stderr, notices for engine
    //   builds, fallbacks and errors, aji_encode's log only with --verbose (and on a failure), and a
    //   summary at the end.
    // - Lines (--progress lines), for programs, on stdout, one event per line:
    //     PLAN <videos> <to encode> <to skip>
    //     VIDEO_START <n> <of> <input>        (n counts from 1)
    //     VIDEO_SKIP <n> <of> <output>        the output exists (skip-existing)
    //     PROGRESS phase=... frame=... ...     aji_encode's own line, passed through as is
    //     VIDEO_DONE <n> <of> <output>
    //     VIDEO_ERROR <n> <of> <message>
    //     NOTE <text>                          worth showing (no chain for the size, a fallback)
    //     BACKEND_FALLBACK <message>           libaji's TensorRT-RTX -> TensorRT fallback
    //     DONE <written> <skipped> <failed>
    //   aji_encode's log goes to stderr in this mode.
    public abstract class Report
    {
        public abstract void Info(string text);
        public abstract void Plan(int videos, int encode, int skip);
        public abstract void VideoStart(int n, int of, PlannedVideo v);
        public abstract void VideoSkip(int n, int of, PlannedVideo v);
        public abstract void Progress(string line, AjiProgress p);
        public abstract void VideoDone(int n, int of, PlannedVideo v, TimeSpan took);
        public abstract void VideoError(int n, int of, PlannedVideo v, string message, IReadOnlyList<string> log);
        public abstract void Note(string text);
        public abstract void Fallback(string message);
        public abstract void AjiLog(string line);
        public abstract void Done(int written, int skipped, int failed, TimeSpan took);

        public static string Duration(TimeSpan d)
        {
            var s = (long)d.TotalSeconds;
            return s switch
            {
                < 60 => $"{s}s",
                < 3600 => $"{s / 60}m{s % 60:00}s",
                _ => $"{s / 3600}h{s / 60 % 60:00}m",
            };
        }
    }

    public sealed class LinesReport : Report
    {
        private readonly object _lock = new();

        private void Line(string s)
        {
            lock (_lock)
            {
                Console.Out.WriteLine(s);
                Console.Out.Flush();
            }
        }

        public override void Info(string text) => Console.Error.WriteLine(text);
        public override void Plan(int videos, int encode, int skip) => Line($"PLAN {videos} {encode} {skip}");
        public override void VideoStart(int n, int of, PlannedVideo v) => Line($"VIDEO_START {n} {of} {v.Input}");
        public override void VideoSkip(int n, int of, PlannedVideo v) => Line($"VIDEO_SKIP {n} {of} {v.Output}");
        public override void Progress(string line, AjiProgress p) => Line(line);
        public override void VideoDone(int n, int of, PlannedVideo v, TimeSpan took) => Line($"VIDEO_DONE {n} {of} {v.Output}");
        public override void VideoError(int n, int of, PlannedVideo v, string message, IReadOnlyList<string> log) =>
            Line($"VIDEO_ERROR {n} {of} {message.Replace('\n', ' ')}");
        public override void Note(string text) => Line($"NOTE {text}");
        public override void Fallback(string message) => Line($"BACKEND_FALLBACK {message}");
        public override void AjiLog(string line) => Console.Error.WriteLine(line);
        public override void Done(int written, int skipped, int failed, TimeSpan took) => Line($"DONE {written} {skipped} {failed}");
    }

    public sealed class HumanReport(bool verbose) : Report
    {
        private static readonly CultureInfo English = CultureInfo.InvariantCulture;
        private readonly bool _tty = !Console.IsErrorRedirected;
        private readonly object _lock = new();
        private int _barLength;
        private DateTime _lastDraw;
        private string _prefix = "";
        private DateTime _videoStart;
        private string _lastPhase = "";

        private void Clear()
        {
            if (_barLength > 0)
            {
                Console.Error.Write("\r" + new string(' ', _barLength) + "\r");
                _barLength = 0;
            }
        }

        private void Say(string text)
        {
            lock (_lock)
            {
                Clear();
                Console.Error.WriteLine(text);
            }
        }

        public override void Info(string text) => Say(text);

        public override void Plan(int videos, int encode, int skip)
        {
            if (videos > 1 || skip > 0)
            {
                Say(skip > 0
                    ? $"{videos} videos: {encode} to upscale, {skip} already done (skipped)"
                    : $"{videos} videos to upscale");
            }
        }

        public override void VideoStart(int n, int of, PlannedVideo v)
        {
            _prefix = of > 1 ? $"[{n}/{of}] " : "";
            _videoStart = DateTime.UtcNow;
            _lastPhase = "";
            Say($"{_prefix}{v.Input}\n{new string(' ', _prefix.Length)}-> {v.Output}");
        }

        public override void VideoSkip(int n, int of, PlannedVideo v)
        {
            if (verbose)
            {
                Say($"exists, skipped: {v.Output}");
            }
        }

        public override void Progress(string line, AjiProgress p)
        {
            lock (_lock)
            {
                var force = p.Phase != _lastPhase;
                _lastPhase = p.Phase;
                var every = _tty ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10);
                if (!force && DateTime.UtcNow - _lastDraw < every)
                {
                    return;
                }
                _lastDraw = DateTime.UtcNow;
                var status = Status(p);
                if (_tty)
                {
                    var pad = Math.Max(0, _barLength - status.Length);
                    Console.Error.Write("\r" + status + new string(' ', pad));
                    _barLength = Math.Max(_barLength, status.Length);
                }
                else
                {
                    Console.Error.WriteLine(status);
                }
            }
        }

        private string Status(AjiProgress p)
        {
            var elapsed = Duration(DateTime.UtcNow - _videoStart);
            if (p.PreparingEngine)
            {
                return $"{_prefix}preparing the engine (a first run per model and size builds it)  {elapsed}";
            }
            const int W = 24;
            var frac = Math.Clamp(p.Percent / 100.0, 0, 1);
            var filled = (int)Math.Round(frac * W);
            var frames = p.TotalFrames > 0 ? $" {p.Frame}/{p.TotalFrames}" : p.Frame > 0 ? $" {p.Frame} frames" : "";
            var fps = p.Fps > 0 ? string.Create(English, $"  {p.Fps:0.0} fps") : "";
            var eta = p.Remaining is TimeSpan r ? $"  ETA {Duration(r)}" : "";
            return string.Create(English, $"{_prefix}{(int)p.Percent,3}% [{new string('#', filled)}{new string('-', W - filled)}]{frames}{fps}{eta}  {elapsed}");
        }

        public override void VideoDone(int n, int of, PlannedVideo v, TimeSpan took)
        {
            if (verbose || of > 1)
            {
                Say($"{_prefix}done in {Duration(took)}");
            }
            else
            {
                lock (_lock)
                {
                    Clear();
                }
            }
        }

        public override void VideoError(int n, int of, PlannedVideo v, string message, IReadOnlyList<string> log)
        {
            lock (_lock)
            {
                Clear();
                if (!verbose)
                {
                    foreach (var l in log)
                    {
                        Console.Error.WriteLine($"  {l}");
                    }
                }
                Console.Error.WriteLine($"error: {v.Input}: {message}");
            }
        }

        public override void Note(string text) => Say($"note: {text}");
        public override void Fallback(string message) => Say($"warning: {message}");

        public override void AjiLog(string line)
        {
            if (verbose)
            {
                Say(line);
            }
        }

        public override void Done(int written, int skipped, int failed, TimeSpan took)
        {
            var msg = $"Done: {written} written in {Duration(took)}";
            if (skipped > 0)
            {
                msg += $", {skipped} skipped (already there)";
            }
            if (failed > 0)
            {
                msg += $", {failed} failed";
            }
            Say(msg);
        }
    }
}
