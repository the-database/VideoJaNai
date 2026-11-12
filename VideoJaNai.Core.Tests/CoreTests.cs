using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        public void TheNewestTemporalFixVersionIsUsed()
        {
            using var t = new TempDir();
            t.File("temporalfix_s1_v1_op18_fp16.onnx");
            t.File("temporalfix_s1_v1.1_op18_fp16.onnx");
            t.File("temporalfix_s2_v9_op18_fp16.onnx");
            t.File("temporalfix_s2_v10_op18_fp16.onnx");
            Assert.EndsWith("temporalfix_s1_v1.1_op18_fp16.onnx", TemporalFix.ModelPath(t.Path, 1));
            Assert.EndsWith("temporalfix_s2_v10_op18_fp16.onnx", TemporalFix.ModelPath(t.Path, 2));
            Assert.Null(TemporalFix.ModelPath(t.Path, 3));
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
        public void OnDamageContinueIsPassedOnlyWhenOnAndOnlyToAnEncode()
        {
            var paths = AjiPaths.ForAnimeJaNaiFolder("/x/animejanai", "/tmp/c.conf");
            var off = new EncodeJob { Overwrite = true };
            var on = off with { ContinueOnDamage = true };
            Assert.DoesNotContain("--on-damage", AjiArgs.Build(off, paths, "/in/a.mkv", "/out/a.mkv", AjiMode.Encode));
            Assert.Contains("--overwrite --on-damage continue --progress line", AjiArgs.Build(on, paths, "/in/a.mkv", "/out/a.mkv", AjiMode.Encode));
            Assert.DoesNotContain("--on-damage", AjiArgs.Build(on, paths, "/in/a.mkv", null, AjiMode.Preview));
        }

        [Fact]
        public void NoReuseHeldFramesIsPassedOnlyWhenOffAndOnlyToAnEncode()
        {
            var paths = AjiPaths.ForAnimeJaNaiFolder("/x/animejanai", "/tmp/c.conf");
            var on = new EncodeJob { Overwrite = true };
            Assert.True(on.ReuseHeldFrames);
            var off = on with { ReuseHeldFrames = false };
            Assert.DoesNotContain("reuse-held", AjiArgs.Build(on, paths, "/in/a.mkv", "/out/a.mkv", AjiMode.Encode));
            Assert.DoesNotContain("reuse-held", AjiArgs.Build(on, paths, "/in/a.mkv", null, AjiMode.Preview));
            Assert.Contains("--overwrite --no-reuse-held-frames --progress line", AjiArgs.Build(off, paths, "/in/a.mkv", "/out/a.mkv", AjiMode.Encode));
            // the preview session never reuses frames
            Assert.DoesNotContain("reuse-held", AjiArgs.Build(off, paths, "/in/a.mkv", null, AjiMode.Preview));
        }

        [Fact]
        public void TheWorkflowSaysWhetherHeldFramesAreReused()
        {
            Assert.True(new WorkflowSettings().ReuseHeldFrames);
            Assert.True(EncodeJob.FromWorkflow(new WorkflowSettings(), Backend.TensorRt, null, EncoderPresets.NvencHevc, "").ReuseHeldFrames);
            var wf = new WorkflowSettings { ReuseHeldFrames = false };
            Assert.False(EncodeJob.FromWorkflow(wf, Backend.TensorRt, null, EncoderPresets.NvencHevc, "").ReuseHeldFrames);
            Assert.False(Presets.Job("balanced", wf, Backend.TensorRt, null, EncoderPresets.NvencHevc, "").ReuseHeldFrames);
        }

        [Fact]
        public void TheWorkflowSaysWhetherDamagedVideosAreFinished()
        {
            Assert.False(EncodeJob.FromWorkflow(new WorkflowSettings(), Backend.TensorRt, null, EncoderPresets.NvencHevc, "").ContinueOnDamage);
            var wf = new WorkflowSettings { FinishDamagedVideos = true };
            Assert.True(EncodeJob.FromWorkflow(wf, Backend.TensorRt, null, EncoderPresets.NvencHevc, "").ContinueOnDamage);
            Assert.True(Presets.Job("balanced", wf, Backend.TensorRt, null, EncoderPresets.NvencHevc, "").ContinueOnDamage);
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

        [Fact]
        public void TheEngineIsBeingPreparedUntilTheEncodeStarts()
        {
            var p = new AjiProgress();
            Assert.False(p.PreparingEngine);
            p.Handle("PROGRESS phase=build_engine frame=0 total=0 fps=0.00 pct=0");
            Assert.True(p.PreparingEngine);
            p.Handle("PROGRESS phase=build_engine frame=0 total=0 fps=0.00 pct=100");
            Assert.True(p.PreparingEngine);
            p.Handle("PROGRESS phase=encode frame=32 total=320 fps=0.00 pct=10");
            Assert.False(p.PreparingEngine);
        }
    }

    public class DamageTests
    {
        [Theory]
        [InlineData("DAMAGE t=754.500 frame=18093", 754.5, 18093L)]
        [InlineData("DAMAGE t=0.000 frame=0", 0.0, 0L)]
        [InlineData("DAMAGE t=12.345 frame=-1", 12.345, null)]
        [InlineData("DAMAGE frame=7 t=3.250", 3.25, 7L)]
        public void DamageLinesParse(string line, double seconds, long? frame)
        {
            Assert.True(DamageSpot.IsDamageLine(line));
            Assert.Equal(new DamageSpot((decimal)seconds, frame), DamageSpot.Parse(line));
        }

        [Theory]
        [InlineData("PROGRESS phase=encode frame=32 total=320 fps=0.00 pct=10")]
        [InlineData("DAMAGED t=1.000 frame=1")]
        [InlineData("DAMAGE frame=12")]
        [InlineData("[aji:1] DAMAGE t=1.000 frame=1")]
        public void OtherLinesAreNotDamage(string line) => Assert.Null(DamageSpot.Parse(line));

        [Theory]
        [InlineData(754.5, false, "12:34")]
        [InlineData(754.5, true, "12:34.5")]
        [InlineData(754.94, false, "12:34")]
        [InlineData(754.99, true, "12:35.0")]
        [InlineData(9.993, true, "0:10.0")]
        [InlineData(9.993, false, "0:10")]
        [InlineData(3723.25, false, "1:02:03")]
        [InlineData(3723.25, true, "1:02:03.3")]
        [InlineData(0, true, "0:00.0")]
        public void TimesAreRoundedToTheTenthAsAjiEncodeDoes(double seconds, bool tenths, string text) =>
            Assert.Equal(text, new DamageSpot((decimal)seconds, null).Time(tenths));

        [Fact]
        public void TheFailureNamesTheSpot()
        {
            var at = new EncodeResult(1, false) { Damage = [new DamageSpot(754.5M, 18093)], StoppedOnDamage = true };
            Assert.Equal("damaged video at 12:34.5 (frame 18093)", at.Failure);
            Assert.Null(at.FinishedWithDamage);
            var noFrame = at with { Damage = [new DamageSpot(754.5M, null)] };
            Assert.Equal("damaged video at 12:34.5", noFrame.Failure);
            // finishing (--on-damage continue) and then failing for another reason isn't the damage
            Assert.Equal("aji_encode failed (exit code 1)", (at with { StoppedOnDamage = false }).Failure);
        }

        [Fact]
        public void AFinishedVideoListsItsDamagedPlaces()
        {
            var three = new EncodeResult(0, true) { Damage = [new(754.5M, 18093), new(755.6M, 18120), new(2462M, 59029)] };
            Assert.Equal("Finished with damage in 3 places (12:34, 12:35, 41:02)", three.FinishedWithDamage);
            Assert.Equal("Finished with damage in 1 place (0:05)", (three with { Damage = [new(5.2M, 125)] }).FinishedWithDamage);
            Assert.Null(new EncodeResult(0, true).FinishedWithDamage);
            Assert.Null(default(EncodeResult).FinishedWithDamage);
        }

        [Fact]
        public void TheListStopsAfterFivePlaces()
        {
            DamageSpot[] spots = [new(60.1M, 1), new(61.5M, 2), new(62M, 3), new(63M, 4), new(64M, 5), new(65M, 6), new(66M, 7)];
            Assert.Equal("Finished with damage in 7 places (1:00, 1:01, 1:02, 1:03, 1:04 and 2 more)", DamageText.Finished(spots));
        }

        [Fact]
        public void AReadErrorIsTheFailure()
        {
            var read = new EncodeResult(1, false) { ReadError = "reading the input failed after 0:09.5: I/O error" };
            Assert.Equal("reading the input failed after 0:09.5: I/O error", read.Failure);
        }
    }

    // A fake aji_encode in t that counts its runs (one line per run in runs.txt) and keeps its
    // arguments (the last run's in args.txt), prints `lines` (stdout) and `stderr`, writes the file
    // `writes` (when given: the partial file of an output, to succeed) and exits with `exitCode`.
    public static class FakeAjiEncode
    {
        public static AjiPaths Create(TempDir t, int exitCode, string? writes, params string[] lines) =>
            Create(t, exitCode, writes, [], lines);

        // also prints `stderr` on stderr, after `lines`
        public static AjiPaths Create(TempDir t, int exitCode, string? writes, string[] stderr, string[] lines)
        {
            var runs = Path.Join(t.Path, "runs.txt");
            var args = Path.Join(t.Path, "args.txt");
            string script;
            if (OperatingSystem.IsWindows())
            {
                string[] write = writes is null ? [] : [$"echo x>\"{writes}\""];
                script = t.File("aji_encode.cmd", string.Join("\r\n",
                    ["@echo off", $"echo run>>\"{runs}\"", $"echo %* >\"{args}\"", .. lines.Select(l => $"echo {l}"),
                     .. stderr.Select(l => $"1>&2 echo {l}"), .. write, $"exit /b {exitCode}"]));
            }
            else
            {
                string[] write = writes is null ? [] : [$"echo x > '{writes}'"];
                script = t.File("aji_encode", string.Join("\n",
                    ["#!/bin/sh", $"echo run >> '{runs}'", $"echo \"$*\" > '{args}'", .. lines.Select(l => $"echo '{l}'"),
                     .. stderr.Select(l => $"echo '{l}' >&2"), .. write, $"exit {exitCode}"]));
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return new AjiPaths(script, Path.Join(t.Path, "trtexec"), t.Path, Path.Join(t.Path, "onnx"),
                                Path.Join(t.Path, "rife"), Path.Join(t.Path, "animejanai.conf"));
        }

        public static int Runs(TempDir t) =>
            File.Exists(Path.Join(t.Path, "runs.txt")) ? File.ReadAllLines(Path.Join(t.Path, "runs.txt")).Length : 0;

        public static string Args(TempDir t) => File.ReadAllText(Path.Join(t.Path, "args.txt")).Trim();
    }

    // One aji_encode per video, which prepares the engine itself (there is no separate engine build
    // run), with a fake aji_encode that prints PROGRESS (and DAMAGE) lines and exits.
    public class EncodeRunnerTests
    {
        private sealed class Lines : IEncodeObserver
        {
            public readonly System.Collections.Concurrent.ConcurrentQueue<string> Stdout = new();
            public int Commands;
            public void Command(string text) => Commands++;
            public void EncodeStarting(string input, string output) { }
            void IEncodeObserver.Stdout(string line) => Stdout.Enqueue(line);
            public void Stderr(string line) { }
        }

        private static AjiPaths FakeAji(TempDir t, int exitCode, params string[] lines) => FakeAjiEncode.Create(t, exitCode, null, lines);

        private static int Runs(TempDir t) => FakeAjiEncode.Runs(t);

        [Fact]
        public async Task ATensorRtEncodeRunsAjiEncodeOnce()
        {
            using var t = new TempDir();
            var paths = FakeAji(t, 0,
                "PROGRESS phase=build_engine frame=0 total=0 fps=0.00 pct=0",
                "PROGRESS phase=build_engine frame=0 total=0 fps=0.00 pct=100",
                "PROGRESS phase=encode frame=10 total=10 fps=0.00 pct=100");
            var lines = new Lines();
            var result = await new EncodeRunner(paths).EncodeToOutputAsync(new EncodeJob { Backend = Backend.TensorRt },
                Path.Join(t.Path, "in.mkv"), Path.Join(t.Path, "out.mkv"), lines, ct: TestContext.Current.CancellationToken);
            Assert.Equal(1, Runs(t));
            Assert.Equal(1, lines.Commands);
            Assert.Equal(3, lines.Stdout.Count(AjiProgress.IsProgressLine));
            // the fake writes no file
            Assert.Equal(new EncodeResult(0, false), result);
        }

        [Fact]
        public async Task AnEncodeThatStopsWhilePreparingTheEngineSaysSo()
        {
            using var t = new TempDir();
            var paths = FakeAji(t, 1, "PROGRESS phase=build_engine frame=0 total=0 fps=0.00 pct=0");
            var result = await new EncodeRunner(paths).EncodeToOutputAsync(new EncodeJob { Backend = Backend.TensorRt },
                Path.Join(t.Path, "in.mkv"), Path.Join(t.Path, "out.mkv"), new Lines(), ct: TestContext.Current.CancellationToken);
            Assert.Equal(1, Runs(t));
            Assert.True(result.EngineFailed);
            Assert.Equal("the engine could not be prepared (exit code 1)", result.Failure);
        }

        [Fact]
        public async Task AnEncodeThatFailsAfterTheEngineIsReadyIsAnEncodeFailure()
        {
            using var t = new TempDir();
            var paths = FakeAji(t, 1,
                "PROGRESS phase=build_engine frame=0 total=0 fps=0.00 pct=0",
                "PROGRESS phase=build_engine frame=0 total=0 fps=0.00 pct=100");
            var result = await new EncodeRunner(paths).EncodeToOutputAsync(new EncodeJob { Backend = Backend.TensorRt },
                Path.Join(t.Path, "in.mkv"), Path.Join(t.Path, "out.mkv"), new Lines(), ct: TestContext.Current.CancellationToken);
            Assert.False(result.EngineFailed);
            Assert.Equal("aji_encode failed (exit code 1)", result.Failure);
        }

        static readonly string[] Damaged =
        [
            "PROGRESS phase=build_engine frame=0 total=0 fps=0.00 pct=0",
            "PROGRESS phase=build_engine frame=0 total=0 fps=0.00 pct=100",
            "PROGRESS phase=encode frame=18080 total=64000 fps=0.00 pct=28",
            "DAMAGE t=754.500 frame=18093",
        ];

        [Fact]
        public async Task AnEncodeThatStopsAtDamageSaysWhere()
        {
            using var t = new TempDir();
            var paths = FakeAji(t, 1, Damaged);
            var lines = new Lines();
            var result = await new EncodeRunner(paths).EncodeToOutputAsync(new EncodeJob { Backend = Backend.TensorRt },
                Path.Join(t.Path, "in.mkv"), Path.Join(t.Path, "out.mkv"), lines, ct: TestContext.Current.CancellationToken);
            Assert.DoesNotContain("--on-damage", FakeAjiEncode.Args(t));
            Assert.False(result.Written);
            Assert.True(result.StoppedOnDamage);
            Assert.Equal([new DamageSpot(754.5M, 18093)], result.Damage);
            Assert.Equal("damaged video at 12:34.5 (frame 18093)", result.Failure);
            // the observer still sees the line (the log)
            Assert.Contains("DAMAGE t=754.500 frame=18093", lines.Stdout);
        }

        [Fact]
        public async Task AnEncodeThatContinuesListsTheDamage()
        {
            using var t = new TempDir();
            var output = Path.Join(t.Path, "out.mkv");
            var paths = FakeAjiEncode.Create(t, 0, PartialOutput.PathFor(output),
                [.. Damaged, "DAMAGE t=755.100 frame=18108", "DAMAGE t=2462.000 frame=-1",
                 "PROGRESS phase=encode frame=64000 total=64000 fps=0.00 pct=100"]);
            var result = await new EncodeRunner(paths).EncodeToOutputAsync(new EncodeJob { Backend = Backend.TensorRt, ContinueOnDamage = true },
                Path.Join(t.Path, "in.mkv"), output, new Lines(), ct: TestContext.Current.CancellationToken);
            Assert.EndsWith("--on-damage continue --progress line", FakeAjiEncode.Args(t));
            Assert.True(result.Written);
            Assert.True(File.Exists(output));
            Assert.False(result.StoppedOnDamage);
            Assert.Equal([new DamageSpot(754.5M, 18093), new DamageSpot(755.1M, 18108), new DamageSpot(2462M, null)], result.Damage);
            Assert.Equal("Finished with damage in 3 places (12:34, 12:35, 41:02)", result.FinishedWithDamage);
        }

        [Fact]
        public async Task AnEncodeThatContinuesAndThenFailsIsNotStoppedAtTheDamage()
        {
            using var t = new TempDir();
            var paths = FakeAji(t, 1, Damaged);
            var result = await new EncodeRunner(paths).EncodeToOutputAsync(new EncodeJob { Backend = Backend.TensorRt, ContinueOnDamage = true },
                Path.Join(t.Path, "in.mkv"), Path.Join(t.Path, "out.mkv"), new Lines(), ct: TestContext.Current.CancellationToken);
            Assert.False(result.StoppedOnDamage);
            Assert.Single(result.Damage);
            Assert.Equal("aji_encode failed (exit code 1)", result.Failure);
        }

        [Fact]
        public async Task AFailedReadIsTheFailureInBothModes()
        {
            foreach (var keep in new[] { false, true })
            {
                using var t = new TempDir();
                var paths = FakeAjiEncode.Create(t, 1, null, stderr: ["reading the input failed after 0:09.5: I/O error"],
                                                 lines: ["PROGRESS phase=encode frame=228 total=1000 fps=0.00 pct=22"]);
                var result = await new EncodeRunner(paths).EncodeToOutputAsync(new EncodeJob { Backend = Backend.TensorRt, ContinueOnDamage = keep },
                    Path.Join(t.Path, "in.mkv"), Path.Join(t.Path, "out.mkv"), new Lines(), ct: TestContext.Current.CancellationToken);
                Assert.False(result.StoppedOnDamage);
                Assert.Equal("reading the input failed after 0:09.5: I/O error", result.Failure);
            }
        }

        // aji_encode's own line (src/encode.c): the result at the end of an encode, on stderr
        const string Reused = "held frames: reused 1055 of 3412 (30.9%)";

        [Fact]
        public async Task AWrittenEncodeReportsItsHeldFrameReuse()
        {
            using var t = new TempDir();
            var output = Path.Join(t.Path, "out.mkv");
            var paths = FakeAjiEncode.Create(t, 0, PartialOutput.PathFor(output),
                stderr: ["held frames: reusing the last upscale for unchanged frames (tile threshold 2.50, fade threshold 0.150, at most 24 in a row; --no-reuse-held-frames turns it off)", Reused],
                lines: ["PROGRESS phase=encode frame=3412 total=3412 fps=0.00 pct=100"]);
            var result = await new EncodeRunner(paths).EncodeToOutputAsync(new EncodeJob { Backend = Backend.TensorRt },
                Path.Join(t.Path, "in.mkv"), output, new Lines(), ct: TestContext.Current.CancellationToken);
            Assert.DoesNotContain("reuse-held", FakeAjiEncode.Args(t));
            Assert.True(result.Written);
            Assert.Equal(new HeldFrameReuse(1055, 3412), result.HeldFrames);
            Assert.Equal("Reused the upscale for 31% of frames (1,055 of 3,412)", result.HeldFrames!.Value.Text);
        }

        [Fact]
        public async Task NoReuseReportedIsNone()
        {
            foreach (var (exitCode, stderr) in new[] { (0, "held frames: not reused (RIFE interpolates the frames)"), (1, Reused) })
            {
                using var t = new TempDir();
                var output = Path.Join(t.Path, "out.mkv");
                var paths = FakeAjiEncode.Create(t, exitCode, exitCode == 0 ? PartialOutput.PathFor(output) : null, stderr: [stderr],
                                                 lines: ["PROGRESS phase=encode frame=10 total=10 fps=0.00 pct=100"]);
                var result = await new EncodeRunner(paths).EncodeToOutputAsync(new EncodeJob { Backend = Backend.TensorRt, ReuseHeldFrames = false },
                    Path.Join(t.Path, "in.mkv"), output, new Lines(), ct: TestContext.Current.CancellationToken);
                Assert.Contains("--no-reuse-held-frames", FakeAjiEncode.Args(t));
                Assert.Equal(exitCode == 0, result.Written);
                Assert.Null(result.HeldFrames);
            }
        }

        [Fact]
        public async Task AnEncodeWithoutDamageReportsNone()
        {
            using var t = new TempDir();
            var output = Path.Join(t.Path, "out.mkv");
            var paths = FakeAjiEncode.Create(t, 0, PartialOutput.PathFor(output), "PROGRESS phase=encode frame=10 total=10 fps=0.00 pct=100");
            var result = await new EncodeRunner(paths).EncodeToOutputAsync(new EncodeJob { Backend = Backend.TensorRt, ContinueOnDamage = true },
                Path.Join(t.Path, "in.mkv"), output, new Lines(), ct: TestContext.Current.CancellationToken);
            Assert.Equal(new EncodeResult(0, true), result);
            Assert.Null(result.FinishedWithDamage);
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

    // An encode writes next to its output and the output appears only when aji_encode succeeds, so
    // a failed or stopped run leaves nothing for skip-existing to keep and replaces nothing.
    public class PartialOutputTests
    {
        [Fact]
        public void PartialFilesKeepTheContainerExtension()
        {
            Assert.Equal(Path.Join("d", "ep-animejanai" + BatchPlanning.PartialMarker + ".mkv"),
                         PartialOutput.PathFor(Path.Join("d", "ep-animejanai.mkv")));
        }

        [Fact]
        public async Task SuccessMovesThePartialFileToTheOutput()
        {
            using var t = new TempDir();
            var output = Path.Join(t.Path, "ep-animejanai.mkv");
            string? written = null;
            var result = await PartialOutput.WriteAsync(output, temp =>
            {
                written = temp;
                File.WriteAllText(temp, "new");
                return Task.FromResult(0);
            });
            Assert.Equal(new EncodeResult(0, true), result);
            Assert.Equal(PartialOutput.PathFor(output), written);
            Assert.Equal("new", File.ReadAllText(output));
            Assert.False(File.Exists(written));
        }

        [Fact]
        public async Task AFailedEncodeLeavesNoOutputAndDeletesThePartialFile()
        {
            using var t = new TempDir();
            var output = Path.Join(t.Path, "ep-animejanai.mkv");
            var result = await PartialOutput.WriteAsync(output, temp =>
            {
                File.WriteAllText(temp, "half");
                return Task.FromResult(1);
            });
            Assert.False(result.Written);
            Assert.Equal("aji_encode failed (exit code 1)", result.Failure);
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(PartialOutput.PathFor(output)));
        }

        [Fact]
        public async Task AFailedEncodeKeepsTheOutputThatWasThere()
        {
            using var t = new TempDir();
            var output = t.File("ep-animejanai.mkv", "old");
            var result = await PartialOutput.WriteAsync(output, temp =>
            {
                File.WriteAllText(temp, "half");
                return Task.FromResult(-1);
            });
            Assert.False(result.Written);
            Assert.Equal("old", File.ReadAllText(output));
            Assert.False(File.Exists(PartialOutput.PathFor(output)));
        }

        [Fact]
        public async Task ExitZeroWithoutAFileIsNotWritten()
        {
            using var t = new TempDir();
            var output = Path.Join(t.Path, "ep-animejanai.mkv");
            var result = await PartialOutput.WriteAsync(output, _ => Task.FromResult(0));
            Assert.False(result.Written);
            Assert.Equal("aji_encode wrote no output", result.Failure);
            Assert.False(File.Exists(output));
        }

        [Fact]
        public async Task AStoppedEncodeDeletesThePartialFileAndKeepsTheOutput()
        {
            using var t = new TempDir();
            var output = t.File("ep-animejanai.mkv", "old");
            await Assert.ThrowsAsync<OperationCanceledException>(() => PartialOutput.WriteAsync(output, temp =>
            {
                File.WriteAllText(temp, "half");
                throw new OperationCanceledException();
            }));
            Assert.Equal("old", File.ReadAllText(output));
            Assert.False(File.Exists(PartialOutput.PathFor(output)));
        }
    }

    public class SingleFilePlanTests
    {
        [Fact]
        public void AVideoWrittenOverItselfIsAnError()
        {
            using var t = new TempDir();
            var file = t.File("ep.mkv");
            var plan = BatchPlanning.Plan([file], t.Path, "%filename%.mkv", skipExisting: false);
            Assert.Contains("would be written over itself", plan.Problem);
            var ok = BatchPlanning.Plan([file], t.Path, BatchPlan.DefaultFilename, skipExisting: false);
            Assert.Null(ok.Problem);
            Assert.Equal(Path.Join(t.Path, "ep-animejanai.mkv"), ok.Videos.Single().Output);
        }
    }

    // aji_encode's "held frames: reused N of M (P%)" line, and the text VideoJaNai shows for it.
    public class HeldFrameReuseTests
    {
        [Theory]
        [InlineData("held frames: reused 1055 of 3412 (30.9%)", 1055, 3412)]
        [InlineData("held frames: reused 0 of 2000 (0.0%)", 0, 2000)]
        // a .cmd echo drops the lone %
        [InlineData("held frames: reused 12 of 24 (50.0)", 12, 24)]
        public void TheResultLineParses(string line, long reused, long frames) =>
            Assert.Equal(new HeldFrameReuse(reused, frames), HeldFrameReuse.Parse(line));

        [Theory]
        [InlineData("held frames: not reused (RIFE interpolates the frames)")]
        [InlineData("held frames: not reused (the chain is temporal)")]
        [InlineData("held frames: reusing the last upscale for unchanged frames (tile threshold 2.50, fade threshold 0.150, at most 24 in a row; --no-reuse-held-frames turns it off)")]
        [InlineData("held frames: reused 5 of 0 (nan%)")]
        [InlineData("PROGRESS phase=encode frame=10 total=10 fps=0.00 pct=100")]
        [InlineData("[aji:1] held frames: reused 1 of 2 (50.0%)")]
        public void OtherLinesAreNotAResult(string line) => Assert.Null(HeldFrameReuse.Parse(line));

        [Theory]
        [InlineData(1055, 3412, "Reused the upscale for 31% of frames (1,055 of 3,412)")]
        [InlineData(0, 3412, "Reused the upscale for 0% of frames (0 of 3,412)")]
        [InlineData(1, 1000, "Reused the upscale for <1% of frames (1 of 1,000)")]
        [InlineData(24, 24, "Reused the upscale for 100% of frames (24 of 24)")]
        public void TheTextGivesThePercentAndTheFrames(long reused, long frames, string text) =>
            Assert.Equal(text, new HeldFrameReuse(reused, frames).Text);

        [Fact]
        public void VideosAddUp()
        {
            Assert.Equal(new HeldFrameReuse(30, 300), HeldFrameReuse.Sum([new(10, 100), new(20, 200)]));
            Assert.Null(HeldFrameReuse.Sum([]));
        }
    }
}
