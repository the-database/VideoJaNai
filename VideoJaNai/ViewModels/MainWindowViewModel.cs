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
using Velopack;
using Velopack.Sources;

namespace VideoJaNai.ViewModels
{
    [DataContract]
    public class MainWindowViewModel : ViewModelBase
    {
        public static readonly List<string> VIDEO_EXTENSIONS = [".mkv", ".mp4", ".mpg", ".mpeg", ".avi", ".mov", ".wmv"];

        private static readonly CultureInfo ENGLISH_CULTURE = CultureInfo.GetCultureInfo("en-US");

        private readonly UpdateManager _um;
        private UpdateInfo? _update = null;

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
                if (x && string.IsNullOrWhiteSpace(PythonPipList))
                {
                    await PopulatePythonPipList();
                }
            });

            _um = new UpdateManager(new GithubSource("https://github.com/the-database/VideoJaNai", null, false));
            CheckForUpdates();
        }

        private CancellationTokenSource? _cancellationTokenSource;
        private Process? _runningProcess = null;

        public bool IsInstalled => _um?.IsInstalled ?? false;

        private bool _showCheckUpdateButton = true;
        public bool ShowCheckUpdateButton
        {
            get => _showCheckUpdateButton;
            set => this.RaiseAndSetIfChanged(ref _showCheckUpdateButton, value);
        }

        private bool _showDownloadButton = false;
        public bool ShowDownloadButton
        {
            get => _showDownloadButton;
            set
            {
                this.RaiseAndSetIfChanged(ref _showDownloadButton, value);
                this.RaisePropertyChanged(nameof(ShowCheckUpdateButton));
            }
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

        public string AppVersion => _um?.CurrentVersion?.ToString() ?? "";

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

        private string _overwriteCommand => CurrentWorkflow.OverwriteExistingVideos ? "-y" : "";

        public static readonly string _ffmpegX265 = "libx265 -crf 16 -preset slow -x265-params \"sao=0:bframes=8:psy-rd=1.5:psy-rdoq=2:aq-mode=3:ref=6\" -max_interleave_delta 0";
        public static readonly string _ffmpegX264 = "libx264 -crf 13 -preset slow -max_interleave_delta 0";
        public static readonly string _ffmpegHevcNvenc = "hevc_nvenc -preset p7 -profile:v main10 -b:v 50M -max_interleave_delta 0";
        public static readonly string _ffmpegLossless = "ffv1 -max_interleave_delta 0";

        public static readonly string _tensorRtDynamicEngine = "--fp16 --minShapes=input:1x3x8x8 --optShapes=input:1x3x1080x1920 --maxShapes=input:1x3x1080x1920 --inputIOFormats=fp16:chw --outputIOFormats=fp16:chw --tacticSources=+CUDNN,-CUBLAS,-CUBLAS_LT --skipInference";
        public static readonly string _tensorRtStaticEngine = "--fp16 --optShapes=input:%video_resolution% --inputIOFormats=fp16:chw --outputIOFormats=fp16:chw --tacticSources=+CUDNN,-CUBLAS,-CUBLAS_LT --skipInference";
        public static readonly string _tensorRtStaticOnnx = "--fp16 --inputIOFormats=fp16:chw --outputIOFormats=fp16:chw --tacticSources=+CUDNN,-CUBLAS,-CUBLAS_LT --skipInference";
        public static readonly string _tensorRtStaticBf16Engine = "--bf16 --optShapes=input:%video_resolution% --inputIOFormats=fp16:chw --outputIOFormats=fp16:chw --tacticSources=+CUDNN,-CUBLAS,-CUBLAS_LT --skipInference";

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

        private bool _runningPython = false;
        [IgnoreDataMember]
        public bool RunningPython
        {
            get => _runningPython;
            set
            {
                this.RaiseAndSetIfChanged(ref _runningPython, value);
                this.RaisePropertyChanged(nameof(AllowReinstall));
            }
        }

        public bool AllowReinstall => !RunningPython && !Upscaling;

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

        public string PythonPath => _pythonService.PythonPath;

        private string _pythonPipList = string.Empty;
        public string PythonPipList
        {
            get => _pythonPipList;
            set => this.RaiseAndSetIfChanged(ref _pythonPipList, value);
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
                    var modelsPath = Path.Combine(_pythonService.VsmlrtModelsPath, "rife");

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
            var confPath = Path.Combine(_pythonService.AnimeJaNaiDirectory, "animejanai.conf");
            var backend = CurrentWorkflow.DirectMlSelected ? "DirectML" : CurrentWorkflow.NcnnSelected ? "NCNN" : "TensorRT";
            HashSet<string> filesNeedingEngine = new();
            var configText = new StringBuilder($@"[global]
logging=yes
backend={backend}
backend_path={_pythonService.BackendDirectory}
[slot_1]
profile_name=encode
");

            for (var i = 0; i < CurrentWorkflow.UpscaleSettings.Count; i++)
            {
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
            configText.AppendLine(string.Create(ENGLISH_CULTURE, $"chain_1_rife_factor_numerator={CurrentWorkflow.RifeFactorNumerator}"));
            configText.AppendLine(string.Create(ENGLISH_CULTURE, $"chain_1_rife_factor_denominator={CurrentWorkflow.RifeFactorDenominator}"));
            configText.AppendLine($"chain_1_rife_model={RifeLabelToValue(CurrentWorkflow.RifeModel)}");
            configText.AppendLine($"chain_1_rife_ensemble={ensemble}");
            configText.AppendLine(string.Create(ENGLISH_CULTURE, $"chain_1_rife_scene_detect_threshold={CurrentWorkflow.RifeSceneDetectThreshold}"));
            configText.AppendLine(string.Create(ENGLISH_CULTURE, $"chain_1_final_resize_height={CurrentWorkflow.FinalResizeHeight}"));
            configText.AppendLine(string.Create(ENGLISH_CULTURE, $"chain_1_final_resize_factor={CurrentWorkflow.FinalResizeFactor}"));
            configText.AppendLine($"chain_1_tensorrt_engine_settings={(CurrentWorkflow.TensorRtEngineSettingsAuto ? "" : CurrentWorkflow.TensorRtEngineSettings)}");

            File.WriteAllText(confPath, configText.ToString());
        }

        public async Task CheckEngines(string inputFilePath)
        {
            if (!CurrentWorkflow.TensorRtSelected)
            {
                return;
            }

            for (var i = 0; i < CurrentWorkflow.UpscaleSettings.Count; i++)
            {
                // TODO testing
                //var enginePath = @$".\mpv-upscale-2x_animejanai\animejanai\onnx\{Path.GetFileNameWithoutExtension(UpscaleSettings[i].OnnxModelPath)}.engine";

                //if (!File.Exists(enginePath))
                //{
                await GenerateEngine(inputFilePath);
                //}
            }

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
            var cmd = $@"{Path.GetRelativePath(_pythonService.BackendDirectory, _pythonService.VspipePath)} -c y4m --arg ""slot=1"" --arg ""video_path={inputFilePath}"" ""{Path.GetFullPath("./backend/animejanai/core/animejanai_encode.vpy")}"" - | ""{_pythonService.FfmpegPath}"" {_overwriteCommand} -i pipe: -i ""{inputFilePath}"" -map 0:v -c:v {CurrentWorkflow.FfmpegVideoSettings} -max_interleave_delta 0 -map 1:t? -map 1:a?  -map 1:s? -c:t copy -c:a copy -c:s copy ""{outputFilePath}""";
            ConsoleQueueEnqueue($"Upscaling with command: {cmd}");
            await RunCommand($@" /C {cmd}");
        }

        public async Task GenerateEngine(string inputFilePath)
        {
            var cmd = $@"{Path.GetRelativePath(_pythonService.BackendDirectory, _pythonService.VspipePath)} -c y4m --arg ""slot=1"" --arg ""video_path={inputFilePath}"" --start 0 --end 1 ""{Path.GetFullPath("./backend/animejanai/core/animejanai_encode.vpy")}"" -p .";
            ConsoleQueueEnqueue($"Generating TensorRT engine with command: {cmd}");
            await RunCommand($@" /C {cmd}");
        }

        public async Task RunCommand(string command)
        {
            // Create a new process to run the CMD command
            using (var process = new Process())
            {
                _runningProcess = process;
                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.Arguments = command;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.WorkingDirectory = _pythonService.BackendDirectory;

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

                            ConsoleQueueEnqueue(e.Data);
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
            await Task.Run(async () =>
            {
                var failureMsg = "Backend setup failed. Try reinstalling Python environment or report the issue on GitHub if it persists.";
                BackendSetupSubStatusQueueClear();
                IsExtractingBackend = true;

                if (!_pythonService.IsPythonInstalled())
                {
                    try
                    {
                        // 1. Install embedded Python + portable VS
                        await InstallPortableVapourSynth();

                        // 2. Python dependencies
                        await RunInstallCommand(_pythonService.InstallUpdatePythonDependenciesCommand);

                        // 3. VapourSynth plugins
                        await RunInstallCommand(_pythonService.InstallVapourSynthPluginsCommand);
                        await InstallVapourSynthMiscFilters();
                        await InstallVapourSynthAkarin();

                        // 4. vs-mlrt
                        await InstallVsmlrt();

                        // 5. RIFE models
                        await InstallRife();

                        CleanupInstall();
                    }
                    catch (Exception ex)
                    {
                        BackendSetupSubStatusQueueEnqueue(ex.Message);
                        if (ex.StackTrace != null)
                        {
                            BackendSetupSubStatusQueueEnqueue(ex.StackTrace);
                        }
                        ExtractingBackendFailed = true;
                        BackendSetupMainStatus = failureMsg;
                        return;
                    }
                }
                else
                {
                    if (Program.WasFirstRun)
                    {
                        try
                        {
                            var installedVsmlrtVersion = new Version(await RunVsmlrtVersion());

                            if (installedVsmlrtVersion.CompareTo(_pythonService.VsmlrtMinVersion) < 0)
                            {
                                Directory.Delete(_pythonService.PythonDirectory, true);
                                await CheckAndExtractBackend();
                                return;
                            }
                        }
                        catch (Exception) { }
                    }
                }

                if (!_pythonService.AreModelsInstalled())
                {
                    try
                    {
                        await InstallModels();
                    }
                    catch (Exception ex)
                    {
                        BackendSetupSubStatusQueueEnqueue(ex.Message);
                        if (ex.StackTrace != null)
                        {
                            BackendSetupSubStatusQueueEnqueue(ex.StackTrace);
                        }
                        ExtractingBackendFailed = true;
                        BackendSetupMainStatus = failureMsg;
                        return;
                    }
                }

                if (!_pythonService.IsFfmpegInstalled())
                {
                    try
                    {
                        await InstallFfmpeg();
                    }
                    catch (Exception ex)
                    {
                        BackendSetupSubStatusQueueEnqueue(ex.Message);
                        if (ex.StackTrace != null)
                        {
                            BackendSetupSubStatusQueueEnqueue(ex.StackTrace);
                        }
                        ExtractingBackendFailed = true;
                        BackendSetupMainStatus = failureMsg;
                        return;
                    }
                }

                IsExtractingBackend = false;
            });
        }

        public async Task PopulatePythonPipList()
        {
            RunningPython = true;
            try
            {
                var pipList = await RunPythonPipList();
                PythonPipList = $"Python Packages:\n{pipList}";
                //var vsrepos = await RunVsrepoInstalled(); // too slow
                var vsmlrtVer = await RunVsmlrtVersion();
                PythonPipList = $"Python Packages:\n{pipList}\n\nvsmlrt.py Version:\n{vsmlrtVer}";
            }
            catch (Exception) { }
            RunningPython = false;
        }

        public async Task ReinstallBackend()
        {
            if (Directory.Exists(_pythonService.FfmpegDirectory))
            {
                Directory.Delete(_pythonService.FfmpegDirectory, true);
            }

            if (Directory.Exists(_pythonService.PythonDirectory))
            {
                Directory.Delete(_pythonService.PythonDirectory, true);
            }

            await CheckAndExtractBackend();
        }

        public async Task<string> RunVsmlrtVersion()
        {
            return await RunPythonGetOutput(@$"""{Path.Combine(_pythonService.AnimeJaNaiDirectory, "core", "vsmlrt_version.py")}""");
        }

        public async Task<string> RunVsrepoInstalled()
        {
            return await RunPythonGetOutput(@".\vsrepo.py -p installed");
        }

        public async Task<string> RunPythonPipList()
        {
            return await RunPythonGetOutput("-m pip list");
        }

        public async Task<string> RunPythonGetOutput(string args)
        {
            List<string> result = [];

            // Create a new process to run the CMD command
            using (var process = new Process())
            {
                _runningProcess = process;
                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.Arguments = @$"/C .\python.exe {args}";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.WorkingDirectory = _pythonService.PythonDirectory;
                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                // Create a StreamWriter to write the output to a log file
                try
                {
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            Debug.WriteLine(e.Data);
                        }
                    };

                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            result.Add(e.Data);
                            Debug.WriteLine(e.Data);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await process.WaitForExitAsync();
                }
                catch (IOException) { }
            }

            return string.Join("\n", result);
        }

        private async Task InstallPortableVapourSynth()
        {
            // Download Python Installer
            BackendSetupMainStatus = "Downloading Portable VapourSynth Installer...";
            var downloadUrl = $"https://github.com/vapoursynth/vapoursynth/releases/download/R69/Install-Portable-VapourSynth-R69.ps1";
            var targetPath = Path.Join(_pythonService.BackendDirectory, "installvs.ps1");
            await Downloader.DownloadFileAsync(downloadUrl, targetPath, (progress) =>
            {
                BackendSetupMainStatus = $"Downloading Portable VapourSynth Installer ({progress}%)...";
            });

            // Install Python 
            BackendSetupMainStatus = "Installing Embedded Python with Portable VapourSynth...";

            using (PowerShell powerShell = PowerShell.Create())
            {
                powerShell.AddScript("Set-ExecutionPolicy RemoteSigned -Scope Process -Force");
                powerShell.AddScript("Import-Module Microsoft.PowerShell.Archive");

                var scriptContents = File.ReadAllText(targetPath);

                powerShell.AddScript(scriptContents);
                powerShell.AddParameter("Unattended");
                powerShell.AddParameter("TargetFolder", _pythonService.PythonDirectory);

                if (Directory.Exists(_pythonService.PythonDirectory))
                {
                    Directory.Delete(_pythonService.PythonDirectory, true);
                }

                PSDataCollection<PSObject> outputCollection = [];
                outputCollection.DataAdded += (sender, e) =>
                {
                    BackendSetupSubStatusQueueEnqueue(outputCollection[e.Index].ToString());
                };

                try
                {
                    IAsyncResult asyncResult = powerShell.BeginInvoke<PSObject, PSObject>(null, outputCollection);
                    powerShell.EndInvoke(asyncResult);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"An error occurred: {ex.Message}");
                }

                if (powerShell.Streams.Error.Count > 0)
                {
                    foreach (var error in powerShell.Streams.Error)
                    {
                        Debug.WriteLine($"Error: {error}");
                    }
                }
            }

            File.Delete(targetPath);
        }

        private async Task InstallVapourSynthMiscFilters()
        {
            BackendSetupMainStatus = "Downloading VapourSynth Misc Filters...";
            var downloadUrl = "https://github.com/vapoursynth/vs-miscfilters-obsolete/releases/download/R2/miscfilters-r2.7z";
            var targetPath = Path.Join(_pythonService.BackendDirectory, "miscfilters.7z");
            await Downloader.DownloadFileAsync(downloadUrl, targetPath, (progress) =>
            {
                BackendSetupMainStatus = $"Downloading VapourSynth Misc Filters ({progress}%)...";
            });

            BackendSetupMainStatus = "Extracting VapourSynth Misc Filters...";
            var targetExtractPath = Path.Combine(_pythonService.VapourSynthPluginsPath, "temp");
            Directory.CreateDirectory(targetExtractPath);

            using (ArchiveFile archiveFile = new(targetPath))
            {
                archiveFile.Extract(targetExtractPath);

                File.Copy(
                    Path.Combine(targetExtractPath, "win64", "MiscFilters.dll"),
                    Path.Combine(_pythonService.VapourSynthPluginsPath, "MiscFilters.dll")
                );
            }
            Directory.Delete(targetExtractPath, true);
            File.Delete(targetPath);
        }

        async Task InstallVapourSynthAkarin()
        {
            Console.WriteLine("Downloading VapourSynth Akarin...");
            var downloadUrl = "https://github.com/AkarinVS/vapoursynth-plugin/releases/download/v0.96/akarin-release-lexpr-amd64-v0.96g3.7z";
            var targetPath = Path.GetFullPath("akarin.7z");
            await Downloader.DownloadFileAsync(downloadUrl, targetPath, (progress) =>
            {
                Console.WriteLine($"Downloading VapourSynth Akarin ({progress}%)...");
            });

            Console.WriteLine("Extracting VapourSynth Akarin...");
            var targetExtractPath = _pythonService.VapourSynthPluginsPath;
            Directory.CreateDirectory(targetExtractPath);

            using (ArchiveFile archiveFile = new(targetPath))
            {
                archiveFile.Extract(targetExtractPath);
            }
            File.Delete(targetPath);
        }

        private async Task InstallVsmlrt()
        {
            Console.WriteLine("Downloading vs-mlrt...");
            var baseDownloadUrl = "https://github.com/AmusementClub/vs-mlrt/releases/download/v15.9/";
            var fileNames = new[] { "vsmlrt-windows-x64-cuda.v15.9.7z.001", "vsmlrt-windows-x64-cuda.v15.9.7z.002" };
            var targetPaths = fileNames.Select(f => Path.GetFullPath(f)).ToArray();

            double lastProgress = -1;
            int updateThreshold = 5;

            for (int i = 0; i < fileNames.Length; i++)
            {
                string downloadUrl = baseDownloadUrl + fileNames[i];
                string targetPath = targetPaths[i];

                await Downloader.DownloadFileAsync(downloadUrl, targetPath, (progress) =>
                {
                    if (progress >= lastProgress + updateThreshold)
                    {
                        Console.WriteLine($"Downloading {fileNames[i]} ({progress}%)...");
                        lastProgress = progress;
                    }
                });
            }


            Console.WriteLine("Extracting vs-mlrt (this may take several minutes)...");
            var targetDirectory = Path.Join(_pythonService.VapourSynthPluginsPath);
            Directory.CreateDirectory(targetDirectory);

            string sevenZipPath = Path.Combine(_pythonService.PythonDirectory, "7z.exe");
            string archivePath = targetPaths[0];

            var process = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = sevenZipPath,
                    Arguments = $"x \"{archivePath}\" -o\"{targetDirectory}\" -y",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"7-Zip extraction failed: {error}");
                return;
            }

            Console.WriteLine("Extraction complete.");
            File.Move(Path.Combine(targetDirectory, "vsmlrt.py"), Path.Combine(_pythonService.PythonDirectory, "vsmlrt.py"));

            foreach (var targetPath in targetPaths)
            {
                File.Delete(targetPath);
            }
        }

        async Task InstallRife()
        {
            List<string> models = [
                "rife_v4.7.7z",
                "rife_v4.8.7z",
                "rife_v4.9.7z",
                "rife_v4.10.7z",
                "rife_v4.11.7z",
                "rife_v4.12.7z",
                "rife_v4.12_lite.7z",
                "rife_v4.13.7z",
                "rife_v4.13_lite.7z",
                "rife_v4.14.7z",
                "rife_v4.14_lite.7z",
                "rife_v4.15.7z",
                "rife_v4.15_lite.7z",
                "rife_v4.16_lite.7z",
                "rife_v4.17.7z",
                "rife_v4.17_lite.7z",
                "rife_v4.18.7z",
                "rife_v4.19.7z",
                "rife_v4.20.7z",
                "rife_v4.21.7z",
                "rife_v4.22.7z",
            ];

            var downloadUrlBase = "https://github.com/AmusementClub/vs-mlrt/releases/download/external-models/";

            foreach (var model in models)
            {
                var downloadUrl = downloadUrlBase + model;
                var targetPath = Path.GetFullPath(model);
                await Downloader.DownloadFileAsync(downloadUrl, targetPath, _ => { });

                using (ArchiveFile archiveFile = new(targetPath))
                {
                    Directory.CreateDirectory(_pythonService.VsmlrtModelsPath);
                    archiveFile.Extract(_pythonService.VsmlrtModelsPath);
                    var onnxFiles = Directory.GetFiles(Path.Combine(_pythonService.VsmlrtModelsPath, "rife"));
                }

                File.Delete(targetPath);
            }
        }

        private async Task InstallFfmpeg()
        {
            BackendSetupMainStatus = "Downloading ffmpeg...";
            var downloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-git-essentials.7z";
            var targetPath = Path.Join(_pythonService.BackendDirectory, "ffmpeg.7z");
            await Downloader.DownloadFileAsync(downloadUrl, targetPath, (progress) =>
            {
                BackendSetupMainStatus = $"Downloading ffmpeg ({progress}%)...";
            });

            BackendSetupMainStatus = "Extracting ffmpeg...";
            using (ArchiveFile ffmpegArchive = new(targetPath))
            {
                ffmpegArchive.Extract(_pythonService.FfmpegDirectory);
            }

            var directories = Directory.GetDirectories(_pythonService.FfmpegDirectory);

            if (directories.Length > 0)
            {
                var files = Directory.GetFiles(Path.Combine(directories.First(), "bin"));

                foreach (string file in files)
                {
                    string fileName = Path.GetFileName(file);
                    string destinationPath = Path.Combine(_pythonService.FfmpegDirectory, fileName);
                    File.Move(file, destinationPath);
                }
                try
                {
                    Directory.Delete(directories.First(), true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
            }
            File.Delete(targetPath);
        }

        private void CleanupInstall()
        {
            List<string> dirs = ["doc", "vs-temp-dl", "Scripts", "sdk", "wheel"];

            foreach (var dir in dirs)
            {
                var targetDir = Path.Combine(_pythonService.BackendDirectory, dir);
                if (Directory.Exists(targetDir))
                {
                    Directory.Delete(targetDir, true);
                }
            }

            foreach (var dir in Directory.GetDirectories(_pythonService.VsmlrtModelsPath))
            {
                if (Path.GetFileName(dir) != "rife")
                {
                    Directory.Delete(dir, true);
                }
            }
        }

        public async Task<string[]> RunInstallCommand(string cmd)
        {
            Debug.WriteLine(cmd);

            // Create a new process to run the CMD command
            using (var process = new Process())
            {
                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.Arguments = @$"/C {cmd}";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                process.StartInfo.WorkingDirectory = _pythonService.PythonDirectory;

                var result = string.Empty;

                // Create a StreamWriter to write the output to a log file
                try
                {
                    //using var outputFile = new StreamWriter("error.log", append: true);
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            //Debug.WriteLine($"STDERR = {e.Data}");
                            BackendSetupSubStatusQueueEnqueue(e.Data);
                        }
                    };

                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            result = e.Data;
                            //Debug.WriteLine($"STDOUT = {e.Data}");
                            BackendSetupSubStatusQueueEnqueue(e.Data);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine(); // Start asynchronous reading of the output
                    await process.WaitForExitAsync();
                }
                catch (IOException) { }
            }

            return [];
        }

        private async Task InstallModels()
        {
            // 6. Download ONNX models
            var downloadUrl = "https://github.com/the-database/mpv-upscale-2x_animejanai/releases/download/3.0.0/2x_AnimeJaNai_HD_V3_ModelsOnly.zip";
            Directory.CreateDirectory(_pythonService.ModelsDirectory);
            var targetPath = Path.Join(_pythonService.ModelsDirectory, "models.zip");
            await Downloader.DownloadFileAsync(downloadUrl, targetPath, (progress) =>
            {
                BackendSetupMainStatus = $"Downloading AnimeJaNai models ({progress}%)...";
            });

            BackendSetupMainStatus = "Extracting AnimeJaNai models...";
            _pythonService.ExtractZip(targetPath, _pythonService.ModelsDirectory, (double progress) =>
            {
                BackendSetupMainStatus = $"Extracting AnimeJaNai models ({progress}%)...";
            });

            var directories = Directory.GetDirectories(_pythonService.ModelsDirectory);
            if (directories.Length > 0)
            {
                var files = Directory.GetFiles(directories.First(), "*.onnx");

                foreach (string file in files)
                {
                    string fileName = Path.GetFileName(file);
                    string destinationPath = Path.Combine(_pythonService.ModelsDirectory, fileName);
                    File.Move(file, destinationPath);
                }

                Directory.Delete(directories.First(), true);
            }

            File.Delete(targetPath);
        }

        public async Task CheckForUpdates()
        {
            try
            {
                if (IsInstalled)
                {
                    await Task.Run(async () =>
                    {
                        _update = await _um.CheckForUpdatesAsync().ConfigureAwait(true);
                    });

                    UpdateStatus();

                    if (AutoUpdateEnabled)
                    {
                        await DownloadUpdate();
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateStatusText = $"Check for update failed: {ex.Message}";
            }
        }

        public async Task DownloadUpdate()
        {
            try
            {
                if (_update != null)
                {
                    ShowDownloadButton = false;
                    await _um.DownloadUpdatesAsync(_update, Progress).ConfigureAwait(true);
                    UpdateStatus();
                }
            }
            catch
            {

            }
        }

        public void ApplyUpdate()
        {
            if (_update != null)
            {
                ShowApplyButton = false;
                _um.ApplyUpdatesAndRestart(_update);
            }
        }

        private void UpdateStatus()
        {
            ShowDownloadButton = false;
            ShowApplyButton = false;
            ShowCheckUpdateButton = true;

            if (_update != null)
            {
                UpdateStatusText = $"Update is available: {_update.TargetFullRelease.Version}";
                ShowDownloadButton = true;
                ShowCheckUpdateButton = false;

                if (_um.IsUpdatePendingRestart)
                {
                    UpdateStatusText = $"Update ready, pending restart to install version: {_update.TargetFullRelease.Version}";
                    ShowDownloadButton = false;
                    ShowApplyButton = true;
                    ShowCheckUpdateButton = false;
                }
                else
                {
                }
            }
            else
            {
                UpdateStatusText = "No updates found";
            }
        }

        private void Progress(int percent)
        {
            UpdateStatusText = $"Downloading update {_update?.TargetFullRelease.Version} ({percent}%)...";
        }
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

        private bool _tensorRtSelected = true;
        [DataMember]
        public bool TensorRtSelected
        {
            get => _tensorRtSelected;
            set
            {
                this.RaiseAndSetIfChanged(ref _tensorRtSelected, value);
                this.RaisePropertyChanged(nameof(ShowTensorRtEngineSettings));
            }
        }

        private bool _directMlSelected = false;
        [DataMember]
        public bool DirectMlSelected
        {
            get => _directMlSelected;
            set
            {
                this.RaiseAndSetIfChanged(ref _directMlSelected, value);
                this.RaisePropertyChanged(nameof(ShowTensorRtEngineSettings));
            }
        }

        private bool _ncnnSelected = false;
        [DataMember]
        public bool NcnnSelected
        {
            get => _ncnnSelected;
            set
            {
                this.RaiseAndSetIfChanged(ref _ncnnSelected, value);
                this.RaisePropertyChanged(nameof(ShowTensorRtEngineSettings));
            }
        }

        public bool TensorRtEngineDynamicSelected => TensorRtEngineSettings == MainWindowViewModel._tensorRtDynamicEngine;
        public bool TensorRtEngineStaticSelected => TensorRtEngineSettings == MainWindowViewModel._tensorRtStaticEngine;
        public bool TensorRtEngineStaticOnnxSelected => TensorRtEngineSettings == MainWindowViewModel._tensorRtStaticOnnx;
        public bool TensorRtEngineStaticBf16Selected => TensorRtEngineSettings == MainWindowViewModel._tensorRtStaticBf16Engine;

        private bool _tensorRtEngineSettingsAuto = true;
        [DataMember]
        public bool TensorRtEngineSettingsAuto
        {
            get => _tensorRtEngineSettingsAuto;
            set => this.RaiseAndSetIfChanged(ref _tensorRtEngineSettingsAuto, value);
        }


        private string _tensorRtEngineSettings = MainWindowViewModel._tensorRtDynamicEngine;
        [DataMember]
        public string TensorRtEngineSettings
        {
            get => _tensorRtEngineSettings;
            set
            {
                this.RaiseAndSetIfChanged(ref _tensorRtEngineSettings, value);
                this.RaisePropertyChanged(nameof(TensorRtEngineDynamicSelected));
                this.RaisePropertyChanged(nameof(TensorRtEngineStaticSelected));
                this.RaisePropertyChanged(nameof(TensorRtEngineStaticOnnxSelected));
                this.RaisePropertyChanged(nameof(TensorRtEngineStaticBf16Selected));
            }
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
            set
            {
                this.RaiseAndSetIfChanged(ref _showAdvancedSettings, value);
                this.RaisePropertyChanged(nameof(ShowTensorRtEngineSettings));
            }
        }

        public bool ShowTensorRtEngineSettings => ShowAdvancedSettings && TensorRtSelected;

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
        }

        public void SetFfmpegX264()
        {
            FfmpegVideoSettings = MainWindowViewModel._ffmpegX264;
        }

        public void SetFfmpegHevcNvenc()
        {
            FfmpegVideoSettings = MainWindowViewModel._ffmpegHevcNvenc;
        }

        public void SetFfmpegLossless()
        {
            FfmpegVideoSettings = MainWindowViewModel._ffmpegLossless;
        }

        public void SetTensorRtSelected()
        {
            TensorRtSelected = true;
            DirectMlSelected = false;
            NcnnSelected = false;
        }

        public void SetDirectMlSelected()
        {
            DirectMlSelected = true;
            TensorRtSelected = false;
            NcnnSelected = false;
        }

        public void SetNcnnSelected()
        {
            NcnnSelected = true;
            TensorRtSelected = false;
            DirectMlSelected = false;
        }

        public void SetDynamicEngine()
        {
            TensorRtEngineSettings = MainWindowViewModel._tensorRtDynamicEngine;
        }

        public void SetStaticEngine()
        {
            TensorRtEngineSettings = MainWindowViewModel._tensorRtStaticEngine;
        }

        public void SetStaticOnnx()
        {
            TensorRtEngineSettings = MainWindowViewModel._tensorRtStaticOnnx;
        }

        public void SetStaticBf16Engine()
        {
            TensorRtEngineSettings = MainWindowViewModel._tensorRtStaticBf16Engine;
        }

        public void SetTensorRtEngineSettingsAutoYes()
        {
            TensorRtEngineSettingsAuto = true;
        }

        public void SetTensorRtEngineSettingsAutoNo()
        {
            TensorRtEngineSettingsAuto = false;
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