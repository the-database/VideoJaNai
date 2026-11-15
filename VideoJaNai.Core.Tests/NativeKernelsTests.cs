using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VideoJaNai.Cli;
using VideoJaNai.Core;
using Xunit;

namespace VideoJaNai.Core.Tests
{
    // aji_encode's native-kernel lines (libaji 0.31), as it logs them in its numbered chain steps.
    public class NativeKernelsTests
    {
        const string Span = "1. Native kernels: 2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16 (27 kernels, 64 ch, pixel-unshuffle 2, depth-to-space 4)";
        const string Rife = "1. Native kernels: rife_v4.22 (4 blocks, scales 8/4/2/1, 8 feature channels)";
        const string TemporalFix = "3. Native kernels: temporalfix_s2_v1_op18_fp16 (TemporalFix, 7 frames, stages 96/64/48 channels, 1 streams)";
        const string NotUsed = "3. Native kernels not used for 2x_test_resize_fp16: unsupported op Resize on the data path; using TensorRT-RTX";
        const string NotUsedMemory = "12. Native kernels not used for 4x_HAT_L: a whole 2560x1440 frame needs ~34.6 GB, 30.3 GB free (keeping 4.2 GB); using TensorRT";

        [Fact]
        public void ANativeModelIsReportedWithWhatRunsIt()
        {
            var step = NativeKernels.FromAjiLog(Span)!;
            Assert.Equal("2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16", step.Model);
            Assert.True(step.Used);
            Assert.Equal("27 kernels, 64 ch, pixel-unshuffle 2, depth-to-space 4", step.Detail);
            Assert.Equal(Span[3..], step.Message);
        }

        [Fact]
        public void RifeAndTemporalFixAreModelsToo()
        {
            Assert.Equal(("rife_v4.22", true, "4 blocks, scales 8/4/2/1, 8 feature channels"), Parts(Rife));
            Assert.Equal(("temporalfix_s2_v1_op18_fp16", true, "TemporalFix, 7 frames, stages 96/64/48 channels, 1 streams"), Parts(TemporalFix));
        }

        [Fact]
        public void AModelNotOnNativeKernelsSaysWhyWithoutTheBackend()
        {
            Assert.Equal(("2x_test_resize_fp16", false, "unsupported op Resize on the data path"), Parts(NotUsed));
            Assert.Equal("Native kernels not used for 2x_test_resize_fp16: unsupported op Resize on the data path; using TensorRT-RTX",
                         NativeKernels.FromAjiLog(NotUsed)!.Message);
            // a reason with parentheses, at a two-digit step
            Assert.Equal(("4x_HAT_L", false, "a whole 2560x1440 frame needs ~34.6 GB, 30.3 GB free (keeping 4.2 GB)"), Parts(NotUsedMemory));
        }

        [Theory]
        [InlineData("2. Applied Model: 2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16;    New Video Resolution: 3840x2160")]
        [InlineData("Active Upscale Chain: 1;    Resolution Range: 1280x720 - 1920x1080;    FPS Range: 0 - inf")]
        [InlineData("[aji:1] TensorRT-RTX failed (configure: engine build failed); using TensorRT instead")]
        [InlineData("held frames: reused 77 of 137 (56.2%)")]
        [InlineData("PROGRESS phase=encode frame=10 total=137 fps=0.00 pct=7")]
        [InlineData("")]
        public void OtherLinesAreNotNativeKernelLines(string line) => Assert.Null(NativeKernels.FromAjiLog(line));

        [Fact]
        public void EveryModelOnNativeKernels()
        {
            var s = new NativeKernelSummary();
            Assert.NotNull(s.Add(Rife));
            Assert.Null(s.Add("2. Applied Model: x;    New Video Resolution: 3840x2160"));
            Assert.NotNull(s.Add(Span));
            Assert.True(s.AllUsed);
            Assert.Equal((2, 2), (s.UsedCount, s.Total));
            Assert.Null(s.FirstNotUsed);
        }

        [Fact]
        public void OneOfTwoModels()
        {
            var s = new NativeKernelSummary();
            s.Add(Span);
            s.Add(NotUsed);
            Assert.False(s.AllUsed);
            Assert.Equal((1, 2), (s.UsedCount, s.Total));
            Assert.Equal("2x_test_resize_fp16", s.FirstNotUsed!.Model);
            Assert.Equal(["2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16", "2x_test_resize_fp16"], s.Models.Select(m => m.Model));
        }

        // a batch: each model once; one that ran natively on one video but not on the next counts as not
        // native (the change is reported), and never goes back
        [Fact]
        public void ABatchCountsEachModelOnceAndKeepsTheFirstReasonItWasNotNative()
        {
            var s = new NativeKernelSummary();
            Assert.NotNull(s.Add(Span));
            Assert.Null(s.Add(Span));
            const string spanNotUsed = "1. Native kernels not used for 2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16: out of memory; using TensorRT";
            Assert.False(s.Add(spanNotUsed)!.Used);
            Assert.Null(s.Add(Span));
            Assert.Null(s.Add(spanNotUsed.Replace("out of memory", "another reason")));
            Assert.Equal(1, s.Total);
            Assert.Equal("out of memory", s.FirstNotUsed!.Detail);
            s.Clear();
            Assert.Equal(0, s.Total);
            Assert.False(s.AllUsed);
        }

        static (string, bool, string) Parts(string line)
        {
            var s = NativeKernels.FromAjiLog(line)!;
            return (s.Model, s.Used, s.Detail);
        }
    }

    // The command line says which models run on the native kernels, once per model for a batch.
    public class RunNativeKernelsTests
    {
        [Fact]
        public async Task EachModelIsReportedOnceForTheRun()
        {
            using var t = new TempDir();
            var a = t.File(Path.Join("in", "a.mkv"));
            var b = t.File(Path.Join("in", "b.mkv"));
            var outA = Path.Join(t.Path, "out", "a-animejanai.mkv");
            var outB = Path.Join(t.Path, "out", "b-animejanai.mkv");
            // the fake writes one partial output; the second video fails, which doesn't matter here
            var paths = FakeAjiEncode.Create(t, 0, PartialOutput.PathFor(outA),
                stderr: ["1. Native kernels: rife_v4.22 (4 blocks, scales 8/4/2/1, 8 feature channels)",
                         "2. Native kernels not used for 2x_test_resize_fp16: unsupported op Resize on the data path; using TensorRT-RTX"],
                lines: ["PROGRESS phase=encode frame=10 total=10 fps=0.00 pct=100"]);
            var plan = new RunPlan
            {
                Install = new Install(paths),
                Job = new EncodeJob { Backend = Backend.TensorRtRtx, Overwrite = true },
                Videos = [new PlannedVideo(a, outA, PlanAction.Encode), new PlannedVideo(b, outB, PlanAction.Encode)],
                Heading = "test",
            };
            var events = new RunDamageTests.Events();
            await Run.ExecuteAsync(plan, events, TestContext.Current.CancellationToken);
            Assert.Equal(["NATIVE_KERNELS Native kernels: rife_v4.22 (4 blocks, scales 8/4/2/1, 8 feature channels)",
                          "NATIVE_KERNELS Native kernels not used for 2x_test_resize_fp16: unsupported op Resize on the data path; using TensorRT-RTX"],
                         events.All.Where(e => e.StartsWith("NATIVE_KERNELS ")));
        }
    }
}
