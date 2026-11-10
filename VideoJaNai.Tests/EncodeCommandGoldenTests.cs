using AnimeJaNaiConverterGui.Services;
using Avalonia.Collections;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VideoJaNai.Core;
using VideoJaNai.ViewModels;
using Xunit;

namespace VideoJaNai.Tests
{
    // What the GUI hands aji_encode for a workflow: the conf it writes and the command line, for
    // each mode (encode, preview session). Pinned in Golden/*.txt, so moving the
    // policy into VideoJaNai.Core (shared with the videojanai CLI) can't change a byte of it.
    // VIDEOJANAI_UPDATE_GOLDEN=1 rewrites the files (only do that for an intended change).
    public class EncodeCommandGoldenTests : IDisposable
    {
        sealed class TempPythonService(string root) : IPythonService
        {
            public bool IsInferenceInstalled() => true;
            public bool IsTensorRtInstalled() => true;
            public bool IsTensorRtRtxInstalled() => true;
            public bool AreModelsInstalled() => true;
            public bool IsFfmpegInstalled() => true;
            public string BackendDirectory => root;
            public string AnimeJaNaiDirectory => Path.Join(root, "animejanai");
            public string ModelsDirectory => Path.Join(AnimeJaNaiDirectory, "onnx");
            public string FfmpegDirectory => Path.Join(root, "ffmpeg");
            public string FfmpegPath => Path.Join(FfmpegDirectory, "ffmpeg");
            public string InferenceDirectory => Path.Join(AnimeJaNaiDirectory, "inference");
            public string RifeModelsDirectory => Path.Join(AnimeJaNaiDirectory, "rife");
            public string AjiEncodePath => Path.Join(InferenceDirectory, PythonService.Exe("aji_encode"));
            public string TrtexecPath => Path.Join(InferenceDirectory, PythonService.Exe("trtexec"));
            public string ConfPath => Path.Join(AnimeJaNaiDirectory, "animejanai.conf");
            public void ExtractZip(string archivePath, string outFolder, ProgressChanged progressChanged) { }
            public AvaloniaList<string> AllModels { get; } = [];
        }

        const string Probe = """
            GPU index=0 vendor=nvidia default=1 integrated=0 id=9223372036854776064 name=NVIDIA GeForce RTX 5090
            GPU index=1 vendor=intel default=0 integrated=1 id=512 name=Intel(R) UHD Graphics 770
            ENCODER name=hevc_nvenc yuv420p=1 yuv420p10=1
            ENCODER name=av1_nvenc yuv420p=1 yuv420p10=1
            ENCODER name=hevc_qsv yuv420p=1 yuv420p10=1
            """;

        readonly string _root = Path.Combine(Path.GetTempPath(), $"vj-golden-{Guid.NewGuid():N}");
        readonly string _sourceModels;

