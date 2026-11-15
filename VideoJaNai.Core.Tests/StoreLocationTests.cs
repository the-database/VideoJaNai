using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VideoJaNai.Cli;
using VideoJaNai.Core;
using Xunit;

namespace VideoJaNai.Core.Tests
{
    // Where the shared pack store is, through aji-runtime 0.2.0+ (location, move, reset). The tests
    // that set AJI_RUNTIME_STORE live in this one class, so they never run alongside each other.
    public class StoreLocationTests
    {
        [Fact]
        public void LocationJsonIsRead()
        {
            Assert.Equal(new StoreLocation.Info(@"C:\a", false, null, true),
                StoreLocation.Parse("""{"root": "C:\\a", "moved": false, "target": null, "available": true}"""));
            var moved = StoreLocation.Parse("""{"root": "C:\\a", "moved": true, "target": "F:\\b", "available": false}""")!;
            Assert.Equal(@"F:\b", moved.Folder);
            Assert.True(moved.Missing);
            Assert.False(new StoreLocation.Info(@"C:\a", true, @"F:\b", true).Missing);
            // a folder this copy was set to (aji-runtime.conf) whose drive is gone is missing too
            Assert.True(new StoreLocation.Info(@"G:\JaNai", false, null, false).Missing);
            Assert.Null(StoreLocation.Parse("not json"));
        }

        // `VideoJaNaiUpdater --storage --json`, for App Settings > Storage.
        [Fact]
        public void TheStorageChoiceIsRead()
        {
            var inside = StoreLocation.ParseChoice("""
                {"choice": "inside", "root": "D:\\vjn\\animejanai\\runtime", "moved": false, "target": null, "available": true,
                 "inside": "D:\\vjn\\animejanai\\runtime", "shared": "C:\\Users\\me\\AppData\\Local\\the-database\\aji", "installed": true, "bytes": 445644800}
                """)!;
            Assert.True(inside is { Choice: StoreLocation.StoreChoice.Inside, Installed: true, Available: true, IsShared: false, Bytes: 445644800 });
            Assert.Equal(@"D:\vjn\animejanai\runtime", inside.Folder);
            Assert.Equal(@"C:\Users\me\AppData\Local\the-database\aji", inside.SharedFolder);

            var shared = StoreLocation.ParseChoice("""{"choice": "shared", "root": "C:\\aji", "moved": true, "target": "F:\\aji", "available": true, "shared": "C:\\aji", "installed": true}""")!;
            Assert.True(shared is { IsShared: true, MovedHere: true, Missing: false, Bytes: null });
            Assert.Equal(@"F:\aji", shared.Folder);
            // the shared folder was moved, but this copy keeps its runtime inside
            var insideMoved = StoreLocation.ParseChoice("""{"choice": "inside", "root": "D:\\in", "moved": true, "target": "F:\\aji", "shared": "C:\\aji"}""")!;
            Assert.True(insideMoved is { Folder: @"D:\in", MovedHere: false, SharedFolder: @"F:\aji" });
            var custom = StoreLocation.ParseChoice("""{"choice": "custom", "root": "G:\\JaNai", "available": false}""")!;
            Assert.True(custom is { Choice: StoreLocation.StoreChoice.Custom, Missing: true, Installed: false, Inside: null });

            Assert.Null(StoreLocation.ParseChoice("""{"choice": "inside", "root": "D:\\in", "bytes": -1}""")!.Bytes);
            Assert.Null(StoreLocation.ParseChoice("""{"choice": "inside", "root": "D:\\in", "bytes": "lots"}""")!.Bytes);
            // anything else is an updater without the choice
            Assert.Null(StoreLocation.ParseChoice("""{"root": "C:\\a", "moved": false, "target": null, "available": true}"""));
            Assert.Null(StoreLocation.ParseChoice("""{"choice": "elsewhere", "root": "C:\\a"}"""));
            Assert.Null(StoreLocation.ParseChoice("""{"choice": "inside", "root": ""}"""));
            Assert.Null(StoreLocation.ParseChoice("Usage: VideoJaNaiUpdater.exe [--check]"));
            Assert.Null(StoreLocation.ParseChoice("[1]"));
            // aji-runtime's answer is the shared store
            Assert.True(StoreLocation.Parse("""{"root": "C:\\a", "available": true}""") is { Choice: null, IsShared: true, SharedFolder: @"C:\a" });
        }

