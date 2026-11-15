using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoJaNai.Core;
using Xunit;

namespace VideoJaNai.Core.Tests
{
    // Which copy of VideoJaNai the updater is in, for the pack store (StoreCopy, which the updater
    // compiles in): the installed copy keeps the plain ref, any other copy its own, and only its own
    // ref counts as installed.
    public class StoreCopyTests
    {
        [Fact]
        public void TheInstalledCopyIsToldFromAZipCopy()
        {
            using var t = new TempDir();
            var dir = Path.Join(t.Path, "copy-kind");
            Directory.CreateDirectory(dir);
            Assert.False(StoreCopy.IsSetupInstall(dir, []), "an empty folder");
            File.WriteAllText(Path.Join(dir, "uninstall-notes.exe"), "");
            File.WriteAllText(Path.Join(dir, "unins.exe"), "");
            Assert.False(StoreCopy.IsSetupInstall(dir, []), "another exe named like an uninstaller");
            File.WriteAllText(Path.Join(dir, "unins000.exe"), "");
            Assert.True(StoreCopy.IsSetupInstall(dir, []), "an install with unins000.exe");
            File.Delete(Path.Join(dir, "unins000.exe"));
            File.WriteAllText(Path.Join(dir, StoreCopy.SetupMarker), "");
            Assert.True(StoreCopy.IsSetupInstall(dir, []), "the setup's marker");

            // the installer's uninstall entry (InstallLocation has a trailing separator, App Path none)
            File.WriteAllText(Path.Join(dir, "unins000.exe"), "");
            Assert.True(StoreCopy.IsSetupInstall(dir, [dir + Path.DirectorySeparatorChar, dir]), "the entry naming this folder");
            if (OperatingSystem.IsWindows())
            {
                Assert.True(StoreCopy.IsSetupInstall(dir, [dir.ToUpperInvariant() + Path.DirectorySeparatorChar]), "folder case");
            }
            var elsewhere = Path.Join(t.Path, "installed-elsewhere");
            Assert.False(StoreCopy.IsSetupInstall(dir, [elsewhere + Path.DirectorySeparatorChar, elsewhere]),
                "a copy of the installed folder (the entry names another folder) took the installed copy's id");
            var zip = Path.Join(t.Path, "copy-kind-zip");
            Directory.CreateDirectory(zip);
            Assert.False(StoreCopy.IsSetupInstall(zip, [zip]), "an entry naming a folder without the setup's files");
        }

