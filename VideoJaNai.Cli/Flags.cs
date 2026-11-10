using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoJaNai.Cli
{
    public sealed class UsageException(string message) : Exception(message);

    // The command line: run options (the same as videojanai.ini's) plus what only flags say.
    public sealed class Flags
    {
        public Options Options { get; } = new();
        public string? Config;
        public bool NoConfig;
        public string? Settings;           // the GUI's appstate2.json
        public string? OutputFile;         // -o naming a video file
        public bool Lines;                 // --progress lines
        public bool Verbose;
        public bool Check;                 // setup --check
        public bool NoRife;                // setup --no-rife
        public bool Help;
        public bool Version;
        public List<string> Positional { get; } = [];

        // Flags that take no value.
        static readonly HashSet<string> Switches =
        [
            "--no-config", "--no-upscale", "--upscale", "--skip-existing", "--overwrite",
            "--stop-on-damage", "--keep-damaged",
            "--temporal-fix-before-upscale", "--rife", "--rife-ensemble", "--rife-after-upscale",
            "-v", "--verbose", "--check", "--no-rife", "-h", "--help", "-V", "--version",
        ];

        public static Flags Parse(IReadOnlyList<string> args)
        {
            var f = new Flags();
            var o = f.Options;
            var onlyPositional = false;
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i];
                if (onlyPositional || a == "-" || !a.StartsWith('-') || a.Length < 2)
                {
                    f.Positional.Add(a);
                    continue;
                }
                if (a == "--")
                {
                    onlyPositional = true;
                    continue;
                }
                string key = a;
                string? inline = null;
                var eq = a.IndexOf('=');
                if (a.StartsWith("--", StringComparison.Ordinal) && eq > 0)
                {
                    key = a[..eq];
                    inline = a[(eq + 1)..];
                }
                if (Switches.Contains(key))
                {
                    if (inline != null)
                    {
                        throw new UsageException($"{key} takes no value");
                    }
                }
                string Value()
                {
                    if (inline != null)
                    {
                        return inline;
                    }
                    if (i + 1 >= args.Count)
                    {
                        throw new UsageException($"{key} needs a value");
                    }
                    return args[++i];
                }
                T Parsed<T>(string what, Func<string, T?> parse) where T : struct
                {
                    var v = Value();
                    return parse(v) ?? throw new UsageException($"{key} should be {what}, not '{v}'");
                }
                string Choice(IReadOnlyList<string> choices, Func<string, string?>? parse = null)
                {
                    var v = Value();
                    return (parse != null ? parse(v) : choices.FirstOrDefault(c => c.Equals(v, StringComparison.OrdinalIgnoreCase)))
                        ?? throw new UsageException($"{key} should be one of {string.Join(", ", choices)}, not '{v}'");
                }

                switch (key)
                {
                    case "-c": case "--config": f.Config = Value(); break;
                    case "--no-config": f.NoConfig = true; break;
                    case "--settings": f.Settings = Value(); break;
                    case "-i": case "--input": o.Inputs.Add(Value()); break;
                    case "-o": case "--output": o.OutputFolder = Value(); break;
                    case "--filename": o.Filename = Value(); break;
                    case "--suffix": o.Suffix = Value(); break;
                    case "--skip-existing": o.SkipExisting = true; break;
                    case "--overwrite": o.SkipExisting = false; break;
                    case "--stop-on-damage": o.KeepDamaged = false; break;
                    case "--keep-damaged": o.KeepDamaged = true; break;

                    case "--preset": o.Preset = Choice(Options.Presets); break;
                    case "--model": o.Models.Add(Value()); break;
                    case "--workflow": o.Workflow = Value(); break;
                    case "--no-upscale": o.Upscale = false; break;
                    case "--upscale": o.Upscale = true; break;

                    case "--deinterlace": o.Deinterlace = Choice(Options.Deinterlaces); break;
                    case "--square-pixels": o.SquarePixels = Choice(Options.SquarePixelModes); break;

                    case "--temporal-fix":
                        o.TemporalFix = Parsed<int>("1, 2 or 3 (0: off)", v => Numbers.Int(v) is int s and >= 0 and <= 3 ? s : null);
                        break;
                    case "--temporal-fix-before-upscale": o.TemporalFixBeforeUpscale = true; break;
                    case "--temporal-fix-scene-threshold": o.TemporalFixSceneThreshold = Parsed("a number like 0.150", Numbers.Decimal); break;

                    case "--rife": o.Rife = true; break;
                    case "--rife-model":
                        {
                            var v = Value();
                            o.RifeModel = RifeNames.Parse(v) ?? throw new UsageException($"{key} should be a RIFE version like 4.22 or 4.25-lite, not '{v}'");
                            o.Rife ??= true;
                            break;
                        }
                    case "--rife-factor":
                        {
                            var v = Value();
                            o.RifeFactor = RifeNames.Factor(v) ?? throw new UsageException($"{key} should be a number like 2, or a fraction like 5/2, not '{v}'");
                            o.Rife ??= true;
                            break;
                        }
                    case "--rife-ensemble": o.RifeEnsemble = true; o.Rife ??= true; break;
                    case "--rife-after-upscale": o.RifeBeforeUpscale = false; o.Rife ??= true; break;
                    case "--rife-scene-threshold": o.RifeSceneThreshold = Parsed("a number like 0.150", Numbers.Decimal); break;

                    case "--encoder": o.Encoder = Choice(EncoderNames.Choices, EncoderNames.Parse); break;
                    case "--ffmpeg": o.Ffmpeg = Value(); break;
                    case "--pix-fmt": case "--pixel-format": o.PixFmt = Choice(Options.PixFmts, PixFmtNames.Parse); break;
                    case "--height": o.Height = Parsed("a height in pixels, like 1440", Numbers.Int); break;
                    case "--resize-percent": o.ResizePercent = Parsed("a percentage, like 75", Numbers.Int); break;

                    case "--backend": o.Backend = Choice(Options.Backends); break;
                    case "--device": case "--devices": case "--gpu": o.Device = Value(); break;
                    case "--engine-settings": o.EngineSettings = Value(); break;
                    case "--aji-dir": o.AjiDir = Value(); break;

                    case "--progress":
                        {
                            var v = Value();
                            f.Lines = v.ToLowerInvariant() switch
                            {
                                "lines" or "line" => true,
                                "human" or "bar" => false,
                                _ => throw new UsageException($"--progress should be human or lines, not '{v}'"),
                            };
                            break;
                        }
                    case "-v": case "--verbose": f.Verbose = true; break;
                    case "--check": f.Check = true; break;
                    case "--no-rife": f.NoRife = true; break;
                    case "-h": case "--help": f.Help = true; break;
                    case "-V": case "--version": f.Version = true; break;
                    default:
                        throw new UsageException($"unknown option {key} (see --help)");
                }
            }
            if (o.Height != null && o.ResizePercent != null)
            {
                throw new UsageException("give only one of --height and --resize-percent");
            }
            var sources = (o.Preset != null ? 1 : 0) + (o.Models.Count > 0 ? 1 : 0) + (o.Workflow != null ? 1 : 0);
            if (sources > 1)
            {
                throw new UsageException("give only one of --preset, --model or --workflow");
            }
            return f;
        }
    }
}
