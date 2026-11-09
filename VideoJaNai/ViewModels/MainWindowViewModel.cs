using AnimeJaNaiConverterGui.Services;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Newtonsoft.Json;
using ReactiveUI;
using ReactiveUI.Primitives;
using Splat;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoJaNai.Lang;

namespace VideoJaNai.ViewModels
{
    [DataContract]
    public class MainWindowViewModel : ViewModelBase
    {
        // what batch mode picks up from the input folder (anything libav reads works as a single video)
        public static readonly List<string> VIDEO_EXTENSIONS = [.. BatchPlan.VideoExtensions];

        private string? _availableUpdateVersion;
        // Auto update: the version unpacked in update-staging/ (installs when the app closes), the
        // background download in progress, and the last error to show.
        private string? _readyUpdateVersion;
        private CancellationTokenSource? _updateDownloadCts;
        private string? _updateDownloadProgress;
        private string? _installError;
        private string? _downloadError;
        private bool _updateChecksStarted;
        private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

        private readonly IPythonService _pythonService;
        private readonly IUpdateManagerService _updateManagerService;

        public MainWindowViewModel(IPythonService? pythonService = null, IUpdateManagerService? updateManagerService = null)
        {
            _pythonService = pythonService ?? Locator.Current.GetService<IPythonService>()!;
            _updateManagerService = updateManagerService ?? Locator.Current.GetService<IUpdateManagerService>()!;

            var g1 = this.WhenAnyValue
            (
                x => x.SelectedWorkflowIndex
            ).Subscribe(x =>
            {
                CurrentWorkflow?.Validate();
            });



            this.WhenAnyValue(x => x.ShowAppSettings).Subscribe(async x =>
            {
                if (x)
                {
                    await RefreshStorage();
                    await RefreshComponents();
                }
            });
        }

        private CancellationTokenSource? _cancellationTokenSource;
        private Process? _runningProcess = null;
        // aji_encode's PROGRESS lines -> the progress bar (fps measured from the frame counter, ETA)
        private readonly AjiProgress _progress = new();

        private double _progressValue;
        [IgnoreDataMember]
        public double ProgressValue
        {
            get => _progressValue;
            set => this.RaiseAndSetIfChanged(ref _progressValue, value);
        }

        private string _progressText = string.Empty;
        [IgnoreDataMember]
        public string ProgressText
        {
            get => _progressText;
            set => this.RaiseAndSetIfChanged(ref _progressText, value);
        }

        private string _progressPhase = string.Empty;
        [IgnoreDataMember]
        public string ProgressPhase
        {
            get => _progressPhase;
            set => this.RaiseAndSetIfChanged(ref _progressPhase, value);
        }

        // Editable-combo suggestions for the TensorRT engine-settings UI (mirrors AnimeJaNaiConfEditor).
        public string[] CommonResolutions { get; } =
            ["0x0", "640x360", "640x480", "720x480", "768x576", "960x540", "1024x576", "1280x720", "1440x1080", "1920x1080"];
        public string[] BuilderOptimizationLevels { get; } = ["0", "1", "2", "3", "4", "5"];

        public bool IsInstalled => _updateManagerService.IsInstalled;

        private bool _showCheckUpdateButton = true;
        public bool ShowCheckUpdateButton
        {
            get => _showCheckUpdateButton;
            set => this.RaiseAndSetIfChanged(ref _showCheckUpdateButton, value);
        }

        private bool _showApplyButton = false;
        public bool ShowApplyButton
        {
            get => _showApplyButton;
            set
            {
                this.RaiseAndSetIfChanged(ref _showApplyButton, value);
                this.RaisePropertyChanged(nameof(ShowCheckUpdateButton));
            }
        }

        public string AppVersion => _updateManagerService.AppVersion;

        private string _updateStatusText = string.Empty;
        public string UpdateStatusText
        {
            get => _updateStatusText;
            set => this.RaiseAndSetIfChanged(ref _updateStatusText, value);
        }

        // the workflow sidebar's width, dragged with its splitter
        private double _sidebarWidth = 220;
        [DataMember]
        public double SidebarWidth
        {
            get => _sidebarWidth;
            set => this.RaiseAndSetIfChanged(ref _sidebarWidth, value);
        }

        private bool _autoUpdate = true;
        [DataMember]
        public bool AutoUpdateEnabled
        {
            get => _autoUpdate;
            set => this.RaiseAndSetIfChanged(ref _autoUpdate, value);
        }

        // The inference backend, app-wide: chosen once per PC, so it's an App Setting (earlier
        // versions kept it per workflow; null until taken over from those, see MigrateBackend).
        private bool? _useDirectMl;
        [DataMember]
        public bool? UseDirectMl
        {
            get => _useDirectMl;
            set
            {
                this.RaiseAndSetIfChanged(ref _useDirectMl, value);
                BackendChanged();
            }
        }

        // The NVIDIA backend when DirectML isn't selected (TensorRT-RTX is Windows only): true for
        // TensorRT-RTX, false for TensorRT, null while the user hasn't picked one, which means
        // TensorRT-RTX whenever its component is installed (the updater installs it on RTX 20 /
        // GTX 16-series and newer, where it's the default) and TensorRT otherwise. States saved by
        // versions without the field load as null, so they get the same default. A flag next to
        // UseDirectMl rather than a backend enum: an older VideoJaNai reading a newer state sees
        // UseDirectMl=false and runs TensorRT, which is what TensorRT-RTX falls back to anyway. Null
        // is never written, so a version that has the field as a plain bool still loads a state
        // saved here.
        private bool? _useTensorRtRtx;
        [DataMember]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public bool? UseTensorRtRtx
        {
            get => _useTensorRtRtx;
            set
            {
                this.RaiseAndSetIfChanged(ref _useTensorRtRtx, value);
                BackendChanged();
            }
        }

        private void BackendChanged()
        {
            this.RaisePropertyChanged(nameof(TensorRtBackendSelected));
            this.RaisePropertyChanged(nameof(TensorRtRtxBackendSelected));
            this.RaisePropertyChanged(nameof(DirectMlBackendSelected));
            foreach (var wf in Workflows ?? [])
            {
                wf.BackendChanged();
            }
            EncodersChanged();     // the upscaling GPU, so the listed encoders, follow the backend
            _ = RefreshComponentsIfChoiceChanged();     // and what the component packs are for
        }

        public bool DirectMlBackendSelected => UseDirectMl == true && UpscaleWorkflow.DirectMlAvailable;
        public bool TensorRtRtxBackendSelected => !DirectMlBackendSelected && UpscaleWorkflow.TensorRtRtxAvailable &&
                                                  (UseTensorRtRtx ?? TensorRtRtxInstalled);
        public bool TensorRtBackendSelected => !DirectMlBackendSelected && !TensorRtRtxBackendSelected;

        public void SetBackendTensorRt() => SetBackend(directMl: false, tensorRtRtx: false);
        public void SetBackendTensorRtRtx() => SetBackend(directMl: false, tensorRtRtx: true);
        public void SetBackendDirectMl() => SetBackend(directMl: true, tensorRtRtx: false);

        private void SetBackend(bool directMl, bool tensorRtRtx)
        {
            UseTensorRtRtx = tensorRtRtx;
            UseDirectMl = directMl;
        }

        // Settings saved before the backend became app-wide: keep what the selected workflow used.
        public void MigrateBackend()
        {
            UseDirectMl ??= CurrentWorkflow?.LegacyDirectMlSelected ?? false;
        }

        private string _validationText = string.Empty;
        public string ValidationText
        {
            get => _validationText;
            set
            {
                this.RaiseAndSetIfChanged(ref _validationText, value);
                this.RaisePropertyChanged(nameof(LeftStatus));
            }
        }

        private string _backendSetupMainStatus = string.Empty;
        public string BackendSetupMainStatus
        {
            get => this._backendSetupMainStatus;
            set
            {
                this.RaiseAndSetIfChanged(ref _backendSetupMainStatus, value);
            }
        }

        public string BackendSetupSubStatusText => string.Join("\n", BackendSetupSubStatusQueue);

        private static readonly int BACKEND_SETUP_SUB_STATUS_QUEUE_CAPACITY = 50;

        private ConcurrentQueue<string> _backendSetupSubStatusQueue = new();
        public ConcurrentQueue<string> BackendSetupSubStatusQueue
        {
            get => this._backendSetupSubStatusQueue;
            set
            {
                this.RaiseAndSetIfChanged(ref _backendSetupSubStatusQueue, value);
                this.RaisePropertyChanged(nameof(BackendSetupSubStatusText));
            }
        }

        public string ConsoleText => string.Join("\n", ConsoleQueue);

        private static readonly int CONSOLE_QUEUE_CAPACITY = 1000;

        private ConcurrentQueue<string> _consoleQueue = new();
        public ConcurrentQueue<string> ConsoleQueue
        {
            get => this._consoleQueue;
            set
            {
                this.RaiseAndSetIfChanged(ref _consoleQueue, value);
                this.RaisePropertyChanged(nameof(ConsoleText));
            }
        }

        public string ModelsDirectory => _pythonService.ModelsDirectory;

        // The bundled vs_temporalfix model for a strength (temporalfix_s2_v1_op18_fp16.onnx, ...): the
        // newest version in the models folder, or null when it is missing.
        public string? TemporalFixModelPath(int strength) => TemporalFix.ModelPath(ModelsDirectory, strength);

        // Where aji_encode, the models and the conf are, for Core's run code.
        public AjiPaths Paths => new(_pythonService.AjiEncodePath, _pythonService.TrtexecPath, _pythonService.InferenceDirectory,
                                     _pythonService.ModelsDirectory, _pythonService.RifeModelsDirectory, _pythonService.ConfPath);

        public bool TensorRtInstalled => _pythonService.IsTensorRtInstalled();
        public bool TensorRtRtxInstalled => _pythonService.IsTensorRtRtxInstalled();

        // TensorRT-RTX needs its own component (aji_trtrtx.dll + the TensorRT-RTX runtime) and the
        // TensorRT runtime one (cudart, and TensorRT to fall back to). Null when both are installed.
        public string? TensorRtRtxComponentsMissingText()
        {
            var missing = new List<string>();
            if (!TensorRtRtxInstalled)
            {
                missing.Add(ComponentItem.TitleFor("trt-rtx"));
            }
            if (!TensorRtInstalled)
            {
                missing.Add(ComponentItem.TitleFor("trt-runtime"));
            }
            return missing.Count == 0 ? null
                : string.Format(CultureInfo.CurrentCulture, Resources.TensorRtRtxComponentsMissingText, string.Join(", ", missing));
        }

        public static readonly string _ffmpegX265 = EncoderPresets.FfmpegX265;
        public static readonly string _ffmpegX264 = EncoderPresets.FfmpegX264;
        public static readonly string _ffmpegHevcNvenc = EncoderPresets.FfmpegHevcNvenc;
        public static readonly string _ffmpegLossless = EncoderPresets.FfmpegLossless;

        // ---- encoder detection ------------------------------------------------------------
        // `aji_encode --probe-encoders` output from the last run, kept so the encoder rows are
        // right at startup; RefreshEncoderProbeAsync re-runs it (~4 s, in the background) on every
        // launch, so a new GPU or driver shows up by itself. Null: never probed, or an aji_encode
        // without the probe, which leaves the encoders as they were before detection (NVENC + CPU).
        private string? _encoderProbeOutput;
        [DataMember]
        public string? EncoderProbeOutput
        {
            get => _encoderProbeOutput;
            set
            {
                this.RaiseAndSetIfChanged(ref _encoderProbeOutput, value);
                _encoderProbe = EncoderProbe.Parse(value);
                EncodersChanged();
            }
        }

        private EncoderProbe? _encoderProbe;
        public EncoderProbe? Probe => _encoderProbe;

        // GPU encoders of the GPUs that don't do the upscale are hidden unless this is on: they
        // work, but take every frame over PCIe to a usually weaker encoder, for nothing.
        private bool _showOtherGpuEncoders;
        [DataMember]
        public bool ShowOtherGpuEncoders
        {
            get => _showOtherGpuEncoders;
            set
            {
                this.RaiseAndSetIfChanged(ref _showOtherGpuEncoders, value);
                EncodersChanged();
            }
        }

        // ---- GPU choice ----------------------------------------------------------------------
        // The GPU that does the upscale, app-wide like the backend: a probe GPU's id (aji_encode's
        // --device), or null for Auto, which on DirectML is the GPU Windows ranks first for high
        // performance and on TensorRT CUDA's first (normally the fastest NVIDIA card). Name and
        // position (among the probe's GPUs) find a GPU again whose id is gone, see EncoderProbe.Find.
        private string? _selectedGpuId;
        [DataMember]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? SelectedGpuId
        {
            get => _selectedGpuId;
            set => this.RaiseAndSetIfChanged(ref _selectedGpuId, value);
        }

        private string? _selectedGpuName;
        [DataMember]
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? SelectedGpuName
        {
            get => _selectedGpuName;
            set => this.RaiseAndSetIfChanged(ref _selectedGpuName, value);
        }

        private int _selectedGpuPosition = -1;
        [DataMember]
        public int SelectedGpuPosition
        {
            get => _selectedGpuPosition;
            set => this.RaiseAndSetIfChanged(ref _selectedGpuPosition, value);
        }

        // The GPUs the backend can upscale on: every GPU on DirectML, the NVIDIA ones on TensorRT
        // and TensorRT-RTX. Only GPUs with an id, so none from an aji_encode without --device.
        // Graphics cards first, integrated graphics last (otherwise in the probe's order): Windows
        // and Direct3D tell those apart, but not which of two graphics cards upscales faster.
        public IReadOnlyList<EncoderProbe.Gpu> BackendGpus => GpuChoice.BackendGpus(_encoderProbe, DirectMlBackendSelected);

        // An integrated GPU chosen while the backend has a graphics card to run on: upscaling there is
        // far slower, so App Settings says so (a warning, not a block).
        public bool GpuChoiceIntegratedWarning =>
            ChosenGpu?.Integrated == true && BackendGpus.Any(g => g.Integrated == false);

