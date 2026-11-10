using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoJaNai.Core
{
    // Model presets: which bundled model runs for which source size, after libaji's built-in
    // profiles (mpv-AnimeJaNai's defaults, aji_conf.cpp add_builtin_slots): the HD model for
    // 720p-1080p sources, the SD model below 720p. Without their frame-rate limit, which is for
    // real-time playback; a source above 1080p matches no chain and keeps its size.
    public static class Presets
    {
        public const string HdBalanced = "2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16";
        public const string HdPerformance = "2x_AnimeJaNai_HD_V3.1_Performance_SPANF3_b5f48_unshuffle_fp16";
        public const string HdSharpBalanced = "2x_AnimeJaNai_HD_V3.1Sharp1_Balanced_SPANF3_b8f64_unshuffle_fp16";
        public const string HdSharpPerformance = "2x_AnimeJaNai_HD_V3.1Sharp1_Performance_SPANF3_b5f48_unshuffle_fp16";
        public const string Sd = "2x_AnimeJaNai_SD_V1beta34_Compact_1x3xHxW_dyn-HW_strong_fp16_op21_dynamo";

        public const string Default = "balanced";

        public sealed record Rule(string MinResolution, string MaxResolution, string Model);

        public static readonly IReadOnlyDictionary<string, IReadOnlyList<Rule>> All =
            new Dictionary<string, IReadOnlyList<Rule>>(StringComparer.OrdinalIgnoreCase)
            {
                ["balanced"] = [new("1280x720", "1920x1080", HdBalanced), new("0x0", "1280x720", Sd)],
                ["performance"] = [new("1280x720", "1920x1080", HdPerformance), new("0x0", "1280x720", Sd)],
                ["balanced-sharp"] = [new("1280x720", "1920x1080", HdSharpBalanced), new("0x0", "1280x720", Sd)],
                ["performance-sharp"] = [new("1280x720", "1920x1080", HdSharpPerformance), new("0x0", "1280x720", Sd)],
            };

        // The models a preset needs that aren't in the models folder.
        public static IEnumerable<string> MissingModels(string preset, string modelsDirectory) =>
            All[preset].Select(r => r.Model).Distinct()
                .Where(m => !File.Exists(Path.Join(modelsDirectory, m + ".onnx")))
                .Select(m => m + ".onnx");

        // A workflow's job with the preset's chains in place of its models: one chain per rule, each
        // with the workflow's other steps (temporal fix) around the rule's model.
        public static EncodeJob Job(string preset, WorkflowSettings wf, Backend backend, string? device,
                                    EncoderPreset defaultEncoder, string modelsDirectory)
        {
            var rules = All[preset];
            EncodeJob? first = null;
            var chains = new List<ChainSpec>();
            foreach (var rule in rules)
            {
                var copy = Copy(wf);
                copy.EnableUpscale = true;
                copy.UpscaleSettings = [new ModelSettings { OnnxModelPath = Path.Join(modelsDirectory, rule.Model + ".onnx") }];
                var job = EncodeJob.FromWorkflow(copy, backend, device, defaultEncoder, modelsDirectory);
                first ??= job;
                chains.Add(job.Chains[0] with { MinResolution = rule.MinResolution, MaxResolution = rule.MaxResolution });
            }
            return first! with { Chains = chains };
        }

        private static WorkflowSettings Copy(WorkflowSettings wf) => new()
        {
            WorkflowName = wf.WorkflowName,
            SelectedTabIndex = wf.SelectedTabIndex,
            InputFilePath = wf.InputFilePath,
            InputFolderPath = wf.InputFolderPath,
            OutputFilename = wf.OutputFilename,
            OutputFolderPath = wf.OutputFolderPath,
            OverwriteExistingVideos = wf.OverwriteExistingVideos,
            FinishDamagedVideos = wf.FinishDamagedVideos,
            FfmpegVideoSettings = wf.FfmpegVideoSettings,
            Deinterlace = wf.Deinterlace,
            SquarePixels = wf.SquarePixels,
            OutputPixFmt = wf.OutputPixFmt,
            TrtEngineSettings = wf.TrtEngineSettings,
            FinalResizeHeight = wf.FinalResizeHeight,
            FinalResizeFactor = wf.FinalResizeFactor,
            UpscaleSettings = [.. wf.UpscaleSettings],
            EnableUpscale = wf.EnableUpscale,
            EnableTemporalFix = wf.EnableTemporalFix,
            TemporalFixStrength = wf.TemporalFixStrength,
            TemporalFixSceneThreshold = wf.TemporalFixSceneThreshold,
            TemporalFixBeforeUpscale = wf.TemporalFixBeforeUpscale,
            EnableRife = wf.EnableRife,
            RifeModel = wf.RifeModel,
            RifeEnsemble = wf.RifeEnsemble,
            RifeFactorNumerator = wf.RifeFactorNumerator,
            RifeFactorDenominator = wf.RifeFactorDenominator,
            RifeSceneDetectThreshold = wf.RifeSceneDetectThreshold,
            RifeBeforeUpscale = wf.RifeBeforeUpscale,
        };
    }
}
