// Package builder for VideoJaNai (Inno-installer distribution model).
//
// Assembles the redistributable install tree (the VideoJaNai app + VideoJaNaiUpdater + the libaji
// inference dir + ONNX models) and, under --packs, carves the heavy hardware-specific pieces
// (TensorRT runtime, per-GPU-generation builder resources, RIFE models) into component-*.7z +
// packs.json release assets, then slims them out of the core. End users only ever download the
// small core + their GPU's component packs (never the multi-GB vs-mlrt bundle).
//
//   BuildVideoJaNai.exe <version> [--app-dir <dir>] [--updater <exe>] [--packs]
//   BuildVideoJaNai.exe <version> --packs-only [dir]   (emit packs from an existing tree)
//
// Dev overrides (skip large downloads on local builds):
//   VIDEOJANAI_VSMLRT_DIR  -> a local extracted vsmlrt-cuda/ dir (TensorRT runtime source)
//   VIDEOJANAI_FFMPEG_DIR  -> a local extracted gyan ffmpeg shared dir (with bin/)
//   AJI_LOCAL_ZIP          -> a locally built aji-windows-x64.zip
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using SevenZipExtractor;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using static Downloader;

// Third-party component versions. Bump these together when cutting a release.
// aji_trt.dll must be built against the SAME TensorRT major.minor as the vs-mlrt runtime
// (v16.x == TensorRT 11.0).
const string VsMlrtCudaVersion = "v16.test1";              // AmusementClub/vs-mlrt cuda release (TensorRT 11)
const string AjiVersion        = "v0.4.0";                 // the-database/animejanai-inference (multi-GPU kernels + --pix-fmt + zero-copy 10-bit)
const string SevenZipVersion   = "2501";                  // 7-zip "extra" standalone console
const string RifeModelsVersion = "models-rife-fp16-1";    // animejanai-inference rife fp16 release
const string FfmpegVersion     = "8.1.1";                 // gyan.dev ffmpeg shared build (dev DLLs for aji_encode)

// TensorRT runtime files taken from the vs-mlrt cuda archive's vsmlrt-cuda/ directory. Everything
// else there (cuDNN, cuBLAS, onnxruntime, lean/dispatch runtimes) is unused by aji's TensorRT path.
string[] inferenceRuntimeFiles =
[
    "nvinfer_11.dll",
    "nvinfer_plugin_11.dll",
    "nvonnxparser_11.dll",
    "trtexec.exe",
];
string[] inferenceRuntimePrefixes =
[
    "cudart64_",
    "nvinfer_builder_resource_",
];

if (args.Length < 1)
{
    throw new ArgumentException("Version is required.  Usage: BuildVideoJaNai <version> [--app-dir <dir>] [--updater <exe>] [--packs]");
}

var version = args[0];
var assemblyDirectory = AppContext.BaseDirectory;
var installDirectory = Path.Combine(assemblyDirectory, $"VideoJaNai-v{version}");

// --packs-only [dir]: emit component packs from an already-built install tree and exit.
int packsOnlyIndex = Array.IndexOf(args, "--packs-only");
if (packsOnlyIndex >= 0 && packsOnlyIndex + 1 < args.Length && Directory.Exists(args[packsOnlyIndex + 1]))
{
    installDirectory = Path.GetFullPath(args[packsOnlyIndex + 1]);
}

