using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VideoJaNai.Core;

namespace VideoJaNai.Cli
{
    // A run that can't start: the message says why (exit code 2).
    public sealed class RunException(string message) : Exception(message);

    public sealed class RunPlan
    {
        public required Install Install { get; init; }
        public required EncodeJob Job { get; init; }
        public required List<PlannedVideo> Videos { get; init; }
        public required string Heading { get; init; }
        public List<string> Notes { get; } = [];
    }

    // Settings layered as mangajanai does: built-in defaults < an imported workflow (--workflow, or
    // the GUI's selected one with --settings) < videojanai.ini < flags. Then this PC: the backend,
    // the GPU, the default encoder, and the videos to run.
    public static class Resolve
    {
        public static string? FindConfig(Flags f)
        {
            if (f.NoConfig || f.Settings != null)
            {
                return null;
            }
            if (f.Config != null)
            {
                return f.Config;
            }
            return new[] { Path.Join(Environment.CurrentDirectory, Config.FileName), Path.Join(Install.ExeDir, Config.FileName) }
                .FirstOrDefault(File.Exists);
        }

        public static async Task<RunPlan> PlanAsync(Flags f)
        {
            var file = FindConfig(f) is string path ? Config.Load(path) : new Options();
            var o = f.Options;
            string? Pick(Func<Options, string?> get) => get(o) ?? get(file);

            // the workflow layer
            AppState? gui = null;
            WorkflowSettings wf;
            string? workflowLabel = null;
            if (f.Settings != null)
            {
                gui = Load(f.Settings, AppState.Load);
                wf = gui.SelectedWorkflow ?? throw new RunException($"{f.Settings}: no workflow is selected");
                workflowLabel = $"the GUI's workflow \"{wf.WorkflowName}\"";
            }
            else if ((o.Workflow ?? (file.Workflow is string w ? PathText.Relative(file.BaseDir, w) : null)) is string awf)
            {
                wf = Load(awf, AppState.LoadWorkflow);
                workflowLabel = $"workflow {Path.GetFileName(awf)}";
            }
            else
            {
                wf = new WorkflowSettings();
            }
            var fromWorkflow = workflowLabel != null;

            // settings file, then flags
            foreach (var layer in new[] { file, o })
            {
                Apply(layer, wf);
            }

            // the models: flags, else the file's, else the workflow's, else the default preset
            string? preset = null;
            var models = o.Models.Count > 0 ? o.Models.Select(m => (m, "")).ToList()
                       : file.Models.Count > 0 ? file.Models.Select(m => (m, file.BaseDir)).ToList()
                       : null;
            if (o.Preset != null || (models == null && o.Workflow == null && file.Preset != null))
            {
                preset = o.Preset ?? file.Preset;
            }
            else if (models == null && !fromWorkflow)
            {
                preset = Presets.Default;
            }

            var install = Install.Find(Pick(x => x.AjiDir) is string aji ? PathText.Relative(o.AjiDir != null ? "" : file.BaseDir, aji) : null);
            if (!install.AjiEncodeExists)
            {
                throw new RunException($"aji_encode not found in {install.Paths.InferenceDirectory}. Run `videojanai setup`, or point --aji-dir at a VideoJaNai animejanai folder.");
            }
            var modelsDir = install.Paths.ModelsDirectory;
            if (models != null)
            {
                wf.UpscaleSettings = [.. models.Select(m => new ModelSettings { OnnxModelPath = ModelPath(m.Item1, m.Item2, modelsDir) })];
            }
            if (!wf.EnableUpscale)
            {
                preset = null;
            }

            // this PC
            var probeText = await install.ProbeAsync();
            var nvidia = Nvml.Gpus;
            var probe = EncoderProbe.Parse(probeText)?.WithNvidia(nvidia);
            var storeMissing = await install.MissingStoreFolderAsync();
            var backend = PickBackend(Pick(x => x.Backend), gui, install, probe, nvidia);
            var directMl = backend == Backend.DirectMl;
            // DirectML needs nothing from the store; without that choice, auto would pick it only
            // because the store's TensorRT can't be found
            if (storeMissing != null && !(directMl && (Pick(x => x.Backend) == "directml" || gui?.UseDirectMl == true)))
            {
                throw new RunException(Install.StoreMissingText(storeMissing));
            }
            var gpu = PickGpu(Pick(x => x.Device), gui, probe, backend);
            var vendor = GpuChoice.InferenceVendor(probe, directMl, gpu);
            var defaultEncoder = GpuChoice.DefaultEncoder(probe, vendor);

            var job = preset != null
                ? Presets.Job(preset, wf, backend, GpuChoice.Device(probe, directMl, gpu), defaultEncoder, modelsDir)
                : EncodeJob.FromWorkflow(wf, backend, GpuChoice.Device(probe, directMl, gpu), defaultEncoder, modelsDir);

            Check(job, wf, preset, install, probe);

            // the videos
            var inputs = o.Inputs.Concat(f.Positional).ToList();
            var inputBase = "";
            if (inputs.Count == 0 && file.Inputs.Count > 0)
            {
                inputs = file.Inputs;
                inputBase = file.BaseDir;
            }
            if (inputs.Count == 0 && fromWorkflow)
            {
                var p = wf.SelectedTabIndex == 0 ? wf.InputFilePath : wf.InputFolderPath;
                if (!string.IsNullOrWhiteSpace(p))
                {
                    inputs = [p];
                }
            }
            if (inputs.Count == 0)
            {
                throw new RunException("nothing to upscale: give a video or a folder (or [input] path in videojanai.ini)");
            }
            var outputFolder = o.OutputFolder ?? (file.OutputFolder is string fo ? PathText.Relative(file.BaseDir, fo) : null)
                               ?? (fromWorkflow && !string.IsNullOrWhiteSpace(wf.OutputFolderPath) ? wf.OutputFolderPath : null);
            var filename = Pick(x => x.Filename)
                           ?? (Pick(x => x.Suffix) is string suffix ? $"{BatchPlan.FilenameToken}{suffix}.mkv" : null)
                           ?? (string.IsNullOrWhiteSpace(wf.OutputFilename) ? BatchPlan.DefaultFilename : wf.OutputFilename);
            if (!filename.Contains(BatchPlan.FilenameToken) && inputs.Count > 1)
            {
                throw new RunException($"the output file name '{filename}' has no {BatchPlan.FilenameToken}, so every video would write the same file");
            }
            var skipExisting = o.SkipExisting ?? file.SkipExisting ?? !wf.OverwriteExistingVideos;
            var keepDamaged = KeepDamaged(o, file, wf);
            var videos = PlanVideos(inputs.Select(i => PathText.Relative(inputBase, i)).ToList(), outputFolder, filename, skipExisting);

            var plan = new RunPlan
            {
                Install = install,
                // each video is written to a temporary file first
                Job = job with { Overwrite = true, ContinueOnDamage = keepDamaged },
                Videos = videos,
                Heading = Heading(job, preset, wf, workflowLabel, gpu, probe, install),
            };
            if (install.IsGuiInstall)
            {
                plan.Notes.Add($"using VideoJaNai's runtime and models in {Path.GetDirectoryName(install.AnimeJaNaiDirectory)}");
            }
            return plan;
        }

