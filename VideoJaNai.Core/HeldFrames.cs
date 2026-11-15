using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoJaNai.Core
{
    // aji_encode's held-frame reuse, as its log reports it at the end of an encode (stderr):
    // "held frames: reused 1055 of 3412 (30.9%)": of the frames it compared, how many got the
    // previous frame's upscale again instead of their own. Where reuse doesn't apply (RIFE, a
    // temporal chain, a backend without it) it logs "held frames: not reused (<why>)" instead, and
    // with --no-reuse-held-frames, or an aji_encode from before reuse, nothing.
    public readonly record struct HeldFrameReuse(long Reused, long Frames)
    {
        private static readonly CultureInfo English = CultureInfo.InvariantCulture;

        private static readonly Regex Line = new(@"^held frames: reused ([0-9]+) of ([0-9]+)\b", RegexOptions.CultureInvariant);

        // null for any other line
        public static HeldFrameReuse? Parse(string line)
        {
            var m = Line.Match(line);
            return m.Success &&
                   long.TryParse(m.Groups[1].Value, NumberStyles.None, English, out var reused) &&
                   long.TryParse(m.Groups[2].Value, NumberStyles.None, English, out var frames) &&
                   frames > 0 && reused <= frames
                ? new HeldFrameReuse(reused, frames)
                : null;
        }

        // A run's videos together (null when none reported).
        public static HeldFrameReuse? Sum(IEnumerable<HeldFrameReuse> videos)
        {
            long reused = 0, frames = 0;
            foreach (var v in videos)
            {
                reused += v.Reused;
                frames += v.Frames;
            }
            return frames > 0 ? new HeldFrameReuse(reused, frames) : null;
        }

        // "31%"; "<1%" when some were reused but under half a percent
        public string Percent
        {
            get
            {
                var pct = Math.Round(100.0 * Reused / Frames, MidpointRounding.AwayFromZero);
                return pct == 0 && Reused > 0 ? "<1%" : string.Create(English, $"{pct:0}%");
            }
        }

        // "Reused the upscale for 31% of frames (1,055 of 3,412)"
        public string Text => string.Create(English, $"Reused the upscale for {Percent} of frames ({Reused:N0} of {Frames:N0})");
    }
}