        // The updater reads the uninstall entry of the AppId the installer has.
        [Fact]
        public void TheUpdaterReadsTheInstallersAppId()
        {
            var iss = File.ReadAllText(Path.Join(RepoRoot(), "installer", "videojanai.iss"));
            var ids = Regex.Matches(iss, @"^AppId=(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Replace("{{", "{")).ToList();
            Assert.Equal(ids.Order(), StoreCopy.SetupAppIds.Order());
            Assert.Contains(StoreCopy.SetupMarker, iss);
            Assert.Contains("unref --app-id videojanai --app-path", iss);
        }

        // The package never ships a copy's store setting or the store inside it, and updates keep them.
        [Fact]
        public void ThePackageKeepsEachCopysStore()
        {
            var builder = File.ReadAllText(Path.Join(RepoRoot(), "BuildVideoJaNai", "Program.cs"));
            var block = builder[builder.IndexOf("user_preserve = new[]", StringComparison.Ordinal)..];
            block = block[..block.IndexOf("},", StringComparison.Ordinal)];
            var preserve = Regex.Matches(block, @"^\s*""([^""]+)"",", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
            Assert.Contains("aji-runtime.conf", preserve);
            Assert.Contains("animejanai/runtime", preserve);
            Assert.Equal(StoreCopy.InsideStore.Replace('\\', '/'), "animejanai/runtime");
            Assert.Contains("must not ship aji-runtime.conf", builder);
            using var t = new TempDir();
            Assert.Equal(Path.Join(t.Path, "animejanai", "runtime"), StoreCopy.InsideStoreDir(t.Path));
        }

        static string Show(string? appPath, bool hasRef = true) => JsonSerializer.Serialize(new
        {
            app_id = "videojanai",
            app_path = appPath,
            has_ref = hasRef,
            ref_id = "videojanai",
            packs = new object[]
            {
                new { name = "trt-runtime", version = "11.4", dir = @"C:\s\packs\trt-runtime\11.4", installed = true },
                new { name = "trtrtx-runtime", version = "1.7", dir = @"C:\s\packs\trtrtx-runtime\1.7", installed = true },
                new { name = "trt-sm89", version = "11.4", dir = @"C:\s\packs\trt-sm89\11.4", installed = false },
            },
        });

        // Only this copy's ref counts: a plain ref naming another copy's exe is another copy's.
        [Fact]
        public void OnlyThisCopysRefCounts()
        {
            var me = OperatingSystem.IsWindows() ? @"C:\Apps\VideoJaNai\VideoJaNai.exe" : "/apps/VideoJaNai/VideoJaNai";
            var same = OperatingSystem.IsWindows() ? @"c:\apps\VIDEOJANAI\.\VideoJaNai.exe" : "/apps/VideoJaNai/./VideoJaNai";
            var own = StoreCopy.ParseOwnRef(Show(same), me);
            Assert.NotNull(own);
            Assert.Equal("videojanai", own.RefId);
            Assert.Equal(["trt-runtime", "trtrtx-runtime"], own.Packs.Keys.Order());
            Assert.Null(StoreCopy.ParseOwnRef(Show(OperatingSystem.IsWindows() ? @"C:\Other\VideoJaNai\VideoJaNai.exe" : "/other/VideoJaNai"), me));
            Assert.Null(StoreCopy.ParseOwnRef(Show(null, hasRef: false), me));
            Assert.Null(StoreCopy.ParseOwnRef("not json", me));
            Assert.Null(StoreCopy.ParseOwnRef("[]", me));
        }

        // `aji-runtime location --json` (0.3.0), and which of the three choices a store is.
        [Fact]
        public void TheStoreLocationIsRead()
        {
            var loc = StoreCopy.ParseLocation("""
                {"available": false, "default_root": "C:\\u\\aji", "moved": false, "relative": true,
                 "root": "D:\\p\\animejanai\\runtime", "setting": "animejanai\\runtime",
                 "settings_file": "D:\\p\\aji-runtime.conf", "source": "settings", "target": null}
                """);
            Assert.True(loc is { Available: false, Source: "settings", Relative: true, Folder: @"D:\p\animejanai\runtime", SettingsFile: @"D:\p\aji-runtime.conf" });
            var moved = StoreCopy.ParseLocation("""{"available": false, "moved": true, "root": "C:\\u\\aji", "target": "E:\\aji"}""");
            Assert.True(moved is { Folder: @"E:\aji", Source: null }, "a moved store's folder is its target; an old aji-runtime has no source");
            Assert.Null(StoreCopy.ParseLocation("""{"root": ""}"""));
            Assert.Null(StoreCopy.ParseLocation("nope"));

            if (OperatingSystem.IsWindows())
            {
                Assert.Equal("inside", StoreCopy.Choice(loc!, @"d:\P\animejanai\runtime\"));
                var shared = StoreCopy.ParseLocation("""{"root": "C:/u/aji", "default_root": "C:\\u\\aji", "source": "default"}""")!;
                Assert.Equal("shared", StoreCopy.Choice(shared, @"D:\p\animejanai\runtime"));
                var custom = StoreCopy.ParseLocation("""{"root": "G:\\JaNai", "default_root": "C:\\u\\aji", "source": "settings"}""")!;
                Assert.Equal("custom", StoreCopy.Choice(custom, @"D:\p\animejanai\runtime"));
            }
        }

        static string RepoRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Join(d.FullName, "VideoJaNai.sln")))
                {
                    return d.FullName;
                }
            }
            throw new InvalidOperationException("VideoJaNai.sln not found above the test's folder");
        }
    }
}
