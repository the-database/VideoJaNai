using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoJaNai.Cli
{
    // videojanai.ini, the command line's saved settings. Every setting is optional and mirrors a
    // flag. See Ini for the format's rules.
    public static class Config
    {
        public const string FileName = "videojanai.ini";

        // What `videojanai init` writes, and the reference for every setting.
        public const string Template = """
; videojanai settings
;
; Run `videojanai` with no arguments to use this file. It is found in the current folder, else
; next to the program; -c <file> picks another. Anything given on the command line overrides it.
; Every setting is optional: an empty one uses the default (shown in its comment), or the
; workflow's setting when [upscale] workflow names one.
; Relative paths are relative to this file's folder. Lines starting with ; are comments.

[input]
; What to upscale: a video, or a folder (searched with its subfolders). Add more "path =" lines
; for more.
path =

[output]
; Where the results go (default: next to each video; for a folder, "<folder> animejanai").
; Folders keep their subfolders.
folder =
; The file name, where %filename% is the video's name without its extension (default
; %filename%-animejanai.mkv). The extension picks the container (.mkv, .mp4, ...).
filename =
; Or just what's added to the name: ep01.mkv -> ep01_x2.mkv (used when filename is empty).
suffix =
; Keep results that already exist instead of redoing them (yes or no; default yes).
skip_existing =
; TV recordings and incomplete downloads can have damaged frames. no (default): such a video
; fails, and the error says where the damage is. yes: it is finished, and the damaged spots are
; listed.
keep_damaged =

[upscale]
; Which models run. Pick one of these three (default: preset balanced):
;   preset   = balanced: AnimeJaNai HD V3.1 Balanced for 720p-1080p sources, the SD model below
;              720p (mpv-AnimeJaNai's default); also performance, balanced-sharp, performance-sharp.
;              Sources above 1080p are left at their size.
;   model    = an ONNX model, for every source (a path, or a file name in the models folder).
;              Add more "model =" lines to run them one after another.
;   workflow = a .awf workflow exported from VideoJaNai (all of its settings; the settings in this
;              file still override them)
preset =
model =
workflow =
; Turn upscaling off to only deinterlace, fix shimmer or interpolate (yes or no; default yes).
upscale =

[source]
; Deinterlacing: off (default), ivtc (telecined film, e.g. NTSC anime DVDs: back to 23.976 fps) or bwdif
; (true interlaced video, one frame per field).
deinterlace =
; Non-square pixels (DVD, DV) stretched to square: off (default), before or after the upscale.
square_pixels =

[temporal_fix]
; vs_temporalfix, which steadies shimmer: strength 1, 2 or 3 (empty or 0: off).
strength =
; Run it before the upscale, at the source resolution: 2-3x faster, but it can't steady shimmer
; the upscale adds (yes or no; default no).
before_upscale =
; Scene detection: neighbor frames that differ more than this are treated as another scene
; and not mixed in (default 0.150; 0: off).
scene_threshold =

[interpolation]
; RIFE frame interpolation (yes or no; default no).
rife =
; The RIFE model, like 4.22 (default) or 4.25 lite.
model =
; How many times the frame rate: 2 (default), or a fraction like 5/2 (24 -> 60 fps).
factor =
; RIFE's ensemble mode (yes or no; default no).
ensemble =
; Interpolate before the upscale, at the source resolution, which is faster (yes or no;
; default yes).
before_upscale =
; Scene detection: no in-between frames across a change bigger than this (default 0.150).
scene_threshold =

[encode]
; auto (default: HEVC on the upscaling GPU, else x265), nvenc-hevc, nvenc-av1,
; nvenc-lossless, amf-hevc, amf-av1, qsv-hevc, qsv-av1, x265, x264 or ffv1.
encoder =
; Or your own FFmpeg encoder settings: "<encoder> <options>", like libx265 -crf 18 -preset slow
; (overrides encoder).
ffmpeg =
; yuv420p10 (10-bit 4:2:0, default), yuv420p (8-bit), yuv444p10 or yuv444p (4:4:4: CPU encoders only).
pixel_format =

[resize]
; The final size after upscaling. Set at most one: height in pixels (keeping the shape), or
; percent of the upscaled size.
height =
percent =

[gpu]
; auto (default), tensorrt, tensorrt-rtx or directml (tensorrt-rtx and directml: Windows only)
backend =
; auto (default), or a GPU number from `videojanai devices` (or its name)
device =
; TensorRT engine build flags (trtexec style). Empty: the default.
engine_settings =
; Where aji_encode's animejanai folder is (empty: next to the program, else an installed
; VideoJaNai's).
aji_folder =
""";

        static readonly string[] Sections = ["input", "output", "upscale", "source", "temporal_fix", "interpolation", "encode", "resize", "gpu"];

        public static Options Load(string path)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                throw new IniException($"{path}: {e.Message}");
            }
            try
            {
                var o = Parse(Ini.Decode(bytes));
                o.BaseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
                return o;
            }
            catch (IniException e)
            {
                throw new IniException($"{path}: {e.Message}");
            }
        }

        public static Options Parse(string text)
        {
            var o = new Options();
            var seen = new Dictionary<string, int>();
            foreach (var sec in Ini.Sections(text))
            {
                if (!Sections.Contains(sec.Name))
                {
                    throw new IniException($"line {sec.Line}: unknown section [{sec.Name}]{Ini.Suggest(sec.Name, Sections)}");
                }
                if (seen.TryGetValue(sec.Name, out var first))
                {
                    throw new IniException($"line {sec.Line}: [{sec.Name}] appears twice (also on line {first}); keep one");
                }
                seen[sec.Name] = sec.Line;
                var r = new IniReader(sec);
                switch (sec.Name)
                {
                    case "input":
                        o.Inputs.AddRange(r.All("path").Select(p => p.Value));
                        r.Finish(["path"]);
                        break;
                    case "output":
                        o.OutputFolder = r.String("folder");
                        o.Filename = r.String("filename");
                        o.Suffix = r.String("suffix");
                        o.SkipExisting = r.Switch("skip_existing");
                        o.KeepDamaged = r.Switch("keep_damaged");
                        r.Finish(["folder", "filename", "suffix", "skip_existing", "keep_damaged"]);
                        break;
                    case "upscale":
                        o.Preset = r.Choice("preset", Options.Presets);
                        o.Models.AddRange(r.All("model").Select(m => m.Value));
                        o.Workflow = r.String("workflow");
                        o.Upscale = r.Switch("upscale");
                        r.Finish(["preset", "model", "workflow", "upscale"]);
                        var sources = (o.Preset != null ? 1 : 0) + (o.Models.Count > 0 ? 1 : 0) + (o.Workflow != null ? 1 : 0);
                        if (sources > 1)
                        {
                            throw new IniException($"line {sec.Line}: pick one way to choose the models in [upscale]: preset, model or workflow");
                        }
                        break;
                    case "source":
                        o.Deinterlace = r.Choice("deinterlace", Options.Deinterlaces);
                        o.SquarePixels = r.Choice("square_pixels", Options.SquarePixelModes);
                        r.Finish(["deinterlace", "square_pixels"]);
                        break;
                    case "temporal_fix":
                        o.TemporalFix = r.One("strength") is var (sn, sv)
                            ? (Numbers.Int(sv) is int s and >= 0 and <= 3 ? s
                               : Switches.Parse(sv) is bool b ? (b ? 2 : 0)
                               : throw new IniException($"line {sn}: strength should be 1, 2 or 3, not '{sv}'"))
                            : null;
                        o.TemporalFixBeforeUpscale = r.Switch("before_upscale");
                        o.TemporalFixSceneThreshold = r.Decimal("scene_threshold", "a number like 0.150");
                        r.Finish(["strength", "before_upscale", "scene_threshold"]);
                        break;
                    case "interpolation":
                        o.Rife = r.Switch("rife");
                        o.RifeModel = r.One("model") is var (mn, mv)
                            ? RifeNames.Parse(mv) ?? throw new IniException($"line {mn}: model should be a RIFE version like 4.22 or 4.25 lite, not '{mv}'")
                            : null;
                        o.RifeFactor = r.One("factor") is var (fn, fv)
                            ? RifeNames.Factor(fv) ?? throw new IniException($"line {fn}: factor should be a number like 2, or a fraction like 5/2, not '{fv}'")
                            : null;
                        o.RifeEnsemble = r.Switch("ensemble");
                        o.RifeBeforeUpscale = r.Switch("before_upscale");
                        o.RifeSceneThreshold = r.Decimal("scene_threshold", "a number like 0.150");
                        r.Finish(["rife", "model", "factor", "ensemble", "before_upscale", "scene_threshold"]);
                        break;
                    case "encode":
                        o.Encoder = r.Choice("encoder", EncoderNames.Choices, EncoderNames.Parse);
                        o.Ffmpeg = r.String("ffmpeg");
                        o.PixFmt = r.Choice("pixel_format", Options.PixFmts, PixFmtNames.Parse);
                        r.Finish(["encoder", "ffmpeg", "pixel_format"]);
                        break;
                    case "resize":
                        o.Height = r.Int("height", "a height in pixels, like 1440");
                        o.ResizePercent = r.Int("percent", "a percentage, like 75");
                        r.Finish(["height", "percent"]);
                        if (o.Height != null && o.ResizePercent != null)
                        {
                            throw new IniException($"line {sec.Line}: set only one of height and percent in [resize]");
                        }
                        break;
                    case "gpu":
                        o.Backend = r.Choice("backend", Options.Backends);
                        o.Device = r.String("device");
                        o.EngineSettings = r.String("engine_settings");
                        o.AjiDir = r.String("aji_folder");
                        r.Finish(["backend", "device", "engine_settings", "aji_folder"]);
                        break;
                }
            }
            return o;
        }
    }
}
