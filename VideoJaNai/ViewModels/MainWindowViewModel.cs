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
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Runtime.Serialization;
using System.Text;
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

            _um = new UpdateManager(new GithubSource("https://github.com/the-database/VideoJaNai", null, false));
            CheckForUpdates();
        }

        private CancellationTokenSource? _cancellationTokenSource;
        private Process? _runningProcess = null;

        private static readonly Dictionary<string, string> rifeModelMapping = new()
        {
            { "RIFE 4.14", 414.ToString() },
            { "RIFE 4.14 Lite", 4141.ToString() },
            { "RIFE 4.13", 413.ToString() },
            { "RIFE 4.13 Lite", 413.ToString() },
            { "RIFE 4.6", 46.ToString() },
        };

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
        //public string LeftStatus
        //{
        //    get 
        //    {
        //        return !Valid ? ValidationText.Replace("\n", " ") : $"{InputStatusText} selected for upscaling.";
        //    }
        //}



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

        public void SetupAnimeJaNaiConfSlot1()
        {
            var confPath = Path.Combine(_pythonService.AnimeJaNaiDirectory, "animejanai.conf");
            var backend = CurrentWorkflow.DirectMlSelected ? "DirectML" : CurrentWorkflow.NcnnSelected ? "NCNN" : "TensorRT";
            HashSet<string> filesNeedingEngine = new();
            var configText = new StringBuilder($@"[global]
logging=yes
backend={backend}
[slot_1]
profile_name=encode
");

            for (var i = 0; i < CurrentWorkflow.UpscaleSettings.Count; i++)
            {
                var targetCopyPath = Path.Combine(_pythonService.ModelsDirectory, Path.GetFileName(CurrentWorkflow.UpscaleSettings[i].OnnxModelPath)); // TODO @$".\mpv-upscale-2x_animejanai\animejanai\onnx\{

                if (Path.GetFullPath(targetCopyPath) != Path.GetFullPath(CurrentWorkflow.UpscaleSettings[i].OnnxModelPath))
                {
                    File.Copy(CurrentWorkflow.UpscaleSettings[i].OnnxModelPath, targetCopyPath, true);
                }

                configText.AppendLine(@$"chain_1_model_{i + 1}_resize_height_before_upscale={CurrentWorkflow.UpscaleSettings[i].ResizeHeightBeforeUpscale}
chain_1_model_{i + 1}_resize_factor_before_upscale={CurrentWorkflow.UpscaleSettings[i].ResizeFactorBeforeUpscale}
chain_1_model_{i + 1}_name={Path.GetFileNameWithoutExtension(CurrentWorkflow.UpscaleSettings[i].OnnxModelPath)}");
            }

            var rife = CurrentWorkflow.EnableRife ? "yes" : "no";
            var ensemble = CurrentWorkflow.RifeEnsemble ? "yes" : "no";
            configText.AppendLine($"chain_1_rife={rife}");
            configText.AppendLine($"chain_1_rife_factor_numerator={CurrentWorkflow.RifeFactorNumerator}");
            configText.AppendLine($"chain_1_rife_factor_denominator={CurrentWorkflow.RifeFactorDenominator}");
            configText.AppendLine($"chain_1_rife_model={rifeModelMapping[CurrentWorkflow.RifeModel]}");
            configText.AppendLine($"chain_1_rife_ensemble={ensemble}");
            configText.AppendLine($"chain_1_rife_scene_detect_threshold={CurrentWorkflow.RifeSceneDetectThreshold}");
            configText.AppendLine($"chain_1_final_resize_height={CurrentWorkflow.FinalResizeHeight}");
            configText.AppendLine($"chain_1_final_resize_factor={CurrentWorkflow.FinalResizeFactor}");
            configText.AppendLine($"chain_1_tensorrt_engine_settings={CurrentWorkflow.TensorRtEngineSettings}");

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
            var cmd = $@"{_pythonService.VspipePath} -c y4m --arg ""slot=1"" --arg ""video_path={inputFilePath}"" ./animejanai_encode.vpy - | {_pythonService.FfmpegPath} {_overwriteCommand} -i pipe: -i ""{inputFilePath}"" -map 0:v -c:v {CurrentWorkflow.FfmpegVideoSettings} -max_interleave_delta 0 -map 1:t? -map 1:a?  -map 1:s? -c:t copy -c:a copy -c:s copy ""{outputFilePath}""";
            ConsoleQueueEnqueue($"Upscaling with command: {cmd}");
            await RunCommand($@" /C {cmd}");
        }

        public async Task GenerateEngine(string inputFilePath)
        {
            var cmd = $@"{_pythonService.VspipePath} -c y4m --arg ""slot=1"" --arg ""video_path={inputFilePath}"" --start 0 --end 1 ./animejanai_encode.vpy -p .";
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
                process.StartInfo.WorkingDirectory = Path.Combine(_pythonService.AnimeJaNaiDirectory, "core");

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

        public void CheckAndExtractBackend()
        {
            Task.Run(async () =>
            {
                IsExtractingBackend = true;

                if (!_pythonService.IsPythonInstalled())
                {
                    // 1. Install embedded Python + portable VS
                    await InstallPortableVapourSynth();

                    // 2. Python dependencies
                    await RunInstallCommand(_pythonService.InstallUpdatePythonDependenciesCommand);

                    // 3. VapourSynth plugins
                    await RunInstallCommand(_pythonService.InstallVapourSynthPluginsCommand);
                    await InstallVapourSynthMiscFilters();

                    // 4. vs-mlrt
                    await InstallVsmlrt();

                    // 5. ffmpeg
                    await InstallFfmpeg();
                }
                else
                {

                }

                if (!_pythonService.AreModelsInstalled())
                {
                    await InstallModels();
                }

                IsExtractingBackend = false;
            });
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
                    Debug.WriteLine("DataAdded");
                    Debug.WriteLine(e.Index);
                    Debug.WriteLine(outputCollection[e.Index]);
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

        private async Task InstallVsmlrt()
        {
            BackendSetupMainStatus = "Downloading vs-mlrt...";
            var downloadUrl = "https://github.com/AmusementClub/vs-mlrt/releases/download/v14.test3/vsmlrt-windows-x64-cuda.v14.test3.7z";
            var targetPath = Path.Join(_pythonService.BackendDirectory, "vsmlrt.7z");
            await Downloader.DownloadFileAsync(downloadUrl, targetPath, (progress) =>
            {
                BackendSetupMainStatus = $"Downloading vs-mlrt ({progress}%)...";
            });

            BackendSetupMainStatus = "Extracting vs-mlrt (this may take several minutes)...";
            using (ArchiveFile archiveFile = new(targetPath))
            {
                var targetDirectory = Path.Join(_pythonService.VapourSynthPluginsPath);
                Directory.CreateDirectory(targetDirectory);
                archiveFile.Extract(targetDirectory);
                File.Move(Path.Combine(targetDirectory, "vsmlrt.py"), Path.Combine(_pythonService.PythonDirectory, "vsmlrt.py"));
            }

            File.Delete(targetPath);
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

        private List<string> _rifeModelList = ["RIFE 4.14", "RIFE 4.14 Lite", "RIFE 4.13", "RIFE 4.13 Lite", "RIFE 4.6"];

        public List<string> RifeModelList
        {
            get => _rifeModelList;
            set => this.RaiseAndSetIfChanged(ref _rifeModelList, value);
        }

        private string _rifeModel = "RIFE 4.14";
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