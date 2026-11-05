using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VideoJaNai.Core
{
    // The GUI's saved state (appstate2.json) and its exported workflows (.awf), read as plain JSON.
    // They are Newtonsoft TypeNameHandling.All files ("$type" names of GUI classes, lists as
    // {"$type", "$values"}); the type names are ignored. Read-only: the command line never writes
    // the GUI's state. Missing or null fields keep the GUI's defaults, as its deserializer does.
    public sealed class AppState
    {
        public List<WorkflowSettings> Workflows { get; } = [];
        public int SelectedWorkflowIndex { get; set; }

        // App Settings
        public bool? UseDirectMl { get; set; }
        public bool? UseTensorRtRtx { get; set; }
        public string? SelectedGpuId { get; set; }
        public string? SelectedGpuName { get; set; }
        public int SelectedGpuPosition { get; set; } = -1;
        public string? EncoderProbeOutput { get; set; }

        public WorkflowSettings? SelectedWorkflow =>
            SelectedWorkflowIndex >= 0 && SelectedWorkflowIndex < Workflows.Count ? Workflows[SelectedWorkflowIndex] : null;

        public static AppState Load(string path) => Parse(File.ReadAllText(path));

        public static AppState Parse(string json)
        {
            using var doc = JsonDocument.Parse(json, Options);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Workflows", out _))
            {
                throw new FormatException("not a VideoJaNai settings file (no Workflows)");
            }
            var state = new AppState
            {
                SelectedWorkflowIndex = Int(root, "SelectedWorkflowIndex") ?? 0,
                UseDirectMl = Bool(root, "UseDirectMl"),
                UseTensorRtRtx = Bool(root, "UseTensorRtRtx"),
                SelectedGpuId = Str(root, "SelectedGpuId"),
                SelectedGpuName = Str(root, "SelectedGpuName"),
                SelectedGpuPosition = Int(root, "SelectedGpuPosition") ?? -1,
                EncoderProbeOutput = Str(root, "EncoderProbeOutput"),
            };
            bool? legacyDirectMl = null;
            foreach (var w in Values(root, "Workflows"))
            {
                if (state.Workflows.Count == state.SelectedWorkflowIndex)
                {
                    legacyDirectMl = Bool(w, "DirectMlSelected");
                }
                state.Workflows.Add(ReadWorkflow(w));
            }
            // Settings saved before the backend became app-wide keep what the selected workflow
            // used (MainWindowViewModel.MigrateBackend).
            state.UseDirectMl ??= legacyDirectMl ?? false;
            return state;
        }

        // An exported workflow (.awf): one workflow object.
        public static WorkflowSettings LoadWorkflow(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("UpscaleSettings", out _))
            {
                throw new FormatException("not a VideoJaNai workflow (no UpscaleSettings)");
            }
            return ReadWorkflow(doc.RootElement);
        }

        private static readonly JsonDocumentOptions Options = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        private static WorkflowSettings ReadWorkflow(JsonElement w)
        {
            var d = new WorkflowSettings();
            var wf = new WorkflowSettings
            {
                WorkflowName = Str(w, "WorkflowName") ?? d.WorkflowName,
                SelectedTabIndex = Int(w, "SelectedTabIndex") ?? d.SelectedTabIndex,
                InputFilePath = Str(w, "InputFilePath") ?? d.InputFilePath,
                InputFolderPath = Str(w, "InputFolderPath") ?? d.InputFolderPath,
                OutputFilename = Str(w, "OutputFilename") ?? d.OutputFilename,
                OutputFolderPath = Str(w, "OutputFolderPath") ?? d.OutputFolderPath,
                OverwriteExistingVideos = Bool(w, "OverwriteExistingVideos") ?? d.OverwriteExistingVideos,
                FfmpegVideoSettings = Str(w, "FfmpegVideoSettings") ?? d.FfmpegVideoSettings,
                Deinterlace = Str(w, "Deinterlace") ?? d.Deinterlace,
                SquarePixels = Str(w, "SquarePixels") ?? d.SquarePixels,
                OutputPixFmt = Str(w, "OutputPixFmt") ?? d.OutputPixFmt,
                // the GUI normalizes saved engine settings on load (App)
                TrtEngineSettings = TrtEngine.Normalize(Str(w, "TrtEngineSettings") ?? d.TrtEngineSettings),
                FinalResizeHeight = Int(w, "FinalResizeHeight") ?? d.FinalResizeHeight,
                FinalResizeFactor = Int(w, "FinalResizeFactor") ?? d.FinalResizeFactor,
                EnableUpscale = Bool(w, "EnableUpscale") ?? d.EnableUpscale,
                EnableTemporalFix = Bool(w, "EnableTemporalFix") ?? d.EnableTemporalFix,
                TemporalFixStrength = Int(w, "TemporalFixStrength") ?? d.TemporalFixStrength,
                TemporalFixSceneThreshold = Dec(w, "TemporalFixSceneThreshold") ?? d.TemporalFixSceneThreshold,
                TemporalFixBeforeUpscale = Bool(w, "TemporalFixBeforeUpscale") ?? d.TemporalFixBeforeUpscale,
                EnableRife = Bool(w, "EnableRife") ?? d.EnableRife,
                RifeModel = Str(w, "RifeModel") ?? d.RifeModel,
                RifeEnsemble = Bool(w, "RifeEnsemble") ?? d.RifeEnsemble,
                RifeFactorNumerator = Int(w, "RifeFactorNumerator") ?? d.RifeFactorNumerator,
                RifeFactorDenominator = Int(w, "RifeFactorDenominator") ?? d.RifeFactorDenominator,
                RifeSceneDetectThreshold = Dec(w, "RifeSceneDetectThreshold") ?? d.RifeSceneDetectThreshold,
                RifeBeforeUpscale = Bool(w, "RifeBeforeUpscale") ?? d.RifeBeforeUpscale,
            };
            foreach (var m in Values(w, "UpscaleSettings"))
            {
                var dm = new ModelSettings();
                wf.UpscaleSettings.Add(new ModelSettings
                {
                    OnnxModelPath = Str(m, "OnnxModelPath") ?? dm.OnnxModelPath,
                    ResizeHeightBeforeUpscale = Int(m, "ResizeHeightBeforeUpscale") ?? dm.ResizeHeightBeforeUpscale,
                    ResizeFactorBeforeUpscale = Dec(m, "ResizeFactorBeforeUpscale") ?? dm.ResizeFactorBeforeUpscale,
                    TemporalSceneThreshold = Dec(m, "TemporalSceneThreshold") ?? dm.TemporalSceneThreshold,
                });
            }
            return wf;
        }

        // A list as Newtonsoft writes it with type names ({"$type", "$values": [...]}) or plain.
        private static IEnumerable<JsonElement> Values(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v))
            {
                yield break;
            }
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("$values", out var values))
            {
                v = values;
            }
            if (v.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item;
                }
            }
        }

        private static JsonElement? Get(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

        private static string? Str(JsonElement e, string name) =>
            Get(e, name) is JsonElement v && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool? Bool(JsonElement e, string name) =>
            Get(e, name) is JsonElement v && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

        private static int? Int(JsonElement e, string name) =>
            Get(e, name) is JsonElement v && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? (int)d : null;

        private static decimal? Dec(JsonElement e, string name) =>
            Get(e, name) is JsonElement v && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
    }
}
