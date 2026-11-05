using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VideoJaNai.Core;

namespace VideoJaNai.Cli
{
    // Every run setting the flags or videojanai.ini can give; null = not given. Flags override the
    // file, the file overrides an imported workflow, which overrides the built-in defaults.
    public sealed class Options
    {
        public List<string> Inputs { get; } = [];
        public string? OutputFolder;
        public string? Filename;
        public string? Suffix;
        public bool? SkipExisting;

        public string? Preset;
        public List<string> Models { get; } = [];
        public string? Workflow;
        public bool? Upscale;

        public string? Deinterlace;
        public string? SquarePixels;

        public int? TemporalFix;              // strength 1-3, 0 = off
        public bool? TemporalFixBeforeUpscale;
        public decimal? TemporalFixSceneThreshold;

        public bool? Rife;
        public string? RifeModel;             // a label: "RIFE 4.22", "RIFE 4.25 Lite"
        public (int Num, int Den)? RifeFactor;
        public bool? RifeEnsemble;
        public bool? RifeBeforeUpscale;
        public decimal? RifeSceneThreshold;

        public string? Encoder;               // "auto" or an EncoderNames key
        public string? Ffmpeg;                // custom "<codec> <args>"
        public string? PixFmt;
        public int? Height;
        public int? ResizePercent;

        public string? Backend;               // auto, tensorrt, tensorrt-rtx, directml
        public string? Device;                // auto, a number from `devices`, an id or a name
        public string? EngineSettings;
        public string? AjiDir;

        // Where relative paths in these options are relative to (a settings file's folder).
        public string BaseDir = "";

        public static readonly IReadOnlyList<string> Presets = ["balanced", "performance", "balanced-sharp", "performance-sharp"];
        public static readonly IReadOnlyList<string> Backends = ["auto", "tensorrt", "tensorrt-rtx", "directml"];
        public static readonly IReadOnlyList<string> Deinterlaces = ["off", "ivtc", "bwdif"];
        public static readonly IReadOnlyList<string> SquarePixelModes = ["off", "before", "after"];
        public static readonly IReadOnlyList<string> PixFmts = ["yuv420p10", "yuv420p", "yuv444p10", "yuv444p"];
    }

    public static class EncoderNames
    {
        public static readonly IReadOnlyList<(string Name, EncoderPreset Preset)> All =
        [
            ("nvenc-hevc", EncoderPresets.NvencHevc),
            ("nvenc-av1", EncoderPresets.NvencAv1),
            ("nvenc-lossless", EncoderPresets.NvencLossless),
            ("amf-hevc", EncoderPresets.AmfHevc),
            ("amf-av1", EncoderPresets.AmfAv1),
            ("qsv-hevc", EncoderPresets.QsvHevc),
            ("qsv-av1", EncoderPresets.QsvAv1),
            ("x265", EncoderPresets.X265),
            ("x264", EncoderPresets.X264),
            ("ffv1", EncoderPresets.Ffv1),
        ];

        public static IReadOnlyList<string> Choices => ["auto", .. All.Select(e => e.Name)];

        public static EncoderPreset? Find(string name) =>
            All.FirstOrDefault(e => e.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).Preset;

        public static string NameOf(EncoderPreset p) => All.FirstOrDefault(e => e.Preset == p).Name ?? p.SummaryLabel;

        public static string? Parse(string v) =>
            v.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto"
            : Find(v) is not null ? v.Trim().ToLowerInvariant() : null;
    }

    public static class RifeNames
    {
        // "4.22", "4.25 lite", "4.25-lite", "RIFE 4.22" -> "RIFE 4.22" / "RIFE 4.25 Lite"
        public static string? Parse(string v)
        {
            var t = v.Trim();
            if (t.StartsWith("rife", StringComparison.OrdinalIgnoreCase))
            {
                t = t[4..].Trim(' ', '_', '-', 'v', 'V');
            }
            var lite = false;
            foreach (var suffix in new[] { " lite", "-lite", "_lite", "lite" })
            {
                if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    lite = true;
                    t = t[..^suffix.Length].Trim();
                    break;
                }
            }
            var parts = t.Split('.');
            if (parts.Length != 2 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)))
            {
                return null;
            }
            return $"RIFE {parts[0]}.{parts[1]}" + (lite ? " Lite" : "");
        }

        // "2", "2x", "5/2" -> (num, den)
        public static (int, int)? Factor(string v)
        {
            var t = v.Trim().TrimEnd('x', 'X').Trim();
            var slash = t.IndexOf('/');
            if (slash < 0)
            {
                return Numbers.Int(t) is int n && n > 0 ? (n, 1) : null;
            }
            return Numbers.Int(t[..slash]) is int a && Numbers.Int(t[(slash + 1)..]) is int b && a > 0 && b > 0 ? (a, b) : null;
        }
    }

    public static class PixFmtNames
    {
        public static string? Parse(string v)
        {
            var t = v.Trim().ToLowerInvariant().Replace(" ", "").Replace(":", "");
            return t switch
            {
                "yuv420p10" or "yuv420p10le" or "42010" or "420p10" or "10bit" or "10-bit" or "10bit420" or "10-bit420" => "yuv420p10",
                "yuv420p" or "420" or "420p" or "8bit" or "8-bit" or "8bit420" or "8-bit420" => "yuv420p",
                "yuv444p10" or "yuv444p10le" or "444p10" or "10bit444" or "10-bit444" => "yuv444p10",
                "yuv444p" or "444" or "444p" or "8bit444" or "8-bit444" => "yuv444p",
                _ => null,
            };
        }
    }
}
