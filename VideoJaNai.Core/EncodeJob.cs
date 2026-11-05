using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VideoJaNai.Core
{
    public enum Backend { TensorRt, TensorRtRtx, DirectMl }

    public static class BackendNames
    {
        // [global] backend= in the conf
        public static string Conf(Backend b) => b switch
        {
            Backend.DirectMl => "DirectML",
            Backend.TensorRtRtx => "TensorRT-RTX",
            _ => "TensorRT",
        };

        // aji_encode --backend (must agree with the conf's)
        public static string Arg(Backend b) => b switch
        {
            Backend.DirectMl => "directml",
            Backend.TensorRtRtx => "tensorrt-rtx",
            _ => "tensorrt",
        };

        public static string Label(Backend b) => b switch
        {
            Backend.DirectMl => "DirectML",
            Backend.TensorRtRtx => "TensorRT-RTX",
            _ => "TensorRT",
        };

        // The backend the settings pick, the GUI's rule: DirectML when chosen (Windows), else
        // TensorRT-RTX when chosen, or when nobody chose and it is installed (Windows), else TensorRT.
        public static Backend Resolve(bool? useDirectMl, bool? useTensorRtRtx, bool tensorRtRtxInstalled) =>
            useDirectMl == true && Platform.DirectMlAvailable ? Backend.DirectMl
            : Platform.TensorRtRtxAvailable && (useTensorRtRtx ?? tensorRtRtxInstalled) ? Backend.TensorRtRtx
            : Backend.TensorRt;
    }

    // One model step of a chain, as the conf writes it.
    public sealed record ModelStep(string OnnxPath, int? ResizeHeight, decimal? ResizeFactor, decimal? TemporalSceneThreshold);

    // A chain: the sources it applies to (by pixel count, libaji's chain matching: the first chain
    // whose range holds the source wins; 0x0..0x0 is every source) and its models.
    public sealed record ChainSpec
    {
        public string MinResolution { get; init; } = "0x0";
        public string MaxResolution { get; init; } = "0x0";
        public IReadOnlyList<ModelStep> Models { get; init; } = [];
    }

    public sealed record RifeSpec(bool Enabled, string Model, int? Numerator, int? Denominator,
                                  decimal? SceneDetectThreshold, bool Ensemble, bool BeforeUpscale);

    // Everything aji_encode is told for a run: the conf's content and the command line's settings.
    public sealed record EncodeJob
    {
        public Backend Backend { get; init; }
        // aji_encode --device (a probe GPU id), null for the backend's default GPU
        public string? Device { get; init; }
        public string TrtEngineSettings { get; init; } = TrtEngine.DefaultSettings;
        public IReadOnlyList<ChainSpec> Chains { get; init; } = [new ChainSpec()];
        public RifeSpec Rife { get; init; } = new(false, "422", 2, 1, 0.150M, false, true);
        public string Deinterlace { get; init; } = "off";
        public string SquarePixels { get; init; } = "off";
        // "<codec> <encoder args...>", an EncoderPreset's Settings or custom
        public string FfmpegVideoSettings { get; init; } = EncoderPresets.FfmpegHevcNvenc;
        public string PixFmt { get; init; } = "yuv420p10";
        public int? FinalResizeHeight { get; init; } = 0;
        public int? FinalResizeFactor { get; init; } = 100;
        public bool Overwrite { get; init; }

        public IEnumerable<ModelStep> AllModels => Chains.SelectMany(c => c.Models);

        // A workflow on this machine. The temporal fix model comes from the models folder (the
        // newest version of the strength's file); a missing one leaves the step out, which
        // EncodeChecks reports.
        public static EncodeJob FromWorkflow(WorkflowSettings wf, Backend backend, string? device,
                                             EncoderPreset defaultEncoder, string modelsDirectory)
        {
            var models = new List<ModelStep>();
            var temporalFix = wf.EnableTemporalFix ? TemporalFix.ModelPath(modelsDirectory, wf.TemporalFixStrength ?? 2) : null;
            var temporalFixFirst = temporalFix != null && wf.TemporalFixBeforeUpscale;
            if (temporalFixFirst)
            {
                models.Add(new ModelStep(temporalFix!, 0, 100M, wf.TemporalFixSceneThreshold ?? ModelSettings.DefaultTemporalSceneThreshold));
            }
            foreach (var m in wf.EnableUpscale ? wf.UpscaleSettings : [])
            {
                // Temporal models: the window size comes from the onnx itself; only the scene-change
                // threshold is a setting (written only when it carries something, like the Manager).
                var threshold = m.TemporalSceneThreshold ?? ModelSettings.DefaultTemporalSceneThreshold;
                var temporal = OnnxInputShape.TemporalFrames(m.OnnxModelPath) > 1;
                models.Add(new ModelStep(m.OnnxModelPath, m.ResizeHeightBeforeUpscale ?? 0, m.ResizeFactorBeforeUpscale ?? 100,
                                         temporal || threshold != ModelSettings.DefaultTemporalSceneThreshold ? threshold : null));
            }
            if (temporalFix != null && !temporalFixFirst)
            {
                models.Add(new ModelStep(temporalFix, 0, 100M, wf.TemporalFixSceneThreshold ?? ModelSettings.DefaultTemporalSceneThreshold));
            }

            return new EncodeJob
            {
                Backend = backend,
                Device = device,
                TrtEngineSettings = wf.TrtEngineSettings,
                Chains = [new ChainSpec { Models = models }],
                Rife = new RifeSpec(wf.EnableRife, Core.Rife.LabelToValue(wf.RifeModel), wf.RifeFactorNumerator ?? 2,
                                    wf.RifeFactorDenominator ?? 1, wf.RifeSceneDetectThreshold, wf.RifeEnsemble, wf.RifeBeforeUpscale),
                Deinterlace = string.IsNullOrEmpty(wf.Deinterlace) ? "off" : wf.Deinterlace,
                SquarePixels = string.IsNullOrEmpty(wf.SquarePixels) ? "off" : wf.SquarePixels,
                FfmpegVideoSettings = wf.UsesDefaultEncoder ? defaultEncoder.Settings : wf.FfmpegVideoSettings,
                PixFmt = wf.OutputPixFmt,
                FinalResizeHeight = wf.FinalResizeHeight ?? 0,
                FinalResizeFactor = wf.FinalResizeFactor ?? 100,
                Overwrite = wf.OverwriteExistingVideos,
            };
        }
    }

    public static class TemporalFix
    {
        // 0 would be a pass-through (vs_temporalfix's own strength 0 returns the clip untouched).
        public static IReadOnlyList<int> Strengths { get; } = [1, 2, 3];

        // The bundled vs_temporalfix model for a strength (temporalfix_s2_v1_op18_fp16.onnx, ...): the
        // newest version in the models folder, or null when it is missing.
        public static string? ModelPath(string modelsDirectory, int strength)
        {
            if (!Directory.Exists(modelsDirectory))
            {
                return null;
            }
            return Directory.EnumerateFiles(modelsDirectory, $"temporalfix_s{strength}_v*.onnx")
                .Order(StringComparer.OrdinalIgnoreCase)
                .LastOrDefault();
        }
    }

    public static class Rife
    {
        // "RIFE 4.22" -> "422", "RIFE 4.25 Lite" -> "4251" (the conf's rife_model code)
        public static string LabelToValue(string rifeLabel)
        {
            if (string.IsNullOrEmpty(rifeLabel))
            {
                return rifeLabel;
            }
            var m = Regex.Match(rifeLabel, @"RIFE (\d+)\.(\d+)( Lite)?");
            if (m.Success)
            {
                var value = $"{m.Groups[1].Value}{m.Groups[2].Value}";
                if (m.Groups[3].Success)
                {
                    value += "1";
                }
                return value;
            }
            throw new ArgumentException(rifeLabel);
        }

        public static string ValueToLabel(string rifeValue)
        {
            if (string.IsNullOrEmpty(rifeValue))
            {
                return rifeValue;
            }
            var dec = rifeValue.Length == 2 ? rifeValue[1].ToString() : rifeValue.Substring(1, 2);
            var modelName = $"RIFE {rifeValue[0]}.{dec}";
            if (rifeValue.Length >= 4 && rifeValue.EndsWith('1'))
            {
                modelName += " Lite";
            }
            return modelName;
        }

        // The RIFE models in the folder (rife_v4.22.onnx, rife_v4.25_lite.onnx, ...) as labels, newest first.
        public static List<string> Models(string rifeModelsDirectory)
        {
            if (!Directory.Exists(rifeModelsDirectory))
            {
                return [];
            }
            var models = new List<string>();
            foreach (var file in Directory.GetFiles(rifeModelsDirectory, searchPattern: "*.onnx"))
            {
                var m = Regex.Match(Path.GetFileName(file), @"rife_v(\d+)\.(\d+)(_lite)?(_ensemble)?.onnx");
                if (m.Success)
                {
                    var model = m.Groups[1].Value + m.Groups[2].Value;
                    if (file.Contains("_lite"))
                    {
                        model += "1";
                    }
                    if (!models.Contains(model))
                    {
                        models.Add(model);
                    }
                }
            }
            models.Sort((m1, m2) =>
            {
                var m1i = decimal.Parse(m1[..Math.Min(3, m1.Length)]);
                var m2i = decimal.Parse(m2[..Math.Min(3, m2.Length)]);
                if (m1.Length > 3)
                {
                    m1i += .1m;
                }
                if (m2.Length > 3)
                {
                    m2i += .1m;
                }
                return m2i.CompareTo(m1i);
            });
            return [.. models.Select(ValueToLabel)];
        }
    }
}
