using System;
using System.IO;
using System.Linq;
using VideoJaNai.Core;
using Xunit;

namespace VideoJaNai.Core.Tests
{
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vjcore-{Guid.NewGuid():N}");
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string rel, string text = "x")
        {
            var p = System.IO.Path.Join(Path, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            System.IO.File.WriteAllText(p, text);
            return p;
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    public class PresetTests
    {
        [Fact]
        public void BalancedIsTheHdModelFor720To1080AndSdBelow()
        {
            using var t = new TempDir();
            var job = Presets.Job("balanced", new WorkflowSettings(), Backend.TensorRt, null, EncoderPresets.NvencHevc, t.Path);
            Assert.Equal(2, job.Chains.Count);
            Assert.Equal(("1280x720", "1920x1080"), (job.Chains[0].MinResolution, job.Chains[0].MaxResolution));
            Assert.Equal(Presets.HdBalanced, Path.GetFileNameWithoutExtension(job.Chains[0].Models.Single().OnnxPath));
            Assert.Equal(("0x0", "1280x720"), (job.Chains[1].MinResolution, job.Chains[1].MaxResolution));
            Assert.Equal(Presets.Sd, Path.GetFileNameWithoutExtension(job.Chains[1].Models.Single().OnnxPath));

            var conf = AjiConf.Build(job).Replace("\r\n", "\n");
            Assert.Contains("chain_1_min_resolution=1280x720\nchain_1_max_resolution=1920x1080\nchain_1_min_fps=0\nchain_1_max_fps=0\n", conf);
            Assert.Contains($"chain_1_model_1_name={Presets.HdBalanced}\n", conf);
            Assert.Contains("chain_2_min_resolution=0x0\nchain_2_max_resolution=1280x720\n", conf);
            Assert.Contains($"chain_2_model_1_name={Presets.Sd}\n", conf);
            Assert.Contains("chain_2_rife=no\n", conf);
        }

        [Fact]
        public void APresetKeepsTheTemporalFixAroundEachChainsModel()
        {
            using var t = new TempDir();
            t.File("temporalfix_s3_v1_op18_fp16.onnx");
            var wf = new WorkflowSettings { EnableTemporalFix = true, TemporalFixStrength = 3 };
            var job = Presets.Job("performance-sharp", wf, Backend.TensorRtRtx, "7", EncoderPresets.X265, t.Path);
            foreach (var chain in job.Chains)
            {
                Assert.Equal(2, chain.Models.Count);
                Assert.Equal("temporalfix_s3_v1_op18_fp16", Path.GetFileNameWithoutExtension(chain.Models[1].OnnxPath));
            }
            Assert.Equal(Presets.HdSharpPerformance, Path.GetFileNameWithoutExtension(job.Chains[0].Models[0].OnnxPath));
            Assert.Equal("7", job.Device);
            Assert.Equal(EncoderPresets.X265.Settings, job.FfmpegVideoSettings);
        }

        [Fact]
        public void MissingModelsAreNamed()
        {
            using var t = new TempDir();
            t.File(Presets.Sd + ".onnx");
            Assert.Equal([Presets.HdBalanced + ".onnx"], Presets.MissingModels("balanced", t.Path).ToList());
        }
    }

    public class EncodeJobTests
    {
        [Fact]
        public void TheDefaultEncoderStandsInForAnEmptyOrLegacySetting()
        {
            foreach (var settings in new[] { "", "  ", EncoderPresets.LegacyDefaultSettings })
            {
                var job = EncodeJob.FromWorkflow(new WorkflowSettings { FfmpegVideoSettings = settings }, Backend.TensorRt, null, EncoderPresets.AmfHevc, "");
                Assert.Equal(EncoderPresets.AmfHevc.Settings, job.FfmpegVideoSettings);
            }
            var own = EncodeJob.FromWorkflow(new WorkflowSettings { FfmpegVideoSettings = "libx264 -crf 20" }, Backend.TensorRt, null, EncoderPresets.AmfHevc, "");
            Assert.Equal("libx264 -crf 20", own.FfmpegVideoSettings);
        }

        [Fact]
        public void UpscaleOffKeepsOnlyTheOtherSteps()
        {
            using var t = new TempDir();
            t.File("temporalfix_s2_v1_op18_fp16.onnx");
            var wf = new WorkflowSettings
            {
                EnableUpscale = false,
                EnableTemporalFix = true,
                UpscaleSettings = [new ModelSettings { OnnxModelPath = "a.onnx" }],
            };
            var job = EncodeJob.FromWorkflow(wf, Backend.TensorRt, null, EncoderPresets.NvencHevc, t.Path);
            Assert.Equal("temporalfix_s2_v1_op18_fp16", Path.GetFileNameWithoutExtension(job.Chains[0].Models.Single().OnnxPath));
        }

        [Fact]
        public void TheLastTemporalFixVersionByNameIsUsed()
        {
            // (by name: v1_ sorts after v1.1_, so a v1 left next to a v1.1 would win; the bundle
            // has one version per strength)
            using var t = new TempDir();
            t.File("temporalfix_s1_v1_op18_fp16.onnx");
            t.File("temporalfix_s1_v2_op18_fp16.onnx");
            Assert.EndsWith("temporalfix_s1_v2_op18_fp16.onnx", TemporalFix.ModelPath(t.Path, 1));
            Assert.Null(TemporalFix.ModelPath(t.Path, 2));
        }

        [Fact]
        public void TrtEngineSettingsAreWrittenOnlyWhenChangedAndNotForDirectMl()
        {
            var job = new EncodeJob { TrtEngineSettings = "--builderOptimizationLevel=3" };
            Assert.Contains("trt_engine_settings=--builderOptimizationLevel=3", AjiConf.Build(job));
            Assert.DoesNotContain("trt_engine_settings", AjiConf.Build(job with { Backend = Backend.DirectMl }));
            Assert.DoesNotContain("trt_engine_settings", AjiConf.Build(job with { TrtEngineSettings = TrtEngine.DefaultSettings }));
        }

        [Fact]
        public void ArgsLeaveOutThePreviewWhenNoneIsGiven()
        {
            var paths = AjiPaths.ForAnimeJaNaiFolder("/x/animejanai", "/tmp/c.conf");
            var args = AjiArgs.Build(new EncodeJob { Overwrite = true }, paths, "/in/a.mkv", "/out/a.mkv", AjiMode.Encode);
            Assert.DoesNotContain("--preview", args);
            Assert.Contains("--overwrite --progress line", args);
            Assert.Contains("--conf \"/tmp/c.conf\"", args);
        }

        [Fact]
        public void FfmpegSettingsSplitIntoCodecAndQualityWithoutTheMuxerOption()
        {
            Assert.Equal(("hevc_nvenc", "-preset p7 -profile:v main10 -b:v 50M"), AjiArgs.SplitFfmpegSettings(EncoderPresets.FfmpegHevcNvenc));
            Assert.Equal(("ffv1", ""), AjiArgs.SplitFfmpegSettings(EncoderPresets.FfmpegLossless));
        }

        [Fact]
        public void RifeLabelsAndValues()
        {
            Assert.Equal("422", Rife.LabelToValue("RIFE 4.22"));
            Assert.Equal("4251", Rife.LabelToValue("RIFE 4.25 Lite"));
            Assert.Equal("RIFE 4.25 Lite", Rife.ValueToLabel("4251"));
            Assert.Equal("RIFE 4.6", Rife.ValueToLabel("46"));
            Assert.Throws<ArgumentException>(() => Rife.LabelToValue("nonsense"));
        }

        [Fact]
        public void RifeModelsAreListedNewestFirst()
        {
            using var t = new TempDir();
            t.File("rife_v4.6.onnx");
            t.File("rife_v4.22.onnx");
            t.File("rife_v4.25_lite.onnx");
            t.File("rife_v4.22_ensemble.onnx");
            Assert.Equal(["RIFE 4.25 Lite", "RIFE 4.22", "RIFE 4.6"], Rife.Models(t.Path));
        }
    }

    public class GpuChoiceTests
    {
        const string Probe = """
            GPU index=0 vendor=nvidia default=0 integrated=0 id=100 name=NVIDIA GeForce RTX 4070
            GPU index=1 vendor=amd default=1 integrated=1 id=200 name=AMD Radeon(TM) Graphics
            ENCODER name=hevc_nvenc yuv420p=1 yuv420p10=1
            ENCODER name=hevc_amf yuv420p=1 yuv420p10=0
            """;

        [Fact]
        public void TensorRtUpscalesOnNvidiaAndEncodesWithNvenc()
        {
            var probe = EncoderProbe.Parse(Probe);
            Assert.Equal(["100"], GpuChoice.BackendGpus(probe, directMl: false).Select(g => g.Id));
            Assert.Equal(EncoderGroup.Nvidia, GpuChoice.InferenceVendor(probe, false, null));
            Assert.Equal(EncoderPresets.NvencHevc, GpuChoice.DefaultEncoder(probe, EncoderGroup.Nvidia));
        }

        [Fact]
        public void DirectMlAutoIsTheDefaultGpuAndGraphicsCardsComeFirst()
        {
            var probe = EncoderProbe.Parse(Probe);
            Assert.Equal(["100", "200"], GpuChoice.BackendGpus(probe, directMl: true).Select(g => g.Id));
            Assert.Equal("200", GpuChoice.Auto(probe, true)?.Id);
            Assert.Equal(EncoderGroup.Amd, GpuChoice.InferenceVendor(probe, true, null));
            Assert.Equal(EncoderPresets.AmfHevc, GpuChoice.DefaultEncoder(probe, EncoderGroup.Amd));
        }

        [Fact]
        public void WithoutAProbeNvencHevcOrX265()
        {
            Assert.Equal(EncoderPresets.NvencHevc, GpuChoice.DefaultEncoder(null, EncoderGroup.Nvidia));
            Assert.Equal(EncoderPresets.X265, GpuChoice.DefaultEncoder(null, EncoderGroup.Amd));
            Assert.Equal(EncoderPresets.X265, GpuChoice.DefaultEncoder(null, null));
        }

        [Fact]
        public void EncoderProblemsAreTheGuisMessages()
        {
            var probe = EncoderProbe.Parse(Probe);
            Assert.Equal(["AMF HEVC can't encode 10-bit on this PC. Choose 8-bit 4:2:0 or another encoder."],
                         EncodeChecks.EncoderProblems(probe, EncoderPresets.AmfHevc.Settings, "yuv420p10").ToList());
            Assert.Empty(EncodeChecks.EncoderProblems(probe, EncoderPresets.AmfHevc.Settings, "yuv420p"));
            Assert.Single(EncodeChecks.EncoderProblems(probe, EncoderPresets.NvencHevc.Settings, "yuv444p"));
            Assert.Empty(EncodeChecks.EncoderProblems(probe, EncoderPresets.X265.Settings, "yuv444p10"));
            Assert.NotNull(EncodeChecks.DirectMlFormatProblem(Backend.DirectMl, "yuv444p"));
            Assert.Null(EncodeChecks.DirectMlFormatProblem(Backend.TensorRt, "yuv444p"));
        }

        [Fact]
        public void TheBackendRuleIsTheGuis()
        {
            Assert.Equal(OperatingSystem.IsWindows() ? Backend.DirectMl : Backend.TensorRt, BackendNames.Resolve(true, null, true));
            Assert.Equal(OperatingSystem.IsWindows() ? Backend.TensorRtRtx : Backend.TensorRt, BackendNames.Resolve(false, null, true));
            Assert.Equal(Backend.TensorRt, BackendNames.Resolve(false, null, false));
            Assert.Equal(Backend.TensorRt, BackendNames.Resolve(false, false, true));
        }
    }

    public class ProgressTests
    {
        [Fact]
        public void FpsComesFromTheFrameCounterAndTheTextFromPercent()
        {
            var p = new AjiProgress();
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.False(p.Handle("[aji:1] something", t0));
            Assert.True(p.Handle("PROGRESS phase=build_engine frame=0 total=0 fps=0.00 pct=0", t0));
            Assert.Equal("build_engine", p.Phase);
            Assert.Equal(0, p.Fps);
            Assert.True(p.Handle("PROGRESS phase=encode frame=32 total=320 fps=0.00 pct=10", t0));
            Assert.True(p.Handle("PROGRESS phase=encode frame=64 total=320 fps=0.00 pct=20", t0.AddSeconds(1)));
            Assert.Equal(32, p.Fps, 3);
            Assert.Equal(320, p.TotalFrames);
            Assert.Equal(64, p.Frame);
            Assert.Equal("20.0% - 32.0 fps", p.Text);
            p.Handle("PROGRESS phase=encode frame=320 total=320 fps=0.00 pct=100", t0.AddSeconds(5));
            Assert.Equal("100.0%", p.Text);
            p.Reset();
            Assert.Equal("", p.Phase);
            Assert.Equal(0, p.Percent);
        }
    }

    public class BatchPlanTests
    {
        [Fact]
        public void OutputPathReplacesTheFilenameToken()
        {
            var o = BatchPlan.OutputPath("/out", "%filename%-animejanai.mkv", "/in/ep 01.mp4");
            Assert.Equal(Path.Join(Path.GetFullPath("/out"), "ep 01-animejanai.mkv"), o);
        }

        [Fact]
        public void FolderVideosAreRecursiveWithTheirSubfolders()
        {
            using var t = new TempDir();
            t.File("b.MKV");
            t.File("a.mp4");
            t.File("notes.txt");
            t.File(Path.Join("s1", "c.ts"));
            var found = BatchPlan.FolderVideos(t.Path, recursive: true).Select(v => (Path.GetFileName(v.Path), v.RelativeFolder)).ToList();
            Assert.Equal([("a.mp4", ""), ("b.MKV", ""), ("c.ts", "s1")], found);
            Assert.Equal(2, BatchPlan.FolderVideos(t.Path, recursive: false).Count());
        }
    }
}