string? ArgValue(string flag)
{
    int i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var inferencePath = Path.Combine(installDirectory, "animejanai", "inference");
var onnxPath = Path.Combine(installDirectory, "animejanai", "onnx");
var rifePath = Path.Combine(installDirectory, "animejanai", "rife");

// Standalone 7-Zip console (7za.exe): extracts the multi-part vs-mlrt archive + emits component
// packs, and ships at the install root for the updater (manifest archive_tool).
async Task InstallSevenZip()
{
    Console.WriteLine("Downloading 7-Zip standalone console...");
    var downloadUrl = $"https://www.7-zip.org/a/7z{SevenZipVersion}-extra.7z";
    var targetPath = Path.GetFullPath("7z-extra.7z");
    await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading 7-Zip ({p}%)..."));

    var targetExtractPath = Path.GetFullPath("7z-extra-temp");
    Directory.CreateDirectory(targetExtractPath);
    using (ArchiveFile archiveFile = new(targetPath))
    {
        archiveFile.Extract(targetExtractPath);
    }
    File.Copy(Path.Combine(targetExtractPath, "x64", "7za.exe"), Path.Combine(installDirectory, "7za.exe"), true);
    Directory.Delete(targetExtractPath, true);
    File.Delete(targetPath);
}

async Task InstallInferenceRuntime()
{
    Directory.CreateDirectory(inferencePath);
    var cudaDirectory = Environment.GetEnvironmentVariable("VIDEOJANAI_VSMLRT_DIR");
    string? tempDirectory = null;

    if (!string.IsNullOrEmpty(cudaDirectory))
    {
        Console.WriteLine($"Using local TensorRT runtime: {cudaDirectory}");
    }
    else
    {
        Console.WriteLine("Downloading TensorRT runtime (from the vs-mlrt cuda release)...");
        var baseDownloadUrl = $"https://github.com/AmusementClub/vs-mlrt/releases/download/{VsMlrtCudaVersion}/";
        var fileNames = new[]
        {
            $"vsmlrt-windows-x64-cuda.{VsMlrtCudaVersion}.7z.001",
            $"vsmlrt-windows-x64-cuda.{VsMlrtCudaVersion}.7z.002",
        };
        var targetPaths = fileNames.Select(Path.GetFullPath).ToArray();
        double lastProgress = -1;
        for (int i = 0; i < fileNames.Length; i++)
        {
            int idx = i;
            await DownloadFileAsync(baseDownloadUrl + fileNames[i], targetPaths[i], p =>
            {
                if (p >= lastProgress + 5) { Console.WriteLine($"Downloading {fileNames[idx]} ({p}%)..."); lastProgress = p; }
            });
        }

        Console.WriteLine("Extracting TensorRT runtime (this may take several minutes)...");
        tempDirectory = Path.GetFullPath("vsmlrt-temp");
        Directory.CreateDirectory(tempDirectory);
        // Only vsmlrt-cuda/ is needed; extracting just that subtree skips the plugin DLLs.
        await RunProcess(Path.Combine(installDirectory, "7za.exe"),
                         $"x \"{targetPaths[0]}\" -o\"{tempDirectory}\" \"vsmlrt-cuda\\*\" -r- -y");
        cudaDirectory = Path.Combine(tempDirectory, "vsmlrt-cuda");
        foreach (var t in targetPaths) File.Delete(t);
    }

    foreach (var file in Directory.GetFiles(cudaDirectory))
    {
        var name = Path.GetFileName(file);
        bool keep = inferenceRuntimeFiles.Contains(name) ||
                    inferenceRuntimePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
                    name.Contains("LICENSE", StringComparison.OrdinalIgnoreCase);
        if (keep)
        {
            File.Copy(file, Path.Combine(inferencePath, name), true);
        }
    }

    if (tempDirectory != null) Directory.Delete(tempDirectory, true);
}

async Task InstallAji()
{
    Directory.CreateDirectory(inferencePath);
    var localZip = Environment.GetEnvironmentVariable("AJI_LOCAL_ZIP");
    string targetPath;
    if (!string.IsNullOrEmpty(localZip))
    {
        Console.WriteLine($"Using local aji build: {localZip}");
        targetPath = localZip;
    }
    else
    {
        Console.WriteLine("Downloading aji (native inference shim + aji_encode)...");
        var downloadUrl = $"https://github.com/the-database/animejanai-inference/releases/download/{AjiVersion}/aji-windows-x64.zip";
        targetPath = Path.GetFullPath("aji-windows-x64.zip");
        await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading aji ({p}%)..."));
    }

    ExtractZip(targetPath, inferencePath, _ => { });

    if (string.IsNullOrEmpty(localZip)) File.Delete(targetPath);
}

// ffmpeg shared (dev) build: aji_encode.exe dynamically links libavformat/avcodec/avutil/avfilter/
// swscale (+ swresample/postproc deps), so the matching shared DLLs must sit next to it in the
// inference dir. gyan's GPL shared build includes libx264/libx265 (needed for software encodes).
async Task InstallFfmpeg()
{
    Directory.CreateDirectory(inferencePath);
    var ffmpegDir = Environment.GetEnvironmentVariable("VIDEOJANAI_FFMPEG_DIR");
    string? tempDirectory = null;

    if (!string.IsNullOrEmpty(ffmpegDir))
    {
        Console.WriteLine($"Using local ffmpeg shared build: {ffmpegDir}");
    }
    else
    {
        Console.WriteLine("Downloading ffmpeg shared build...");
        var downloadUrl = $"https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-{FfmpegVersion}-full_build-shared.7z";
        var targetPath = Path.GetFullPath("ffmpeg-shared.7z");
        await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading ffmpeg ({p}%)..."));
        tempDirectory = Path.GetFullPath("ffmpeg-temp");
        Directory.CreateDirectory(tempDirectory);
        using (ArchiveFile archiveFile = new(targetPath)) archiveFile.Extract(tempDirectory);
        File.Delete(targetPath);
        // archive roots at ffmpeg-<ver>-full_build-shared/
        ffmpegDir = Directory.GetDirectories(tempDirectory).FirstOrDefault(d => Path.GetFileName(d).StartsWith("ffmpeg"))
                    ?? tempDirectory;
    }

    var binDir = Path.Combine(ffmpegDir, "bin");
    foreach (var dll in Directory.GetFiles(binDir, "*.dll"))
    {
        File.Copy(dll, Path.Combine(inferencePath, Path.GetFileName(dll)), true);
    }
    var ffmpegLicense = Path.Combine(ffmpegDir, "LICENSE.txt");
    if (File.Exists(ffmpegLicense))
    {
        File.Copy(ffmpegLicense, Path.Combine(inferencePath, "ffmpeg_LICENSE.txt"), true);
    }

    if (tempDirectory != null) Directory.Delete(tempDirectory, true);
}

async Task InstallRife()
{
    Console.WriteLine("Downloading RIFE fp16 models...");
    var downloadUrl = $"https://github.com/the-database/animejanai-inference/releases/download/{RifeModelsVersion}/rife-fp16-1.7z";
    var targetPath = Path.GetFullPath("rife-fp16.7z");
    await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading RIFE fp16 models ({p}%)..."));

    Directory.CreateDirectory(rifePath);
    using (ArchiveFile archiveFile = new(targetPath)) archiveFile.Extract(rifePath);
    File.Delete(targetPath);
}

// ONNX models are version-controlled in this project (onnx/, ~6 MB) rather than downloaded — they
// are small, and this keeps the shipped set in lockstep with the repo (matches mpv-AnimeJaNai's
// committed model set). The csproj copies onnx/ next to the built exe.
void InstallModels()
{
    var modelsSource = Path.Combine(assemblyDirectory, "onnx");
    if (!Directory.Exists(modelsSource))
    {
        Console.WriteLine($"WARNING: bundled models dir not found at {modelsSource}; no models will ship.");
        return;
    }
    Console.WriteLine("Copying bundled ONNX models...");
    Directory.CreateDirectory(onnxPath);
    foreach (var onnx in Directory.GetFiles(modelsSource, "*.onnx", SearchOption.AllDirectories))
    {
        File.Copy(onnx, Path.Combine(onnxPath, Path.GetFileName(onnx)), true);
    }
}

// The TensorRT SLA requires this attribution when redistributing the runtime.
void WriteThirdPartyNotices()
{
    var notice = """
        Third-party components in this directory
        ========================================

        NVIDIA TensorRT runtime (nvinfer_11.dll, nvinfer_plugin_11.dll,
        nvonnxparser_11.dll, nvinfer_builder_resource_*.dll, trtexec.exe)
        and NVIDIA CUDA runtime (cudart64_*.dll), redistributed under the
        NVIDIA TensorRT Software License Agreement and CUDA Toolkit EULA:

            This software contains source code provided by NVIDIA Corporation.

        These files are obtained from the vs-mlrt project's release archives
        (https://github.com/AmusementClub/vs-mlrt), which redistributes them
        under the same terms.

        FFmpeg shared libraries (avcodec/avformat/avutil/avfilter/swscale/
        swresample/postproc DLLs) from gyan.dev's GPL build, licensed under
        the GNU GPL v3 (https://www.gyan.dev/ffmpeg/builds/); see ffmpeg_LICENSE.txt.

        aji.dll / aji_trt.dll / aji_dml.dll / aji_encode.exe / aji_harness*.exe /
        aji_kernel_test.exe:
        https://github.com/the-database/animejanai-inference
        """;
    File.WriteAllText(Path.Combine(inferencePath, "THIRD_PARTY_NOTICES.txt"), notice);
}

// version.txt + manifest.json at the install root. The updater reads these to know the installed
// version, decide overlay-vs-full updates (deps compare), and which paths to preserve. VideoJaNai
// ships no separate overlay asset, so the updater always does a full (slim-core) update; manifest
// stays local for user_preserve during that copy.
void WriteVersionAndManifest()
{
    File.WriteAllText(Path.Combine(installDirectory, "version.txt"), version);

    var manifest = new
    {
        package_version = version,
        player_executable = "VideoJaNai.exe",
        archive_tool = "7za.exe",
        // Heavy deps; bumping any of these is a real (full) update.
        deps = new
        {
            aji = AjiVersion,
            inference_runtime = VsMlrtCudaVersion,
            rife = RifeModelsVersion,
            ffmpeg = FfmpegVersion,
            sevenzip = SevenZipVersion,
        },
        // Runtime-created user data is never in the shipped package, so a full update's tree-copy
        // never touches it; these are listed defensively.
        user_preserve = new[]
        {
            "animejanai/animejanai.conf",
            "components.json",
        },
    };

    var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(installDirectory, "manifest.json"), json);
}