        // --keep-damaged / --stop-on-damage, else videojanai.ini's keep_damaged, else the workflow's
        // "Finish videos with damaged frames" (off by default)
        public static bool KeepDamaged(Options flags, Options file, WorkflowSettings wf) =>
            flags.KeepDamaged ?? file.KeepDamaged ?? wf.FinishDamagedVideos;

        static T Load<T>(string path, Func<string, T> load)
        {
            try
            {
                return load(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            {
                throw new RunException($"{path}: {e.Message}");
            }
        }

        // A settings layer over the workflow.
        static void Apply(Options x, WorkflowSettings wf)
        {
            if (x.Upscale is bool up) wf.EnableUpscale = up;
            if (x.ReuseHeldFrames is bool reuse) wf.ReuseHeldFrames = reuse;
            if (x.Deinterlace != null) wf.Deinterlace = x.Deinterlace;
            if (x.SquarePixels != null) wf.SquarePixels = x.SquarePixels;
            if (x.TemporalFix is int tf)
            {
                wf.EnableTemporalFix = tf > 0;
                if (tf > 0) wf.TemporalFixStrength = tf;
            }
            if (x.TemporalFixBeforeUpscale is bool tfb) wf.TemporalFixBeforeUpscale = tfb;
            if (x.TemporalFixSceneThreshold is decimal tft) wf.TemporalFixSceneThreshold = tft;
            if (x.Rife is bool r) wf.EnableRife = r;
            if (x.RifeModel != null) wf.RifeModel = x.RifeModel;
            if (x.RifeFactor is var (num, den)) { wf.RifeFactorNumerator = num; wf.RifeFactorDenominator = den; }
            if (x.RifeEnsemble is bool re) wf.RifeEnsemble = re;
            if (x.RifeBeforeUpscale is bool rb) wf.RifeBeforeUpscale = rb;
            if (x.RifeSceneThreshold is decimal rt) wf.RifeSceneDetectThreshold = rt;
            if (x.Ffmpeg != null) wf.FfmpegVideoSettings = x.Ffmpeg;
            else if (x.Encoder != null) wf.FfmpegVideoSettings = x.Encoder == "auto" ? "" : EncoderNames.Find(x.Encoder)!.Settings;
            if (x.PixFmt != null) wf.OutputPixFmt = x.PixFmt;
            if (x.Height is int h) { wf.FinalResizeHeight = h; wf.FinalResizeFactor = 100; }
            if (x.ResizePercent is int pct) { wf.FinalResizeHeight = 0; wf.FinalResizeFactor = pct; }
            if (x.EngineSettings != null) wf.TrtEngineSettings = TrtEngine.Normalize(x.EngineSettings);
        }

        // A model given by path (relative to `baseDir`), else by name in the models folder.
        static string ModelPath(string m, string baseDir, string modelsDir)
        {
            var p = PathText.Relative(baseDir, m);
            if (File.Exists(p))
            {
                return Path.GetFullPath(p);
            }
            var name = Path.GetFileName(m);
            foreach (var candidate in new[] { name, name + ".onnx" })
            {
                var inModels = Path.Join(modelsDir, candidate);
                if (File.Exists(inModels))
                {
                    return inModels;
                }
            }
            throw new RunException($"model not found: {m} (not a file, and not in {modelsDir})");
        }

        // Auto: the GUI's choice with --settings; otherwise TensorRT-RTX where it's installed, then
        // TensorRT on an NVIDIA GPU it runs on (`nvidia`: NVML's GPUs), then DirectML (Windows). An
        // NVIDIA GPU too old for TensorRT counts as none; TensorRT on a PC with only such GPUs stops
        // here with why, rather than in a failed engine build.
        internal static Backend PickBackend(string? choice, AppState? gui, Install install, EncoderProbe? probe,
                                            IReadOnlyList<NvidiaGpu> nvidia)
        {
            var backend = PickBackendUnchecked(choice, gui, install, probe, nvidia);
            if (backend != Backend.DirectMl && TensorRtGpus.TooOldText(nvidia, BackendNames.Label(backend)) is string tooOld)
            {
                throw new RunException(tooOld + (Platform.DirectMlAvailable
                    ? " Use --backend directml."
                    : " On Linux, VideoJaNai upscales on TensorRT only."));
            }
            return backend;
        }

        static Backend PickBackendUnchecked(string? choice, AppState? gui, Install install, EncoderProbe? probe,
                                            IReadOnlyList<NvidiaGpu> nvidia)
        {
            switch (choice)
            {
                case "tensorrt": return Backend.TensorRt;
                case "tensorrt-rtx":
                    if (!Platform.TensorRtRtxAvailable)
                    {
                        throw new RunException("TensorRT-RTX is Windows only; use --backend tensorrt");
                    }
                    return Backend.TensorRtRtx;
                case "directml":
                    if (!Platform.DirectMlAvailable)
                    {
                        throw new RunException("DirectML is Windows only; use --backend tensorrt");
                    }
                    return Backend.DirectMl;
            }
            if (gui != null)
            {
                return BackendNames.Resolve(gui.UseDirectMl, gui.UseTensorRtRtx, install.TensorRtRtxInstalled);
            }
            var tensorRtGpu = probe?.Gpus.Any(g => g.TensorRtCapable)
                ?? (nvidia.Count > 0 ? nvidia.Any(g => g.TensorRtCapable) : install.TensorRtInstalled);
            if (tensorRtGpu && install.TensorRtInstalled)
            {
                return Platform.TensorRtRtxAvailable && install.TensorRtRtxInstalled ? Backend.TensorRtRtx : Backend.TensorRt;
            }
            return Platform.DirectMlAvailable ? Backend.DirectMl : Backend.TensorRt;
        }

        // A GPU number from `videojanai devices` (the probe's order), its id, or its name (or part
        // of it, when that names one GPU). Auto: the GUI's choice with --settings, else none.
        internal static EncoderProbe.Gpu? PickGpu(string? choice, AppState? gui, EncoderProbe? probe, Backend backend)
        {
            var directMl = backend == Backend.DirectMl;
            if (string.IsNullOrWhiteSpace(choice) || choice.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                choice.Equals("first", StringComparison.OrdinalIgnoreCase))
            {
                return gui != null ? GpuChoice.Chosen(probe, directMl, gui.SelectedGpuId, gui.SelectedGpuName, gui.SelectedGpuPosition) : null;
            }
            if (probe is null)
            {
                throw new RunException("can't pick a GPU: this aji_encode can't list them (--probe-encoders)");
            }
            var gpus = probe.Gpus;
            var t = choice.Trim();
            var gpu = int.TryParse(t, out var n) && n >= 0 && n < gpus.Count && !gpus.Any(g => g.Id == t) ? gpus[n]
                : gpus.FirstOrDefault(g => g.Id == t)
                ?? gpus.FirstOrDefault(g => g.Name.Equals(t, StringComparison.OrdinalIgnoreCase));
            if (gpu is null)
            {
                var partial = gpus.Where(g => g.Name.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
                gpu = partial.Count == 1 ? partial[0]
                    : throw new RunException(partial.Count > 1
                        ? $"--device '{t}' matches {partial.Count} GPUs; use its number from `videojanai devices`"
                        : $"no GPU '{t}' (see `videojanai devices`)");
            }
            if (gpu.Id is null)
            {
                throw new RunException("this aji_encode can't pick a GPU (no GPU ids); update VideoJaNai");
            }
            if (!GpuChoice.BackendGpus(probe, directMl).Contains(gpu))
            {
                throw new RunException(gpu.Vendor == EncoderGroup.Nvidia && gpu.Nvidia is NvidiaGpu { TensorRtCapable: false } old
                    ? $"{TensorRtGpus.TooOldText(old, BackendNames.Label(backend))} " +
                      (Platform.DirectMlAvailable ? "Use --backend directml for it, or another GPU." : "Choose another GPU.")
                    : $"{gpu.Name} can't run {BackendNames.Label(backend)}: TensorRT needs an NVIDIA GPU (use --backend directml for others)");
            }
            return gpu;
        }

        // What stops the run before it starts: missing models or runtime, an encoder or format this
        // PC can't encode.
        static void Check(EncodeJob job, WorkflowSettings wf, string? preset, Install install, EncoderProbe? probe)
        {
            var problems = new List<string>();
            if (preset != null)
            {
                foreach (var m in Presets.MissingModels(preset, install.Paths.ModelsDirectory))
                {
                    problems.Add($"the {preset} preset's model is missing: {m} (in {install.Paths.ModelsDirectory})");
                }
            }
            else
            {
                if (wf.EnableUpscale && wf.UpscaleSettings.Count == 0)
                {
                    problems.Add("no model to upscale with: give --model or --preset (or turn upscaling off with --no-upscale)");
                }
                foreach (var m in wf.EnableUpscale ? wf.UpscaleSettings : [])
                {
                    if (string.IsNullOrWhiteSpace(m.OnnxModelPath))
                    {
                        problems.Add("the workflow has a model step with no model file (pick one in VideoJaNai, or give --model or --preset)");
                    }
                    else if (!File.Exists(m.OnnxModelPath))
                    {
                        problems.Add($"model not found: {m.OnnxModelPath}");
                    }
                }
            }
            if (wf.EnableTemporalFix && TemporalFix.ModelPath(install.Paths.ModelsDirectory, wf.TemporalFixStrength ?? 2) == null)
            {
                problems.Add($"the temporal fix model for strength {wf.TemporalFixStrength ?? 2} is missing (temporalfix_s{wf.TemporalFixStrength ?? 2}_v*.onnx in {install.Paths.ModelsDirectory})");
            }
            if (wf.EnableRife && !Rife.Models(install.Paths.RifeModelsDirectory).Contains(wf.RifeModel))
            {
                problems.Add(Directory.Exists(install.Paths.RifeModelsDirectory)
                    ? $"{wf.RifeModel} isn't installed (have: {string.Join(", ", Rife.Models(install.Paths.RifeModelsDirectory))})"
                    : "the RIFE models aren't installed: run `videojanai setup` (it installs them unless --no-rife)");
            }
            switch (job.Backend)
            {
                case Backend.TensorRt when !install.TensorRtInstalled:
                    problems.Add("the TensorRT runtime isn't installed: run `videojanai setup`" + (Platform.DirectMlAvailable ? " (or use --backend directml)" : ""));
                    break;
                case Backend.TensorRtRtx when !install.TensorRtRtxInstalled || !install.TensorRtInstalled:
                    problems.Add("TensorRT-RTX needs its runtime and TensorRT's: run `videojanai setup`");
                    break;
                case Backend.DirectMl when !install.DirectMlInstalled:
                    problems.Add($"DirectML isn't in {install.Paths.InferenceDirectory}");
                    break;
            }
            problems.AddRange(EncodeChecks.EncoderProblems(probe, job.FfmpegVideoSettings, job.PixFmt));
            if (EncodeChecks.DirectMlFormatProblem(job.Backend, job.PixFmt) is string dml)
            {
                problems.Add(dml);
            }
            if (problems.Count > 0)
            {
                throw new RunException(string.Join("\n", problems.Distinct()));
            }
        }

        // Inputs -> videos (Core's planner, the GUI's folder mode's too); -o may name the file.
        public static List<PlannedVideo> PlanVideos(IReadOnlyList<string> inputs, string? outputFolder, string filename, bool skipExisting)
        {
            var plan = BatchPlanning.Plan(inputs, outputFolder, filename, skipExisting, outputMayBeFile: true);
            return plan.Problem is string problem ? throw new RunException(problem) : plan.Videos;
        }

        static string Heading(EncodeJob job, string? preset, WorkflowSettings wf, string? workflowLabel,
                              EncoderProbe.Gpu? gpu, EncoderProbe? probe, Install install)
        {
            var directMl = job.Backend == Backend.DirectMl;
            var gpuName = gpu?.Name ?? GpuChoice.Auto(probe, directMl)?.Name;
            var parts = new List<string> { BackendNames.Label(job.Backend) + (gpuName != null ? $" on {gpuName}" : "") };
            if (workflowLabel != null)
            {
                parts.Add(workflowLabel);
            }
            if (preset != null)
            {
                parts.Add($"preset {preset}");
            }
            else if (wf.EnableUpscale)
            {
                parts.Add(string.Join(" -> ", wf.UpscaleSettings.Select(m => Path.GetFileNameWithoutExtension(m.OnnxModelPath))));
            }
            else
            {
                parts.Add("no upscaling");
            }
            if (wf.EnableTemporalFix) parts.Add($"temporal fix {wf.TemporalFixStrength}");
            if (wf.EnableRife) parts.Add($"{wf.RifeModel} x{wf.RifeFactorNumerator}" + (wf.RifeFactorDenominator != 1 ? $"/{wf.RifeFactorDenominator}" : ""));
            if (job.Deinterlace != "off") parts.Add(job.Deinterlace == "ivtc" ? "IVTC" : "bwdif");
            if (!job.ReuseHeldFrames) parts.Add("upscaling every frame");
            var preset_ = EncoderPresets.Find(job.FfmpegVideoSettings);
            parts.Add((preset_?.SummaryLabel ?? EncoderPresets.CodecOf(job.FfmpegVideoSettings)) + " " + job.PixFmt switch
            {
                "yuv420p" => "8-bit 4:2:0",
                "yuv444p" => "8-bit 4:4:4",
                "yuv444p10" => "10-bit 4:4:4",
                _ => "10-bit 4:2:0",
            });
            if (job.FinalResizeHeight is int h && h > 0) parts.Add($"{h}p");
            else if (job.FinalResizeFactor is int f && f > 0 && f != 100) parts.Add($"{f}%");
            return string.Join(", ", parts);
        }
    }
}
