using AnimeJaNaiConverterGui.Services;
using Avalonia.Collections;
using Newtonsoft.Json;
using ReactiveUI;
using SevenZipExtractor;
using Splat;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoJaNai.ViewModels
{
    [DataContract]
    public class MainWindowViewModel : ViewModelBase
    {
        public static readonly List<string> VIDEO_EXTENSIONS = [".mkv", ".mp4", ".mpg", ".mpeg", ".avi", ".mov", ".wmv"];

        private static readonly CultureInfo ENGLISH_CULTURE = CultureInfo.GetCultureInfo("en-US");

        private string? _availableUpdateVersion;

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
                    await RefreshComponents();
                }
            });

            CheckForUpdates();
        }

        private CancellationTokenSource? _cancellationTokenSource;
        private Process? _runningProcess = null;
        private readonly IETACalculator _etaCalculator = new ETACalculator(10, 5.0);

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

        private bool _autoUpdate = true;
        [DataMember]
        public bool AutoUpdateEnabled
        {
            get => _autoUpdate;
            set => this.RaiseAndSetIfChanged(ref _autoUpdate, value);
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

        public static readonly string _ffmpegX265 = "libx265 -crf 16 -preset slow -x265-params \"sao=0:bframes=8:psy-rd=1.5:psy-rdoq=2:aq-mode=3:ref=6\" -max_interleave_delta 0";
        public static readonly string _ffmpegX264 = "libx264 -crf 13 -preset slow -max_interleave_delta 0";
        public static readonly string _ffmpegHevcNvenc = "hevc_nvenc -preset p7 -profile:v main10 -b:v 50M -max_interleave_delta 0";
        public static readonly string _ffmpegLossless = "ffv1 -max_interleave_delta 0";

        private bool _showConsole = false;
        public bool ShowConsole
        {
            get => _showConsole;
            set => this.RaiseAndSetIfChanged(ref _showConsole, value);
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
                this.RaisePropertyChanged(nameof(LeftStatus));
                this.RaisePropertyChanged(nameof(AllowReinstall));
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

            if (CurrentWorkflow.Valid && !Upscaling)
            {
                var overwriteText = CurrentWorkflow.OverwriteExistingVideos ? "overwritten" : "skipped";

                // input file
                if (CurrentWorkflow.SelectedTabIndex == 0)
                {
                    StringBuilder status = new();
                    var skipFiles = 0;

                    var outputFilePath = Path.Join(
                                                    Path.GetFullPath(CurrentWorkflow.OutputFolderPath),
                                                    CurrentWorkflow.OutputFilename.Replace("%filename%", Path.GetFileNameWithoutExtension(CurrentWorkflow.InputFilePath)));

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
                    var existFileCount = 0;
                    var totalFileCount = 0;

                    var videos = Directory.EnumerateFiles(CurrentWorkflow.InputFolderPath, "*.*", SearchOption.AllDirectories)
                        .Where(file => VIDEO_EXTENSIONS.Any(ext => file.ToLower().EndsWith(ext)));
                    var filesCount = 0;

                    foreach (var inputVideoPath in videos)
                    {
                        var outputFilePath = Path.Join(
                                                    Path.GetFullPath(CurrentWorkflow.OutputFolderPath),
                                                    CurrentWorkflow.OutputFilename.Replace("%filename%", Path.GetFileNameWithoutExtension(inputVideoPath)));

                        // if out file exists, exist count ++
                        // if overwrite image OR out file doesn't exist, count image++
                        var fileExists = File.Exists(outputFilePath);

                        if (fileExists)
                        {
                            existFileCount++;
                        }

                        if (!fileExists || CurrentWorkflow.OverwriteExistingVideos)
                        {
                            filesCount++;
                        }
                    }

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
                    var models = new List<string>();
                    // TODO: re-check this regex/discovery against the new models-rife-fp16-1 filenames.
                    var modelsPath = _pythonService.RifeModelsDirectory;

                    if (!Directory.Exists(modelsPath))
                    {
                        return [];
                    }

                    var files = Directory.GetFiles(modelsPath, searchPattern: "*.onnx");

                    foreach (var file in files)
                    {
                        Debug.WriteLine(file);
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

                    models.Sort(delegate (string m1, string m2)
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

                    _rifeModels = [.. models.Select(m => RifeValueToLabel(m))];
                }

                return _rifeModels;
            }
        }

        public static string RifeLabelToValue(string rifeLabel)
        {
            if (string.IsNullOrEmpty(rifeLabel))
            {
                return rifeLabel;
            }
            else
            {
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
            }

            throw new ArgumentException(rifeLabel);
        }

        public static string RifeValueToLabel(string rifeValue)
        {
            string dec;

            if (string.IsNullOrEmpty(rifeValue))
            {
                return rifeValue;
            }

            if (rifeValue.Length == 2)
            {
                dec = rifeValue[1].ToString();
            }
            else
            {
                dec = rifeValue.Substring(1, 2);
            }

            var modelName = $"RIFE {rifeValue[0]}.{dec}";

            if (rifeValue.Length >= 4 && rifeValue.EndsWith('1'))
            {
                modelName += " Lite";
            }

            return modelName;
        }

        public void SetupAnimeJaNaiConfSlot1()
        {
            // Emits the canonical config_version=2 schema consumed by the libaji engine
            // (see AnimeJaNaiConfEditor). There is no backend_path, no per-chain
            // final_resize, and no per-chain tensorrt settings.
            var backend = CurrentWorkflow.DirectMlSelected ? "DirectML" : "TensorRT";

            var configText = new StringBuilder();
            configText.AppendLine("[global]");
            configText.AppendLine("config_version=2");
            configText.AppendLine("logging=yes");
            configText.AppendLine($"backend={backend}");

            // trt_engine_settings is intentionally omitted: with TensorRT 11 the engine builds
            // stronglyTyped by default (precision is taken from the ONNX model), so VideoJaNai no
            // longer exposes a precision/engine-settings picker.

            configText.AppendLine("[slot_1]");
            configText.AppendLine("profile_name=encode");
            configText.AppendLine("chain_1_min_resolution=0x0");
            configText.AppendLine("chain_1_max_resolution=0x0");
            configText.AppendLine("chain_1_min_fps=0");
            configText.AppendLine("chain_1_max_fps=0");

            for (var i = 0; i < CurrentWorkflow.UpscaleSettings.Count; i++)
            {
                // aji resolves models by bare name against --model-dir, so copy the selected onnx in.
                Directory.CreateDirectory(_pythonService.ModelsDirectory);
                var targetCopyPath = Path.Combine(_pythonService.ModelsDirectory, Path.GetFileName(CurrentWorkflow.UpscaleSettings[i].OnnxModelPath));

                if (Path.GetFullPath(targetCopyPath) != Path.GetFullPath(CurrentWorkflow.UpscaleSettings[i].OnnxModelPath))
                {
                    File.Copy(CurrentWorkflow.UpscaleSettings[i].OnnxModelPath, targetCopyPath, true);
                }

                configText.AppendLine(string.Create(ENGLISH_CULTURE, @$"chain_1_model_{i + 1}_resize_height_before_upscale={CurrentWorkflow.UpscaleSettings[i].ResizeHeightBeforeUpscale}
chain_1_model_{i + 1}_resize_factor_before_upscale={CurrentWorkflow.UpscaleSettings[i].ResizeFactorBeforeUpscale}
chain_1_model_{i + 1}_name={Path.GetFileNameWithoutExtension(CurrentWorkflow.UpscaleSettings[i].OnnxModelPath)}"));
            }

            var rife = CurrentWorkflow.EnableRife ? "yes" : "no";
            var ensemble = CurrentWorkflow.RifeEnsemble ? "yes" : "no";
            configText.AppendLine($"chain_1_rife={rife}");
            configText.AppendLine($"chain_1_rife_model={RifeLabelToValue(CurrentWorkflow.RifeModel)}");
            configText.AppendLine(string.Create(ENGLISH_CULTURE, $"chain_1_rife_factor_numerator={CurrentWorkflow.RifeFactorNumerator}"));
            configText.AppendLine(string.Create(ENGLISH_CULTURE, $"chain_1_rife_factor_denominator={CurrentWorkflow.RifeFactorDenominator}"));
            configText.AppendLine(string.Create(ENGLISH_CULTURE, $"chain_1_rife_scene_detect_threshold={CurrentWorkflow.RifeSceneDetectThreshold}"));
            configText.AppendLine($"chain_1_rife_ensemble={ensemble}");

            Directory.CreateDirectory(_pythonService.AnimeJaNaiDirectory);
            File.WriteAllText(_pythonService.ConfPath, configText.ToString());
        }

        public async Task CheckEngines(string inputFilePath)
        {
            // TensorRT builds/caches engines on first use; DirectML needs no prebuild.
            if (!CurrentWorkflow.TensorRtSelected)
            {
                return;
            }

            await GenerateEngine(inputFilePath);
        }

        public async Task RunUpscale()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var ct = _cancellationTokenSource.Token;
            ShowConsole = true;

            //var task = Task.Run(async () =>
            //{
            ct.ThrowIfCancellationRequested();
            ConsoleQueueClear();
            Upscaling = true;
            SetupAnimeJaNaiConfSlot1();

            if (CurrentWorkflow.SelectedTabIndex == 0)
            {
                ct.ThrowIfCancellationRequested();
                await CheckEngines(CurrentWorkflow.InputFilePath);
                ct.ThrowIfCancellationRequested();
                var outputFilePath = Path.Join(
                            Path.GetFullPath(CurrentWorkflow.OutputFolderPath),
                            CurrentWorkflow.OutputFilename.Replace("%filename%", Path.GetFileNameWithoutExtension(CurrentWorkflow.InputFilePath)));
                await RunUpscaleSingle(CurrentWorkflow.InputFilePath, outputFilePath);
                ct.ThrowIfCancellationRequested();
            }
            else
            {
                var files = Directory.GetFiles(CurrentWorkflow.InputFolderPath).Where(file => VIDEO_EXTENSIONS.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).ToList();

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    await CheckEngines(file);
                    ct.ThrowIfCancellationRequested();
                    var outputFilePath = Path.Join(
                            Path.GetFullPath(CurrentWorkflow.OutputFolderPath),
                            CurrentWorkflow.OutputFilename.Replace("%filename%", Path.GetFileNameWithoutExtension(file)));
                    ct.ThrowIfCancellationRequested();
                    await RunUpscaleSingle(file, outputFilePath);
                    ct.ThrowIfCancellationRequested();
                }
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

        public async Task RunUpscaleSingle(string inputFilePath, string outputFilePath)
        {
            var args = BuildAjiEncodeArgs(inputFilePath, outputFilePath, buildOnly: false);
            ConsoleQueueEnqueue($"Upscaling with command: {_pythonService.AjiEncodePath} {args}");
            await RunAjiEncode(args, resetProgress: true);
        }

        public async Task GenerateEngine(string inputFilePath)
        {
            var args = BuildAjiEncodeArgs(inputFilePath, null, buildOnly: true);
            ConsoleQueueEnqueue($"Generating TensorRT engine with command: {_pythonService.AjiEncodePath} {args}");
            await RunAjiEncode(args, resetProgress: false);
        }

        private string BuildAjiEncodeArgs(string inputFilePath, string? outputFilePath, bool buildOnly)
        {
            var backend = CurrentWorkflow.DirectMlSelected ? "directml" : "tensorrt";

            var sb = new StringBuilder();
            sb.Append(ENGLISH_CULTURE, $"--input \"{inputFilePath}\" ");
            sb.Append(ENGLISH_CULTURE, $"--conf \"{_pythonService.ConfPath}\" ");
            sb.Append("--slot 1 ");
            sb.Append(ENGLISH_CULTURE, $"--model-dir \"{_pythonService.ModelsDirectory}\" ");
            sb.Append(ENGLISH_CULTURE, $"--rife-model-dir \"{_pythonService.RifeModelsDirectory}\" ");
            sb.Append(ENGLISH_CULTURE, $"--trtexec \"{_pythonService.TrtexecPath}\" ");
            sb.Append(ENGLISH_CULTURE, $"--backend {backend} ");

            if (buildOnly)
            {
                sb.Append("--build-only ");
            }
            else
            {
                sb.Append(ENGLISH_CULTURE, $"--output \"{outputFilePath}\" ");

                var (vcodec, vquality) = MapFfmpegSettings(CurrentWorkflow.FfmpegVideoSettings);
                sb.Append(ENGLISH_CULTURE, $"--vcodec {vcodec} ");
                if (!string.IsNullOrWhiteSpace(vquality))
                {
                    sb.Append(ENGLISH_CULTURE, $"--vquality \"{vquality}\" ");
                }

                sb.Append(ENGLISH_CULTURE, $"--pix-fmt {CurrentWorkflow.OutputPixFmt} ");

                // final_resize has no conf field in the new engine; pass it to aji_encode directly.
                if (CurrentWorkflow.FinalResizeHeight is int frh && frh > 0)
                {
                    sb.Append(ENGLISH_CULTURE, $"--final-resize-height {frh} ");
                }
                else if (CurrentWorkflow.FinalResizeFactor is int frf && frf > 0 && frf != 100)
                {
                    sb.Append(ENGLISH_CULTURE, $"--final-resize-factor {frf} ");
                }

                if (CurrentWorkflow.OverwriteExistingVideos)
                {
                    sb.Append("--overwrite ");
                }
            }

            sb.Append("--progress line");
            return sb.ToString();
        }

        // Splits a VideoJaNai ffmpeg preset ("<codec> <encoder args...>") into the aji_encode
        // --vcodec / --vquality pair. The muxer-level option is dropped (aji_encode owns muxing).
        private static (string vcodec, string vquality) MapFfmpegSettings(string settings)
        {
            var cleaned = settings.Replace("-max_interleave_delta 0", "").Trim();
            var spaceIdx = cleaned.IndexOf(' ');
            if (spaceIdx < 0)
            {
                return (cleaned, string.Empty);
            }

            return (cleaned[..spaceIdx], cleaned[(spaceIdx + 1)..].Trim());
        }

        public async Task RunAjiEncode(string arguments, bool resetProgress)
        {
            if (resetProgress)
            {
                ResetProgress();
            }

            using (var process = new Process())
            {
                _runningProcess = process;
                process.StartInfo.FileName = _pythonService.AjiEncodePath;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.WorkingDirectory = _pythonService.InferenceDirectory;
                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                // Create a StreamWriter to write the output to a log file
                using (var outputFile = new StreamWriter("error.log", append: true))
                {
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            outputFile.WriteLine(e.Data); // Write the output to the log file
                            ConsoleQueueEnqueue(e.Data);
                        }
                    };

                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            outputFile.WriteLine(e.Data); // Write the output to the log file

                            // aji_encode emits machine-readable "PROGRESS ..." lines on stdout;
                            // route those to the progress bar instead of the console.
                            if (!TryHandleProgressLine(e.Data))
                            {
                                ConsoleQueueEnqueue(e.Data);
                            }
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine(); // Start asynchronous reading of the output
                    await process.WaitForExitAsync();
                }
                ChildProcessTracker.AddProcess(process);
            }
        }

        private bool TryHandleProgressLine(string line)
        {
            if (!line.StartsWith("PROGRESS", StringComparison.Ordinal))
            {
                return false;
            }

            var phaseMatch = Regex.Match(line, @"phase=(\w+)");
            if (phaseMatch.Success)
            {
                ProgressPhase = phaseMatch.Groups[1].Value;
            }

            var pctMatch = Regex.Match(line, @"pct=([0-9]+(?:\.[0-9]+)?)");
            if (pctMatch.Success && double.TryParse(pctMatch.Groups[1].Value, NumberStyles.Float, ENGLISH_CULTURE, out var pct))
            {
                ProgressValue = pct;

                if (pct is > 0 and < 100)
                {
                    _etaCalculator.Update((float)(pct / 100.0));
                    ProgressText = _etaCalculator.ETAIsAvailable
                        ? string.Create(ENGLISH_CULTURE, $"{pct:0.0}% - ETA {_etaCalculator.ETR:hh\\:mm\\:ss}")
                        : string.Create(ENGLISH_CULTURE, $"{pct:0.0}%");
                }
                else
                {
                    ProgressText = string.Create(ENGLISH_CULTURE, $"{pct:0.0}%");
                }
            }

            return true;
        }

        private void ResetProgress()
        {
            _etaCalculator.Reset();
            ProgressValue = 0;
            ProgressText = string.Empty;
            ProgressPhase = string.Empty;
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
            if (CurrentWorkflow != null)
            {
                var lines = JsonConvert.SerializeObject(Workflows[0], NewtonsoftJsonSuspensionDriver.Settings);
                var workflow = JsonConvert.DeserializeObject<UpscaleWorkflow>(lines, NewtonsoftJsonSuspensionDriver.Settings);
                var workflowIndex = CurrentWorkflow.WorkflowIndex;
                var workflowName = $"Workflow {workflowIndex + 1}";

                if (workflow != null)
                {
                    var defaultWorkflow = new UpscaleWorkflow
                    {
                        Vm = this,
                        WorkflowIndex = workflowIndex,
                        WorkflowName = workflowName,
                        UpscaleSettings = [new()],
                    };

                    CurrentWorkflow = defaultWorkflow;
                }
            }
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
                workflow.Vm = CurrentWorkflow.Vm;
                CurrentWorkflow = workflow;
            }
        }

        public void WriteCurrentWorkflowToFile(string fullPath)
        {
            var lines = JsonConvert.SerializeObject(CurrentWorkflow, NewtonsoftJsonSuspensionDriver.Settings);
            File.WriteAllText(fullPath, lines);
        }

        public async Task CheckAndExtractBackend()
        {
            // Components are installed by the Inno setup's component page. On launch, just verify the
            // engine is present; if not (skipped/offline at install) and we're installed, run the
            // updater's --auto to fetch the GPU-matched components, with progress on the setup overlay.
            if (_pythonService.IsInferenceInstalled() || !_updateManagerService.IsInstalled)
            {
                return;
            }

            await Task.Run(async () =>
            {
                BackendSetupSubStatusQueueClear();
                IsExtractingBackend = true;
                BackendSetupMainStatus = "Installing components for your hardware...";
                try
                {
                    var rc = await _updateManagerService.RunUpdaterStreamingAsync("--auto", BackendSetupSubStatusQueueEnqueue);
                    if (rc != 0)
                    {
                        ExtractingBackendFailed = true;
                        BackendSetupMainStatus = "Component setup failed. You can retry from App Settings.";
                        return;
                    }
                }
                catch (Exception ex)
                {
                    BackendSetupSubStatusQueueEnqueue(ex.Message);
                    ExtractingBackendFailed = true;
                    BackendSetupMainStatus = "Component setup failed. You can retry from App Settings.";
                    return;
                }
                IsExtractingBackend = false;
            });
        }

        public async Task ReinstallBackend()
        {
            // Re-fetch the GPU-matched components via the updater (--auto).
            ComponentsBusy = true;
            ComponentStatusText = "Reinstalling components...";
            try
            {
                await _updateManagerService.RunUpdaterStreamingAsync("--auto", line => ComponentStatusText = line);
            }
            catch (Exception ex) { ComponentStatusText = ex.Message; }
            ComponentsBusy = false;
            await RefreshComponents();
        }

        public async Task RefreshComponents()
        {
            try
            {
                var json = await _updateManagerService.GetComponentsJsonAsync();
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }
                var root = Newtonsoft.Json.Linq.JObject.Parse(json);
                var gpu = root["gpu"];
                bool nvidia = (bool?)gpu?["nvidia"] ?? false;
                var gpuName = (string?)gpu?["name"] ?? "";
                var sm = (string?)gpu?["sm"] ?? "";
                GpuStatusText = nvidia
                    ? $"{gpuName} ({sm})"
                    : "No NVIDIA GPU detected — offline upscaling currently requires an NVIDIA GPU.";

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
                    if (!installed && !recommended && !preselect && name != "rife")
                    {
                        continue;
                    }
                    items.Add(new ComponentItem(name, (long?)p["bytes"] ?? 0, installed, recommended, preselect));
                }
                Components = items;
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
        }

        public async Task CheckForUpdates()
        {
            try
            {
                if (_updateManagerService.IsInstalled)
                {
                    _availableUpdateVersion = await _updateManagerService.CheckForUpdateAsync().ConfigureAwait(true);
                    UpdateStatus();
                }
            }
            catch (Exception ex)
            {
                UpdateStatusText = $"Check for update failed: {ex.Message}";
            }
        }

        // The bundled updater downloads + applies (overlay-vs-full) in one step, then relaunches,
        // so there's no separate in-app download. Launch it and exit so it can replace files.
        public void ApplyUpdate()
        {
            ShowApplyButton = false;
            UpdateStatusText = "Updating and restarting...";
            _updateManagerService.ApplyUpdateAndRestart();
            Environment.Exit(0);
        }

        private void UpdateStatus()
        {
            if (!string.IsNullOrEmpty(_availableUpdateVersion))
            {
                UpdateStatusText = $"Update available: {_availableUpdateVersion}";
                ShowApplyButton = true;
                ShowCheckUpdateButton = false;
            }
            else
            {
                UpdateStatusText = "No updates found";
                ShowApplyButton = false;
                ShowCheckUpdateButton = true;
            }
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
        public string Title => Name switch
        {
            "trt-runtime" => "TensorRT runtime",
            "rife" => "RIFE interpolation models",
            "trt-ptx" => "TensorRT kernels: other NVIDIA GPUs",
            _ when Name.StartsWith("trt-sm") => $"TensorRT kernels: {SmFamily(Name[6..])}",
            _ => Name,
        };

        public string Description => Name switch
        {
            "trt-runtime" => "The fastest upscaling engine, for NVIDIA GPUs. Without it, upscaling falls back to the slower DirectML engine.",
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

            this.WhenAnyValue(x => x.Vm).Subscribe(x =>
            {
                sub?.Dispose();
                sub = Vm.WhenAnyValue(
                    x => x.SelectedWorkflowIndex,
                    x => x.RequestShowAppSettings
                    ).Subscribe(x =>
                    {
                        this.RaisePropertyChanged(nameof(ActiveWorkflow));
                        Vm?.RaisePropertyChanged("Workflows");
                    });
            });

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

        public bool FfmpegX265Selected => FfmpegVideoSettings == MainWindowViewModel._ffmpegX265;
        public bool FfmpegX264Selected => FfmpegVideoSettings == MainWindowViewModel._ffmpegX264;
        public bool FfmpegHevcNvencSelected => FfmpegVideoSettings == MainWindowViewModel._ffmpegHevcNvenc;


        public bool FfmpegLosslessSelected => FfmpegVideoSettings == MainWindowViewModel._ffmpegLossless;

        private string _ffmpegVideoSettings = MainWindowViewModel._ffmpegHevcNvenc;
        [DataMember]
        public string FfmpegVideoSettings
        {
            get => _ffmpegVideoSettings;
            set
            {
                this.RaiseAndSetIfChanged(ref _ffmpegVideoSettings, value);
                this.RaisePropertyChanged(nameof(FfmpegX265Selected));
                this.RaisePropertyChanged(nameof(FfmpegX264Selected));
                this.RaisePropertyChanged(nameof(FfmpegHevcNvencSelected));
                this.RaisePropertyChanged(nameof(FfmpegLosslessSelected));
            }
        }

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

        private bool _tensorRtSelected = true;
        [DataMember]
        public bool TensorRtSelected
        {
            get => _tensorRtSelected;
            set => this.RaiseAndSetIfChanged(ref _tensorRtSelected, value);
        }

        private bool _directMlSelected = false;
        [DataMember]
        public bool DirectMlSelected
        {
            get => _directMlSelected;
            set => this.RaiseAndSetIfChanged(ref _directMlSelected, value);
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
            set => this.RaiseAndSetIfChanged(ref _upscaleSettings, value);
        }

        private bool _enableRife = false;
        [DataMember]
        public bool EnableRife
        {
            get => _enableRife;
            set => this.RaiseAndSetIfChanged(ref _enableRife, value);
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

        private bool _showAdvancedSettings = false;
        [DataMember]
        public bool ShowAdvancedSettings
        {
            get => _showAdvancedSettings;
            set => this.RaiseAndSetIfChanged(ref _showAdvancedSettings, value);
        }

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

        public void SetFfmpegX265()
        {
            FfmpegVideoSettings = MainWindowViewModel._ffmpegX265;
            Validate();
        }

        public void SetFfmpegX264()
        {
            FfmpegVideoSettings = MainWindowViewModel._ffmpegX264;
            Validate();
        }

        public void SetFfmpegHevcNvenc()
        {
            FfmpegVideoSettings = MainWindowViewModel._ffmpegHevcNvenc;
            Validate();
        }

        public void SetFfmpegLossless()
        {
            FfmpegVideoSettings = MainWindowViewModel._ffmpegLossless;
            Validate();
        }

        public void SetOutputPixFmt420P8() { OutputPixFmt = "yuv420p"; Validate(); }
        public void SetOutputPixFmt420P10() { OutputPixFmt = "yuv420p10"; Validate(); }
        public void SetOutputPixFmt444P8() { OutputPixFmt = "yuv444p"; Validate(); }
        public void SetOutputPixFmt444P10() { OutputPixFmt = "yuv444p10"; Validate(); }

        public void SetTensorRtSelected()
        {
            TensorRtSelected = true;
            DirectMlSelected = false;
        }

        public void SetDirectMlSelected()
        {
            DirectMlSelected = true;
            TensorRtSelected = false;
        }

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

            foreach (var upscaleModel in UpscaleSettings)
            {
                if (!File.Exists(upscaleModel.OnnxModelPath))
                {
                    valid = false;
                    validationText.Add("ONNX Model Path is required.");
                }
            }

            if (OutputIs444 && FfmpegHevcNvencSelected)
            {
                valid = false;
                validationText.Add("4:4:4 output requires a software encoder (x265 / x264 / Lossless); NVENC HEVC does not support 4:4:4.");
            }

            Valid = valid;

            if (Vm != null)
            {
                var numFilesToUpscale = Vm.CheckInputs();
                if (numFilesToUpscale == 0)
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
            set => this.RaiseAndSetIfChanged(ref _onnxModelPath, value);
        }

        public bool EnableResizeFactor => ResizeHeightBeforeUpscale == 0;
    }
}