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
using System.Text;

namespace AnimeJaNaiConverterGui.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        public MainWindowViewModel() 
        {
            AddModel();

            this.WhenAnyValue(x => x.InputFilePath, x => x.OutputFilePath, 
                x => x.InputFolderPath, x => x.OutputFolderPath,
                x => x.SelectedTabIndex).Subscribe(x =>
            {
                Validate();
            });
        }

        private int _selectedTabIndex;
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

        private string _consoleText;
        public string ConsoleText
        {
            get => _consoleText;
            set
            {
                if (_consoleText != value)
                {
                    this.RaiseAndSetIfChanged(ref _consoleText, value);
                }
            }
        }


        private static readonly string _ffmpegX265 = "libx265 -crf 16 -preset slow -x265-params \"sao=0:bframes=8:psy-rd=1.5:psy-rdoq=2:aq-mode=3:ref=6\"";
        private static readonly string _ffmpegX264 = "libx264 -crf 13 -preset slow";
        private static readonly string _ffmpegHevcNvenc = "hevc_nvenc -preset p7 -profile:v main10 -b:v 50M";
        private static readonly string _ffmpegLossless = "ffv1";

        public bool FfmpegX265Selected => FfmpegVideoSettings == _ffmpegX265;
        public bool FfmpegX264Selected => FfmpegVideoSettings == _ffmpegX264;
        public bool FfmpegHevcNvencSelected => FfmpegVideoSettings == _ffmpegHevcNvenc;


        public bool FfmpegLosslessSelected => FfmpegVideoSettings == _ffmpegLossless;

        private string _ffmpegVideoSettings = _ffmpegHevcNvenc;
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

        private string _inputFilePath = string.Empty;
        public string InputFilePath
        {
            get => _inputFilePath;
            set => this.RaiseAndSetIfChanged(ref _inputFilePath, value);
        }

        private string _inputFolderPath = string.Empty;
        public string InputFolderPath
        {
            get => _inputFolderPath;
            set => this.RaiseAndSetIfChanged(ref _inputFolderPath, value);
        }

        private string _outputFilePath = string.Empty;
        public string OutputFilePath
        {
            get => _outputFilePath;
            set => this.RaiseAndSetIfChanged(ref _outputFilePath, value);
        }

        private string _outputFolderPath = string.Empty;
        public string OutputFolderPath
        {
            get => _outputFolderPath;
            set => this.RaiseAndSetIfChanged(ref _outputFolderPath, value);
        }

        private AvaloniaList<UpscaleModel> _upscaleSettings = new();
        public AvaloniaList<UpscaleModel> UpscaleSettings
        {
            get => _upscaleSettings;
            set => this.RaiseAndSetIfChanged(ref _upscaleSettings, value);
        }

        private bool _enableRife = false;
        public bool EnableRife
        {
            get => _enableRife;
            set => this.RaiseAndSetIfChanged(ref _enableRife, value);
        }

        private bool _showAdvancedSettings = false;
        public bool ShowAdvancedSettings
        {
            get => _showAdvancedSettings;
            set => this.RaiseAndSetIfChanged(ref _showAdvancedSettings, value);
        }

        private bool _valid = false;
        public bool Valid
        {
            get => _valid;
            set => this.RaiseAndSetIfChanged(ref _valid, value);
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

        public void Validate()
        {
            if (SelectedTabIndex == 0)
            {
                var fileModeIsValid = File.Exists(InputFilePath) && !string.IsNullOrWhiteSpace(OutputFilePath);
                if (!fileModeIsValid)
                {
                    Valid = false;
                    return;
                }
            }
            else
            {
                var folderModeIsValid = Directory.Exists(InputFolderPath) && !string.IsNullOrWhiteSpace(OutputFolderPath);
                if (!folderModeIsValid)
                {
                    Valid = false;
                    return;
                }
            }

            foreach (var upscaleModel in UpscaleSettings)
            {
                if (!File.Exists(upscaleModel.OnnxModelPath))
                {
                    Valid = false;
                    return;
                }
            }

            Valid = true;
        }

        public void SetupAnimeJaNaiConfSlot1()
        {
            var confPath = Path.GetFullPath(@".\mpv-upscale-2x_animejanai\portable_config\shaders\animejanai_v2.conf");

            var configText = new StringBuilder(@"[global]
logging=yes
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

        public void RunUpscale()
        {
            SetupAnimeJaNaiConfSlot1();

            if (SelectedTabIndex == 0)
            {
                RunUpscaleSingle(InputFilePath, OutputFilePath);
            }
            else
            {
                var videoFileExtensions = new HashSet<string>{ ".mp4", ".avi", ".mkv", ".mov", ".wmv" };
                var files = Directory.GetFiles(InputFolderPath).Where(file => videoFileExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)).ToList();

                foreach (var file in files)
                {
                    var outputFilePath = Path.Combine(OutputFolderPath, Path.GetFileName(file));
                    RunUpscaleSingle(file, outputFilePath);
                }
            }

            return;
        }

        public void RunUpscaleSingle(string inputFilePath, string outputFilePath)
        {
            // Create a new process to run the CMD command
            using (var process = new Process())
            {
                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.Arguments = $@" /C ..\..\VSPipe.exe -c y4m --arg ""slot=1"" --arg ""video_path={inputFilePath}"" ./animejanai_v2_encode.vpy - | ffmpeg -i pipe: -i ""{inputFilePath}"" -map 0:v -c:v {FfmpegVideoSettings} -map 1:t? -map 1:a?  -map 1:s? -c:t copy -c:a copy -c:s copy ""{outputFilePath}""";
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = false;
                process.StartInfo.WorkingDirectory = Path.GetFullPath(@".\mpv-upscale-2x_animejanai\portable_config\shaders");

                process.Start();
                ChildProcessTracker.AddProcess(process);
                process.WaitForExit();
            }
        }
    }

    public class UpscaleModel : ReactiveObject
    {
        private string _modelHeader = string.Empty;
        public string ModelHeader
        {
            get => _modelHeader;
            set => this.RaiseAndSetIfChanged(ref _modelHeader, value);
        }

        private int _resizeHeightBeforeUpscale = 0;
        public int ResizeHeightBeforeUpscale
        {
            get => _resizeHeightBeforeUpscale; 
            set => this.RaiseAndSetIfChanged(ref _resizeHeightBeforeUpscale, value);
        }

        private double _resizeFactorBeforeUpscale = 1.0;
        public double ResizeFactorBeforeUpscale
        {
            get => _resizeFactorBeforeUpscale;
            set => this.RaiseAndSetIfChanged(ref _resizeFactorBeforeUpscale, value);
        }

        private string _onnxModelPath = string.Empty;
        public string OnnxModelPath
        {
            get => _onnxModelPath;
            set => this.RaiseAndSetIfChanged(ref _onnxModelPath, value);
        }
    }
}