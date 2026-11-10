using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VideoJaNai.Core
{
    // A workflow's settings that shape a run, as plain data: the fields of the GUI's UpscaleWorkflow
    // (same names, same defaults), which the GUI copies in (UpscaleWorkflow.ToSettings) and the
    // command line reads from appstate2.json / an exported .awf (AppState) or builds from its own
    // settings. Both then go through EncodeJob.FromWorkflow, so a workflow runs the same everywhere.
    public sealed class WorkflowSettings
    {
        public string WorkflowName { get; set; } = string.Empty;

        // 0: one input video (InputFilePath), otherwise a folder (InputFolderPath)
        public int SelectedTabIndex { get; set; }
        public string InputFilePath { get; set; } = string.Empty;
        public string InputFolderPath { get; set; } = string.Empty;
        public string OutputFilename { get; set; } = "%filename%-animejanai.mkv";
        public string OutputFolderPath { get; set; } = string.Empty;
        public bool OverwriteExistingVideos { get; set; }
        // finish a video with damaged frames (TV recordings, incomplete downloads), listing them,
        // instead of failing it at the first
        public bool FinishDamagedVideos { get; set; }

        // empty (or the legacy bundled default): this PC's default encoder
        public string FfmpegVideoSettings { get; set; } = string.Empty;
        public string Deinterlace { get; set; } = "off";
        public string SquarePixels { get; set; } = "off";
        public string OutputPixFmt { get; set; } = "yuv420p10";
        public string TrtEngineSettings { get; set; } = TrtEngine.DefaultSettings;
        public int? FinalResizeHeight { get; set; } = 0;
        public int? FinalResizeFactor { get; set; } = 100;

        public List<ModelSettings> UpscaleSettings { get; set; } = [];
        public bool EnableUpscale { get; set; } = true;

        public bool EnableTemporalFix { get; set; }
        public int? TemporalFixStrength { get; set; } = 2;
        public decimal? TemporalFixSceneThreshold { get; set; } = ModelSettings.DefaultTemporalSceneThreshold;
        public bool TemporalFixBeforeUpscale { get; set; }

        public bool EnableRife { get; set; }
        public string RifeModel { get; set; } = "RIFE 4.22";
        public bool RifeEnsemble { get; set; }
        public int? RifeFactorNumerator { get; set; } = 2;
        public int? RifeFactorDenominator { get; set; } = 1;
        public decimal? RifeSceneDetectThreshold { get; set; } = 0.150M;
        public bool RifeBeforeUpscale { get; set; } = true;

        public bool UsesDefaultEncoder =>
            string.IsNullOrWhiteSpace(FfmpegVideoSettings) ||
            FfmpegVideoSettings.Trim() == EncoderPresets.LegacyDefaultSettings;
    }

    public sealed class ModelSettings
    {
        public const decimal DefaultTemporalSceneThreshold = 0.150M;

        public string OnnxModelPath { get; set; } = string.Empty;
        public int? ResizeHeightBeforeUpscale { get; set; } = 0;
        public decimal? ResizeFactorBeforeUpscale { get; set; } = 100;
        public decimal? TemporalSceneThreshold { get; set; } = DefaultTemporalSceneThreshold;
    }

    // TensorRT engine-build settings (trtexec-style flags, also read by TensorRT-RTX).
    public static class TrtEngine
    {
        // Must stay byte-for-byte equal to libaji's DEFAULT_TRT_ENGINE_SETTINGS, so the conf can
        // leave it out (write-minimal, like AnimeJaNaiConfEditor).
        public const string DefaultSettings =
            "--builderOptimizationLevel=5 --optShapes=input:%video_resolution% --skipInference";

        // Settings saved by older versions: strip flags that are dead on TensorRT 11 (libaji's
        // sanitize_settings_trt11 removes them before building anyway), keeping everything else.
        public static string Normalize(string settings)
        {
            if (string.IsNullOrWhiteSpace(settings))
            {
                return settings;
            }
            settings = Regex.Replace(settings, @"\s*--(?:fp16|bf16|int8|best|buildOnly|stronglyTyped)\b", "");
            settings = Regex.Replace(settings, @"\s*--(?:inputIOFormats|outputIOFormats|tacticSources)=\S+", "");
            return Regex.Replace(settings, @"\s+", " ").Trim();
        }
    }

    public static class Platform
    {
        // DirectML and TensorRT-RTX ship for Windows only; elsewhere both run on TensorRT.
        public static bool DirectMlAvailable => OperatingSystem.IsWindows();
        public static bool TensorRtRtxAvailable => OperatingSystem.IsWindows();

        // "aji_encode" -> "aji_encode.exe" on Windows; Linux binaries carry no suffix.
        public static string Exe(string stem) => OperatingSystem.IsWindows() ? stem + ".exe" : stem;
    }
}