        // The chosen GPU when the backend can use it, else null: Auto, and no --device.
        public EncoderProbe.Gpu? ChosenGpu =>
            GpuChoice.Chosen(_encoderProbe, DirectMlBackendSelected, SelectedGpuId, SelectedGpuName, SelectedGpuPosition);

        // The GPU row is there whenever the probe found a GPU the backend can use, so the settings
        // always say which GPU upscales (a sanity check, and something to share when troubleshooting);
        // it can only be changed when there's more than one.
        public bool GpuChoiceVisible => BackendGpus.Count > 0;
        public bool GpuChoiceEnabled => BackendGpus.Count > 1;

        // The GPU Auto upscales on, when the probe can tell: DirectML's default GPU, or on TensorRT
        // the only NVIDIA GPU (with several, CUDA picks the fastest, which the probe doesn't know).
        private EncoderProbe.Gpu? AutoGpu => GpuChoice.Auto(_encoderProbe, DirectMlBackendSelected);

        // Auto, then each GPU. Auto names the GPU it means where the probe knows it (AutoGpu).
        // The same array while the labels are the same: the list clears its selection when its items
        // are replaced, and writes that back, so a GPU picked in it would be undone.
        private string[] _gpuChoices = [];
        public string[] GpuChoices
        {
            get
            {
                var gpus = BackendGpus;
                var auto = AutoGpu is EncoderProbe.Gpu d
                    ? string.Format(CultureInfo.CurrentCulture, Resources.GpuAutoNamedLabel, EncoderProbe.Label(gpus, d))
                    : Resources.GpuAutoLabel;
                string[] choices = [auto, .. gpus.Select(g => g.Integrated == true
                    ? string.Format(CultureInfo.CurrentCulture, Resources.GpuIntegratedLabel, EncoderProbe.Label(gpus, g))
                    : EncoderProbe.Label(gpus, g))];
                if (!choices.SequenceEqual(_gpuChoices))
                {
                    _gpuChoices = choices;
                }
                return _gpuChoices;
            }
        }

        // True while SelectedGpuChoiceIndex reports -1 to put the list's selection back, see below.
        private bool _gpuChoiceResync;