void ExtractZip(string archivePath, string outFolder, ProgressChanged progressChanged)
{
    using var fsInput = File.OpenRead(archivePath);
    using var zf = new ZipFile(fsInput);
    for (var i = 0; i < zf.Count; i++)
    {
        ZipEntry zipEntry = zf[i];
        if (!zipEntry.IsFile) continue;
        var fullZipToPath = Path.Combine(outFolder, zipEntry.Name);
        var directoryName = Path.GetDirectoryName(fullZipToPath);
        if (directoryName?.Length > 0) Directory.CreateDirectory(directoryName);
        var buffer = new byte[4096];
        using (var zipStream = zf.GetInputStream(zipEntry))
        using (Stream fsOutput = File.Create(fullZipToPath))
        {
            StreamUtils.Copy(zipStream, fsOutput, buffer);
        }
        progressChanged?.Invoke(Math.Round((double)i / zf.Count * 100, 0));
    }
}

async Task RunProcess(string fileName, string arguments)
{
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        }
    };
    process.Start();
    await process.StandardOutput.ReadToEndAsync();
    string error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new Exception($"{fileName} failed (exit {process.ExitCode}): {error}");
    }
}

void CopyDirectory(string srcDir, string targetDir)
{
    Directory.CreateDirectory(targetDir);
    foreach (string file in Directory.GetFiles(srcDir))
    {
        File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
    }
    foreach (string subDir in Directory.GetDirectories(srcDir))
    {
        CopyDirectory(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)));
    }
}

