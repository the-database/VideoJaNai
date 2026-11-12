using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VideoJaNai.Cli;
using VideoJaNai.Core;
using Xunit;

namespace VideoJaNai.Core.Tests
{
    public class IniTests
    {
        [Fact]
        public void TheTemplateParsesAndSetsNothing()
        {
            var o = Config.Parse(Config.Template);
            Assert.Empty(o.Inputs);
            Assert.Empty(o.Models);
            Assert.Null(o.Preset);
            Assert.Null(o.OutputFolder);
            Assert.Null(o.Filename);
            Assert.Null(o.SkipExisting);
            Assert.Null(o.KeepDamaged);
            Assert.Null(o.ReuseHeldFrames);
            Assert.Null(o.Encoder);
            Assert.Null(o.Backend);
            Assert.Null(o.TemporalFix);
            Assert.Null(o.Rife);
        }

        [Fact]
        public void ValuesAreTakenAsTyped()
        {
            var o = Config.Parse("""
                [Input]
                path = "D:\Anime\Show #1"
                path = D:\more; stuff
                ; a comment
                # another

                [output]
                folder = 'D:\out'
                skip_existing = No
                keep_damaged = yes
                [upscale]
                model = a.onnx
                model = b.onnx
                reuse_held_frames = no
                [encode]
                encoder = X265
                pixel_format = 8-bit
                [interpolation]
                rife = on
                model = 4.25 lite
                factor = 5/2
                [temporal_fix]
                strength = 3
                [resize]
                height = 1440px
                [gpu]
                backend = TensorRT
                """);
            Assert.Equal([@"D:\Anime\Show #1", @"D:\more; stuff"], o.Inputs);
            Assert.Equal(@"D:\out", o.OutputFolder);
            Assert.False(o.SkipExisting);
            Assert.True(o.KeepDamaged);
            Assert.Equal(["a.onnx", "b.onnx"], o.Models);
            Assert.False(o.ReuseHeldFrames);
            Assert.Equal("x265", o.Encoder);
            Assert.Equal("yuv420p", o.PixFmt);
            Assert.True(o.Rife);
            Assert.Equal("RIFE 4.25 Lite", o.RifeModel);
            Assert.Equal((5, 2), o.RifeFactor);
            Assert.Equal(3, o.TemporalFix);
            Assert.Equal(1440, o.Height);
            Assert.Equal("tensorrt", o.Backend);
        }

        [Theory]
        [InlineData("[output]\nfoldr = x", "line 2: unknown setting 'foldr' in [output] (did you mean folder?)")]
        [InlineData("[outptu]\n", "line 1: unknown section [outptu] (did you mean output?)")]
        [InlineData("folder = x", "line 1: 'folder' comes before any [section]; put it under one, like [output]")]
        [InlineData("[output]\nfolder", "line 2: expected setting = value, found: folder")]
        [InlineData("[output\n", "line 1: a section name needs a closing ]: [output")]
        [InlineData("[output]\nfolder = a\nfolder = b", "line 3: folder is set twice in [output] (also on line 2); keep one")]
        [InlineData("[output]\nskip_existing = maybe", "line 2: skip_existing should be yes or no, not 'maybe'")]
        [InlineData("[output]\nkeep_damage = yes", "line 2: unknown setting 'keep_damage' in [output] (did you mean keep_damaged?)")]
        [InlineData("[upscale]\npreset = fast", "line 2: preset should be one of balanced, performance, balanced-sharp, performance-sharp, not 'fast'")]
        [InlineData("[upscale]\npreset = balanced\nmodel = a.onnx", "line 1: pick one way to choose the models in [upscale]: preset, model or workflow")]
        [InlineData("[resize]\nheight = 1080\npercent = 50", "line 1: set only one of height and percent in [resize]")]
        [InlineData("[temporal_fix]\nstrength = 5", "line 2: strength should be 1, 2 or 3, not '5'")]
        [InlineData("[output]\n[output]", "line 2: [output] appears twice (also on line 1); keep one")]
        public void MistakesNameTheLine(string text, string message)
        {
            Assert.Equal(message, Assert.Throws<IniException>(() => Config.Parse(text)).Message);
        }