        // Shown where a store is used and TensorRT is, or can be, installed.
        [Fact]
        public void TheSectionShowsWhereTensorRtIsOrCanBe()
        {
            var info = new StoreLocation.Info(@"C:\aji", false, null, true);
            Assert.False(StoreLocation.Shows(null, true, true), "no store");
            Assert.True(StoreLocation.Shows(info, true, false), "TensorRT installed");
            Assert.True(StoreLocation.Shows(info, false, true) && StoreLocation.Shows(info, false, null), "an NVIDIA GPU (or not known yet)");
            Assert.False(StoreLocation.Shows(info, false, false), "no GPU that runs TensorRT, and no TensorRT");
        }

        // A folder I choose: a store as it is, a new or empty folder, else a folder of its own inside.
        [Fact]
        public void AChosenFolderIsUsedAsAStoreOrGetsOneInside()
        {
            using var t = new TempDir();
            var pick = Path.Join(t.Path, "pick");
            Assert.Equal(pick, StoreLocation.ChoiceTarget(pick));
            Directory.CreateDirectory(pick);
            Assert.Equal(pick, StoreLocation.ChoiceTarget(pick));
            File.WriteAllText(Path.Join(pick, "holiday.mkv"), "x");
            var own = Path.Join(pick, "JaNai shared runtime");
            Assert.Equal(own, StoreLocation.ChoiceTarget(pick));
            Directory.CreateDirectory(own);
            File.WriteAllText(Path.Join(own, "store.json"), """{"format": 1}""");
            Assert.Equal(own, StoreLocation.ChoiceTarget(pick));
            Assert.True(StoreLocation.IsStore(own));
            var store = Path.Join(t.Path, "other-app-store");
            Directory.CreateDirectory(Path.Join(store, "packs"));
            File.WriteAllText(Path.Join(store, "store.json"), """{"format": 1}""");
            Assert.Equal(store, StoreLocation.ChoiceTarget(store));

            var info = new StoreLocation.Info(@"C:\aji", false, null, true)
            {
                Choice = StoreLocation.StoreChoice.Custom, Inside = @"D:\vjn\animejanai\runtime", Shared = @"C:\aji",
            };
            Assert.Equal("inside", StoreLocation.ChoiceArgument(info, @"D:\vjn\animejanai\runtime"));
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal("inside", StoreLocation.ChoiceArgument(info, @"d:\VJN\animejanai\runtime\"));
            }
            Assert.Equal("shared", StoreLocation.ChoiceArgument(info, @"C:\aji"));
            Assert.Equal(own, StoreLocation.ChoiceArgument(info, own));
        }

        [Fact]
        public void MoveProgressIsRead()
        {
            Assert.True(StoreLocation.TryProgress("AJI_MOVE 10 20 ", out var done, out var total) && (done, total) == (10, 20));
            Assert.False(StoreLocation.TryProgress("AJI_MOVE 10", out _, out _));
            Assert.False(StoreLocation.TryProgress("Moving...", out _, out _));
        }

        [Fact]
        public void AMoveGoesIntoAFolderOfItsOwnWhenThePickedOneHasFiles()
        {
            using var t = new TempDir();
            Assert.Equal(t.Path, StoreLocation.MoveTarget(t.Path));
            Assert.Equal(Path.Join(t.Path, "new"), StoreLocation.MoveTarget(Path.Join(t.Path, "new")));
            File.WriteAllText(Path.Join(t.Path, "mine.txt"), "x");
            Assert.Equal(Path.Join(t.Path, "JaNai shared runtime"), StoreLocation.MoveTarget(t.Path));
        }

        // videojanai stops with this instead of "the TensorRT runtime isn't installed": the shared
        // folder moved to a missing drive is reset; a folder this copy was set to is replaced.
        [Fact]
        public void TheCommandLineSaysHowToFixAMissingStore()
        {
            var text = Install.StoreMissingText(new StoreLocation.Info(@"C:\aji", true, @"F:\aji", false));
            Assert.StartsWith(@"the TensorRT runtime's folder F:\aji isn't available. Is its drive connected?", text);
            Assert.Contains("aji-runtime reset", text);
            Assert.Contains("videojanai setup", text);
            var custom = Install.StoreMissingText(new StoreLocation.Info(@"G:\JaNai", false, null, false));
            Assert.StartsWith(@"the TensorRT runtime's folder G:\JaNai isn't available.", custom);
            Assert.Contains("VideoJaNaiUpdater --storage", custom);
            Assert.DoesNotContain("aji-runtime reset", custom);
        }

        [Fact]
        public async Task WithoutAjiRuntimeNothingCountsAsMissing()
        {
            using var t = new TempDir();
            Assert.Null(await StoreLocation.GetAsync(StoreLocation.ExePath(t.Path)));
            Assert.Null(await Install.MissingStoreAsync(t.Path, null));
        }

        // Against a real aji-runtime (AJI_RUNTIME_EXE: a 0.2.0+ build), on a store in a temporary
        // folder: what the Storage buttons and videojanai's check run.
        [Fact]
        public async Task MovingBackAndResettingThroughAjiRuntime()
        {
            var exe = Environment.GetEnvironmentVariable("AJI_RUNTIME_EXE");
            Assert.SkipUnless(File.Exists(exe), "set AJI_RUNTIME_EXE to an aji-runtime 0.2.0+ build");
            var dir = Path.Join(Path.GetTempPath(), $"videojanai-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var old = Environment.GetEnvironmentVariable("AJI_RUNTIME_STORE");
            try
            {
                var root = Path.Join(dir, "store");
                Directory.CreateDirectory(Path.Join(root, "packs", "trt", "1"));
                File.WriteAllText(Path.Join(root, "store.json"), """{"format": 1}""");
                File.WriteAllBytes(Path.Join(root, "packs", "trt", "1", "nvinfer.dll"), new byte[1_000_000]);
                Environment.SetEnvironmentVariable("AJI_RUNTIME_STORE", root);

                Assert.Equal(new StoreLocation.Info(root, false, null, true), await StoreLocation.GetAsync(exe));
                Assert.Null(await StoreLocation.MissingFolderAsync(exe));

                var dest = Path.Join(dir, "other-drive");
                var progress = new List<(long, long)>();
                Assert.Null(await StoreLocation.ChangeAsync(exe, ["move", dest], (d, t) => progress.Add((d, t))));
                Assert.True(progress[^1].Item1 == progress[^1].Item2 && progress[^1].Item2 > 1_000_000, "progress reaches the store's size");
                var moved = await StoreLocation.GetAsync(exe);
                Assert.True(moved is { Moved: true, Available: true });
                Assert.Equal(Path.GetFullPath(dest), Path.GetFullPath(moved!.Folder));
                Assert.True(File.Exists(Path.Join(root, "packs", "trt", "1", "nvinfer.dll")), "read through the default path");
                Assert.True(StoreLocation.Size(moved.Folder) >= 1_000_000);

                Assert.Contains("move it back instead", await StoreLocation.ChangeAsync(exe, ["reset"], null));

                Directory.Move(dest, dest + "-unplugged");
                Assert.True(await StoreLocation.GetAsync(exe) is { Missing: true });
                Assert.Equal(Path.GetFullPath(dest), Path.GetFullPath((await Install.MissingStoreAsync(Path.GetDirectoryName(exe)))!.Folder));
                Assert.Contains("missing", await StoreLocation.ChangeAsync(exe, ["move", "--default"], null));
                Assert.Null(await StoreLocation.ChangeAsync(exe, ["reset"], null));
                Assert.Equal(new StoreLocation.Info(root, false, null, true), await StoreLocation.GetAsync(exe));

                Directory.Move(dest + "-unplugged", dest);
                Assert.Contains("isn't empty", await StoreLocation.ChangeAsync(exe, ["move", dest], null));
                Assert.Null(await StoreLocation.ChangeAsync(exe, ["move", Path.Join(dir, "d2")], null));
                Assert.Null(await StoreLocation.ChangeAsync(exe, ["move", "--default"], null));
                Assert.True(await StoreLocation.GetAsync(exe) is { Moved: false });
            }
            finally
            {
                Environment.SetEnvironmentVariable("AJI_RUNTIME_STORE", old);
                // a failed step can leave the default path a link (maybe a dangling one): remove the
                // link itself first, so cleaning up never follows it
                var link = new DirectoryInfo(Path.Join(dir, "store"));
                try
                {
                    if (link.LinkTarget is not null)
                    {
                        link.Delete();
                    }
                    Directory.Delete(dir, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
