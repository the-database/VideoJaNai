using Avalonia.Collections;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using Splat;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AnimeJaNaiConverterGui.Services
{
    public delegate void ProgressChanged(double percentage);

    // Resolves the libaji backend paths and the installed ONNX model list. (Formerly managed the
    // embedded Python / VapourSynth / vs-mlrt backend — that is gone; the native engine + its
    // component packs are installed by the Inno setup / VideoJaNaiUpdater.)
    public class PythonService : IPythonService
    {
        private readonly IUpdateManagerService _updateManagerService;

        public PythonService(IUpdateManagerService? updateManagerService = null)
        {
            _updateManagerService = updateManagerService ?? Locator.Current.GetService<IUpdateManagerService>()!;
        }

        // Installed (Inno): everything lives under the per-user install dir next to VideoJaNai.exe
        // (writable). Dev: a local .\backend folder.
        public string BackendDirectory => (_updateManagerService?.IsInstalled ?? false) ? _updateManagerService.InstallDir : Path.GetFullPath(@".\backend");
        public string AnimeJaNaiDirectory => Path.Join(BackendDirectory, "animejanai");
        public string ModelsDirectory => Path.Join(AnimeJaNaiDirectory, "onnx");
        public string FfmpegDirectory => Path.Join(BackendDirectory, "ffmpeg");
        public string FfmpegPath => Path.GetFullPath(Path.Join(FfmpegDirectory, "ffmpeg.exe"));

        // libaji native engine paths.
        public string InferenceDirectory => Path.Join(AnimeJaNaiDirectory, "inference");
        public string RifeModelsDirectory => Path.Join(AnimeJaNaiDirectory, "rife");
        public string AjiEncodePath => Path.GetFullPath(Path.Join(InferenceDirectory, "aji_encode.exe"));
        public string TrtexecPath => Path.GetFullPath(Path.Join(InferenceDirectory, "trtexec.exe"));
        public string ConfPath => Path.Join(AnimeJaNaiDirectory, "animejanai.conf");

        // "Installed" means the engine can actually run: aji_encode (slim core) AND the TensorRT
        // runtime (trtexec, which ships in the downloadable trt-runtime component pack, not the
        // core). Checking only aji_encode would always be true post-install and the first-run
        // --auto component fallback would never fire when the packs are missing.
        public bool IsInferenceInstalled() => File.Exists(AjiEncodePath) && File.Exists(TrtexecPath);
        public bool AreModelsInstalled() => Directory.Exists(ModelsDirectory) && Directory.GetFiles(ModelsDirectory).Length > 0;
        public bool IsFfmpegInstalled() => File.Exists(FfmpegPath);

        public void ExtractZip(string archivePath, string outFolder, ProgressChanged progressChanged)
        {
            using var fsInput = File.OpenRead(archivePath);
            using var zf = new ZipFile(fsInput);
            for (var i = 0; i < zf.Count; i++)
            {
                ZipEntry zipEntry = zf[i];
                if (!zipEntry.IsFile)
                {
                    continue;
                }
                var fullZipToPath = Path.Combine(outFolder, zipEntry.Name);
                var directoryName = Path.GetDirectoryName(fullZipToPath);
                if (directoryName?.Length > 0)
                {
                    Directory.CreateDirectory(directoryName);
                }
                var buffer = new byte[4096];
                using var zipStream = zf.GetInputStream(zipEntry);
                using Stream fsOutput = File.Create(fullZipToPath);
                StreamUtils.Copy(zipStream, fsOutput, buffer);
                progressChanged?.Invoke(Math.Round((double)i / zf.Count * 100, 0));
            }
        }

        private AvaloniaList<string>? _allModels;

        public AvaloniaList<string> AllModels
        {
            get
            {
                if (_allModels == null)
                {
                    try
                    {
                        // The libaji engine consumes ONNX models only.
                        var models = new AvaloniaList<string>(Directory.GetFiles(ModelsDirectory).Where(filename =>
                            Path.GetExtension(filename).Equals(".onnx", StringComparison.CurrentCultureIgnoreCase))
                        .Select(filename => Path.GetFileName(filename))
                        .Order().ToList());

                        Debug.WriteLine($"GetAllModels: {models.Count}");
                        _allModels = models;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        Debug.WriteLine($"GetAllModels: DirectoryNotFoundException");
                        return [];
                    }
                }

                return _allModels;
            }
        }
    }
}