        public int SelectedGpuChoiceIndex
        {
            get => _gpuChoiceResync ? -1 : ChosenGpu is EncoderProbe.Gpu g ? BackendGpus.ToList().IndexOf(g) + 1 : 0;
            set
            {
                // The list clears its selection (-1) when its items are replaced: the probe's GPUs
                // arriving after the window opened, or a backend change. Raising the same index
                // again doesn't restore it (the binding only passes on a changed value), so once the
                // list has settled, report -1 and then the index.
                if (value < 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _gpuChoiceResync = true;
                        this.RaisePropertyChanged(nameof(SelectedGpuChoiceIndex));
                        _gpuChoiceResync = false;
                        this.RaisePropertyChanged(nameof(SelectedGpuChoiceIndex));
                    });
                    return;
                }
                if (value == SelectedGpuChoiceIndex)
                {
                    return;
                }
                var gpus = BackendGpus;
                var gpu = value > 0 && value <= gpus.Count ? gpus[value - 1] : null;
                SelectedGpuId = gpu?.Id;
                SelectedGpuName = gpu?.Name;
                SelectedGpuPosition = gpu is null || _encoderProbe is null ? -1 : _encoderProbe.Gpus.ToList().IndexOf(gpu);
                EncodersChanged();     // the upscaling GPU, so the listed encoders, follow the choice
                _ = RefreshComponentsIfChoiceChanged();     // and what the component packs are for
            }
        }

        // The GPU that does the upscale: the chosen one, else Auto's, which is an NVIDIA card on
        // TensorRT and the probe's default GPU on DirectML (null when the probe couldn't tell).
        public EncoderGroup? InferenceGpuVendor => GpuChoice.InferenceVendor(_encoderProbe, DirectMlBackendSelected, ChosenGpu);

        // Works on this PC: CPU presets always; GPU ones when the probe opened their encoder.
        // Without a probe only NVENC HEVC / Lossless are offered, as before detection existed.
        public bool PresetAvailable(EncoderPreset p) => GpuChoice.PresetAvailable(_encoderProbe, p);

        public bool GroupAvailable(EncoderGroup g) => EncoderPresets.For(g).Any(PresetAvailable);

        // Listed in the encoder row: the CPU, and the upscaling GPU's encoders (every GPU's when
        // that GPU is unknown or ShowOtherGpuEncoders is on). A workflow adds its own selection.
        public bool GroupListed(EncoderGroup g) =>
            g == EncoderGroup.Cpu ||
            (GroupAvailable(g) && (ShowOtherGpuEncoders || InferenceGpuVendor is null || InferenceGpuVendor == g));

        // the "Show other GPUs" checkbox only appears when it would show something
        public bool HasOtherGpuEncoders =>
            InferenceGpuVendor is EncoderGroup v &&
            new[] { EncoderGroup.Nvidia, EncoderGroup.Amd, EncoderGroup.Intel }.Any(g => g != v && GroupAvailable(g));

        // What a workflow nobody picked an encoder for uses: HEVC on the upscaling GPU, else x265.
        public EncoderPreset DefaultEncoderPreset => GpuChoice.DefaultEncoder(_encoderProbe, InferenceGpuVendor);

        private void EncodersChanged()
        {
            this.RaisePropertyChanged(nameof(Probe));
            this.RaisePropertyChanged(nameof(GpuChoiceVisible));
            this.RaisePropertyChanged(nameof(GpuChoiceEnabled));
            this.RaisePropertyChanged(nameof(GpuChoiceIntegratedWarning));
            this.RaisePropertyChanged(nameof(GpuChoices));
            this.RaisePropertyChanged(nameof(SelectedGpuChoiceIndex));
            this.RaisePropertyChanged(nameof(HasOtherGpuEncoders));
            foreach (var wf in Workflows ?? [])
            {
                wf.EncodersChanged();
            }
        }

        public async Task RefreshEncoderProbeAsync()
        {
            // an older aji_encode rejects the flag (exit 2, usage on stderr): keep what we had
            if (await AjiProcess.ProbeAsync(Paths) is string output)
            {
                await Dispatcher.UIThread.InvokeAsync(() => EncoderProbeOutput = output);
            }
        }

        // After the main window opens: components first (the probe needs aji_encode), then the
        // encoder probe. While the shared runtime's folder is missing the component check waits for
        // the next start: --auto can't install into it, and its failure would block the app.
        public async Task StartupChecks()
        {
            await RefreshStorage();
            if (!StorageMissing)
            {
                await CheckAndExtractBackend();
            }
            await RefreshEncoderProbeAsync();
            _followChoiceInComponents = true;
            await RefreshComponentsIfChoiceChanged();   // what a chosen GPU needs, for validation
        }

        private bool _showConsole = false;
        public bool ShowConsole
        {
            get => _showConsole;
            set => this.RaiseAndSetIfChanged(ref _showConsole, value);
        }

        // Preview of the upscale while it runs: aji_encode keeps a JPEG of its latest output frame
        // up to date (--preview) and prints a PREVIEW line each time it rewrites it.
        private static readonly string PreviewPath =
            Path.Combine(Path.GetTempPath(), $"videojanai-preview-{Environment.ProcessId}.jpg");

        private bool _showPreview = true;
        [DataMember]
        public bool ShowPreview
        {
            get => _showPreview;
            set => this.RaiseAndSetIfChanged(ref _showPreview, value);
        }

        private Bitmap? _previewImage;
        [IgnoreDataMember]
        public Bitmap? PreviewImage
        {
            get => _previewImage;
            set
            {
                var old = _previewImage;
                this.RaiseAndSetIfChanged(ref _previewImage, value);
                if (!ReferenceEquals(old, value))
                {
                    old?.Dispose();
                }
            }
        }

        // Called off the UI thread (process output): read the whole file first, so aji_encode can
        // replace it again right away, then swap the image in on the UI thread.
        private void LoadPreview()
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(PreviewPath);
            }
            catch (IOException)
            {
                return;   // being replaced; the next PREVIEW line brings it
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    PreviewImage = new Bitmap(ms);
                }
                catch (Exception)
                {
                    // a bad frame only costs the preview
                }
            });
        }

        // The preview window: a player over an aji_encode --preview-session with the current
        // settings, to look at the upscale anywhere in the video before encoding it. One at a
        // time; an upscale starting closes it (and the GPU memory its session holds).
        private PreviewWindowViewModel? _preview;

        public bool PreviewEnabled => !Upscaling;

        public PreviewWindowViewModel CreatePreview()
        {
            _preview = new PreviewWindowViewModel(StartPreviewSession, ConsoleQueueEnqueue);
            return _preview;
        }

        public void PreviewClosed(PreviewWindowViewModel preview)
        {
            if (_preview == preview)
            {
                _preview = null;
            }
        }

        // The video the preview shows: the input video, or the first one in the input folder.
        private string? PreviewInputPath()
        {
            if (CurrentWorkflow.SelectedTabIndex == 0)
            {
                return File.Exists(CurrentWorkflow.InputFilePath) ? CurrentWorkflow.InputFilePath : null;
            }
            if (!Directory.Exists(CurrentWorkflow.InputFolderPath))
            {
                return null;
            }
            return PlanFolder().Videos.FirstOrDefault()?.Input;
        }

        // Folder mode's videos: the folder's and its subfolders' (the output keeps the subfolders),
        // planned as the command line plans a folder. Problem: two videos would write one file.
        internal PlanResult PlanFolder() =>
            BatchPlanning.Plan([CurrentWorkflow!.InputFolderPath],
                               string.IsNullOrWhiteSpace(CurrentWorkflow.OutputFolderPath) ? null : CurrentWorkflow.OutputFolderPath,
                               CurrentWorkflow.OutputFilename, skipExisting: !CurrentWorkflow.OverwriteExistingVideos);

        // Single-file mode's video and its output, planned the same way. Problem: the output would be
        // written over the video.
        internal PlanResult PlanFile() =>
            BatchPlanning.Plan([CurrentWorkflow!.InputFilePath],
                               string.IsNullOrWhiteSpace(CurrentWorkflow.OutputFolderPath) ? null : CurrentWorkflow.OutputFolderPath,
                               CurrentWorkflow.OutputFilename, skipExisting: !CurrentWorkflow.OverwriteExistingVideos);

        // Why the run can't go ahead as set up (from the last CheckInputs), else null.
        public string? InputPlanProblem { get; private set; }

        private (PreviewSession? session, string? error, string? title) StartPreviewSession()
        {
            var input = PreviewInputPath();
            if (input == null)
            {
                return (null, Resources.PreviewNoInputText, null);
            }
            if (CurrentWorkflow.EnableUpscale && CurrentWorkflow.UpscaleSettings.Any(m => !File.Exists(m.OnnxModelPath)))
            {
                return (null, "ONNX Model Path is required.", null);
            }
            if (CurrentWorkflow.EnableTemporalFix && TemporalFixModelPath(CurrentWorkflow.TemporalFixStrength ?? 2) == null)
            {
                return (null, Resources.TemporalFixModelMissingText, null);
            }
            if (StorageNeededButMissing)
            {
                return (null, StorageMissingText, null);
            }
            if (CurrentWorkflow.TensorRtRtxSelected && TensorRtRtxComponentsMissingText() is string rtxMissing)
            {
                return (null, rtxMissing, null);
            }
            if (CurrentWorkflow.TensorRtSelected && !TensorRtInstalled)
            {
                return (null, "TensorRT is not installed. Install it, or select DirectML, in App Settings.", null);
            }
            if (CurrentWorkflow.TensorRtSelected && TensorRtKernelsMissingText() is string kernelsMissing)
            {
                return (null, kernelsMissing, null);
            }

            try
            {
                SetupAnimeJaNaiConfSlot1();
                var args = BuildAjiEncodeArgs(input, null, AjiMode.Preview);
                if (_preview != null)
                {
                    // TensorRT-RTX builds in about a second; TensorRT can take minutes
                    _preview.BuildingEngineText = CurrentWorkflow.TensorRtRtxSelected
                        ? Resources.PreviewBuildingEngineTextTensorRtRtx
                        : Resources.PreviewBuildingEngineText;
                }
                ConsoleQueueEnqueue($"Previewing with command: {_pythonService.AjiEncodePath} {args}");
                var session = PreviewSession.Start(_pythonService.AjiEncodePath, args,
                                                   _pythonService.InferenceDirectory, AjiEnvironment());
                return (session, null, Path.GetFileName(input));
            }
            catch (Exception e)
            {
                return (null, e.Message, null);
            }
        }



        private bool _showAppSettings = false;
        public bool RequestShowAppSettings
        {
            get => _showAppSettings;
            set
            {
                this.RaiseAndSetIfChanged(ref _showAppSettings, value);
                this.RaisePropertyChanged(nameof(ShowAppSettings));
                this.RaisePropertyChanged(nameof(ShowMainForm));
            }
        }

        public bool ShowAppSettings => RequestShowAppSettings && !IsExtractingBackend;

        public bool ShowMainForm => !RequestShowAppSettings && !IsExtractingBackend;

        private string _inputStatusText = string.Empty;
        public string InputStatusText
        {
            get => _inputStatusText;
            set
            {
                this.RaiseAndSetIfChanged(ref _inputStatusText, value);
                this.RaisePropertyChanged(nameof(LeftStatus));
            }
        }

        // libaji's last TensorRT-RTX -> TensorRT fallback this run (see BackendFallback): a warning in
        // the status bar, with the full message as its tooltip, until the next run starts.
        private string _backendFallbackText = string.Empty;
        public string BackendFallbackText
        {
            get => _backendFallbackText;
            set
            {
                this.RaiseAndSetIfChanged(ref _backendFallbackText, value);
                this.RaisePropertyChanged(nameof(ShowBackendFallback));
            }
        }

        public bool ShowBackendFallback => !string.IsNullOrEmpty(BackendFallbackText);

        public string LeftStatus => !CurrentWorkflow.Valid ? ValidationText.Replace("\n", " ") : $"{InputStatusText} selected for upscaling.";

        private bool _componentsBusy = false;
        [IgnoreDataMember]
        public bool ComponentsBusy
        {
            get => _componentsBusy;
            set
            {
                this.RaiseAndSetIfChanged(ref _componentsBusy, value);
                this.RaisePropertyChanged(nameof(AllowReinstall));
                this.RaisePropertyChanged(nameof(AllowStorageChanges));
            }
        }

        public bool AllowReinstall => !ComponentsBusy && !Upscaling;

        private AvaloniaList<ComponentItem> _components = new();
        [IgnoreDataMember]
        public AvaloniaList<ComponentItem> Components
        {
            get => _components;
            set => this.RaiseAndSetIfChanged(ref _components, value);
        }

        // ---- packs for the chosen GPU -------------------------------------------------------
        // Which GPUs the component packs are for, as the updater's --gpus (with a leading space): with
        // a GPU chosen in App Settings, that GPU on TensorRT / TensorRT-RTX, or none on DirectML, which
        // needs no packs. Empty for Auto: every NVIDIA GPU gets its packs, since TensorRT builds on
        // CUDA's fastest card, which a beginner's PC doesn't tell us, and a missing kernel pack breaks
        // engine builds while an extra one only costs disk. A pack the chosen GPU doesn't need is
        // left as it is, and once removed it stays removed (--auto no longer recommends it).
        public string ComponentGpusArgument =>
            ChosenGpu is not EncoderProbe.Gpu g ? ""
            : DirectMlBackendSelected ? " --gpus none"
            : $" --gpus {g.Id}";

        // The ComponentGpusArgument that Components was listed for (null before the first list).
        [IgnoreDataMember]
        public string? ComponentsListedFor { get; set; }

        // Set once the startup checks are done: from then on a GPU or backend change lists the
        // components again, for what the new choice needs.
        private bool _followChoiceInComponents;

        private async Task RefreshComponentsIfChoiceChanged()
        {
            if (_followChoiceInComponents && !ComponentsBusy && _updateManagerService.IsInstalled &&
                ComponentsListedFor != ComponentGpusArgument)
            {
                await RefreshComponents();
                CurrentWorkflow?.Validate();
            }
        }

        // The TensorRT kernel packs the chosen GPU needs that aren't installed (TensorRT with a GPU
        // chosen, once Components is listed for that choice): a message, else null. The TensorRT
        // runtime and TensorRT-RTX have their own checks.
        public string? TensorRtKernelsMissingText()
        {
            if (ChosenGpu is not EncoderProbe.Gpu g || !TensorRtBackendSelected ||
                ComponentsListedFor != ComponentGpusArgument)
            {
                return null;
            }
            var missing = Components
                .Where(c => c.Recommended && !c.Installed && (c.Name.StartsWith("trt-sm", StringComparison.Ordinal) || c.Name == "trt-ptx"))
                .Select(c => c.Title).ToList();
            return missing.Count == 0 ? null
                : string.Format(CultureInfo.CurrentCulture, Resources.TensorRtKernelsMissingText, g.Name, string.Join(", ", missing));
        }

        private string _gpuStatusText = string.Empty;
        [IgnoreDataMember]
        public string GpuStatusText
        {
            get => _gpuStatusText;
            set => this.RaiseAndSetIfChanged(ref _gpuStatusText, value);
        }

        private string _componentStatusText = string.Empty;
        [IgnoreDataMember]
        public string ComponentStatusText
        {
            get => _componentStatusText;
            set => this.RaiseAndSetIfChanged(ref _componentStatusText, value);
        }

        private bool _upscaling = false;
        [IgnoreDataMember]
        public bool Upscaling
        {
            get => _upscaling;
            set
            {
                this.RaiseAndSetIfChanged(ref _upscaling, value);
                this.RaisePropertyChanged(nameof(UpscaleEnabled));
                this.RaisePropertyChanged(nameof(PreviewEnabled));
                this.RaisePropertyChanged(nameof(LeftStatus));
                this.RaisePropertyChanged(nameof(AllowReinstall));
                this.RaisePropertyChanged(nameof(AllowStorageChanges));
            }
        }

        private bool _isExtractingBackend = false;
        public bool IsExtractingBackend
        {
            get => _isExtractingBackend;
            set
            {
                this.RaiseAndSetIfChanged(ref _isExtractingBackend, value);
                this.RaisePropertyChanged(nameof(RequestShowAppSettings));
                this.RaisePropertyChanged(nameof(ShowMainForm));
            }
        }

        private bool _extractingBackendFailed = false;
        public bool ExtractingBackendFailed
        {
            get => _extractingBackendFailed;
            set
            {
                this.RaiseAndSetIfChanged(ref _extractingBackendFailed, value);
            }
        }

        public bool UpscaleEnabled => CurrentWorkflow.Valid && !Upscaling;

        private AvaloniaList<UpscaleWorkflow> _workflows;
        [DataMember]
        public AvaloniaList<UpscaleWorkflow> Workflows
        {
            get => _workflows;
            set => this.RaiseAndSetIfChanged(ref _workflows, value);
        }

        private int _selectedWorkflowIndex = 0;
        [DataMember]
        public int SelectedWorkflowIndex
        {
            get => _selectedWorkflowIndex;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedWorkflowIndex, value);
                this.RaisePropertyChanged(nameof(CurrentWorkflow));
                this.RaisePropertyChanged(nameof(CurrentWorkflow.ActiveWorkflow));
            }
        }

        public UpscaleWorkflow? CurrentWorkflow
        {
            get => Workflows?[SelectedWorkflowIndex];
            set
            {
                if (Workflows != null)
                {
                    Workflows[SelectedWorkflowIndex] = value;
                    this.RaisePropertyChanged(nameof(CurrentWorkflow));
                    this.RaisePropertyChanged(nameof(Workflows));
                }
            }
        }

        public void HandleWorkflowSelected(int workflowIndex)
        {
            SelectedWorkflowIndex = workflowIndex;
            RequestShowAppSettings = false;
        }

        public void HandleAppSettingsSelected()
        {
            RequestShowAppSettings = true;
        }


        public int CheckInputs()
        {

            InputStatusText = "0 video files";
            InputPlanProblem = null;

            if (CurrentWorkflow.Valid && !Upscaling)
            {
                var overwriteText = CurrentWorkflow.OverwriteExistingVideos ? "overwritten" : "skipped";

                // input file
                if (CurrentWorkflow.SelectedTabIndex == 0)
                {
                    StringBuilder status = new();
                    var skipFiles = 0;

                    // planned as folder mode plans, so an output that would replace the video is caught
                    var plan = PlanFile();
                    InputPlanProblem = plan.Problem;
                    if (plan.Videos.Count == 0)
                    {
                        return 0;
                    }
                    var outputFilePath = plan.Videos[0].Output;

                    if (File.Exists(outputFilePath))
                    {
                        status.Append($" (1 video file already exists and will be {overwriteText})");
                        if (!CurrentWorkflow.OverwriteExistingVideos)
                        {
                            skipFiles++;
                        }
                    }

                    var s = skipFiles > 0 ? "s" : "";
                    status.Insert(0, $"{1 - skipFiles} video file{s}");

                    InputStatusText = status.ToString();
                    return 1 - skipFiles;
                }
                else  // input folder
                {
                    List<string> statuses = new();
                    var totalFileCount = 0;

                    // the videos the run takes (subfolders included), so the count is what runs
                    var plan = PlanFolder();
                    InputPlanProblem = plan.Problem;
                    var existFileCount = plan.Videos.Count(v => File.Exists(v.Output));
                    var filesCount = plan.Problem != null ? 0 : plan.Videos.Count(v => v.Action == PlanAction.Encode);

                    var videoS = filesCount == 1 ? "" : "s";
                    var existVideoS = existFileCount == 1 ? "" : "s";
                    var existS = existFileCount == 1 ? "s" : "";

                    statuses.Add($"{filesCount} video file{videoS} ({existFileCount} video file{existVideoS} already exist{existS} and will be {overwriteText})");
                    totalFileCount += filesCount;

                    InputStatusText = $"{string.Join(" and ", statuses)}";
                    return totalFileCount;
                }
            }

            return 0;
        }

        private List<string>? _rifeModels = null;

        public List<string> RifeModels
        {
            get
            {
                if (_rifeModels == null)
                {
                    // TODO: re-check this regex/discovery against the new models-rife-fp16-1 filenames.
                    if (!Directory.Exists(_pythonService.RifeModelsDirectory))
                    {
                        return [];
                    }
                    _rifeModels = Rife.Models(_pythonService.RifeModelsDirectory);
                }

                return _rifeModels;
            }
        }

        public static string RifeLabelToValue(string rifeLabel) => Rife.LabelToValue(rifeLabel);

        public static string RifeValueToLabel(string rifeValue) => Rife.ValueToLabel(rifeValue);

        // The selected workflow as Core's run description, on this PC's backend, GPU and default encoder.
        internal EncodeJob CurrentJob()
        {
            var wf = CurrentWorkflow!;
            var backend = wf.DirectMlSelected ? Backend.DirectMl
                        : wf.TensorRtRtxSelected ? Backend.TensorRtRtx
                        : Backend.TensorRt;
            return EncodeJob.FromWorkflow(wf.ToSettings(), backend, ChosenGpu?.Id, DefaultEncoderPreset, ModelsDirectory);
        }

        // The conf aji_encode runs with (libaji's config_version=2 schema), with the workflow's models
        // copied into the models folder, where aji resolves them by name.
        public void SetupAnimeJaNaiConfSlot1() => AjiConf.Write(CurrentJob(), Paths);

        // 0 when there is nothing to build; otherwise aji_encode --build-only's exit code.
        public async Task<int> CheckEngines(string inputFilePath)
        {
            // TensorRT and TensorRT-RTX build/cache engines on first use (TensorRT-RTX in about a
            // second, so prebuilding it costs little); DirectML needs no prebuild.
            if (!CurrentWorkflow.TensorRtSelected)
            {
                return 0;
            }

            return await GenerateEngine(inputFilePath);
        }

        public async Task RunUpscale()
        {
            ShowConsole = true;
            ConsoleQueueClear();

            // the shared runtime's drive may have gone since the settings were checked
            await RefreshStorage();
            if (StorageNeededButMissing)
            {
                ConsoleQueueEnqueue(StorageMissingText);
                return;
            }

            // the video, or the folder's videos and its subfolders' (the output keeps the subfolders);
            // outputs that exist are skipped unless overwriting, as the status bar says
            var plan = CurrentWorkflow.SelectedTabIndex == 0 ? PlanFile() : PlanFolder();
            if (plan.Problem != null)
            {
                ConsoleQueueEnqueue($"{plan.Problem}.");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            var ct = _cancellationTokenSource.Token;

            //var task = Task.Run(async () =>
            //{
            ct.ThrowIfCancellationRequested();
            BackendFallbackText = string.Empty;
            _preview?.RequestClose();
            Upscaling = true;
            SetupAnimeJaNaiConfSlot1();

            int attempted = 0, failed = 0;
            foreach (var video in plan.Videos)
            {
                ct.ThrowIfCancellationRequested();
                if (video.Action == PlanAction.SkipExisting)
                {
                    ConsoleQueueEnqueue($"Skipping {video.Input}: {video.Output} already exists.");
                    continue;
                }
                attempted++;
                if (await UpscaleVideo(video, ct) is string failure)
                {
                    failed++;
                    ConsoleQueueEnqueue($"{video.Input} was not upscaled: {failure}.");
                }
                ct.ThrowIfCancellationRequested();
            }
            if (failed > 0 && attempted > 1)
            {
                ConsoleQueueEnqueue($"{failed} of {attempted} videos were not upscaled.");
            }

            CurrentWorkflow.Valid = true;
            //}, ct);

            try
            {
                //await task;
                CurrentWorkflow.Validate();
            }
            catch (OperationCanceledException e)
            {
                Console.WriteLine($"{nameof(OperationCanceledException)} thrown with message: {e.Message}");
                Upscaling = false;
                CurrentWorkflow.Validate();
            }
            finally
            {
                _cancellationTokenSource.Dispose();
                Upscaling = false;
                CurrentWorkflow.Validate();
                try
                {
                    File.Delete(PreviewPath);
                    File.Delete(PreviewPath + ".tmp");
                }
                catch (Exception)
                {
                    // a leftover temp file is harmless
                }
            }
        }

        public void CancelUpscale()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                if (_runningProcess != null && !_runningProcess.HasExited)
                {
                    // Kill the process
                    _runningProcess.Kill(true);
                    _runningProcess = null; // Clear the reference to the terminated process
                }
                Upscaling = false;
                CurrentWorkflow.Validate();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

        }

        // One video: the engine prebuild (TensorRT), then the encode. Null when the output was written,
        // else why not; a failed or stopped encode leaves no output (and an output that was there
        // stays as it was).
        private async Task<string?> UpscaleVideo(PlannedVideo video, CancellationToken ct)
        {
            try
            {
                var rc = await CheckEngines(video.Input);
                ct.ThrowIfCancellationRequested();
                if (rc != 0)
                {
                    return $"the TensorRT engine could not be generated (exit code {rc})";
                }
                Directory.CreateDirectory(Path.GetDirectoryName(video.Output)!);
                var result = await RunUpscaleSingle(video.Input, video.Output, ct);
                return result.Written ? null : result.Failure;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return e.Message.TrimEnd('.');
            }
        }

        // Encodes into a partial file next to the output, which becomes the output only when
        // aji_encode succeeds (PartialOutput).
        public async Task<EncodeResult> RunUpscaleSingle(string inputFilePath, string outputFilePath, CancellationToken ct = default)
        {
            using var observer = new RunObserver(this);
            return await new EncodeRunner(Paths).EncodeToOutputAsync(CurrentJob(), inputFilePath, outputFilePath, observer, PreviewPath, ct);
        }

        public async Task<int> GenerateEngine(string inputFilePath)
        {
            // CheckEngines decides when (EncodeRunner.BuildEngineAsync's rule), so build here whatever
            // the backend
            var job = CurrentJob();
            var args = BuildAjiEncodeArgs(inputFilePath, null, AjiMode.BuildOnly);
            var engine = job.Backend == Backend.TensorRtRtx ? "TensorRT-RTX" : "TensorRT";
            ConsoleQueueEnqueue($"Generating {engine} engine with command: {_pythonService.AjiEncodePath} {args}");
            return await RunAjiEncode(args, resetProgress: false);
        }

        internal string BuildAjiEncodeArgs(string inputFilePath, string? outputFilePath, AjiMode mode) =>
            AjiArgs.Build(CurrentJob(), Paths, inputFilePath, outputFilePath, mode, mode == AjiMode.Encode ? PreviewPath : null);

        // aji_encode's exit code
        public async Task<int> RunAjiEncode(string arguments, bool resetProgress)
        {
            if (resetProgress)
            {
                ResetProgress();
            }
            using var observer = new RunObserver(this);
            return await AjiProcess.RunAsync(Paths, arguments, observer.Stdout, observer.Stderr, observer.Started);
        }

        // aji_encode's output during a run: every line to error.log (in the state folder: the working
        // directory may be anywhere, including somewhere read-only) and the console, except PROGRESS
        // lines (the progress bar) and PREVIEW lines (the preview); TensorRT-RTX fallbacks also go
        // to the status bar.
        private sealed class RunObserver : IEncodeObserver, IDisposable
        {
            private readonly MainWindowViewModel _vm;
            private readonly StreamWriter _log;

            public RunObserver(MainWindowViewModel vm)
            {
                _vm = vm;
                _log = new StreamWriter(Path.Combine(Program.AppStateFolder, "error.log"), append: true);
            }

            public void Command(string text) => _vm.ConsoleQueueEnqueue(text);

            public void EncodeStarting(string input, string output) => _vm.ResetProgress();

            public void Started(Process process)
            {
                _vm._runningProcess = process;
                // Into the kill-on-close job right away, so closing VideoJaNai mid-encode also stops
                // aji_encode (and the trtexec it may be running).
                ChildProcessTracker.AddProcess(process);
            }

            public void Stderr(string line)
            {
                Log(line);
                _vm.ConsoleQueueEnqueue(line);
                if (BackendFallback.FromAjiLog(line) is string fallback)
                {
                    _vm.BackendFallbackText = fallback;
                }
            }

            public void Stdout(string line)
            {
                Log(line);
                // aji_encode emits machine-readable "PROGRESS ..." lines on stdout; route those to the
                // progress bar instead of the console, and "PREVIEW ..." lines to the preview.
                if (line.StartsWith("PREVIEW", StringComparison.Ordinal))
                {
                    _vm.LoadPreview();
                }
                else if (!_vm.TryHandleProgressLine(line))
                {
                    _vm.ConsoleQueueEnqueue(line);
                }
            }

            private void Log(string line)
            {
                lock (_log)
                {
                    _log.WriteLine(line);
                }
            }

            public void Dispose() => _log.Dispose();
        }

        private Dictionary<string, string>? AjiEnvironment() => AjiProcess.Environment(_pythonService.InferenceDirectory);

        private bool TryHandleProgressLine(string line)
        {
            if (!_progress.Handle(line))
            {
                return false;
            }
            ProgressPhase = _progress.Phase;
            ProgressValue = _progress.Percent;
            ProgressText = _progress.Text;
            return true;
        }

        private void ResetProgress()
        {
            _progress.Reset();
            ProgressValue = 0;
            ProgressText = string.Empty;
            ProgressPhase = string.Empty;
            PreviewImage = null;
        }

        private void ConsoleQueueClear()
        {
            ConsoleQueue.Clear();
            this.RaisePropertyChanged(nameof(ConsoleText));
        }

        private void ConsoleQueueEnqueue(string value)
        {
            while (ConsoleQueue.Count > CONSOLE_QUEUE_CAPACITY)
            {
                ConsoleQueue.TryDequeue(out var _);
            }
            ConsoleQueue.Enqueue(value);
            this.RaisePropertyChanged(nameof(ConsoleText));
        }

        private void BackendSetupSubStatusQueueClear()
        {
            BackendSetupSubStatusQueue.Clear();
            this.RaisePropertyChanged(nameof(BackendSetupSubStatusText));
        }

        private void BackendSetupSubStatusQueueEnqueue(string value)
        {
            while (BackendSetupSubStatusQueue.Count > BACKEND_SETUP_SUB_STATUS_QUEUE_CAPACITY)
            {
                BackendSetupSubStatusQueue.TryDequeue(out var _);
            }
            BackendSetupSubStatusQueue.Enqueue(value);
            this.RaisePropertyChanged(nameof(BackendSetupSubStatusText));
        }

        public void ResetCurrentWorkflow()
        {
            if (CurrentWorkflow is UpscaleWorkflow old)
            {
                var workflowIndex = old.WorkflowIndex;
                ReplaceCurrentWorkflow(new UpscaleWorkflow
                {
                    Vm = this,
                    WorkflowIndex = workflowIndex,
                    WorkflowName = $"Workflow {workflowIndex + 1}",
                    UpscaleSettings = [new()],
                });
            }
        }

        // Detach the replaced workflow so its subscription on this view model is disposed.
        private void ReplaceCurrentWorkflow(UpscaleWorkflow workflow)
        {
            var old = CurrentWorkflow;
            CurrentWorkflow = workflow;
            if (old != null)
            {
                old.Vm = null;
            }
            workflow.Validate();
        }

        public void ReadWorkflowFileToCurrentWorkflow(string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                return;
            }

            var lines = File.ReadAllText(fullPath);
            var workflow = JsonConvert.DeserializeObject<UpscaleWorkflow>(lines, NewtonsoftJsonSuspensionDriver.Settings);
            if (workflow != null && CurrentWorkflow != null)
            {
                workflow.WorkflowIndex = CurrentWorkflow.WorkflowIndex;
                workflow.Vm = this;
                ReplaceCurrentWorkflow(workflow);
            }
        }

        public void WriteCurrentWorkflowToFile(string fullPath)
        {
            var lines = JsonConvert.SerializeObject(CurrentWorkflow, NewtonsoftJsonSuspensionDriver.Settings);
            File.WriteAllText(fullPath, lines);
        }

        // The version this state was last used with, so an update can refresh components once.
        private string? _lastRunVersion;
        [DataMember]
        public string? LastRunVersion
        {
            get => _lastRunVersion;
            set => this.RaiseAndSetIfChanged(ref _lastRunVersion, value);
        }

        // Whether a saved state was on disk at startup (App sets it before loading): false on a
        // fresh install, whose state comes from the bundled defaults.
        [IgnoreDataMember]
        public bool HadSavedState { get; set; }

        // An update, so time to refresh the component packs: the version changed since the last run.
        // States saved by 2.8.1 and older have no LastRunVersion, but a saved state means the app ran
        // before, so that's an update too (otherwise nobody updating from them would get the
        // trt-rtx pack or a refreshed one). A fresh install has no saved state and keeps the packs its
        // installer's component page picked.
        public static bool IsFirstLaunchAfterUpdate(string? lastRunVersion, string version, bool hadSavedState) =>
            string.IsNullOrEmpty(lastRunVersion) ? hadSavedState : lastRunVersion != version;

        public async Task CheckAndExtractBackend()
        {
            // Components are installed by the Inno setup's component page. On launch, verify the
            // engine is present; if not (skipped/offline at install) and we're installed, run the
            // updater's --auto to fetch the GPU-matched components, with progress on the setup overlay.
            // The first launch after an update runs --auto too: an app update replaces the core but
            // not the component packs, and a pack whose dep moved (a TensorRT, RIFE or aji bump; the
            // trt-rtx pack must match aji.dll) is stale until --auto reinstalls it, and it installs
            // packs newly recommended for this GPU (trt-rtx on RTX 20 / GTX 16-series and newer). A
            // failed refresh keeps the components already installed, so it doesn't block the app.
            var version = _updateManagerService.AppVersion;
            var updated = IsFirstLaunchAfterUpdate(LastRunVersion, version, HadSavedState);
            LastRunVersion = version;
            var missing = !_pythonService.IsInferenceInstalled();
            if (!_updateManagerService.IsInstalled || (!missing && !updated))
            {
                return;
            }

            await Task.Run(async () =>
            {
                BackendSetupSubStatusQueueClear();
                IsExtractingBackend = true;
                BackendSetupMainStatus = missing
                    ? "Installing components for your hardware..."
                    : "Updating components for your hardware...";
                try
                {
                    var rc = await _updateManagerService.RunUpdaterStreamingAsync("--auto" + ComponentGpusArgument, BackendSetupSubStatusQueueEnqueue);
                    if (rc != 0 && missing)
                    {
                        ExtractingBackendFailed = true;
                        BackendSetupMainStatus = "Component setup failed. You can retry from App Settings.";
                        return;
                    }
                }
                catch (Exception ex)
                {
                    BackendSetupSubStatusQueueEnqueue(ex.Message);
                    if (missing)
                    {
                        ExtractingBackendFailed = true;
                        BackendSetupMainStatus = "Component setup failed. You can retry from App Settings.";
                        return;
                    }
                }
                IsExtractingBackend = false;
            });
            // --auto may have installed or removed trt-rtx, which an unchosen backend follows
            BackendChanged();
        }

        public async Task ReinstallBackend()
        {
            // Re-fetch the GPU-matched components via the updater (--auto).
            ComponentsBusy = true;
            ComponentStatusText = "Reinstalling components...";
            try
            {
                await _updateManagerService.RunUpdaterStreamingAsync("--auto" + ComponentGpusArgument, line => ComponentStatusText = line);
            }
            catch (Exception ex) { ComponentStatusText = ex.Message; }
            ComponentsBusy = false;
            await RefreshComponents();
            BackendChanged();
        }

        // ---- Storage: where the shared pack store is (App Settings > Storage) -----------------------
        // The TensorRT runtime, its GPU kernels and TensorRT-RTX's runtime are in the per-user pack
        // store that every JaNai app shares; aji-runtime (StoreLocation) moves it to another drive,
        // back, or resets it when that drive is gone for good. Installed copies that use the store
        // (aji-runtime.json) only: the AppImage has no updater to reinstall with, and a copy from
        // before the store keeps its packs in the install folder. The models and the TensorRT
        // engines stay in the install folder.

        public string AjiRuntimePath => StoreLocation.ExePath(_updateManagerService.InstallDir);

        public static bool UsesPackStore(bool installed, string inferenceDirectory) =>
            installed && File.Exists(RuntimePacks.FilePath(inferenceDirectory));

        private StoreLocation.Info? _storage;
        [IgnoreDataMember]
        public StoreLocation.Info? Storage
        {
            get => _storage;
            set
            {
                this.RaiseAndSetIfChanged(ref _storage, value);
                this.RaisePropertyChanged(nameof(ShowStorage));
                this.RaisePropertyChanged(nameof(StorageFolderText));
                this.RaisePropertyChanged(nameof(StorageMoved));
                this.RaisePropertyChanged(nameof(StorageMissing));
                this.RaisePropertyChanged(nameof(StorageText));
            }
        }

        public bool ShowStorage => _updateManagerService.IsInstalled && Storage is not null;
        public string StorageFolderText => Storage?.Folder ?? "";
        public bool StorageMoved => Storage is { Moved: true, Available: true };
        public bool StorageMissing => Storage is { Missing: true };

        // A run needs the store for TensorRT and TensorRT-RTX (DirectML ships with the app).
        public bool StorageNeededButMissing => StorageMissing && CurrentWorkflow?.DirectMlSelected != true;

        public string StorageMissingText =>
            $"{StoreLocation.MissingText(StorageFolderText)} Reconnect it, or reset the location in App Settings > Storage.";

        public string StorageText => Storage switch
        {
            null => "",
            { Moved: false } => "In the default folder. All your JaNai apps share it.",
            { Available: true } => $"Moved from {Storage.Root}, which now links here. All your JaNai apps share it.",
            _ => "This folder isn't available. Is its drive connected? Until it is, upscaling with TensorRT can't start.",
        };

        private bool _storageBusy;
        [IgnoreDataMember]
        public bool StorageBusy
        {
            get => _storageBusy;
            set
            {
                this.RaiseAndSetIfChanged(ref _storageBusy, value);
                this.RaisePropertyChanged(nameof(AllowStorageChanges));
            }
        }

        public bool AllowStorageChanges => !StorageBusy && AllowReinstall;

        private string _storageStatusText = string.Empty;
        [IgnoreDataMember]
        public string StorageStatusText
        {
            get => _storageStatusText;
            set => this.RaiseAndSetIfChanged(ref _storageStatusText, value);
        }

        public async Task RefreshStorage()
        {
            if (!UsesPackStore(_updateManagerService.IsInstalled, _pythonService.InferenceDirectory))
            {
                return;
            }
            var wasMissing = StorageMissing;
            Storage = await StoreLocation.GetAsync(AjiRuntimePath);
            if (wasMissing != StorageMissing)
            {
                CurrentWorkflow?.Validate();
            }
        }

        // The store's size, for the confirmations.
        public long StorageBytes() => Storage is { } s ? StoreLocation.Size(s.Folder) : 0;

        public Task MoveStorage(string folder) => ChangeStorage(["move", StoreLocation.MoveTarget(folder)], "Moving the shared runtime");

        public Task MoveStorageBack() => ChangeStorage(["move", "--default"], "Moving the shared runtime back");

        // Reset: the store's folder is gone, so the default folder starts empty and this app's
        // components are installed again, as Reinstall components does (the other apps reinstall
        // theirs when they next need them).
        public async Task ResetStorage()
        {
            if (await ChangeStorage(["reset"], "Resetting the storage location") && Storage is { Moved: false })
            {
                StorageStatusText = "Reset. Downloading this app's components again...";
                await ReinstallBackend();
                StorageStatusText = "Done.";
            }
        }

        private async Task<bool> ChangeStorage(string[] args, string doing)
        {
            if (!AllowStorageChanges)
            {
                return false;
            }
            StorageBusy = true;
            ComponentsBusy = true; // no component installs meanwhile
            StorageStatusText = $"{doing}...";
            string? error;
            try
            {
                error = await Task.Run(() => StoreLocation.ChangeAsync(AjiRuntimePath, args, (done, total) =>
                    StorageStatusText = total > 0 ? $"{doing}: {done / 1e9:0.0} of {total / 1e9:0.0} GB" : $"{doing}..."));
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            StorageStatusText = error ?? "Done.";
            ComponentsBusy = false;
            StorageBusy = false;
            await RefreshStorage();
            return error is null;
        }

        public async Task RefreshComponents()
        {
            try
            {
                var gpusArgument = ComponentGpusArgument;
                var json = await _updateManagerService.GetComponentsJsonAsync(gpusArgument);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }
                var root = Newtonsoft.Json.Linq.JObject.Parse(json);
                // gpu: the NVIDIA GPUs the packs are for (every one, or the chosen one);
                // nvidia_present: whether this PC has one at all (older updaters: same as gpu.nvidia)
                var gpu = root["gpu"];
                bool nvidia = (bool?)gpu?["nvidia"] ?? false;
                bool nvidiaPresent = (bool?)root["nvidia_present"] ?? nvidia;
                var gpuName = (string?)gpu?["name"] ?? "";
                var sm = (string?)gpu?["sm"] ?? "";
                GpuStatusText = nvidia ? $"{gpuName} ({sm})"
                    : ChosenGpu is EncoderProbe.Gpu chosen && DirectMlBackendSelected ? $"{chosen.Name}, on DirectML: no components needed."
                    : "No NVIDIA GPU detected — using the built-in DirectML engine.";

                var items = new AvaloniaList<ComponentItem>();
                foreach (var p in (Newtonsoft.Json.Linq.JArray?)root["packs"] ?? new Newtonsoft.Json.Linq.JArray())
                {
                    var name = (string?)p["name"] ?? "";
                    var installed = (bool?)p["installed"] ?? false;
                    var recommended = (bool?)p["recommended"] ?? false;
                    var preselect = (bool?)p["preselect"] ?? false;
                    // Only surface packs relevant to THIS machine: what it uses (recommended), already
                    // has (installed, so it can be removed), is offered by default (preselect), or RIFE
                    // (always a user choice). Per-SM kernel packs for other GPU generations and the
                    // TensorRT stack on non-NVIDIA boxes are hidden — the updater CLI still lists them all.
                    // TensorRT-RTX is recommended only on the GPUs where it's the default (RTX 20 /
                    // GTX 16-series and newer), so it is offered here to every NVIDIA GPU on Windows.
                    bool optIn = name == "trt-rtx" && nvidiaPresent && UpscaleWorkflow.TensorRtRtxAvailable;
                    if (!installed && !recommended && !preselect && name != "rife" && !optIn)
                    {
                        continue;
                    }
                    items.Add(new ComponentItem(name, (long?)p["bytes"] ?? 0, installed, recommended, preselect));
                }
                Components = items;
                ComponentsListedFor = gpusArgument;
            }
            catch (Exception ex)
            {
                ComponentStatusText = $"Could not read components: {ex.Message}";
            }
        }

        public async Task InstallComponent(ComponentItem item)
        {
            if (item is null || ComponentsBusy)
            {
                return;
            }
            ComponentsBusy = true;
            ComponentStatusText = $"Installing {item.Name}...";
            try
            {
                await _updateManagerService.RunUpdaterStreamingAsync($"--install {item.Name}", line => ComponentStatusText = line);
            }
            catch (Exception ex) { ComponentStatusText = ex.Message; }
            ComponentsBusy = false;
            await RefreshComponents();
            // the selected backend's components may have arrived or gone (and an unchosen NVIDIA
            // backend follows whether trt-rtx is installed): re-check the selection and what it needs
            BackendChanged();
        }

        public async Task RemoveComponent(ComponentItem item)
        {
            if (item is null || ComponentsBusy)
            {
                return;
            }
            ComponentsBusy = true;
            ComponentStatusText = $"Removing {item.Name}...";
            try
            {
                await _updateManagerService.RunUpdaterStreamingAsync($"--remove {item.Name}", line => ComponentStatusText = line);
            }
            catch (Exception ex) { ComponentStatusText = ex.Message; }
            ComponentsBusy = false;
            await RefreshComponents();
            // the selected backend's components may have arrived or gone (and an unchosen NVIDIA
            // backend follows whether trt-rtx is installed): re-check the selection and what it needs
            BackendChanged();
        }

        // Called once for the app's state (not every view model instance): check now, then
        // periodically, downloading in the background while Auto Update is on.
        public void StartUpdateChecks()
        {
            if (_updateChecksStarted || !_updateManagerService.IsInstalled)
            {
                return;
            }
            _updateChecksStarted = true;
            if (_updateManagerService.TakeLastUpdateError() is string error)
            {
                _installError = $"The last update failed to install: {error}";
            }
            bool autoUpdate = AutoUpdateEnabled;
            this.WhenAnyValue(x => x.AutoUpdateEnabled).Subscribe(on =>
            {
                if (on == autoUpdate)
                {
                    return; // the current value, delivered on subscribe
                }
                autoUpdate = on;
                if (on)
                {
                    StartUpdateDownload();
                }
                else
                {
                    CancelUpdateDownload();
                }
                UpdateStatus();
            });
            DispatcherTimer.Run(() =>
            {
                _ = CheckForUpdates();
                return true;
            }, UpdateCheckInterval);
            _ = CheckForUpdates();
        }

        public async Task CheckForUpdates()
        {
            try
            {
                if (_updateManagerService.IsInstalled)
                {
                    _availableUpdateVersion = await _updateManagerService.CheckForUpdateAsync().ConfigureAwait(true);
                    if (_readyUpdateVersion != _availableUpdateVersion)
                    {
                        _readyUpdateVersion = null; // a newer release replaced the staged one
                    }
                    UpdateStatus();
                    StartUpdateDownload();
                }
            }
            catch (Exception ex)
            {
                UpdateStatusText = $"Check for update failed: {ex.Message}";
            }
        }

        private void StartUpdateDownload()
        {
            if (!AutoUpdateEnabled || _availableUpdateVersion is null || _readyUpdateVersion == _availableUpdateVersion ||
                _updateDownloadCts != null)
            {
                return;
            }
            var cts = _updateDownloadCts = new CancellationTokenSource();
            _updateDownloadProgress = "";
            UpdateStatus();
            _ = Task.Run(async () =>
            {
                string? ready = null;
                string? failure = null;
                string lastLine = "";
                try
                {
                    ready = await _updateManagerService.DownloadUpdateAsync(line =>
                    {
                        lastLine = line;
                        var percent = Regex.Match(line, @"(\d+)%$");
                        var progress = percent.Success ? $"{percent.Groups[1].Value}%"
                            : line.StartsWith("Extracting") ? "unpacking" : null;
                        if (progress != null)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                _updateDownloadProgress = progress;
                                UpdateStatus();
                            });
                        }
                    }, cts.Token);
                    if (ready is null)
                    {
                        failure = lastLine;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    failure = ex.Message;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    if (_updateDownloadCts == cts)
                    {
                        _updateDownloadCts = null;
                    }
                    cts.Dispose();
                    if (ready != null)
                    {
                        _readyUpdateVersion = ready;
                        _downloadError = null;
                    }
                    else if (failure != null)
                    {
                        _downloadError = $"Downloading the update failed: {failure}";
                    }
                    UpdateStatus();
                });
            });
        }

        private void CancelUpdateDownload()
        {
            _updateDownloadCts?.Cancel();
            _updateDownloadCts = null;
        }

        // The bundled updater downloads (unless already staged) + applies in one step, then relaunches.
        // Launch it and exit so it can replace files.
        public void ApplyUpdate()
        {
            CancelUpdateDownload();
            ShowApplyButton = false;
            UpdateStatusText = "Updating and restarting...";
            _updateManagerService.ApplyUpdateAndRestart();
            Environment.Exit(0);
        }

        // On close: install an update the background download has staged.
        public void OnAppExit()
        {
            CancelUpdateDownload();
            if (AutoUpdateEnabled && _readyUpdateVersion != null)
            {
                _updateManagerService.ApplyUpdateOnExit();
            }
        }

        private void UpdateStatus()
        {
            string status;
            if (string.IsNullOrEmpty(_availableUpdateVersion))
            {
                status = "No updates found";
            }
            else if (_updateDownloadCts != null)
            {
                status = string.IsNullOrEmpty(_updateDownloadProgress)
                    ? $"Downloading update {_availableUpdateVersion}..."
                    : $"Downloading update {_availableUpdateVersion} ({_updateDownloadProgress})...";
            }
            else if (_readyUpdateVersion == _availableUpdateVersion && AutoUpdateEnabled)
            {
                status = $"Version {_availableUpdateVersion} is ready and will install when you close VideoJaNai.";
            }
            else
            {
                status = $"Update available: {_availableUpdateVersion}";
            }
            UpdateStatusText = string.Join("\n", new[] { _installError, _downloadError, status }.Where(x => x != null));
            ShowApplyButton = !string.IsNullOrEmpty(_availableUpdateVersion);
            ShowCheckUpdateButton = !ShowApplyButton;
        }
    }

    // A downloadable runtime component pack, surfaced in the App Settings component manager.
    public class ComponentItem
    {
        public ComponentItem(string name, long bytes, bool installed, bool recommended, bool preselect)
        {
            Name = name;
            Bytes = bytes;
            Installed = installed;
            Recommended = recommended;
            Preselect = preselect;
        }

        public string Name { get; }
        public long Bytes { get; }
        public bool Installed { get; }
        public bool Recommended { get; }
        public bool Preselect { get; }

        // User-facing label for the raw pack id (mirrors the AnimeJaNai Manager). Only the pack(s)
        // relevant to this machine reach the UI, so the SM family shown is the user's own GPU.
        public string Title => TitleFor(Name);

        public static string TitleFor(string name) => name switch
        {
            "trt-runtime" => "TensorRT runtime",
            "trt-rtx" => "TensorRT-RTX",
            "rife" => "RIFE interpolation models",
            "trt-ptx" => "TensorRT kernels: other NVIDIA GPUs",
            _ when name.StartsWith("trt-sm") => $"TensorRT kernels: {SmFamily(name[6..])}",
            _ => name,
        };

        public string Description => Name switch
        {
            "trt-runtime" => "The fastest upscaling engine, for NVIDIA GPUs. Without it, upscaling falls back to the slower DirectML engine.",
            "trt-rtx" => "TensorRT-RTX backend: builds engines in seconds instead of minutes, without trtexec. The default on GeForce RTX 20 / GTX 16 series and newer, where it's installed automatically. Needs the TensorRT runtime too, which it falls back to.",
            "rife" => "Frame interpolation (e.g. 24 → 48 fps). Not needed if you only upscale.",
            "trt-ptx" => "Fallback kernels for NVIDIA GPUs without a dedicated kernel pack. The first engine build is slower.",
            _ when Name.StartsWith("trt-sm") => "Engine-builder kernels matched to your GPU generation.",
            _ => "",
        };

        // CUDA compute capability (sm) -> consumer GPU family.
        private static string SmFamily(string sm) => sm switch
        {
            "75" => "GeForce RTX 20 series (Turing)",
            "80" or "86" => "GeForce RTX 30 series (Ampere)",
            "89" => "GeForce RTX 40 series (Ada)",
            "90" => "Hopper",
            "100" or "120" => "GeForce RTX 50 series (Blackwell)",
            _ => $"sm{sm}",
        };

        public string SizeText => $"{Bytes / 1048576} MB";
        public string StateText => Installed ? "installed" : Recommended ? "recommended" : "available";
        public bool CanInstall => !Installed;
        public bool CanRemove => Installed;
    }

    [DataContract]
    public class UpscaleWorkflow : ReactiveObject
    {
        public UpscaleWorkflow()
        {
            this.WhenAnyValue(
                x => x.InputFilePath,
                x => x.OutputFilename,
                x => x.InputFolderPath,
                x => x.OutputFolderPath,
                x => x.SelectedTabIndex,
                x => x.OverwriteExistingVideos
            ).Subscribe(x =>
            {
                Validate();
            });

            this.WhenAnyValue(x => x.Vm).Subscribe(vm =>
            {
                sub?.Dispose();
                sub = null;
                // Vm is null until App wires it up after loading (and while deserializing);
                // ReactiveUI 26's generated WhenAnyValue throws on a null source.
                if (vm is null)
                {
                    return;
                }
                sub = vm.WhenAnyValue(
                    x => x.SelectedWorkflowIndex,
                    x => x.RequestShowAppSettings
                    ).Subscribe(x =>
                    {
                        this.RaisePropertyChanged(nameof(ActiveWorkflow));
                        Vm?.RaisePropertyChanged("Workflows");
                    });
            });

            // The collapsed sections' summaries follow every setting (and the models' paths).
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not null && !e.PropertyName.EndsWith("Summary", StringComparison.Ordinal))
                {
                    RaiseSummaries();
                }
            };
            HookModels(_upscaleSettings);

            this.WhenAnyValue(x => x.InputFilePath).Subscribe(x =>
            {
                if (string.IsNullOrWhiteSpace(OutputFolderPath) && !string.IsNullOrWhiteSpace(InputFilePath))
                {
                    try
                    {
                        OutputFolderPath = Directory.GetParent(InputFilePath)?.ToString() ?? "";
                    }
                    catch (Exception)
                    {

                    }
                }
            });

            this.WhenAnyValue(x => x.InputFolderPath).Subscribe(x =>
            {
                if (string.IsNullOrWhiteSpace(OutputFolderPath) && !string.IsNullOrWhiteSpace(InputFolderPath))
                {
                    try
                    {
                        OutputFolderPath = $"{InputFolderPath} animejanai";
                    }
                    catch (Exception)
                    {

                    }
                }
            });
        }

        private IDisposable? sub;

        private MainWindowViewModel? _vm;
        public MainWindowViewModel? Vm
        {
            get => _vm;
            set => this.RaiseAndSetIfChanged(ref _vm, value);
        }

        private string _workflowName;
        [DataMember]
        public string WorkflowName
        {
            get => _workflowName;
            set => this.RaiseAndSetIfChanged(ref _workflowName, value);
        }

        private int _workflowIndex;
        [DataMember]
        public int WorkflowIndex
        {
            get => _workflowIndex;
            set => this.RaiseAndSetIfChanged(ref _workflowIndex, value);

        }

        public string WorkflowIcon => $"Numeric{WorkflowIndex + 1}Circle";

        public bool ActiveWorkflow
        {
            get
            {
                Debug.WriteLine($"ActiveWorkflow {WorkflowIndex} == {Vm?.SelectedWorkflowIndex}; {Vm == null}");
                return WorkflowIndex == Vm?.SelectedWorkflowIndex && (!Vm?.ShowAppSettings ?? false);
            }

        }

        private bool _valid = false;
        [IgnoreDataMember]
        public bool Valid
        {
            get => _valid;
            set
            {
                this.RaiseAndSetIfChanged(ref _valid, value);
                if (Vm != null)
                {
                    Vm.RaisePropertyChanged(nameof(Vm.UpscaleEnabled));
                    Vm.RaisePropertyChanged(nameof(Vm.LeftStatus));
                }
            }
        }

        private int _selectedTabIndex;
        [DataMember]
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (_selectedTabIndex != value)
                {
                    this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
                }
            }
        }

        // The encoder settings as saved: empty (or the old bundled default, LegacyDefaultSettings)
        // until someone picks a preset or edits them, which means "this PC's default encoder"
        // (MainWindowViewModel.DefaultEncoderPreset), so it follows the GPU that does the upscale.
        // A pick is never changed for them: one that doesn't work here is reported by Validate.
        private string _ffmpegVideoSettings = string.Empty;
        [DataMember]
        public string FfmpegVideoSettings
        {
            get => _ffmpegVideoSettings;
            set
            {
                this.RaiseAndSetIfChanged(ref _ffmpegVideoSettings, value ?? string.Empty);
                EncodersChanged();
            }
        }

        public bool UsesDefaultEncoder =>
            string.IsNullOrWhiteSpace(FfmpegVideoSettings) ||
            FfmpegVideoSettings.Trim() == EncoderPresets.LegacyDefaultSettings;

        // The settings a run takes, as Core's plain copy (what the command line reads from a saved
        // state too), so both run a workflow the same way.
        public WorkflowSettings ToSettings() => new()
        {
            WorkflowName = WorkflowName,
            SelectedTabIndex = SelectedTabIndex,
            InputFilePath = InputFilePath,
            InputFolderPath = InputFolderPath,
            OutputFilename = OutputFilename,
            OutputFolderPath = OutputFolderPath,
            OverwriteExistingVideos = OverwriteExistingVideos,
            FfmpegVideoSettings = FfmpegVideoSettings,
            Deinterlace = Deinterlace,
            SquarePixels = SquarePixels,
            OutputPixFmt = OutputPixFmt,
            TrtEngineSettings = TrtEngineSettings,
            FinalResizeHeight = FinalResizeHeight,
            FinalResizeFactor = FinalResizeFactor,
            UpscaleSettings = [.. UpscaleSettings.Select(m => new ModelSettings
            {
                OnnxModelPath = m.OnnxModelPath,
                ResizeHeightBeforeUpscale = m.ResizeHeightBeforeUpscale,
                ResizeFactorBeforeUpscale = m.ResizeFactorBeforeUpscale,
                TemporalSceneThreshold = m.TemporalSceneThreshold,
            })],
            EnableUpscale = EnableUpscale,
            EnableTemporalFix = EnableTemporalFix,
            TemporalFixStrength = TemporalFixStrength,
            TemporalFixSceneThreshold = TemporalFixSceneThreshold,
            TemporalFixBeforeUpscale = TemporalFixBeforeUpscale,
            EnableRife = EnableRife,
            RifeModel = RifeModel,
            RifeEnsemble = RifeEnsemble,
            RifeFactorNumerator = RifeFactorNumerator,
            RifeFactorDenominator = RifeFactorDenominator,
            RifeSceneDetectThreshold = RifeSceneDetectThreshold,
            RifeBeforeUpscale = RifeBeforeUpscale,
        };

        // what aji_encode gets
        public string EffectiveFfmpegVideoSettings =>
            UsesDefaultEncoder ? (Vm?.DefaultEncoderPreset ?? EncoderPresets.NvencHevc).Settings : FfmpegVideoSettings;

        // The settings box: shows the effective settings; typing in it makes them this workflow's own.
        [IgnoreDataMember]
        public string FfmpegVideoSettingsText
        {
            get => EffectiveFfmpegVideoSettings;
            set
            {
                if (value != EffectiveFfmpegVideoSettings)
                {
                    FfmpegVideoSettings = value;
                    Validate();
                }
            }
        }

        // null: custom settings
        public EncoderPreset? SelectedPreset => EncoderPresets.Find(EffectiveFfmpegVideoSettings);

        public string SelectedCodec => EncoderPresets.CodecOf(EffectiveFfmpegVideoSettings);

        public EncoderGroup SelectedEncoderGroup => SelectedPreset?.Group ?? EncoderPresets.GroupOf(SelectedCodec);

        private bool SelectedEncoderIsGpu => SelectedEncoderGroup != EncoderGroup.Cpu;

        // Row 1: where it's encoded. Lists what Vm.GroupListed allows, plus the current selection
        // so a choice that doesn't apply here (another PC's GPU, a switched backend) stays visible.
        public IReadOnlyList<EncoderChoice> EncoderGroupChoices =>
            new[] { EncoderGroup.Nvidia, EncoderGroup.Amd, EncoderGroup.Intel, EncoderGroup.Cpu }
                .Where(g => g == SelectedEncoderGroup || (Vm?.GroupListed(g) ?? g is EncoderGroup.Nvidia or EncoderGroup.Cpu))
                .Select(g => new EncoderChoice(EncoderPresets.GroupLabel(g), null, g == SelectedEncoderGroup, () => SelectEncoderGroup(g)))
                .ToList();

        // Row 2: the selected encoder's codecs that work here (plus the selected one).
        public IReadOnlyList<EncoderChoice> CodecChoices =>
            EncoderPresets.For(SelectedEncoderGroup)
                .Where(p => p == SelectedPreset || (Vm?.PresetAvailable(p) ?? !p.IsGpu || p == EncoderPresets.NvencHevc))
                .Select(p => new EncoderChoice(p.Label, p.Description(), p == SelectedPreset, () => SelectPreset(p)))
                .ToList();

        public bool ShowOtherGpuEncodersOption => Vm?.HasOtherGpuEncoders ?? false;

        public void SelectPreset(EncoderPreset preset)
        {
            FfmpegVideoSettings = preset.Settings;
            // Picking a GPU encoder moves a format it can't encode to the nearest one it can
            // (only on this click, never on its own later).
            if (preset.IsGpu && OutputIs444)
            {
                OutputPixFmt = OutputPixFmt == "yuv444p10" ? "yuv420p10" : "yuv420p";
            }
            if (preset.IsGpu && OutputPixFmt == "yuv420p10" && Vm?.Probe is EncoderProbe probe &&
                !probe.Opens(preset.Codec, tenBit: true) && probe.Opens(preset.Codec, tenBit: false))
            {
                OutputPixFmt = "yuv420p";
            }
            Validate();
        }

        // Switch encoder, keeping the kind of codec (HEVC, AV1, lossless) when the new one has it.
        public void SelectEncoderGroup(EncoderGroup group)
        {
            if (group == SelectedEncoderGroup && SelectedPreset != null)
            {
                return;
            }
            var family = SelectedPreset is EncoderPreset current ? EncoderPresets.Family(current) : "hevc";
            var options = EncoderPresets.For(group).Where(p => Vm?.PresetAvailable(p) ?? true).ToList();
            var pick = options.FirstOrDefault(p => EncoderPresets.Family(p) == family)
                       ?? options.FirstOrDefault()
                       ?? EncoderPresets.For(group).First();
            SelectPreset(pick);
        }

        // Format buttons a GPU encoder can't use are disabled (Validate also reports a saved one).
        public bool Output444Enabled => !SelectedEncoderIsGpu;
        public bool Output10BitEnabled => Vm?.Probe is not EncoderProbe probe || probe.Opens(SelectedCodec, tenBit: true);
        public bool Output444P10Enabled => Output444Enabled && Output10BitEnabled;

        public void EncodersChanged()
        {
            this.RaisePropertyChanged(nameof(UsesDefaultEncoder));
            this.RaisePropertyChanged(nameof(EffectiveFfmpegVideoSettings));
            this.RaisePropertyChanged(nameof(FfmpegVideoSettingsText));
            this.RaisePropertyChanged(nameof(SelectedPreset));
            this.RaisePropertyChanged(nameof(SelectedCodec));
            this.RaisePropertyChanged(nameof(SelectedEncoderGroup));
            this.RaisePropertyChanged(nameof(EncoderGroupChoices));
            this.RaisePropertyChanged(nameof(CodecChoices));
            this.RaisePropertyChanged(nameof(ShowOtherGpuEncodersOption));
            this.RaisePropertyChanged(nameof(Output444Enabled));
            this.RaisePropertyChanged(nameof(Output10BitEnabled));
            this.RaisePropertyChanged(nameof(Output444P10Enabled));
            this.RaisePropertyChanged(nameof(OutputSummary));
            // Validate writes the window's status text, so only the shown workflow's
            if (Vm != null && Vm.CurrentWorkflow == this)
            {
                Validate();
            }
        }

        // Deinterlacing of the source -> aji_encode --deinterlace: off, ivtc (telecined film, e.g.
        // NTSC anime DVDs: restores the 23.976 fps frames) or bwdif (true interlaced video,
        // deinterlaced to one frame per field).
        public bool DeinterlaceOffSelected => Deinterlace == "off";
        public bool DeinterlaceIvtcSelected => Deinterlace == "ivtc";
        public bool DeinterlaceBwdifSelected => Deinterlace == "bwdif";

        private string _deinterlace = "off";
        [DataMember]
        public string Deinterlace
        {
            get => _deinterlace ?? "off";
            set
            {
                this.RaiseAndSetIfChanged(ref _deinterlace, value ?? "off");
                this.RaisePropertyChanged(nameof(DeinterlaceOffSelected));
                this.RaisePropertyChanged(nameof(DeinterlaceIvtcSelected));
                this.RaisePropertyChanged(nameof(DeinterlaceBwdifSelected));
            }
        }

        public void SetDeinterlaceOff() => Deinterlace = "off";
        public void SetDeinterlaceIvtc() => Deinterlace = "ivtc";
        public void SetDeinterlaceBwdif() => Deinterlace = "bwdif";

        // Non-square pixels (DVD, DV) stretched to square -> aji_encode --square-pixels: off,
        // before or after processing (upscale / interpolation). Which order looks better depends
        // on the model, so it's the person's choice; the preview compares them.
        public bool SquarePixelsOffSelected => SquarePixels == "off";
        public bool SquarePixelsBeforeSelected => SquarePixels == "before";
        public bool SquarePixelsAfterSelected => SquarePixels == "after";

        private string _squarePixels = "off";
        [DataMember]
        public string SquarePixels
        {
            get => _squarePixels ?? "off";
            set
            {
                this.RaiseAndSetIfChanged(ref _squarePixels, value ?? "off");
                this.RaisePropertyChanged(nameof(SquarePixelsOffSelected));
                this.RaisePropertyChanged(nameof(SquarePixelsBeforeSelected));
                this.RaisePropertyChanged(nameof(SquarePixelsAfterSelected));
            }
        }

        public void SetSquarePixelsOff() => SquarePixels = "off";
        public void SetSquarePixelsBefore() => SquarePixels = "before";
        public void SetSquarePixelsAfter() => SquarePixels = "after";

        // Output pixel format -> aji_encode --pix-fmt (chroma + bit depth), decoupled from source.
        public bool OutputPixFmt420P8Selected => OutputPixFmt == "yuv420p";
        public bool OutputPixFmt420P10Selected => OutputPixFmt == "yuv420p10";
        public bool OutputPixFmt444P8Selected => OutputPixFmt == "yuv444p";
        public bool OutputPixFmt444P10Selected => OutputPixFmt == "yuv444p10";
        public bool OutputIs444 => OutputPixFmt.StartsWith("yuv444", StringComparison.Ordinal);

        private string _outputPixFmt = "yuv420p10";
        [DataMember]
        public string OutputPixFmt
        {
            get => _outputPixFmt;
            set
            {
                this.RaiseAndSetIfChanged(ref _outputPixFmt, value);
                this.RaisePropertyChanged(nameof(OutputPixFmt420P8Selected));
                this.RaisePropertyChanged(nameof(OutputPixFmt420P10Selected));
                this.RaisePropertyChanged(nameof(OutputPixFmt444P8Selected));
                this.RaisePropertyChanged(nameof(OutputPixFmt444P10Selected));
            }
        }

        // TensorRT engine-build settings (trtexec args). The raw string is the single source of
        // truth; Engine Type / dynamic resolutions / builder optimization level are computed from it
        // and rewrite it. Precision is model-driven (TRT 11 strongly-typed by default), so there are
        // no fp16/bf16 options. Trimmed to only the flags that actually matter on TRT 11:
        // --stronglyTyped is a deprecated no-op, and libaji's sanitizer strips --inputIOFormats/
        // --outputIOFormats/--tacticSources anyway. Must stay byte-for-byte equal to libaji's
        // DEFAULT_TRT_ENGINE_SETTINGS so write-minimal can omit it from the conf.
        public const string TrtEngineSettingsDefault = TrtEngine.DefaultSettings;

        private string _trtEngineSettings = TrtEngineSettingsDefault;
        [DataMember]
        public string TrtEngineSettings
        {
            get => _trtEngineSettings;
            set
            {
                this.RaiseAndSetIfChanged(ref _trtEngineSettings, value);
                this.RaisePropertyChanged(nameof(TrtStaticOnnxSelected));
                this.RaisePropertyChanged(nameof(TrtStaticSelected));
                this.RaisePropertyChanged(nameof(TrtDynamicSelected));
                this.RaisePropertyChanged(nameof(TrtDynamicMinResolution));
                this.RaisePropertyChanged(nameof(TrtDynamicOptResolution));
                this.RaisePropertyChanged(nameof(TrtDynamicMaxResolution));
                this.RaisePropertyChanged(nameof(TrtBuilderOptimizationLevel));
            }
        }

        public bool TrtDynamicSelected => TrtEngineSettings.Contains("--minShapes=");
        public bool TrtStaticSelected => TrtEngineSettings.Contains("--optShapes=") && !TrtDynamicSelected;
        public bool TrtStaticOnnxSelected => !TrtEngineSettings.Contains("--optShapes=") && !TrtDynamicSelected;

        private static string ShapeToResolution(string shapeArg)
        {
            var match = Regex.Match(shapeArg, @"input:1x3x(\d+)x(\d+)");
            if (match.Success)
                return $"{match.Groups[2].Value}x{match.Groups[1].Value}";
            return "0x0";
        }

        private static string ResolutionToShape(string resolution)
        {
            var match = Regex.Match(resolution, @"(\d+)x(\d+)");
            if (match.Success)
                return $"1x3x{match.Groups[2].Value}x{match.Groups[1].Value}";
            return "1x3x0x0";
        }

        private static string? ExtractShapeValue(string settings, string prefix)
        {
            var match = Regex.Match(settings, prefix + @"=(\S+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        public string TrtDynamicMinResolution
        {
            get
            {
                var val = ExtractShapeValue(TrtEngineSettings, "--minShapes");
                return val != null ? ShapeToResolution(val) : "8x8";
            }
            set
            {
                var shape = ResolutionToShape(value);
                TrtEngineSettings = Regex.Replace(TrtEngineSettings, @"--minShapes=\S+", $"--minShapes=input:{shape}");
            }
        }

        public string TrtDynamicOptResolution
        {
            get
            {
                if (!TrtDynamicSelected) return "1920x1080";
                var val = ExtractShapeValue(TrtEngineSettings, "--optShapes");
                return val != null ? ShapeToResolution(val) : "1920x1080";
            }
            set
            {
                var shape = ResolutionToShape(value);
                if (TrtDynamicSelected)
                    TrtEngineSettings = Regex.Replace(TrtEngineSettings, @"--optShapes=\S+", $"--optShapes=input:{shape}");
            }
        }

        public string TrtDynamicMaxResolution
        {
            get
            {
                var val = ExtractShapeValue(TrtEngineSettings, "--maxShapes");
                return val != null ? ShapeToResolution(val) : "1920x1080";
            }
            set
            {
                var shape = ResolutionToShape(value);
                TrtEngineSettings = Regex.Replace(TrtEngineSettings, @"--maxShapes=\S+", $"--maxShapes=input:{shape}");
            }
        }

        public string TrtBuilderOptimizationLevel
        {
            get
            {
                var match = Regex.Match(TrtEngineSettings, @"--builderOptimizationLevel=(\d+)");
                return match.Success ? match.Groups[1].Value : "5";
            }
            set
            {
                var match = Regex.Match(value ?? "", @"\d+");
                var level = match.Success ? match.Value : "5";
                if (Regex.IsMatch(TrtEngineSettings, @"--builderOptimizationLevel=\d+"))
                    TrtEngineSettings = Regex.Replace(TrtEngineSettings, @"--builderOptimizationLevel=\d+", $"--builderOptimizationLevel={level}");
                else
                    TrtEngineSettings = InsertBeforeTactics(TrtEngineSettings, $"--builderOptimizationLevel={level}");
            }
        }

        private static string RemoveShapeArgs(string settings)
        {
            var result = Regex.Replace(settings, @"\s*--(?:min|opt|max)Shapes=\S+", "");
            return Regex.Replace(result, @"\s+", " ").Trim();
        }

        private static string InsertBeforeTactics(string settings, string toInsert)
        {
            var idx = settings.IndexOf("--tacticSources", StringComparison.Ordinal);
            if (idx >= 0)
                return settings.Insert(idx, toInsert + " ");
            return settings + " " + toInsert;
        }

        // Migration for engine settings saved by older versions: strip flags that are dead on
        // TensorRT 11, preserving everything else (builder level, shapes, --skipInference, custom
        // flags). Mirrors libaji's sanitize_settings_trt11 (which removes these before building) plus
        // --stronglyTyped: strongly-typed is the default now (a trtexec no-op), typed IO formats and
        // the cuDNN/cuBLAS tactic sources are gone, and the weak-typing precision flags were removed.
        // Purely cosmetic — libaji builds the same engine either way — but keeps the editor clean.
        public static string NormalizeTrtEngineSettings(string settings) => TrtEngine.Normalize(settings);

        public void SetTrtStaticOnnx()
        {
            TrtEngineSettings = RemoveShapeArgs(TrtEngineSettings);
        }

        public void SetTrtStatic()
        {
            var settings = RemoveShapeArgs(TrtEngineSettings);
            TrtEngineSettings = InsertBeforeTactics(settings, "--optShapes=input:%video_resolution%");
        }

        public void SetTrtDynamic()
        {
            var settings = RemoveShapeArgs(TrtEngineSettings);
            TrtEngineSettings = InsertBeforeTactics(settings, "--minShapes=input:1x3x8x8 --optShapes=input:1x3x1080x1920 --maxShapes=input:1x3x1080x1920");
        }

        // DirectML is Windows-only; elsewhere a workflow saved with DirectML runs on TensorRT.
        public static bool DirectMlAvailable => Platform.DirectMlAvailable;

        // TensorRT-RTX (aji_trtrtx) ships for Windows only; elsewhere the setting runs on TensorRT.
        public static bool TensorRtRtxAvailable => Platform.TensorRtRtxAvailable;

        // Either TensorRT backend, TensorRT or TensorRT-RTX: both build engines from the engine
        // settings, and TensorRT-RTX falls back to TensorRT. TensorRtRtxSelected tells them apart.
        private bool _tensorRtSelected = true;
        [DataMember]
        public bool TensorRtSelected
        {
            get => !DirectMlSelected;
            set => _tensorRtSelected = value;
        }

        public bool TensorRtRtxSelected => Vm?.TensorRtRtxBackendSelected ?? false;

        private bool _directMlSelected = false;
        [DataMember]
        public bool DirectMlSelected
        {
            get => (Vm?.UseDirectMl ?? _directMlSelected) && DirectMlAvailable;
            set => _directMlSelected = value;
        }

        // what this workflow had before the backend became an App Setting
        public bool LegacyDirectMlSelected => _directMlSelected;

        public void BackendChanged()
        {
            this.RaisePropertyChanged(nameof(TensorRtSelected));
            this.RaisePropertyChanged(nameof(TensorRtRtxSelected));
            this.RaisePropertyChanged(nameof(DirectMlSelected));
            this.RaisePropertyChanged(nameof(ShowTrtEngineSettings));
            Validate();
        }

        private int? _finalResizeHeight = 0;
        [DataMember]
        public int? FinalResizeHeight
        {
            get => _finalResizeHeight;
            set
            {
                this.RaiseAndSetIfChanged(ref _finalResizeHeight, value ?? 0);
                this.RaisePropertyChanged(nameof(EnableFinalResizeFactor));
            }
        }

        private int? _finalResizeFactor = 100;
        [DataMember]
        public int? FinalResizeFactor
        {
            get => _finalResizeFactor ?? 100;
            set => this.RaiseAndSetIfChanged(ref _finalResizeFactor, value ?? 100);
        }

        public bool EnableFinalResizeFactor => FinalResizeHeight == 0;

        private string _inputFilePath = string.Empty;
        [DataMember]
        public string InputFilePath
        {
            get => _inputFilePath;
            set => this.RaiseAndSetIfChanged(ref _inputFilePath, value);
        }

        private string _inputFolderPath = string.Empty;
        [DataMember]
        public string InputFolderPath
        {
            get => _inputFolderPath;
            set => this.RaiseAndSetIfChanged(ref _inputFolderPath, value);
        }

        private string _outputFilename = "%filename%-animejanai.mkv";
        [DataMember]
        public string OutputFilename
        {
            get => _outputFilename;
            set => this.RaiseAndSetIfChanged(ref _outputFilename, value);
        }

        private string _outputFolderPath = string.Empty;
        [DataMember]
        public string OutputFolderPath
        {
            get => _outputFolderPath;
            set => this.RaiseAndSetIfChanged(ref _outputFolderPath, value);
        }

        private bool _overwriteExistingVideos;
        [DataMember]
        public bool OverwriteExistingVideos
        {
            get => _overwriteExistingVideos;
            set
            {

                this.RaiseAndSetIfChanged(ref _overwriteExistingVideos, value);

            }
        }

        private AvaloniaList<UpscaleModel> _upscaleSettings = new();
        [DataMember]
        public AvaloniaList<UpscaleModel> UpscaleSettings
        {
            get => _upscaleSettings;
            set
            {
                this.RaiseAndSetIfChanged(ref _upscaleSettings, value);
                HookModels(value);
            }
        }

        private void HookModels(AvaloniaList<UpscaleModel>? models)
        {
            if (models is null)
            {
                return;
            }
            void Watch(UpscaleModel m) => m.PropertyChanged += (_, _) => RaiseSummaries();
            foreach (var m in models)
            {
                Watch(m);
            }
            models.CollectionChanged += (_, e) =>
            {
                foreach (UpscaleModel m in e.NewItems ?? Array.Empty<UpscaleModel>())
                {
                    Watch(m);
                }
                RaiseSummaries();
            };
        }

        // Upscaling on or off. The models stay in the list while it's off; the chain just has none,
        // so a workflow can interpolate (or only deinterlace, resize, ...) without losing them.
        private bool _enableUpscale = true;
        [DataMember]
        public bool EnableUpscale
        {
            get => _enableUpscale;
            set
            {
                var changed = value != _enableUpscale;
                this.RaiseAndSetIfChanged(ref _enableUpscale, value);
                // a section follows its switch when the person flips it (not while loading)
                if (changed && Vm != null)
                {
                    UpscaleSectionExpanded = value;
                }
                Validate();
            }
        }

        // vs_temporalfix, appended to the chain after the upscaling models (or on its own, at the source
        // resolution, when upscaling is off). Strength picks one of the bundled models.
        private bool _enableTemporalFix = false;
        [DataMember]
        public bool EnableTemporalFix
        {
            get => _enableTemporalFix;
            set
            {
                var changed = value != _enableTemporalFix;
                this.RaiseAndSetIfChanged(ref _enableTemporalFix, value);
                if (changed && Vm != null)
                {
                    TemporalFixSectionExpanded = value;
                }
                Validate();
            }
        }

        // 0 would be a pass-through (vs_temporalfix's own strength 0 returns the clip untouched).
        public static List<int> TemporalFixStrengths { get; } = [.. TemporalFix.Strengths];

        private int? _temporalFixStrength = 2;
        [DataMember]
        public int? TemporalFixStrength
        {
            get => _temporalFixStrength ?? 2;
            set
            {
                this.RaiseAndSetIfChanged(ref _temporalFixStrength, value ?? 2);
                Validate();
            }
        }

        private decimal? _temporalFixSceneThreshold = UpscaleModel.DefaultTemporalSceneThreshold;
        [DataMember]
        public decimal? TemporalFixSceneThreshold
        {
            get => _temporalFixSceneThreshold ?? UpscaleModel.DefaultTemporalSceneThreshold;
            set => this.RaiseAndSetIfChanged(ref _temporalFixSceneThreshold, value ?? UpscaleModel.DefaultTemporalSceneThreshold);
        }

        private bool _enableRife = false;
        [DataMember]
        public bool EnableRife
        {
            get => _enableRife;
            set
            {
                var changed = value != _enableRife;
                this.RaiseAndSetIfChanged(ref _enableRife, value);
                if (changed && Vm != null)
                {
                    InterpolationSectionExpanded = value;
                }
            }
        }

        // The settings page's sections, open or collapsed to their summary line (per workflow).
        public void ExpandAllSections() => SetAllSectionsExpanded(true);
        public void CollapseAllSections() => SetAllSectionsExpanded(false);

        private void SetAllSectionsExpanded(bool expanded)
        {
            InputSectionExpanded = expanded;
            SourceSectionExpanded = expanded;
            UpscaleSectionExpanded = expanded;
            TemporalFixSectionExpanded = expanded;
            InterpolationSectionExpanded = expanded;
            PostProcessingSectionExpanded = expanded;
            OutputSectionExpanded = expanded;
        }

        private bool _inputSectionExpanded = true;
        [DataMember]
        public bool InputSectionExpanded
        {
            get => _inputSectionExpanded;
            set => this.RaiseAndSetIfChanged(ref _inputSectionExpanded, value);
        }

        private bool _sourceSectionExpanded = false;
        [DataMember]
        public bool SourceSectionExpanded
        {
            get => _sourceSectionExpanded;
            set => this.RaiseAndSetIfChanged(ref _sourceSectionExpanded, value);
        }

        private bool _upscaleSectionExpanded = true;
        [DataMember]
        public bool UpscaleSectionExpanded
        {
            get => _upscaleSectionExpanded;
            set => this.RaiseAndSetIfChanged(ref _upscaleSectionExpanded, value);
        }

        private bool _temporalFixSectionExpanded = false;
        [DataMember]
        public bool TemporalFixSectionExpanded
        {
            get => _temporalFixSectionExpanded;
            set => this.RaiseAndSetIfChanged(ref _temporalFixSectionExpanded, value);
        }

        private bool _interpolationSectionExpanded = false;
        [DataMember]
        public bool InterpolationSectionExpanded
        {
            get => _interpolationSectionExpanded;
            set => this.RaiseAndSetIfChanged(ref _interpolationSectionExpanded, value);
        }

        private bool _postProcessingSectionExpanded = false;
        [DataMember]
        public bool PostProcessingSectionExpanded
        {
            get => _postProcessingSectionExpanded;
            set => this.RaiseAndSetIfChanged(ref _postProcessingSectionExpanded, value);
        }

        private bool _outputSectionExpanded = true;
        [DataMember]
        public bool OutputSectionExpanded
        {
            get => _outputSectionExpanded;
            set => this.RaiseAndSetIfChanged(ref _outputSectionExpanded, value);
        }

        private static string Off => VideoJaNai.Lang.Resources.DeinterlaceOffText;

        public string InputSummary => SelectedTabIndex == 0
            ? Path.GetFileName(InputFilePath ?? string.Empty)
            : InputFolderPath ?? string.Empty;

        public string SourceSummary
        {
            get
            {
                var parts = new List<string>();
                if (Deinterlace == "ivtc") parts.Add("IVTC");
                else if (Deinterlace == "bwdif") parts.Add(VideoJaNai.Lang.Resources.DeinterlaceBwdifText);
                if (SquarePixels != "off")
                {
                    parts.Add($"{VideoJaNai.Lang.Resources.SquarePixelsLabel}: " + (SquarePixels == "before"
                        ? VideoJaNai.Lang.Resources.SquarePixelsBeforeText
                        : VideoJaNai.Lang.Resources.SquarePixelsAfterText));
                }
                return parts.Count > 0 ? string.Join(" · ", parts) : Off;
            }
        }

        public string UpscaleSummary => !EnableUpscale || UpscaleSettings.Count == 0
            ? Off
            : string.Join(" → ", UpscaleSettings.Select(m => Path.GetFileNameWithoutExtension(m.OnnxModelPath ?? string.Empty)));

        public string TemporalFixSummary => !EnableTemporalFix
            ? Off
            : string.Format(CultureInfo.CurrentCulture, VideoJaNai.Lang.Resources.TemporalFixSummaryText, TemporalFixStrength)
              + (EnableUpscale && TemporalFixBeforeUpscale ? " · " + VideoJaNai.Lang.Resources.TemporalFixBeforeUpscaleSummaryText : "");

        public string InterpolationSummary => !EnableRife
            ? Off
            : RifeFactorDenominator == 1
                ? $"{RifeModel} · {RifeFactorNumerator}x"
                : $"{RifeModel} · {RifeFactorNumerator}/{RifeFactorDenominator}x";

        public string OutputSummary
        {
            get
            {
                var parts = new List<string>
                {
                    SelectedPreset?.SummaryLabel ?? VideoJaNai.Lang.Resources.CustomPresetText,
                    OutputPixFmt switch
                    {
                        "yuv420p" => "8-bit 4:2:0",
                        "yuv444p" => "8-bit 4:4:4",
                        "yuv444p10" => "10-bit 4:4:4",
                        _ => "10-bit 4:2:0",
                    },
                };
                return string.Join(" · ", parts);
            }
        }

        public string PostProcessingSummary =>
            FinalResizeHeight is int h && h > 0 ? $"{h}p"
            : FinalResizeFactor is int f && f > 0 && f != 100 ? $"{f}%"
            : Off;

        private void RaiseSummaries()
        {
            this.RaisePropertyChanged(nameof(InputSummary));
            this.RaisePropertyChanged(nameof(SourceSummary));
            this.RaisePropertyChanged(nameof(UpscaleSummary));
            this.RaisePropertyChanged(nameof(TemporalFixSummary));
            this.RaisePropertyChanged(nameof(InterpolationSummary));
            this.RaisePropertyChanged(nameof(PostProcessingSummary));
            this.RaisePropertyChanged(nameof(OutputSummary));
        }

        public List<string> RifeModelList { get => Vm?.RifeModels; }

        private string _rifeModel = "RIFE 4.22";
        [DataMember]
        public string RifeModel
        {
            get => _rifeModel;
            set => this.RaiseAndSetIfChanged(ref _rifeModel, value);
        }

        private bool _rifeEnsemble = false;
        [DataMember]
        public bool RifeEnsemble
        {
            get => _rifeEnsemble;
            set => this.RaiseAndSetIfChanged(ref _rifeEnsemble, value);
        }

        private int? _rifeFactorNumerator = 2;
        [DataMember]
        public int? RifeFactorNumerator
        {
            get => _rifeFactorNumerator ?? 2;
            set => this.RaiseAndSetIfChanged(ref _rifeFactorNumerator, value ?? 2);
        }

        private int? _rifeFactorDenominator = 1;
        [DataMember]
        public int? RifeFactorDenominator
        {
            get => _rifeFactorDenominator ?? 1;
            set => this.RaiseAndSetIfChanged(ref _rifeFactorDenominator, value ?? 1);
        }

        private decimal? _rifeSceneDetectThreshold = 0.150M;
        [DataMember]
        public decimal? RifeSceneDetectThreshold
        {
            get => _rifeSceneDetectThreshold;
            set => this.RaiseAndSetIfChanged(ref _rifeSceneDetectThreshold, value ?? 0.150M);
        }

        // Interpolate at the source resolution, then upscale (faster); the engine's default. False =
        // interpolate the upscaled frames (the old order). Chains with a temporal model always
        // interpolate after upscaling.
        private bool _rifeBeforeUpscale = true;
        [DataMember]
        public bool RifeBeforeUpscale
        {
            get => _rifeBeforeUpscale;
            set => this.RaiseAndSetIfChanged(ref _rifeBeforeUpscale, value);
        }

        // Run temporal fix at the source resolution before upscaling (2-3x faster) instead of on the
        // upscaled frames (highest quality, the default: an encode has no realtime budget).
        private bool _temporalFixBeforeUpscale = false;
        [DataMember]
        public bool TemporalFixBeforeUpscale
        {
            get => _temporalFixBeforeUpscale;
            set => this.RaiseAndSetIfChanged(ref _temporalFixBeforeUpscale, value);
        }

        private bool _showAdvancedSettings = false;
        [DataMember]
        public bool ShowAdvancedSettings
        {
            get => _showAdvancedSettings;
            set
            {
                this.RaiseAndSetIfChanged(ref _showAdvancedSettings, value);
                this.RaisePropertyChanged(nameof(ShowTrtEngineSettings));
            }
        }

        // Engine-settings UI is for the TensorRT backends (TensorRT-RTX too) and lives under Advanced
        // settings.
        public bool ShowTrtEngineSettings => ShowAdvancedSettings && TensorRtSelected;

        public void AddModel()
        {
            UpscaleSettings.Add(new UpscaleModel());
            UpdateModelHeaders();
        }

        public void DeleteModel(UpscaleModel model)
        {
            try
            {
                UpscaleSettings.Remove(model);
            }
            catch (ArgumentOutOfRangeException)
            {

            }

            UpdateModelHeaders();
        }

        private void UpdateModelHeaders()
        {
            for (var i = 0; i < UpscaleSettings.Count; i++)
            {
                UpscaleSettings[i].ModelHeader = $"Model {i + 1}";
            }
        }

        public void SetOutputPixFmt420P8() { OutputPixFmt = "yuv420p"; Validate(); }
        public void SetOutputPixFmt420P10() { OutputPixFmt = "yuv420p10"; Validate(); }
        public void SetOutputPixFmt444P8() { OutputPixFmt = "yuv444p"; Validate(); }
        public void SetOutputPixFmt444P10() { OutputPixFmt = "yuv444p10"; Validate(); }

        public void Validate()
        {
            var valid = true;
            var validationText = new List<string>();
            if (SelectedTabIndex == 0)
            {
                if (!File.Exists(InputFilePath))
                {
                    valid = false;
                    validationText.Add("Input Video is required.");
                }
            }
            else
            {
                if (!Directory.Exists(InputFolderPath))
                {
                    valid = false;
                    validationText.Add("Input Folder is required.");
                }
            }

            if (string.IsNullOrWhiteSpace(OutputFolderPath))
            {
                valid = false;
                validationText.Add("Output Folder is required.");
            }

            if (string.IsNullOrWhiteSpace(OutputFilename))
            {
                valid = false;
                validationText.Add("Output Filename is required.");
            }

            foreach (var upscaleModel in EnableUpscale ? UpscaleSettings : [])
            {
                if (!File.Exists(upscaleModel.OnnxModelPath))
                {
                    valid = false;
                    validationText.Add("ONNX Model Path is required.");
                }
            }

            if (EnableTemporalFix && Vm != null && Vm.TemporalFixModelPath(TemporalFixStrength ?? 2) == null)
            {
                valid = false;
                validationText.Add(VideoJaNai.Lang.Resources.TemporalFixModelMissingText);
            }

            // The encoder: one the probe found unusable here (a workflow from another PC, a GPU
            // that isn't installed), a bit depth it can't encode, or 4:4:4 on any GPU encoder.
            foreach (var problem in EncodeChecks.EncoderProblems(Vm?.Probe, EffectiveFfmpegVideoSettings, OutputPixFmt))
            {
                valid = false;
                validationText.Add(problem);
            }

            if (!DirectMlSelected && Vm?.StorageMissing == true)
            {
                valid = false;
                validationText.Add(Vm.StorageMissingText);
            }
            else if (TensorRtRtxSelected && Vm?.TensorRtRtxComponentsMissingText() is string rtxMissing)
            {
                valid = false;
                validationText.Add(rtxMissing);
            }
            else if (TensorRtSelected && Vm != null && !Vm.TensorRtInstalled)
            {
                valid = false;
                validationText.Add(DirectMlAvailable
                    ? "The TensorRT components are not installed (they are for NVIDIA GPUs). Select DirectML or install TensorRT in App Settings."
                    : "The TensorRT components are not installed. Install them from App Settings.");
            }
            else if (TensorRtSelected && Vm?.TensorRtKernelsMissingText() is string kernelsMissing)
            {
                valid = false;
                validationText.Add(kernelsMissing);
            }

            if (EncodeChecks.DirectMlFormatProblem(DirectMlSelected ? Backend.DirectMl : Backend.TensorRt, OutputPixFmt) is string dmlProblem)
            {
                valid = false;
                validationText.Add(dmlProblem);
            }

            Valid = valid;

            if (Vm != null)
            {
                var numFilesToUpscale = Vm.CheckInputs();
                if (Vm.InputPlanProblem is string planProblem)
                {
                    Valid = false;
                    validationText.Add($"{planProblem}.");
                }
                else if (numFilesToUpscale == 0)
                {
                    Valid = false;
                    validationText.Add($"{Vm.InputStatusText} selected for upscaling. At least one file must be selected.");
                }
                Vm.ValidationText = string.Join("\n", validationText);
            }
        }
    }

    [DataContract]
    public class UpscaleModel : ReactiveObject
    {
        private string _modelHeader = string.Empty;
        [DataMember]
        public string ModelHeader
        {
            get => _modelHeader;
            set => this.RaiseAndSetIfChanged(ref _modelHeader, value);
        }

        private int? _resizeHeightBeforeUpscale = 0;
        [DataMember]
        public int? ResizeHeightBeforeUpscale
        {
            get => _resizeHeightBeforeUpscale ?? 0;
            set
            {
                this.RaiseAndSetIfChanged(ref _resizeHeightBeforeUpscale, value ?? 0);
                this.RaisePropertyChanged(nameof(EnableResizeFactor));
            }
        }

        private decimal? _resizeFactorBeforeUpscale = 100;
        [DataMember]
        public decimal? ResizeFactorBeforeUpscale
        {
            get => _resizeFactorBeforeUpscale ?? 100;
            set => this.RaiseAndSetIfChanged(ref _resizeFactorBeforeUpscale, value ?? 100);
        }

        private string _onnxModelPath = string.Empty;
        [DataMember]
        public string OnnxModelPath
        {
            get => _onnxModelPath;
            set
            {
                this.RaiseAndSetIfChanged(ref _onnxModelPath, value);
                this.RaisePropertyChanged(nameof(TemporalFrames));
                this.RaisePropertyChanged(nameof(IsTemporal));
                this.RaisePropertyChanged(nameof(TemporalDescription));
            }
        }

        public bool EnableResizeFactor => ResizeHeightBeforeUpscale == 0;

        public const decimal DefaultTemporalSceneThreshold = ModelSettings.DefaultTemporalSceneThreshold;

        // Temporal (multi-frame) models upscale each frame from a window of its neighbors. The window
        // size comes from the onnx input shape, as the engine reads it; 1 for a regular model.
        public int TemporalFrames => OnnxInputShape.TemporalFrames(OnnxModelPath);

        public bool IsTemporal => TemporalFrames > 1;

        public string TemporalDescription =>
            string.Format(CultureInfo.CurrentCulture, VideoJaNai.Lang.Resources.TemporalModelDescription, TemporalFrames);

        // Temporal models: neighbors across a scene change (mean frame difference above this) are
        // replaced by the nearest same-scene frame; 0 disables scene detection.
        private decimal? _temporalSceneThreshold = DefaultTemporalSceneThreshold;
        [DataMember]
        public decimal? TemporalSceneThreshold
        {
            get => _temporalSceneThreshold ?? DefaultTemporalSceneThreshold;
            set => this.RaiseAndSetIfChanged(ref _temporalSceneThreshold, value ?? DefaultTemporalSceneThreshold);
        }
    }
}