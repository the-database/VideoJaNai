using System;
using System.IO;
using System.Threading.Tasks;
using VideoJaNai.Core;

namespace VideoJaNai.Cli
{
    // Where the engine is: a VideoJaNai tree's animejanai folder (inference/ with aji_encode and its
    // runtime, onnx/ with the models and engines, rife/). The command line's own is next to the
    // program, completed by `videojanai setup`; without that, on Windows, an installed VideoJaNai's.
    public sealed class Install
    {
        public string AnimeJaNaiDirectory { get; }
        public AjiPaths Paths { get; }

        private Install(string animeJaNaiDirectory, string confPath, string? inferenceDirectory = null)
        {
            AnimeJaNaiDirectory = animeJaNaiDirectory;
            var paths = AjiPaths.ForAnimeJaNaiFolder(animeJaNaiDirectory, confPath);
            Paths = inferenceDirectory is null ? paths : paths with
            {
                InferenceDirectory = inferenceDirectory,
                AjiEncodePath = Path.Join(inferenceDirectory, Platform.Exe("aji_encode")),
                TrtexecPath = RuntimePacks.TrtexecPath(inferenceDirectory),
            };
        }

        public static string ExeDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, '/');

        public static string? GuiInstallDir =>
            OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local
                ? Path.Join(local, "Programs", "VideoJaNai")
                : null;

        // A tree's root (where components.json, the updater and animejanai/ are) is usable once its
        // components are set up: the TensorRT runtime is there (in the tree, or the shared pack store's
        // that aji-runtime.json names), or the updater's hardware setup ran (a PC without an NVIDIA GPU
        // needs no packs; DirectML ships in the core).
        public static bool IsSetUp(string root) =>
            File.Exists(Path.Join(root, "animejanai", "inference", Platform.Exe("aji_encode"))) &&
            (File.Exists(RuntimePacks.TrtexecPath(Path.Join(root, "animejanai", "inference"))) ||
             File.Exists(Path.Join(root, "components.json")));

        // --aji-dir: an animejanai folder, its inference folder, or a tree root.
        public static Install Find(string? ajiDir)
        {
            var conf = Path.Join(Path.GetTempPath(), $"videojanai-{Environment.ProcessId}.conf");
            if (!string.IsNullOrEmpty(ajiDir))
            {
                var dir = Path.GetFullPath(ajiDir);
                if (File.Exists(Path.Join(dir, Platform.Exe("aji_encode"))))
                {
                    // aji_encode's own folder (inference/, or a build folder); the models are next to it
                    var inference = dir.TrimEnd(Path.DirectorySeparatorChar, '/');
                    return new Install(Path.GetDirectoryName(inference)!, conf, inference);
                }
                else if (Directory.Exists(Path.Join(dir, "animejanai", "inference")))
                {
                    dir = Path.Join(dir, "animejanai");
                }
                return new Install(dir, conf);
            }
            var own = ExeDir;
            if (!IsSetUp(own) && GuiInstallDir is string gui && IsSetUp(gui))
            {
                return new Install(Path.Join(gui, "animejanai"), conf);
            }
            return new Install(Path.Join(own, "animejanai"), conf);
        }

        public bool AjiEncodeExists => File.Exists(Paths.AjiEncodePath);
        public bool TensorRtInstalled => File.Exists(Paths.TrtexecPath);
        public bool TensorRtRtxInstalled => RuntimePacks.TensorRtRtxInstalled(Paths.InferenceDirectory);
        public bool DirectMlInstalled => File.Exists(Path.Join(Paths.InferenceDirectory, "aji_dml.dll"));

        public bool IsGuiInstall => GuiInstallDir is string gui &&
            Path.GetFullPath(Path.Join(gui, "animejanai")).Equals(Path.GetFullPath(AnimeJaNaiDirectory), StringComparison.OrdinalIgnoreCase);

        // `aji_encode --probe-encoders`, cached for a day per aji_encode build (it takes seconds);
        // fresh: always probe (devices). Null without an aji_encode that probes.
        public async Task<string?> ProbeAsync(bool fresh = false)
        {
            var cache = ProbeCachePath();
            if (!fresh && cache != null)
            {
                try
                {
                    var info = new FileInfo(cache);
                    if (info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromDays(1))
                    {
                        var text = await File.ReadAllTextAsync(cache);
                        if (EncoderProbe.Parse(text) != null)
                        {
                            return text;
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var output = await AjiProcess.ProbeAsync(Paths);
            if (output != null && cache != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                    await File.WriteAllTextAsync(cache, output);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return output;
        }

        private string? ProbeCachePath()
        {
            try
            {
                var exe = new FileInfo(Paths.AjiEncodePath);
                if (!exe.Exists)
                {
                    return null;
                }
                var key = $"{exe.FullName}|{exe.Length}|{exe.LastWriteTimeUtc.Ticks}";
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
                return string.IsNullOrEmpty(root) ? null : Path.Join(root, "videojanai", $"probe-{hash}.txt");
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