        public EncodeCommandGoldenTests()
        {
            Directory.CreateDirectory(_root);
            // the bundled models: real onnx files, so temporal models read as temporal
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Join(dir.FullName, "VideoJaNai.sln")))
            {
                dir = dir.Parent;
            }
            _sourceModels = Path.Join(dir!.FullName, "BuildVideoJaNai", "onnx");
            var service = new TempPythonService(_root);
            Directory.CreateDirectory(service.ModelsDirectory);
            foreach (var f in Directory.GetFiles(_sourceModels, "temporalfix_*.onnx"))
            {
                File.Copy(f, Path.Join(service.ModelsDirectory, Path.GetFileName(f)));
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        string Model(string stem) => Path.Join(_sourceModels, stem + ".onnx");

        const string HdBalanced = "2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16";
        const string Sd = "2x_AnimeJaNai_SD_V1beta34_Compact_1x3xHxW_dyn-HW_strong_fp16_op21_dynamo";

        (MainWindowViewModel vm, UpscaleWorkflow wf) Create(Action<MainWindowViewModel>? app = null)
        {
            var vm = new MainWindowViewModel(new TempPythonService(_root), new FakeUpdateManagerService());
            var wf = new UpscaleWorkflow { Vm = vm, WorkflowIndex = 0, WorkflowName = "Workflow 1", UpscaleSettings = [new()] };
            vm.Workflows = [wf];
            app?.Invoke(vm);
            return (vm, wf);
        }

        // A workflow saved by the GUI (an exported .awf, and the saved state) runs the same in the
        // GUI, after loading it again, and in the command line, which reads it with Core's AppState.
        // (Loaded, not in memory: a decimal saved as 100.0 comes back as 100.0 in both.)
        void CheckCommandLineParity(MainWindowViewModel vm, string input, string output)
        {
            var awf = Path.Join(_root, "export.awf");
            vm.WriteCurrentWorkflowToFile(awf);
            var state = Path.Join(_root, "appstate2.json");
            new NewtonsoftJsonSuspensionDriver(state).SaveState(vm);
            vm.ReadWorkflowFileToCurrentWorkflow(awf);
            var gui = vm.CurrentJob();
            var saved = AppState.Load(state);
            Assert.Equal(vm.UseDirectMl, saved.UseDirectMl);
            Assert.Equal(vm.UseTensorRtRtx, saved.UseTensorRtRtx);
            Assert.Equal(vm.SelectedGpuId, saved.SelectedGpuId);
            foreach (var wf in new[] { AppState.LoadWorkflow(awf), saved.SelectedWorkflow! })
            {
                var cli = EncodeJob.FromWorkflow(wf, gui.Backend, gui.Device, vm.DefaultEncoderPreset, vm.ModelsDirectory);
                Assert.Equal(AjiConf.Build(gui), AjiConf.Build(cli));
                foreach (var mode in new[] { AjiMode.Encode, AjiMode.Preview })
                {
                    Assert.Equal(AjiArgs.Build(gui, vm.Paths, input, output, mode), AjiArgs.Build(cli, vm.Paths, input, output, mode));
                }
            }
        }

        string Render(MainWindowViewModel vm, string input, string output)
        {
            var parts = new List<string>();
            vm.SetupAnimeJaNaiConfSlot1();
            parts.Add("== conf");
            parts.Add(File.ReadAllText(Path.Join(_root, "animejanai", "animejanai.conf")).TrimEnd());
            parts.Add("== encode");
            parts.Add(vm.BuildAjiEncodeArgs(input, output, AjiMode.Encode));
            parts.Add("== preview");
            parts.Add(vm.BuildAjiEncodeArgs(input, null, AjiMode.Preview));
            var text = string.Join("\n", parts).Replace("\r\n", "\n");
            text = text.Replace(_root, "<root>").Replace(_sourceModels, "<models>").Replace(Path.GetTempPath().TrimEnd('\\', '/'), "<tmp>");
            text = Regex.Replace(text, @"videojanai-preview-\d+\.jpg", "videojanai-preview-<pid>.jpg");
            CheckCommandLineParity(vm, input, output);
            return text.Replace('\\', '/').Replace(".exe\"", "\"") + "\n";
        }

        static void Check(string name, string actual)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Join(dir.FullName, "VideoJaNai.Tests.csproj")))
            {
                dir = dir.Parent;
            }
            var path = Path.Join(dir!.FullName, "Golden", name + ".txt");
            if (Environment.GetEnvironmentVariable("VIDEOJANAI_UPDATE_GOLDEN") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, actual);
                return;
            }
            Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), actual);
        }

        [Fact]
        public void DefaultWorkflowOnTensorRt()
        {
            var (vm, wf) = Create(vm => vm.SetBackendTensorRt());
            wf.UpscaleSettings[0].OnnxModelPath = Model(HdBalanced);
            Check("default-tensorrt", Render(vm, "C:/in/ep01.mkv", "C:/out/ep01-animejanai.mkv"));
        }

        [Fact]
        public void ProbedGpuAndEncoderOnTensorRt()
        {
            var (vm, wf) = Create(vm =>
            {
                vm.EncoderProbeOutput = Probe;
                vm.SetBackendTensorRt();
                vm.SelectedGpuChoiceIndex = 1;
            });
            wf.UpscaleSettings[0].OnnxModelPath = Model(Sd);
            wf.SelectPreset(EncoderPresets.NvencAv1);
            wf.OverwriteExistingVideos = true;
            wf.FinalResizeFactor = 150;
            wf.TrtEngineSettings = "--builderOptimizationLevel=3 --minShapes=input:1x3x8x8 --optShapes=input:1x3x1080x1920 --maxShapes=input:1x3x1080x1920 --skipInference";
            Check("probed-gpu-tensorrt", Render(vm, "C:/in/ep01.mkv", "C:/out/ep01.mkv"));
        }

        [Fact]
        public void ChainWithRifeAndSourceOptionsOnTensorRtRtx()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "TensorRT-RTX is Windows only");
            var (vm, wf) = Create(vm => vm.SetBackendTensorRtRtx());
            wf.UpscaleSettings[0].OnnxModelPath = Model(Sd);
            wf.UpscaleSettings[0].ResizeHeightBeforeUpscale = 540;
            wf.UpscaleSettings.Add(new UpscaleModel { OnnxModelPath = Model(HdBalanced), ResizeFactorBeforeUpscale = 75, TemporalSceneThreshold = 0.2M });
            wf.EnableRife = true;
            wf.RifeModel = "RIFE 4.25 Lite";
            wf.RifeFactorNumerator = 5;
            wf.RifeFactorDenominator = 2;
            wf.RifeEnsemble = true;
            wf.RifeBeforeUpscale = false;
            wf.RifeSceneDetectThreshold = 0.3M;
            wf.Deinterlace = "ivtc";
            wf.SquarePixels = "before";
            wf.FinalResizeHeight = 1440;
            wf.FfmpegVideoSettings = EncoderPresets.X265.Settings;
            wf.OutputPixFmt = "yuv444p10";
            Check("chain-rife-tensorrt-rtx", Render(vm, "C:/in/dvd.vob", "C:/out/dvd.mkv"));
        }

        [Fact]
        public void TemporalFixBeforeUpscaleOnDirectMl()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "DirectML is Windows only");
            var (vm, wf) = Create(vm => vm.SetBackendDirectMl());
            wf.UpscaleSettings[0].OnnxModelPath = Model(HdBalanced);
            wf.EnableTemporalFix = true;
            wf.TemporalFixStrength = 3;
            wf.TemporalFixBeforeUpscale = true;
            wf.TemporalFixSceneThreshold = 0.25M;
            wf.TrtEngineSettings = "--builderOptimizationLevel=4";
            wf.FfmpegVideoSettings = "libx264 -crf 18 -preset medium";
            wf.OutputPixFmt = "yuv420p";
            wf.Deinterlace = "bwdif";
            wf.SquarePixels = "after";
            Check("temporalfix-directml", Render(vm, "C:/in/a b.mp4", "C:/out/a b.mkv"));
        }

        [Fact]
        public void TemporalFixOnlyWithUpscaleOff()
        {
            var (vm, wf) = Create(vm => vm.SetBackendTensorRt());
            wf.UpscaleSettings[0].OnnxModelPath = Model(HdBalanced);
            wf.EnableUpscale = false;
            wf.EnableTemporalFix = true;
            wf.FfmpegVideoSettings = EncoderPresets.Ffv1.Settings;
            Check("temporalfix-only-tensorrt", Render(vm, "C:/in/x.mkv", "C:/out/x.mkv"));
        }
    }
}