async Task Main()
{
    if (Directory.Exists(installDirectory)) Directory.Delete(installDirectory, true);
    Directory.CreateDirectory(installDirectory);

    // The published VideoJaNai app (self-contained win-x64) forms the install root.
    var appDir = ArgValue("--app-dir");
    if (!string.IsNullOrEmpty(appDir))
    {
        Console.WriteLine($"Copying VideoJaNai app from {appDir}...");
        CopyDirectory(Path.GetFullPath(appDir), installDirectory);
    }
    else
    {
        Console.WriteLine("WARNING: no --app-dir given; building inference tree only (no app binaries).");
    }

    await InstallSevenZip();
    await InstallInferenceRuntime();
    await InstallAji();
    await InstallFfmpeg();
    await InstallRife();
    InstallModels();

    // The custom updater ships at the install root next to VideoJaNai.exe.
    var updater = ArgValue("--updater");
    if (!string.IsNullOrEmpty(updater) && File.Exists(updater))
    {
        File.Copy(updater, Path.Combine(installDirectory, "VideoJaNaiUpdater.exe"), true);
    }
    else
    {
        Console.WriteLine("WARNING: no --updater given; VideoJaNaiUpdater.exe not bundled.");
    }

    WriteThirdPartyNotices();
    WriteVersionAndManifest();

    if (args.Contains("--packs"))
    {
        var packFiles = await EmitComponentPacks();
        SlimInstallTree(packFiles);
    }

    Console.WriteLine($"Built {installDirectory}");
}

