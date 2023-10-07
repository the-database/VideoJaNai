using Avalonia.Collections;
using DynamicData;
using ReactiveUI;
using Salaros.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConverterGui.ViewModels
{
    [DataContract]
    public class MainWindowViewModel : ViewModelBase
    {
        public MainWindowViewModel() 
        {
            this.WhenAnyValue(x => x.InputFilePath, x => x.OutputFilePath, 
                x => x.InputFolderPath, x => x.OutputFolderPath,
                x => x.SelectedTabIndex).Subscribe(x =>
            {
                Validate();
            });
        }

        private CancellationTokenSource _cancellationTokenSource;
        private Process? _runningProcess = null;

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

        private string _validationText;
        public string ValidationText
        {
            get => _validationText;
            set
            {
                this.RaiseAndSetIfChanged(ref _validationText, value);
            }
        }

        private string _consoleText;
        public string ConsoleText
        {
            get => _consoleText;
            set
            {
                this.RaiseAndSetIfChanged(ref _consoleText, value);
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
            set  {
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

        private string _outputFilePath = string.Empty;
        [DataMember]
        public string OutputFilePath
        {
            get => _outputFilePath;
            set => this.RaiseAndSetIfChanged(ref _outputFilePath, value);
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

        private bool _showAdvancedSettings = false;
        [DataMember]
        public bool ShowAdvancedSettings
        {
            get => _showAdvancedSettings;
            set => this.RaiseAndSetIfChanged(ref _showAdvancedSettings, value);
        }

        private bool _valid = false;
        [IgnoreDataMember]
        public bool Valid
        {
            get => _valid;
            set
            {
                this.RaiseAndSetIfChanged(ref _valid, value);
                this.RaisePropertyChanged(nameof(UpscaleEnabled));
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

                if (string.IsNullOrWhiteSpace(OutputFilePath))
                {
                    valid = false;
                    validationText.Add("Output Video is required.");
                }
            }
            else
            {
                if (!Directory.Exists(InputFolderPath))
                {
                    valid = false;
                    validationText.Add("Input Folder is required.");
                }

                if (string.IsNullOrWhiteSpace(OutputFolderPath))
                {
                    valid = false;
                    validationText.Add("Output Folder is required.");
                }
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
            ValidationText = string.Join("\n", validationText);
        }

        public void SetupAnimeJaNaiConfSlot1()
        {
            var confPath = Path.GetFullPath(@".\mpv-upscale-2x_animejanai\portable_config\shaders\animejanai_v2.conf");
            var backend = DirectMlSelected ? "DirectML" : NcnnSelected ? "NCNN" : "TensorRT";
            HashSet<string> filesNeedingEngine = new();
            var configText = new StringBuilder($@"[global]
logging=yes
backend={backend}
[slot_1]
");

            for (var i = 0; i < UpscaleSettings.Count; i++)
            {
                var targetCopyPath = @$".\mpv-upscale-2x_animejanai\vapoursynth64\plugins\models\animejanai\{Path.GetFileName(UpscaleSettings[i].OnnxModelPath)}";

                if (Path.GetFullPath(targetCopyPath) != Path.GetFullPath(UpscaleSettings[i].OnnxModelPath))
                {
                    File.Copy(UpscaleSettings[i].OnnxModelPath, targetCopyPath, true);
                }

                configText.AppendLine(@$"chain_1_model_{i + 1}_resize_height_before_upscale={UpscaleSettings[i].ResizeHeightBeforeUpscale}
chain_1_model_{i + 1}_resize_factor_before_upscale={UpscaleSettings[i].ResizeFactorBeforeUpscale}
chain_1_model_{i + 1}_name={Path.GetFileNameWithoutExtension(UpscaleSettings[i].OnnxModelPath)}");
            }

            var rife = EnableRife ? "yes" : "no";
            configText.AppendLine($"chain_1_rife={rife}");

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
                var enginePath = @$".\mpv-upscale-2x_animejanai\vapoursynth64\plugins\models\animejanai\{Path.GetFileNameWithoutExtension(UpscaleSettings[i].OnnxModelPath)}.engine";

                if (!File.Exists(enginePath))
                {
                    await GenerateEngine(inputFilePath);
                }
            }

        }

        public async Task RunUpscale()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var ct = _cancellationTokenSource.Token;

            var task = Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested();
                ConsoleText = "";
                Upscaling = true;
                SetupAnimeJaNaiConfSlot1();

                if (SelectedTabIndex == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await CheckEngines(InputFilePath);
                    ct.ThrowIfCancellationRequested();
                    await RunUpscaleSingle(InputFilePath, OutputFilePath);
                    ct.ThrowIfCancellationRequested();
                }
                else
                {
                    var videoFileExtensions = new HashSet<string> { ".mp4", ".avi", ".mkv", ".mov", ".wmv" };
                    var files = Directory.GetFiles(InputFolderPath).Where(file => videoFileExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).ToList();

                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        await CheckEngines(file);
                        ct.ThrowIfCancellationRequested();
                        var outputFilePath = Path.Combine(OutputFolderPath, Path.GetFileName(file));
                        ct.ThrowIfCancellationRequested();
                        await RunUpscaleSingle(file, outputFilePath);
                        ct.ThrowIfCancellationRequested();
                    }
                }

                Valid = true;
            }, ct);

            try
            {
                await task;
            }
            catch (OperationCanceledException e)
            {
                Console.WriteLine($"{nameof(OperationCanceledException)} thrown with message: {e.Message}");
                Upscaling = false;
            }
            finally
            {
                _cancellationTokenSource.Dispose();
                Upscaling = false;
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
            }
            catch { }
            
        }

        public async Task RunUpscaleSingle(string inputFilePath, string outputFilePath)
        {
            var cmd = $@"..\..\VSPipe.exe -c y4m --arg ""slot=1"" --arg ""video_path={inputFilePath}"" ./animejanai_v2_encode.vpy - | ffmpeg {_overwriteCommand} -i pipe: -i ""{inputFilePath}"" -map 0:v -c:v {FfmpegVideoSettings} -map 1:t? -map 1:a?  -map 1:s? -c:t copy -c:a copy -c:s copy ""{outputFilePath}""";
            ConsoleText += $@"Upscaling with command: {cmd}";
            await RunCommand($@" /C {cmd}");
        }

        public async Task GenerateEngine(string inputFilePath)
        {
            var cmd = $@"..\..\VSPipe.exe -c y4m --arg ""slot=1"" --arg ""video_path={inputFilePath}"" --start 0 --end 1 ./animejanai_v2_encode.vpy -p .";
            ConsoleText += $"Generating TensorRT engine with command: {cmd}";
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
                process.StartInfo.WorkingDirectory = Path.GetFullPath(@".\mpv-upscale-2x_animejanai\portable_config\shaders");

                // Create a StreamWriter to write the output to a log file
                using (var outputFile = new StreamWriter("error.log", append: true))
                {
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            outputFile.WriteLine(e.Data); // Write the output to the log file
                            ConsoleText += e.Data + "\n";
                        }
                    };

                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            outputFile.WriteLine(e.Data); // Write the output to the log file
                            ConsoleText += e.Data + "\n";
                        }
                    };

                    process.Start();
                    process.BeginErrorReadLine(); // Start asynchronous reading of the output
                    await process.WaitForExitAsync();
                }
                ChildProcessTracker.AddProcess(process);
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

        private int _resizeHeightBeforeUpscale = 0;
        [DataMember]
        public int ResizeHeightBeforeUpscale
        {
            get => _resizeHeightBeforeUpscale; 
            set => this.RaiseAndSetIfChanged(ref _resizeHeightBeforeUpscale, value);
        }

        private double _resizeFactorBeforeUpscale = 1.0;
        [DataMember]
        public double ResizeFactorBeforeUpscale
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