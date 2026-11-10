using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VideoJaNai.Core
{
    // A damaged spot in the source video, from aji_encode's "DAMAGE t=<seconds> frame=<index>" line
    // (stdout, next to the PROGRESS lines): the time since the video stream's start, and the source
    // frame (null when aji_encode has none, frame=-1). aji_encode prints one per spot. With
    // --on-damage fail (its default) it stops at the first one and exits nonzero; with
    // --on-damage continue it finishes the file. An aji_encode without the option prints none.
    public readonly record struct DamageSpot(decimal Seconds, long? Frame)
    {
        private static readonly CultureInfo English = CultureInfo.InvariantCulture;

        public static bool IsDamageLine(string line) => line.StartsWith("DAMAGE ", StringComparison.Ordinal);

        // null for a line that isn't a DAMAGE line or has no time
        public static DamageSpot? Parse(string line)
        {
            if (!IsDamageLine(line))
            {
                return null;
            }
            var t = Regex.Match(line, @"\bt=(-?[0-9]+(?:\.[0-9]+)?)\b");
            if (!t.Success || !decimal.TryParse(t.Groups[1].Value, NumberStyles.Float, English, out var seconds))
            {
                return null;
            }
            var f = Regex.Match(line, @"\bframe=(-?[0-9]+)\b");
            long? frame = f.Success && long.TryParse(f.Groups[1].Value, NumberStyles.Integer, English, out var n) && n >= 0 ? n : null;
            return new DamageSpot(seconds, frame);
        }

        // 12:34 or 1:02:03 (rounded down to the second); with tenths, 12:34.5
        public string Time(bool tenths = false)
        {
            var totalTenths = (long)decimal.Floor(Math.Max(0, Seconds) * 10);
            var s = totalTenths / 10;
            var text = s >= 3600
                ? string.Create(English, $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}")
                : string.Create(English, $"{s / 60}:{s % 60:00}");
            return tenths ? string.Create(English, $"{text}.{totalTenths % 10}") : text;
        }

        // "12:34.5 (frame 18093)"
        public string Where => Frame is long f ? string.Create(English, $"{Time(tenths: true)} (frame {f})") : Time(tenths: true);
    }

    public static class DamageText
    {
        // how many times are listed before "and N more"
        public const int Listed = 5;

        // "Finished with 3 damaged frames (12:34, 12:35, 41:02)": the distinct times (to the second),
        // the first few of them
        public static string Finished(IReadOnlyList<DamageSpot> spots)
        {
            var times = spots.Select(s => s.Time()).Distinct().ToList();
            var listed = string.Join(", ", times.Take(Listed));
            var more = times.Count > Listed ? $" and {times.Count - Listed} more" : "";
            return $"Finished with {spots.Count} damaged frame{(spots.Count == 1 ? "" : "s")} ({listed}{more})";
        }
    }
}