        [Fact]
        public void Utf16AndBomFilesDecode()
        {
            var text = "[output]\nfolder = D:\\ü";
            Assert.Equal(text, Ini.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)]));
            Assert.Equal(text, Ini.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]));
            Assert.Throws<IniException>(() => Ini.Decode([0xC3, 0x28]));
        }

        [Fact]
        public void LoadMakesPathsRelativeToTheFile()
        {
            using var t = new TempDir();
            var p = t.File("videojanai.ini", "[upscale]\nworkflow = wf.awf\n");
            var o = Config.Load(p);
            Assert.Equal(Path.GetFullPath(t.Path), o.BaseDir);
            Assert.Equal(Path.Join(o.BaseDir, "wf.awf"), PathText.Relative(o.BaseDir, o.Workflow!));
            var bad = t.File("bad.ini", "x = 1");
            Assert.StartsWith(bad + ": line 1", Assert.Throws<IniException>(() => Config.Load(bad)).Message);
        }
    }

    public class FlagTests
    {
        [Fact]
        public void FlagsAndPositionals()
        {
            var f = Flags.Parse(["a.mkv", "-o", "out", "--model=m.onnx", "--model", "n.onnx", "--rife-factor", "5/2",
                                 "--encoder", "NVENC-AV1", "--pix-fmt", "yuv420p", "--device", "1", "--progress", "lines",
                                 "--overwrite", "--temporal-fix", "2", "--", "-weird.mkv"]);
            Assert.Equal(["a.mkv", "-weird.mkv"], f.Positional);
            Assert.Equal("out", f.Options.OutputFolder);
            Assert.Equal(["m.onnx", "n.onnx"], f.Options.Models);
            Assert.Equal((5, 2), f.Options.RifeFactor);
            Assert.True(f.Options.Rife);
            Assert.Equal("nvenc-av1", f.Options.Encoder);
            Assert.Equal("1", f.Options.Device);
            Assert.True(f.Lines);
            Assert.False(f.Options.SkipExisting);
            Assert.Equal(2, f.Options.TemporalFix);
        }

        [Theory]
        [InlineData(new[] { "--bogus" }, "unknown option --bogus (see --help)")]
        [InlineData(new[] { "--model" }, "--model needs a value")]
        [InlineData(new[] { "--overwrite=yes" }, "--overwrite takes no value")]
        [InlineData(new[] { "--keep-damaged=yes" }, "--keep-damaged takes no value")]
        [InlineData(new[] { "--no-reuse-held-frames=yes" }, "--no-reuse-held-frames takes no value")]
        [InlineData(new[] { "--preset", "balanced", "--model", "x" }, "give only one of --preset, --model or --workflow")]
        [InlineData(new[] { "--height", "1080", "--resize-percent", "50" }, "give only one of --height and --resize-percent")]
        [InlineData(new[] { "--encoder", "h266" }, "--encoder should be one of auto, nvenc-hevc, nvenc-av1, nvenc-lossless, amf-hevc, amf-av1, qsv-hevc, qsv-av1, x265, x264, ffv1, not 'h266'")]
        [InlineData(new[] { "--temporal-fix", "9" }, "--temporal-fix should be 1, 2 or 3 (0: off), not '9'")]
        public void BadFlags(string[] args, string message)
        {
            Assert.Equal(message, Assert.Throws<UsageException>(() => Flags.Parse(args)).Message);
        }

        [Fact]
        public void DamagedVideosStopUnlessKept()
        {
            Assert.Null(Flags.Parse(["a.mkv"]).Options.KeepDamaged);
            Assert.True(Flags.Parse(["--keep-damaged", "a.mkv"]).Options.KeepDamaged);
            Assert.False(Flags.Parse(["--stop-on-damage", "a.mkv"]).Options.KeepDamaged);
            // the last one wins, as --skip-existing / --overwrite
            Assert.False(Flags.Parse(["--keep-damaged", "--stop-on-damage"]).Options.KeepDamaged);
        }

        [Fact]
        public void HeldFramesAreReusedUnlessTurnedOff()
        {
            Assert.Null(Flags.Parse(["a.mkv"]).Options.ReuseHeldFrames);
            Assert.False(Flags.Parse(["--no-reuse-held-frames", "a.mkv"]).Options.ReuseHeldFrames);
            Assert.True(Flags.Parse(["--no-reuse-held-frames", "--reuse-held-frames"]).Options.ReuseHeldFrames);
        }

        [Theory]
        // flag, then videojanai.ini, then the workflow (off by default)
        [InlineData(null, null, false, false)]
        [InlineData(null, null, true, true)]
        [InlineData(null, true, false, true)]
        [InlineData(null, false, true, false)]
        [InlineData(true, false, false, true)]
        [InlineData(false, true, true, false)]
        public void KeepDamagedResolvesFlagThenFileThenWorkflow(bool? flag, bool? file, bool workflow, bool keep)
        {
            var flags = new Options { KeepDamaged = flag };
            var ini = new Options { KeepDamaged = file };
            Assert.Equal(keep, Resolve.KeepDamaged(flags, ini, new WorkflowSettings { FinishDamagedVideos = workflow }));
        }

        [Theory]
        [InlineData("4.22", "RIFE 4.22")]
        [InlineData("RIFE 4.25 Lite", "RIFE 4.25 Lite")]
        [InlineData("4.25-lite", "RIFE 4.25 Lite")]
        [InlineData("rife_v4.6", "RIFE 4.6")]
        [InlineData("four", null)]
        public void RifeVersions(string text, string? label) => Assert.Equal(label, RifeNames.Parse(text));
    }

    public class PlanTests
    {
        [Fact]
        public void FoldersKeepSubfoldersAndExistingOutputsAreSkipped()
        {
            using var t = new TempDir();
            t.File(Path.Join("in", "a.mkv"));
            t.File(Path.Join("in", "s", "b.mp4"));
            t.File(Path.Join("in", "s", "c" + BatchPlanning.PartialMarker + ".mkv"));
            t.File(Path.Join("out", "s", "b-animejanai.mkv"));
            var videos = Resolve.PlanVideos([Path.Join(t.Path, "in")], Path.Join(t.Path, "out"), BatchPlan.DefaultFilename, skipExisting: true);
            Assert.Equal(2, videos.Count);
            Assert.Equal(Path.Join(t.Path, "out", "a-animejanai.mkv"), videos[0].Output);
            Assert.Equal(PlanAction.Encode, videos[0].Action);
            Assert.Equal(Path.Join(t.Path, "out", "s", "b-animejanai.mkv"), videos[1].Output);
            Assert.Equal(PlanAction.SkipExisting, videos[1].Action);
            var redo = Resolve.PlanVideos([Path.Join(t.Path, "in")], Path.Join(t.Path, "out"), BatchPlan.DefaultFilename, skipExisting: false);
            Assert.All(redo, v => Assert.Equal(PlanAction.Encode, v.Action));
        }

        [Fact]
        public void DefaultOutputsAreTheGuis()
        {
            using var t = new TempDir();
            var file = t.File(Path.Join("in", "a.mkv"));
            Assert.Equal(Path.Join(t.Path, "in", "a-animejanai.mkv"),
                         Resolve.PlanVideos([file], null, BatchPlan.DefaultFilename, true).Single().Output);
            Assert.Equal(Path.Join(t.Path, "in animejanai", "a-animejanai.mkv"),
                         Resolve.PlanVideos([Path.Join(t.Path, "in")], null, BatchPlan.DefaultFilename, true).Single().Output);
        }

        [Fact]
        public void OutputInsideTheInputFolderIsNotRescanned()
        {
            using var t = new TempDir();
            t.File(Path.Join("in", "a.mkv"));
            t.File(Path.Join("in", "up", "a-animejanai.mkv"));
            var videos = Resolve.PlanVideos([Path.Join(t.Path, "in")], Path.Join(t.Path, "in", "up"), BatchPlan.DefaultFilename, true);
            Assert.Equal(Path.Join(t.Path, "in", "a.mkv"), videos.Single().Input);
        }

        [Fact]
        public void OneVideoCanNameItsOutputFile()
        {
            using var t = new TempDir();
            var file = t.File("a.mkv");
            var o = Path.Join(t.Path, "x", "named.mp4");
            Assert.Equal(o, Resolve.PlanVideos([file], o, BatchPlan.DefaultFilename, true).Single().Output);
        }

        [Fact]
        public void TwoVideosWritingOneFileIsAnError()
        {
            using var t = new TempDir();
            t.File(Path.Join("in", "a.mkv"));
            t.File(Path.Join("in", "a.mp4"));
            var e = Assert.Throws<RunException>(() => Resolve.PlanVideos([Path.Join(t.Path, "in")], Path.Join(t.Path, "out"), BatchPlan.DefaultFilename, true));
            Assert.Contains("would both be written to", e.Message);
            Assert.Throws<RunException>(() => Resolve.PlanVideos([Path.Join(t.Path, "nope")], null, BatchPlan.DefaultFilename, true));
        }
    }

    // A run's reports for a video with damaged frames, with the fake aji_encode (--progress lines'
    // events).
    public class RunDamageTests
    {
        sealed class Events : Report
        {
            public readonly List<string> All = [];
            void Add(string s)
            {
                lock (All)
                {
                    All.Add(s);
                }
            }
            public override void Info(string text)
            {
                if (!text.StartsWith("videojanai: ", StringComparison.Ordinal))
                {
                    Add($"INFO {text}");
                }
            }
            public override void Plan(int videos, int encode, int skip) => Add($"PLAN {videos} {encode} {skip}");
            public override void VideoStart(int n, int of, PlannedVideo v) => Add($"VIDEO_START {n} {of}");
            public override void VideoSkip(int n, int of, PlannedVideo v) => Add($"VIDEO_SKIP {n} {of}");
            public override void Progress(string line, AjiProgress p) { }
            public override void VideoDone(int n, int of, PlannedVideo v, TimeSpan took) => Add($"VIDEO_DONE {n} {of}");
            public override void VideoError(int n, int of, PlannedVideo v, string message, IReadOnlyList<string> log) => Add($"VIDEO_ERROR {n} {of} {message}");
            public override void Note(string text) => Add($"NOTE {text}");
            public override void Fallback(string message) => Add($"BACKEND_FALLBACK {message}");
            public override void AjiLog(string line) { }
            public override void Done(int written, int skipped, int failed, int damaged, TimeSpan took) => Add($"DONE {written} {skipped} {failed} {damaged}");
        }

        static readonly string[] Damage =
        [
            "PROGRESS phase=encode frame=18080 total=64000 fps=0.00 pct=28",
            "DAMAGE t=754.500 frame=18093",
            "DAMAGE t=755.100 frame=18108",
        ];

        static async Task<(int Rc, List<string> Events)> RunOne(TempDir t, bool keep, int exitCode)
        {
            var input = t.File("in.mkv");
            var output = Path.Join(t.Path, "out", "in-animejanai.mkv");
            var paths = FakeAjiEncode.Create(t, exitCode, exitCode == 0 ? PartialOutput.PathFor(output) : null, Damage);
            var plan = new RunPlan
            {
                Install = new Install(paths),
                Job = new EncodeJob { Backend = Backend.TensorRt, Overwrite = true, ContinueOnDamage = keep },
                Videos = [new PlannedVideo(input, output, PlanAction.Encode)],
                Heading = "test",
            };
            var events = new Events();
            var rc = await Run.ExecuteAsync(plan, events, TestContext.Current.CancellationToken);
            return (rc, events.All);
        }

        [Fact]
        public async Task KeptDamageIsANoteAndCountsInTheSummary()
        {
            using var t = new TempDir();
            var (rc, events) = await RunOne(t, keep: true, exitCode: 0);
            Assert.Equal(0, rc);
            Assert.Contains("--on-damage continue", FakeAjiEncode.Args(t));
            Assert.Equal(["PLAN 1 1 0", "VIDEO_START 1 1", "VIDEO_DONE 1 1",
                          $"NOTE {Path.Join(t.Path, "in.mkv")}: Finished with damage in 2 places (12:34, 12:35)", "DONE 1 0 0 1"], events);
        }

        [Fact]
        public async Task StoppingAtDamageSaysWhereAndHowToKeepIt()
        {
            using var t = new TempDir();
            var (rc, events) = await RunOne(t, keep: false, exitCode: 1);
            Assert.Equal(1, rc);
            Assert.DoesNotContain("--on-damage", FakeAjiEncode.Args(t));
            Assert.Equal(["PLAN 1 1 0", "VIDEO_START 1 1",
                          "VIDEO_ERROR 1 1 damaged video at 12:34.5 (frame 18093); --keep-damaged finishes it anyway", "DONE 0 0 1 0"], events);
        }

        // the held-frame reuse aji_encode reports for a written video follows VIDEO_DONE
        [Fact]
        public async Task HeldFrameReuseFollowsTheVideo()
        {
            using var t = new TempDir();
            var input = t.File("in.mkv");
            var output = Path.Join(t.Path, "out", "in-animejanai.mkv");
            var paths = FakeAjiEncode.Create(t, 0, PartialOutput.PathFor(output), stderr: ["held frames: reused 1055 of 3412 (30.9%)"],
                                             lines: ["PROGRESS phase=encode frame=3412 total=3412 fps=0.00 pct=100"]);
            var plan = new RunPlan
            {
                Install = new Install(paths),
                Job = new EncodeJob { Backend = Backend.TensorRt, Overwrite = true },
                Videos = [new PlannedVideo(input, output, PlanAction.Encode)],
                Heading = "test",
            };
            var events = new Events();
            Assert.Equal(0, await Run.ExecuteAsync(plan, events, TestContext.Current.CancellationToken));
            Assert.Equal(["PLAN 1 1 0", "VIDEO_START 1 1", "VIDEO_DONE 1 1",
                          "INFO Reused the upscale for 31% of frames (1,055 of 3,412)", "DONE 1 0 0 0"], events.All);
        }
    }
}
