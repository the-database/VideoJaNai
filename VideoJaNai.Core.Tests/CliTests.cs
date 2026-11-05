using System;
using System.IO;
using System.Linq;
using System.Text;
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
                [upscale]
                model = a.onnx
                model = b.onnx
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
            Assert.Equal(["a.onnx", "b.onnx"], o.Models);
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
        [InlineData(new[] { "--preset", "balanced", "--model", "x" }, "give only one of --preset, --model or --workflow")]
        [InlineData(new[] { "--height", "1080", "--resize-percent", "50" }, "give only one of --height and --resize-percent")]
        [InlineData(new[] { "--encoder", "h266" }, "--encoder should be one of auto, nvenc-hevc, nvenc-av1, nvenc-lossless, amf-hevc, amf-av1, qsv-hevc, qsv-av1, x265, x264, ffv1, not 'h266'")]
        [InlineData(new[] { "--temporal-fix", "9" }, "--temporal-fix should be 1, 2 or 3 (0: off), not '9'")]
        public void BadFlags(string[] args, string message)
        {
            Assert.Equal(message, Assert.Throws<UsageException>(() => Flags.Parse(args)).Message);
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
            t.File(Path.Join("in", "s", "c" + Run.PartialMarker + ".mkv"));
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

        [Fact]
        public void PartialFilesKeepTheContainerExtension()
        {
            Assert.Equal(Path.Join("d", "ep-animejanai" + Run.PartialMarker + ".mkv"), Run.PartialPath(Path.Join("d", "ep-animejanai.mkv")));
        }
    }
}