// The released full-package is the slim core: TensorRT runtime, per-GPU builder resources, and RIFE
// models ship only as component packs, installed on demand. Keeps the base download small.
void SlimInstallTree(List<string> packFiles)
{
    Console.WriteLine("Slimming install tree (component packs ship separately)...");
    long removed = 0;
    foreach (var rel in packFiles)
    {
        var abs = Path.Combine(installDirectory, rel);
        if (!File.Exists(abs)) continue;
        removed += new FileInfo(abs).Length;
        File.Delete(abs);
    }
    foreach (var dir in Directory.GetDirectories(installDirectory, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(d => d.Length))
    {
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }
    Console.WriteLine($"Slimmed {removed / 1048576} MB out of the package tree.");
}

// Component packs: rooted 7z archives + a packs.json index, downloaded on demand by the updater /
// in-app manager. Archive paths are relative to the install root, so extraction over an install IS
// installation. Returns the expanded per-file list packed (so --packs can strip them from the core).
async Task<List<string>> EmitComponentPacks()
{
    Console.WriteLine("Emitting component packs...");
    var packsDir = Path.Combine(assemblyDirectory, $"packs-v{version}");
    Directory.CreateDirectory(packsDir);
    var sevenZa = Path.Combine(installDirectory, "7za.exe");
    if (!File.Exists(sevenZa)) sevenZa = Path.Combine(assemblyDirectory, "7za.exe");

    var packs = new List<(string Name, string[] Files)>
    {
        ("trt-runtime", Directory.GetFiles(inferencePath)
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                return (n.StartsWith("nvinfer_") && !n.Contains("builder_resource")) ||
                       n.StartsWith("nvonnxparser_") || n.StartsWith("cudart64_") ||
                       n == "trtexec.exe" ||
                       n.Contains("LICENSE", StringComparison.OrdinalIgnoreCase);
            })
            .Select(f => Path.GetRelativePath(installDirectory, f)).ToArray()),
        ("rife", new[] { Path.GetRelativePath(installDirectory, rifePath) }),
    };
    foreach (var f in Directory.GetFiles(inferencePath, "nvinfer_builder_resource_*"))
    {
        // nvinfer_builder_resource_sm120_11.dll -> trt-sm120
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"builder_resource_([a-z0-9]+)_");
        if (m.Success)
        {
            packs.Add(($"trt-{m.Groups[1].Value}", new[] { Path.GetRelativePath(installDirectory, f) }));
        }
    }

    var index = new List<object>();
    var packedFiles = new List<string>();
    foreach (var (name, files) in packs)
    {
        if (files.Length == 0 ||
            !files.Any(f => File.Exists(Path.Combine(installDirectory, f)) || Directory.Exists(Path.Combine(installDirectory, f))))
        {
            Console.WriteLine($"  component-{name}: nothing to pack in this tree, skipped");
            continue;
        }
        var archive = Path.Combine(packsDir, $"component-{name}.7z");
        if (File.Exists(archive)) File.Delete(archive);
        var fileArgs = string.Join(' ', files.Select(f => $"\"{f}\""));
        var psi = new ProcessStartInfo
        {
            FileName = sevenZa,
            Arguments = $"a -spf2 -mx=3 \"{archive}\" {fileArgs}",
            WorkingDirectory = installDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) throw new InvalidOperationException($"7za failed for pack {name}");

        var allFiles = files.SelectMany(f =>
        {
            var abs = Path.Combine(installDirectory, f);
            return Directory.Exists(abs)
                ? Directory.GetFiles(abs, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(installDirectory, x))
                : new[] { f };
        }).Select(f => f.Replace('\\', '/')).ToArray();
        index.Add(new { name, asset = Path.GetFileName(archive), bytes = new FileInfo(archive).Length, files = allFiles });
        packedFiles.AddRange(allFiles);
        Console.WriteLine($"  component-{name}.7z ({new FileInfo(archive).Length / 1048576} MB, {allFiles.Length} files)");
    }
    File.WriteAllText(Path.Combine(packsDir, "packs.json"),
        JsonSerializer.Serialize(new { package_version = version, packs = index },
            new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Packs written to {packsDir}");
    return packedFiles;
}

if (packsOnlyIndex >= 0)
{
    await EmitComponentPacks();
}
else
{
    await Main();
}
