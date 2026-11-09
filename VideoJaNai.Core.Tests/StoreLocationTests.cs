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
            Assert.Null(StoreLocation.Parse("not json"));
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

        // videojanai stops with this instead of "the TensorRT runtime isn't installed".
        [Fact]
        public void TheCommandLineSaysHowToFixAMissingStore()
        {
            var text = Install.StoreMissingText(@"F:\aji");
            Assert.StartsWith(@"the shared runtime's folder F:\aji isn't available. Is its drive connected?", text);
            Assert.Contains("aji-runtime reset", text);
            Assert.Contains("videojanai setup", text);
        }

        [Fact]
        public async Task WithoutAjiRuntimeNothingCountsAsMissing()
        {
            using var t = new TempDir();
            Assert.Null(await StoreLocation.GetAsync(StoreLocation.ExePath(t.Path)));
            Assert.Null(await Install.MissingStoreFolderAsync(t.Path, null));
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
                Assert.Equal(Path.GetFullPath(dest), Path.GetFullPath((await Install.MissingStoreFolderAsync(Path.GetDirectoryName(exe)))!));
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
