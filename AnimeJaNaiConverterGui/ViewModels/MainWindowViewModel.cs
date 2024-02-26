using Avalonia.Collections;
using Avalonia.Controls;
using DynamicData;
using ReactiveUI;
using Salaros.Configuration;
using SevenZipExtractor;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConverterGui.ViewModels
{
    [DataContract]
    public class MainWindowViewModel : ViewModelBase
    {
        public static readonly List<string> VIDEO_EXTENSIONS = [".mkv", ".mp4", ".mpg", ".mpeg", ".avi", ".mov", ".wmv"];

        public MainWindowViewModel()
        {
            this.WhenAnyValue(
                x => x.InputFilePath, x => x.OutputFilename,
                x => x.InputFolderPath, x => x.OutputFolderPath,
                x => x.SelectedTabIndex, x => x.OverwriteExistingVideos).Subscribe(x =>
            {
                Validate();
            });
        }

        private CancellationTokenSource? _cancellationTokenSource;
        private Process? _runningProcess = null;

        public string AppVersion => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private bool _autoUpdate = true;
        [DataMember]
        public bool AutoUpdateEnabled
        {
            get => _autoUpdate;
            set => this.RaiseAndSetIfChanged(ref _autoUpdate, value);
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

        private string _overwriteCommand => OverwriteExistingVideos ? "-y" : "";

        private static readonly string _ffmpegX265 = "libx265 -crf 16 -preset slow -x265-params \"sao=0:bframes=8:psy-rd=1.5:psy-rdoq=2:aq-mode=3:ref=6\"";
        private static readonly string _ffmpegX264 = "libx264 -crf 13 -preset slow";
        private static readonly string _ffmpegHevcNvenc = "hevc_nvenc -preset p7 -profile:v main10 -b:v 50M";
        private static readonly string _ffmpegLossless = "ffv1";

        public bool FfmpegX265Selected => FfmpegVideoSettings == _ffmpegX265;
        public bool FfmpegX264Selected => FfmpegVideoSettings == _ffmpegX264;
        public bool FfmpegHevcNvencSelected => FfmpegVideoSettings == _ffmpegHevcNvenc;


        public bool FfmpegLosslessSelected => FfmpegVideoSettings == _ffmpegLossless;

        private string _ffmpegVideoSettings = _ffmpegHevcNvenc;
        [DataMember]
        public string FfmpegVideoSettings
        {
            get => _ffmpegVideoSettings;
            set {
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
            }
        }

        private string _finalResizeHeight = 0.ToString();
        [DataMember]
        public string FinalResizeHeight
        {
            get => _finalResizeHeight;
            set => this.RaiseAndSetIfChanged(ref _finalResizeHeight, value);
        }

        private string _finalResizeFactor = 100.ToString();
        [DataMember]
        public string FinalResizeFactor
        {
            get => _finalResizeFactor;
            set => this.RaiseAndSetIfChanged(ref _finalResizeFactor, value);
        }

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
        private static readonly Dictionary<string, string> rifeModelMapping = new()
        {
            { "RIFE 4.14", 414.ToString() },
            { "RIFE 4.14 Lite", 4141.ToString() },
            { "RIFE 4.13", 413.ToString() },
            { "RIFE 4.13 Lite", 413.ToString() },
            { "RIFE 4.6", 46.ToString() },
        };
        
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

        private string _rifeFactorNumerator = 2.ToString();
        [DataMember]
        public string RifeFactorNumerator
        {
            get => _rifeFactorNumerator;
            set => this.RaiseAndSetIfChanged(ref _rifeFactorNumerator, value);
        }

        private string _rifeFactorDenominator = 1.ToString();
        [DataMember]
        public string RifeFactorDenominator
        {
            get => _rifeFactorDenominator;
            set => this.RaiseAndSetIfChanged(ref _rifeFactorDenominator, value);
        }

        private string _rifeSceneDetectThreshold = 0.150.ToString();
        [DataMember]
        public string RifeSceneDetectThreshold
        {
            get => _rifeSceneDetectThreshold;
            set => this.RaiseAndSetIfChanged(ref _rifeSceneDetectThreshold, value);
        }

        private bool _showAdvancedSettings = false;
        [DataMember]
        public bool ShowAdvancedSettings
        {
            get => _showAdvancedSettings;
            set => this.RaiseAndSetIfChanged(ref _showAdvancedSettings, value);
        }

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

        public string LeftStatus => !Valid ? ValidationText.Replace("\n", " ") : $"{InputStatusText} selected for upscaling.";
        //public string LeftStatus
        //{
        //    get 
        //    {
        //        return !Valid ? ValidationText.Replace("\n", " ") : $"{InputStatusText} selected for upscaling.";
        //    }
        //}

        private bool _valid = false;
        [IgnoreDataMember]
        public bool Valid
        {
            get => _valid;
            set
            {
                this.RaiseAndSetIfChanged(ref _valid, value);
                this.RaisePropertyChanged(nameof(UpscaleEnabled));
                this.RaisePropertyChanged(nameof(LeftStatus));
            }
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

        public bool UpscaleEnabled => Valid && !Upscaling;

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
            FfmpegVideoSettings = _ffmpegX265;
        }

        public void SetFfmpegX264()
        {
            FfmpegVideoSettings = _ffmpegX264;
        }

        public void SetFfmpegHevcNvenc()
        {
            FfmpegVideoSettings = _ffmpegHevcNvenc;
        }

        public void SetFfmpegLossless()
        {
            FfmpegVideoSettings = _ffmpegLossless;
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

        private int CheckInputs()
        {

            InputStatusText = "0 video files";

            if (Valid && !Upscaling)
            {
                var overwriteText = OverwriteExistingVideos ? "overwritten" : "skipped";

                // input file
                if (SelectedTabIndex == 0)
                {
                    StringBuilder status = new();
                    var skipFiles = 0;

                    var outputFilePath = Path.Join(
                                                    Path.GetFullPath(OutputFolderPath),
                                                    OutputFilename.Replace("%filename%", Path.GetFileNameWithoutExtension(InputFilePath))); 

                    if (File.Exists(outputFilePath))
                    {
                        status.Append($" (1 video file already exists and will be {overwriteText})");
                        if (!OverwriteExistingVideos)
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
                    
                    var videos = Directory.EnumerateFiles(InputFolderPath, "*.*", SearchOption.AllDirectories)
                        .Where(file => VIDEO_EXTENSIONS.Any(ext => file.ToLower().EndsWith(ext)));
                    var filesCount = 0;

                    foreach (var inputVideoPath in videos)
                    {
                        var outputFilePath = Path.Join(
                                                    Path.GetFullPath(OutputFolderPath),
                                                    OutputFilename.Replace("%filename%", Path.GetFileNameWithoutExtension(inputVideoPath)));

                        // if out file exists, exist count ++
                        // if overwrite image OR out file doesn't exist, count image++
                        var fileExists = File.Exists(outputFilePath);

                        if (fileExists)
                        {
                            existFileCount++;
                        }

                        if (!fileExists || OverwriteExistingVideos)
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
            var numFilesToUpscale = CheckInputs();
            if (numFilesToUpscale == 0)
            {
                Valid = false;
                validationText.Add($"{InputStatusText} selected for upscaling. At least one file must be selected.");
            }
            ValidationText = string.Join("\n", validationText);
        }

        public void SetupAnimeJaNaiConfSlot1()
        {
            var confPath = Path.GetFullPath(@".\mpv-upscale-2x_animejanai\animejanai\animejanai.conf");
            var backend = DirectMlSelected ? "DirectML" : NcnnSelected ? "NCNN" : "TensorRT";
            HashSet<string> filesNeedingEngine = new();
            var configText = new StringBuilder($@"[global]
logging=yes
backend={backend}
[slot_1]
profile_name=encode
");

            for (var i = 0; i < UpscaleSettings.Count; i++)
            {
                var targetCopyPath = @$".\mpv-upscale-2x_animejanai\animejanai\onnx\{Path.GetFileName(UpscaleSettings[i].OnnxModelPath)}";

                if (Path.GetFullPath(targetCopyPath) != Path.GetFullPath(UpscaleSettings[i].OnnxModelPath))
                {
                    File.Copy(UpscaleSettings[i].OnnxModelPath, targetCopyPath, true);
                }

                configText.AppendLine(@$"chain_1_model_{i + 1}_resize_height_before_upscale={UpscaleSettings[i].ResizeHeightBeforeUpscale}
chain_1_model_{i + 1}_resize_factor_before_upscale={UpscaleSettings[i].ResizeFactorBeforeUpscale}
chain_1_model_{i + 1}_name={Path.GetFileNameWithoutExtension(UpscaleSettings[i].OnnxModelPath)}");
            }

            var rife = EnableRife ? "yes" : "no";
            var ensemble = RifeEnsemble ? "yes" : "no";
            configText.AppendLine($"chain_1_rife={rife}");
            configText.AppendLine($"chain_1_rife_factor_numerator={RifeFactorNumerator}");
            configText.AppendLine($"chain_1_rife_factor_denominator={RifeFactorDenominator}");
            configText.AppendLine($"chain_1_rife_model={rifeModelMapping[RifeModel]}");
            configText.AppendLine($"chain_1_rife_ensemble={ensemble}");
            configText.AppendLine($"chain_1_rife_scene_detect_threshold={RifeSceneDetectThreshold}");
            configText.AppendLine($"chain_1_final_resize_height={FinalResizeHeight}");
            configText.AppendLine($"chain_1_final_resize_factor={FinalResizeFactor}");

            File.WriteAllText(confPath, configText.ToString());
        }

        public async Task CheckEngines(string inputFilePath)
        {
            if (!TensorRtSelected)
            {
                return;
            }

            for (var i = 0; i < UpscaleSettings.Count; i++)
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

                if (SelectedTabIndex == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await CheckEngines(InputFilePath);
                    ct.ThrowIfCancellationRequested();
                    var outputFilePath = Path.Join(
                                Path.GetFullPath(OutputFolderPath),
                                OutputFilename.Replace("%filename%", Path.GetFileNameWithoutExtension(InputFilePath)));
                    await RunUpscaleSingle(InputFilePath, outputFilePath);
                    ct.ThrowIfCancellationRequested();
                }
                else
                {
                    var files = Directory.GetFiles(InputFolderPath).Where(file => VIDEO_EXTENSIONS.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).ToList();

                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        await CheckEngines(file);
                        ct.ThrowIfCancellationRequested();
                        var outputFilePath = Path.Join(
                                Path.GetFullPath(OutputFolderPath),
                                OutputFilename.Replace("%filename%", Path.GetFileNameWithoutExtension(file)));
                        ct.ThrowIfCancellationRequested();
                        await RunUpscaleSingle(file, outputFilePath);
                        ct.ThrowIfCancellationRequested();
                    }
                }

                Valid = true;
            //}, ct);

            try
            {
                //await task;
                Validate();
            }
            catch (OperationCanceledException e)
            {
                Console.WriteLine($"{nameof(OperationCanceledException)} thrown with message: {e.Message}");
                Upscaling = false;
                Validate();
            }
            finally
            {
                _cancellationTokenSource.Dispose();
                Upscaling = false;
                Validate();
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
                Validate();
            }
            catch { }
            
        }

        public async Task RunUpscaleSingle(string inputFilePath, string outputFilePath)
        {
            var cmd = $@"..\..\VSPipe.exe -c y4m --arg ""slot=1"" --arg ""video_path={inputFilePath}"" ./animejanai_encode.vpy - | ffmpeg {_overwriteCommand} -i pipe: -i ""{inputFilePath}"" -map 0:v -c:v {FfmpegVideoSettings} -map 1:t? -map 1:a?  -map 1:s? -c:t copy -c:a copy -c:s copy ""{outputFilePath}""";
            ConsoleQueueEnqueue($"Upscaling with command: {cmd}");
            await RunCommand($@" /C {cmd}");
        }

        public async Task GenerateEngine(string inputFilePath)
        {
            var cmd = $@"..\..\VSPipe.exe -c y4m --arg ""slot=1"" --arg ""video_path={inputFilePath}"" --start 0 --end 1 ./animejanai_encode.vpy -p .";
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
                process.StartInfo.WorkingDirectory = Path.GetFullPath(@".\mpv-upscale-2x_animejanai\animejanai\core");

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

        public void CheckAndExtractBackend()
        {
            Task.Run(() =>
            {
                var backendArchivePath = Path.GetFullPath("./mpv-upscale-2x_animejanai.7z");

                if (File.Exists(backendArchivePath))
                {
                    IsExtractingBackend = true;
                    using ArchiveFile archiveFile = new(backendArchivePath);
                    archiveFile.Extract(".");
                    archiveFile.Dispose();
                    File.Delete(backendArchivePath);
                    IsExtractingBackend = false;
                }
            });            
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

        private string _resizeHeightBeforeUpscale = 0.ToString();
        [DataMember]
        public string ResizeHeightBeforeUpscale
        {
            get => _resizeHeightBeforeUpscale; 
            set => this.RaiseAndSetIfChanged(ref _resizeHeightBeforeUpscale, value);
        }

        private string _resizeFactorBeforeUpscale = 100.ToString();
        [DataMember]
        public string ResizeFactorBeforeUpscale
        {
            get => _resizeFactorBeforeUpscale;
            set => this.RaiseAndSetIfChanged(ref _resizeFactorBeforeUpscale, value);
        }

        private string _onnxModelPath = string.Empty;
        [DataMember]
        public string OnnxModelPath
        {
            get => _onnxModelPath;
            set => this.RaiseAndSetIfChanged(ref _onnxModelPath, value);
        }
    }
}