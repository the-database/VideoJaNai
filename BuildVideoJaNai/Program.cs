// Package builder for VideoJaNai (Inno-installer distribution model on Windows; tarball/AppImage on
// Linux).
//
// Assembles the redistributable install tree (the VideoJaNai app + VideoJaNaiUpdater + the libaji
// inference dir + ONNX models) and, under --packs, carves the heavy hardware-specific pieces
// (TensorRT runtime, per-GPU-generation builder resources, RIFE models) into component-*.7z +
// packs.json release assets, then slims them out of the core. End users only ever download the
// small core + their GPU's component packs.
//
//   BuildVideoJaNai <version> [--target win-x64|linux-x64] [--app-dir <dir>] [--updater <exe>] [--packs]
//   BuildVideoJaNai <version> --packs-only [dir]   (emit packs from an existing tree)
//
// Target-parameterized like mpv-AnimeJaNai's assembler: every platform-varying name/source lives in
// the Platform descriptor at the bottom. --target defaults to the host RID.
//
// Dev overrides (skip large downloads on local builds):
//   TRT_LOCAL_ZIP          -> a downloaded TensorRT "-external" zip (Windows)
//   TRT_LINUX_ROOT         -> an existing TensorRT install root holding lib/x86_64-linux-gnu + bin (Linux)
//   CUDA_LINUX_LIB         -> a directory holding libcudart.so* (Linux)
//   VIDEOJANAI_FFMPEG_DIR  -> a local extracted gyan ffmpeg shared dir (with bin/) (Windows)
//   AJI_LOCAL_ZIP          -> a locally built aji-windows-x64.zip (Windows)
//   AJI_LOCAL_DIR          -> a directory of aji files copied flat into animejanai/inference (both;
//                             on Linux: libaji.so, libaji_trt.so, aji_encode + its libav libs)
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using SevenZipExtractor;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Downloader;

// Third-party component versions. Bump these together when cutting a release.
//
// The TensorRT runtime + trtexec come straight from NVIDIA's public download (the "-external" zip on
// Windows, the CUDA apt repo's .debs on Linux), the same pins as mpv-AnimeJaNai. TrtCudaVersion is
// its own pin, NOT derivable from TrtVersion (11.3 -> 13.4, 11.4 -> 13.4, ...): guessing it 404s.
const string TrtVersion        = "11.4.0.106";
const string TrtCudaVersion    = "13.4";
// NVIDIA publishes no checksum beside the zip, so this is recorded from a verified download.
const string TrtSha256Win      = "b6fd86301ad803492adf7a06de29ce45e3968d29637a1437d0fb0994f412c980";

// CUDA runtime, from NVIDIA's CUDA redistributable (per-file checksums in
// developer.download.nvidia.com/compute/cuda/redist/redistrib_13.4.1.json).
const string CudartVersion     = "13.4.49";
const string CudartSha256Win   = "e6663f3d3e8949eedc2d5ab92c7c5b9fa3f2a222086d91c42bfc5a38bf2b0225";
const string CudartSha256Linux = "ec3df9f91560db23a064c6b395d4fde17b2893f5bf954c6bd193dd03a44de522";

// Linux TensorRT .debs from the CUDA repo pool; the repo's Packages index publishes these hashes.
const string CudaAptRepo       = "https://developer.download.nvidia.com/compute/cuda/repos/ubuntu2204/x86_64";
var trtDebSha256 = new Dictionary<string, string>
{
    ["libnvinfer11"]        = "bd24db760728779e980c8852ba87e148a0bf9fb4e69d69bc14f3bdabc0423797",
    ["libnvinfer-plugin11"] = "a99bd3de4fd45585d4a1fce8f6a8d6415e1a7946c86d907f64af0b9b487aee66",
    ["libnvonnxparsers11"]  = "d7b0e623e6b8d1c368a000b996938494137e2584b85e2eed8b89f7b34092436c",
    ["libnvinfer-bin"]      = "36b0a4f11c3a15bf9f2d4eb5a33ce02b7248582eb384696a1d80a154abf005f8",
};

// the-database/animejanai-inference. v0.12.0: built against TensorRT 11.4; aji_encode drives temporal
// (multi-frame) models, honors the RIFE-before-upscale order, uploads CPU-decoded / 4:4:4 sources,
// and runs on DirectML (shared D3D11 frames; CUDA delay-loaded, so it starts without the NVIDIA driver).
// On Linux aji_encode ships as its own asset carrying the FFmpeg 9.0 libs it links. v0.13.0: keeps
// source timestamps (VFR), handles legacy/odd-format inputs, builds engines atomically (a killed build
// no longer leaves an empty engine behind), and opts out of Windows power throttling.
const string AjiVersion        = "v0.13.0";
const string SevenZipVersion   = "2602";                  // 7-zip "extra" (Windows) / linux-x64 standalone console
const string RifeModelsVersion = "models-rife-fp16-1";    // animejanai-inference rife fp16 release
// gyan.dev ffmpeg shared build (Windows; the DLLs aji_encode links). Must match the ffmpeg major the
// aji release's aji_encode.exe was built against (aji v0.12.0: 9.0.2 -> avcodec-63.dll).
const string FfmpegVersion     = "9.0.2";
const string FfmpegSha256Win   = "4d2060a8b34a940aa47d785142055bb92a63053781e55f2ace4546edd519a8f5";
// DirectML backend runtime (Windows; aji_dml.dll loads both from its own directory). The same pins
// as mpv-AnimeJaNai: the last DirectML-flavored ORT releases (DML is in sustained engineering).
const string OrtDmlVersion     = "1.24.4";                // Microsoft.ML.OnnxRuntime.DirectML (NuGet)
const string DirectMLVersion   = "1.15.4";                // Microsoft.AI.DirectML (NuGet)

TargetOs SelectTarget(string[] a)
{
    int ti = Array.IndexOf(a, "--target");
    string rid = (ti >= 0 && ti + 1 < a.Length) ? a[ti + 1]
               : (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64");
    return rid switch
    {
        "win-x64" => TargetOs.Windows,
        "linux-x64" => TargetOs.Linux,
        _ => throw new ArgumentException($"Unsupported --target '{rid}' (use win-x64 or linux-x64)"),
    };
}

var plat = SelectTarget(args) == TargetOs.Windows ? Platform.Win : Platform.Linux;
Console.WriteLine($"Target: {plat.Rid}");

if (args.Length < 1 || args[0].StartsWith("--"))
{
    throw new ArgumentException("Version is required.  Usage: BuildVideoJaNai <version> [--target win-x64|linux-x64] [--app-dir <dir>] [--updater <exe>] [--packs]");
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

// Standalone 7-Zip console: builds the component packs, and ships at the install root for the
// updater (manifest archive_tool). 7za.exe on Windows, 7zz on Linux.
async Task InstallSevenZip()
{
    Console.WriteLine("Downloading 7-Zip standalone console...");
    if (plat.IsWindows)
    {
        var downloadUrl = $"https://www.7-zip.org/a/7z{SevenZipVersion}-extra.7z";
        var targetPath = Path.GetFullPath("7z-extra.7z");
        await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading 7-Zip ({p}%)..."));

        var targetExtractPath = Path.GetFullPath("7z-extra-temp");
        Directory.CreateDirectory(targetExtractPath);
        using (ArchiveFile archiveFile = new(targetPath))
        {
            archiveFile.Extract(targetExtractPath);
        }
        File.Copy(Path.Combine(targetExtractPath, "x64", "7za.exe"), Path.Combine(installDirectory, plat.ArchiveTool), true);
        Directory.Delete(targetExtractPath, true);
        File.Delete(targetPath);
    }
    else
    {
        // The linux-x64 console ships as tar.xz; system tar avoids an archive chicken-and-egg.
        var downloadUrl = $"https://www.7-zip.org/a/7z{SevenZipVersion}-linux-x64.tar.xz";
        var targetPath = Path.GetFullPath("7z-linux.tar.xz");
        await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading 7-Zip ({p}%)..."));
        var tmp = Path.GetFullPath("7z-linux-temp");
        Directory.CreateDirectory(tmp);
        await RunProcess("tar", $"-xf \"{targetPath}\" -C \"{tmp}\"");
        var dst = Path.Combine(installDirectory, plat.ArchiveTool);
        File.Copy(Path.Combine(tmp, "7zz"), dst, true);
        await RunProcess("chmod", $"+x \"{dst}\"");
        Directory.Delete(tmp, true);
        File.Delete(targetPath);
    }
}

async Task InstallInferenceRuntime()
{
    Directory.CreateDirectory(inferencePath);
    if (plat.IsWindows)
    {
        // NVIDIA's "-external" zip. Only bin/ ships; the headers, import libs, cmake/ and python/ are
        // build-time material for animejanai-inference.
        var trtDir = string.Join('.', TrtVersion.Split('.').Take(3));
        var asset = $"TensorRT-Enterprise-{TrtVersion}-Windows-amd64-cuda-{TrtCudaVersion}-Release-external.zip";
        var downloadUrl = $"https://developer.nvidia.com/downloads/compute/machine-learning/tensorrt/{trtDir}/zip/{asset}";

        var localZip = Environment.GetEnvironmentVariable("TRT_LOCAL_ZIP");
        string targetPath;
        bool temp = false;
        if (!string.IsNullOrEmpty(localZip))
        {
            Console.WriteLine($"Using local TensorRT archive: {localZip}");
            targetPath = localZip;
        }
        else
        {
            Console.WriteLine($"Downloading TensorRT {TrtVersion} (CUDA {TrtCudaVersion}) from NVIDIA...");
            targetPath = Path.GetFullPath(asset);
            temp = true;
            double lastProgress = -1;
            await DownloadFileAsync(downloadUrl, targetPath, p =>
            {
                if (p >= lastProgress + 5) { Console.WriteLine($"Downloading TensorRT ({p}%)..."); lastProgress = p; }
            });
        }
        VerifySha256(targetPath, TrtSha256Win);

        Console.WriteLine("Extracting TensorRT runtime...");
        ExtractZipFiltered(targetPath, entry =>
        {
            var name = Path.GetFileName(entry);
            if (entry.Contains("/bin/", StringComparison.Ordinal) && KeepInferenceFile(name))
                return Path.Combine(inferencePath, name);
            // NVIDIA's own attribution file, kept beside the binaries it covers.
            if (name.Equals("Acknowledgements.txt", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(inferencePath, "TensorRT_Acknowledgements.txt");
            return null;
        });
        if (temp) File.Delete(targetPath);

        await InstallCudart();
    }
    else
    {
        // Linux: NVIDIA's apt packages, unpacked into the standard /usr layout, so the harvesting
        // below is identical for a download and for an existing install (TRT_LINUX_ROOT).
        var trtRoot = Environment.GetEnvironmentVariable("TRT_LINUX_ROOT");
        var cudaLib = Environment.GetEnvironmentVariable("CUDA_LINUX_LIB");
        var downloadRoot = Path.GetFullPath("trt-linux-root");

        if (string.IsNullOrEmpty(trtRoot))
        {
            await InstallTrtDebs(downloadRoot);
            trtRoot = Path.Combine(downloadRoot, "usr");
        }
        if (string.IsNullOrEmpty(cudaLib))
        {
            cudaLib = await InstallCudartLinux(downloadRoot);
        }

        var trtLib = Path.Combine(trtRoot, "lib", "x86_64-linux-gnu");
        var trtBin = Path.Combine(trtRoot, "bin");
        Console.WriteLine($"Copying TensorRT runtime from {trtLib} + cudart from {cudaLib}...");
        var libGlobs = new[]
        {
            "libnvinfer.so*", "libnvinfer_plugin.so*", "libnvonnxparser.so*",
            "libnvinfer_builder_resource_*.so*",
        };
        foreach (var g in libGlobs)
            foreach (var f in Directory.GetFiles(trtLib, g))
                CopyOrLink(f, Path.Combine(inferencePath, Path.GetFileName(f)));
        foreach (var f in Directory.GetFiles(cudaLib, "libcudart.so*"))
            CopyOrLink(f, Path.Combine(inferencePath, Path.GetFileName(f)));

        var trtexecDst = Path.Combine(inferencePath, "trtexec");
        CopyOrLink(Path.Combine(trtBin, "trtexec"), trtexecDst);
        await RunProcess("chmod", $"+x \"{trtexecDst}\"");

        if (Directory.Exists(downloadRoot)) Directory.Delete(downloadRoot, true);
    }
}

// The Linux counterpart of the TensorRT zip: NVIDIA's .debs straight from the CUDA repo pool.
async Task InstallTrtDebs(string root)
{
    Directory.CreateDirectory(root);
    // libnvinfer11 also carries the per-SM builder resources; libnvinfer-bin is where trtexec lives.
    foreach (var package in new[] { "libnvinfer11", "libnvinfer-plugin11", "libnvonnxparsers11", "libnvinfer-bin" })
    {
        var deb = $"{package}_{TrtVersion}-1+cuda{TrtCudaVersion}_amd64.deb";
        var targetPath = Path.GetFullPath(deb);
        Console.WriteLine($"Downloading {package} ({TrtVersion})...");
        double lastProgress = -1;
        await DownloadFileAsync($"{CudaAptRepo}/{deb}", targetPath, p =>
        {
            if (p >= lastProgress + 25) { Console.WriteLine($"  {package} ({p}%)..."); lastProgress = p; }
        });
        VerifySha256(targetPath, trtDebSha256[package]);
        await RunProcess("dpkg-deb", $"-x \"{targetPath}\" \"{root}\"");
        File.Delete(targetPath);
    }

    var copyright = Path.Combine(root, "usr", "share", "doc", "libnvinfer11", "copyright");
    if (File.Exists(copyright))
    {
        File.Copy(copyright, Path.Combine(inferencePath, "TensorRT_Acknowledgements.txt"), true);
    }
}

// cudart for Linux; returns the directory holding libcudart.so*.
async Task<string> InstallCudartLinux(string root)
{
    var stem = $"cuda_cudart-linux-x86_64-{CudartVersion}-archive";
    var targetPath = Path.GetFullPath($"{stem}.tar.xz");
    Console.WriteLine($"Downloading CUDA runtime {CudartVersion}...");
    await DownloadFileAsync(
        $"https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/linux-x86_64/{stem}.tar.xz",
        targetPath, _ => { });
    VerifySha256(targetPath, CudartSha256Linux);

    Directory.CreateDirectory(root);
    await RunProcess("tar", $"-xf \"{targetPath}\" -C \"{root}\"");
    File.Delete(targetPath);

    var license = Path.Combine(root, stem, "LICENSE");
    if (File.Exists(license))
    {
        File.Copy(license, Path.Combine(inferencePath, "CUDA_LICENSE.txt"), true);
    }
    return Path.Combine(root, stem, "lib");
}

// cudart is not part of the TensorRT archive; it comes from NVIDIA's CUDA redistributable.
async Task InstallCudart()
{
    var stem = $"cuda_cudart-windows-x86_64-{CudartVersion}-archive";
    var downloadUrl = $"https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/{stem}.zip";
    var targetPath = Path.GetFullPath($"{stem}.zip");
    Console.WriteLine($"Downloading CUDA runtime {CudartVersion}...");
    await DownloadFileAsync(downloadUrl, targetPath, _ => { });
    VerifySha256(targetPath, CudartSha256Win);

    ExtractZipFiltered(targetPath, entry =>
    {
        var name = Path.GetFileName(entry);
        if (name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(inferencePath, "CUDA_LICENSE.txt");
        return KeepInferenceFile(name) ? Path.Combine(inferencePath, name) : null;
    });
    File.Delete(targetPath);
}

// Which inference-runtime files from NVIDIA's archives actually ship.
bool KeepInferenceFile(string name) =>
    plat.InferenceRuntimeFiles.Contains(name) ||
    plat.InferenceRuntimePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
    name.Contains("LICENSE", StringComparison.OrdinalIgnoreCase);

void VerifySha256(string path, string expected)
{
    using var stream = File.OpenRead(path);
    var actual = Convert.ToHexString(SHA256.HashData(stream));
    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"SHA-256 mismatch for {Path.GetFileName(path)}{Environment.NewLine}" +
            $"  expected {expected}{Environment.NewLine}" +
            $"  actual   {actual.ToLowerInvariant()}");
    }
    Console.WriteLine($"  sha256 verified: {Path.GetFileName(path)}");
}

async Task InstallAji()
{
    Directory.CreateDirectory(inferencePath);

    var localDir = Environment.GetEnvironmentVariable("AJI_LOCAL_DIR");
    if (!string.IsNullOrEmpty(localDir))
    {
        Console.WriteLine($"Using local aji files: {localDir}");
        foreach (var f in Directory.GetFiles(localDir))
        {
            CopyOrLink(f, Path.Combine(inferencePath, Path.GetFileName(f)));
        }
        if (!plat.IsWindows)
        {
            await RunProcess("chmod", $"+x \"{Path.Combine(inferencePath, plat.Exe("aji_encode"))}\"");
        }
        return;
    }

    var localZip = Environment.GetEnvironmentVariable("AJI_LOCAL_ZIP");
    foreach (var asset in plat.AjiAssets)
    {
        string targetPath;
        bool temp = false;
        if (plat.IsWindows && !string.IsNullOrEmpty(localZip))
        {
            Console.WriteLine($"Using local aji build: {localZip}");
            targetPath = localZip;
        }
        else
        {
            Console.WriteLine($"Downloading {asset}...");
            var downloadUrl = $"https://github.com/the-database/animejanai-inference/releases/download/{AjiVersion}/{asset}";
            targetPath = Path.GetFullPath(asset);
            temp = true;
            await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading {asset} ({p}%)..."));
        }

        if (plat.IsWindows)
            ExtractZip(targetPath, inferencePath, _ => { });
        else
            await RunProcess("tar", $"--zstd -xf \"{targetPath}\" -C \"{inferencePath}\"");

        if (temp) File.Delete(targetPath);
    }
}

// ffmpeg shared (dev) build (Windows): aji_encode.exe dynamically links libavformat/avcodec/avutil/
// avfilter/swscale (+ swresample), so the matching shared DLLs must sit next to it in the inference
// dir. gyan's GPL shared build includes libx264/libx265 (needed for software encodes). On Linux the
// libav libs come inside aji-encode-linux-x64.tar.zst instead.
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
        var downloadUrl = $"https://github.com/GyanD/codexffmpeg/releases/download/{FfmpegVersion}/ffmpeg-{FfmpegVersion}-full_build-shared.7z";
        var targetPath = Path.GetFullPath("ffmpeg-shared.7z");
        await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading ffmpeg ({p}%)..."));
        VerifySha256(targetPath, FfmpegSha256Win);
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
    // gyan's archives have shipped it as both LICENSE.txt (8.x) and LICENSE (9.x)
    var ffmpegLicense = new[] { "LICENSE.txt", "LICENSE" }
        .Select(n => Path.Combine(ffmpegDir, n)).FirstOrDefault(File.Exists);
    if (ffmpegLicense != null)
    {
        File.Copy(ffmpegLicense, Path.Combine(inferencePath, "ffmpeg_LICENSE.txt"), true);
    }

    if (tempDirectory != null) Directory.Delete(tempDirectory, true);
}

// ONNX Runtime + DirectML for the DirectML backend (Windows only). They ship in the core rather than a
// component pack: DirectML is what non-NVIDIA GPUs run on, and those get no TensorRT packs. The
// .nupkg files are plain zips; only the x64 runtime DLLs (and the DirectML license, which the
// redistribution terms require keeping intact) go into the package.
async Task InstallOrtDml()
{
    Directory.CreateDirectory(inferencePath);
    var packages = new (string Name, string Version, string[] CopyFromTo)[]
    {
        ("Microsoft.ML.OnnxRuntime.DirectML", OrtDmlVersion, new[]
        {
            "runtimes/win-x64/native/onnxruntime.dll", "onnxruntime.dll",
        }),
        ("Microsoft.AI.DirectML", DirectMLVersion, new[]
        {
            "bin/x64-win/DirectML.dll", "DirectML.dll",
            "LICENSE.txt", "DirectML_LICENSE.txt",
        }),
    };
    foreach (var (name, pkgVersion, copies) in packages)
    {
        Console.WriteLine($"Downloading {name} {pkgVersion}...");
        var targetPath = Path.GetFullPath($"{name}.{pkgVersion}.nupkg");
        await DownloadFileAsync($"https://www.nuget.org/api/v2/package/{name}/{pkgVersion}", targetPath, _ => { });
        var tempDirectory = Path.GetFullPath($"{name}-temp");
        ExtractZip(targetPath, tempDirectory, _ => { });
        for (var i = 0; i < copies.Length; i += 2)
        {
            File.Copy(Path.Combine(tempDirectory, copies[i].Replace('/', Path.DirectorySeparatorChar)),
                      Path.Combine(inferencePath, copies[i + 1]), true);
        }
        Directory.Delete(tempDirectory, true);
        File.Delete(targetPath);
    }
}

async Task InstallRife()
{
    Console.WriteLine("Downloading RIFE fp16 models...");
    var downloadUrl = $"https://github.com/the-database/animejanai-inference/releases/download/{RifeModelsVersion}/rife-fp16-1.7z";
    var targetPath = Path.GetFullPath("rife-fp16.7z");
    await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading RIFE fp16 models ({p}%)..."));

    Directory.CreateDirectory(rifePath);
    // SevenZipExtractor wraps the Windows 7z.dll; on Linux use the bundled 7zz console.
    if (plat.IsWindows)
    {
        using ArchiveFile archiveFile = new(targetPath);
        archiveFile.Extract(rifePath);
    }
    else
    {
        await RunProcess(Path.Combine(installDirectory, plat.ArchiveTool), $"x \"{targetPath}\" -o\"{rifePath}\" -y");
    }
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
    var trtFiles = plat.IsWindows
        ? "nvinfer_11.dll, nvinfer_plugin_11.dll, nvonnxparser_11.dll,\n" +
          "nvinfer_builder_resource_*.dll, trtexec.exe) and NVIDIA CUDA runtime (cudart64_*.dll)"
        : "libnvinfer.so.11, libnvinfer_plugin.so.11, libnvonnxparser.so.11,\n" +
          "libnvinfer_builder_resource_*.so, trtexec) and NVIDIA CUDA runtime (libcudart.so.13)";
    var trtSource = plat.IsWindows
        ? "These files are obtained from NVIDIA's redistributable TensorRT and CUDA\n" +
          "downloads, redistributed under the same terms."
        : "These files are obtained from NVIDIA's TensorRT and CUDA Linux packages,\n" +
          "redistributed under the same terms.";
    var ffmpeg = plat.IsWindows
        ? "FFmpeg shared libraries (avcodec/avformat/avutil/avfilter/swscale/\n" +
          "swresample DLLs) from gyan.dev's GPL build, licensed under the GNU GPL v3\n" +
          "(https://www.gyan.dev/ffmpeg/builds/); see ffmpeg_LICENSE.txt."
        : "FFmpeg shared libraries (libavcodec/libavformat/libavutil/libavfilter/\n" +
          "libswscale/libswresample) from BtbN's GPL build, licensed under the GNU\n" +
          "GPL v3 (https://github.com/BtbN/FFmpeg-Builds); see ffmpeg_LICENSE.txt.";
    var dml = plat.IsWindows
        ? "ONNX Runtime (onnxruntime.dll), (c) Microsoft Corporation, redistributed\n" +
          "under the MIT license (https://github.com/microsoft/onnxruntime/blob/main/LICENSE).\n\n" +
          "DirectML (DirectML.dll), (c) Microsoft Corporation, redistributed as the\n" +
          "DirectML Redistributable Package under the Microsoft Software License Terms\n" +
          "shipped alongside it as DirectML_LICENSE.txt (use on Windows and Xbox only).\n\n"
        : "";
    var ajiNames = plat.IsWindows
        ? "aji.dll / aji_trt.dll / aji_dml.dll / aji_encode.exe / aji_harness*.exe /\naji_kernel_test.exe"
        : "libaji.so / libaji_trt.so / aji_encode / aji_harness / aji_kernel_test";
    var notice =
        "Third-party components in this directory\n" +
        "========================================\n\n" +
        $"NVIDIA TensorRT runtime ({trtFiles},\n" +
        "redistributed under the NVIDIA TensorRT Software License Agreement and\n" +
        "CUDA Toolkit EULA:\n\n" +
        "    This software contains source code provided by NVIDIA Corporation.\n\n" +
        $"{trtSource} NVIDIA's own acknowledgements accompany them as\n" +
        "TensorRT_Acknowledgements.txt.\n\n" +
        $"{ffmpeg}\n\n" +
        dml +
        $"{ajiNames}:\n" +
        "https://github.com/the-database/animejanai-inference\n";
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
        // Platform-specific names the updater needs, so its code carries no Windows assumptions.
        player_executable = plat.Exe("VideoJaNai"),
        archive_tool = plat.ArchiveTool,
        platform = plat.Rid,
        // Heavy deps; bumping any of these is a real (full) update.
        deps = new
        {
            aji = AjiVersion,
            inference_runtime = $"trt-{TrtVersion}",
            rife = RifeModelsVersion,
            // Linux: the libav libs ride the aji asset, so the aji dep already versions them.
            ffmpeg = plat.IsWindows ? FfmpegVersion : (string?)null,
            ort_dml = plat.IsWindows ? $"{OrtDmlVersion}+{DirectMLVersion}" : (string?)null,
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

// Copy a file, recreating a symlink as a relative symlink so a versioned-library chain
// (libnvinfer.so -> .so.11 -> .so.11.4.0) stores its payload once.
void CopyOrLink(string src, string dst)
{
    if (File.Exists(dst)) File.Delete(dst);
    var info = new FileInfo(src);
    if (info.LinkTarget != null)
        File.CreateSymbolicLink(dst, Path.GetFileName(info.LinkTarget));
    else
        File.Copy(src, dst, true);
}

// Selective zip extraction: mapEntry returns the destination for an entry, or null to skip it. Used
// for the TensorRT and CUDA archives, where only a handful of files out of ~2.5 GB are wanted.
void ExtractZipFiltered(string archivePath, Func<string, string?> mapEntry)
{
    using var fsInput = File.OpenRead(archivePath);
    using var zf = new ZipFile(fsInput);
    var buffer = new byte[81920];
    foreach (ZipEntry zipEntry in zf)
    {
        if (!zipEntry.IsFile) continue;
        var destination = mapEntry(zipEntry.Name);
        if (destination == null) continue;
        var directoryName = Path.GetDirectoryName(destination);
        if (directoryName?.Length > 0) Directory.CreateDirectory(directoryName);
        Console.WriteLine($"  {Path.GetFileName(destination)}");
        using var zipStream = zf.GetInputStream(zipEntry);
        using Stream fsOutput = File.Create(destination);
        StreamUtils.Copy(zipStream, fsOutput, buffer);
    }
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

    // The published VideoJaNai app (self-contained for the target RID) forms the install root.
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
    if (plat.IsWindows)
    {
        await InstallFfmpeg();
        await InstallOrtDml();
    }
    await InstallRife();
    InstallModels();

    // The custom updater ships at the install root next to the app.
    var updater = ArgValue("--updater");
    if (!string.IsNullOrEmpty(updater) && File.Exists(updater))
    {
        var dst = Path.Combine(installDirectory, plat.Exe("VideoJaNaiUpdater"));
        File.Copy(updater, dst, true);
        if (!plat.IsWindows) await RunProcess("chmod", $"+x \"{dst}\"");
    }
    else
    {
        Console.WriteLine($"WARNING: no --updater given; {plat.Exe("VideoJaNaiUpdater")} not bundled.");
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
    var sevenZ = Path.Combine(installDirectory, plat.ArchiveTool);
    if (!File.Exists(sevenZ)) sevenZ = Path.Combine(assemblyDirectory, plat.ArchiveTool);

    // Dep = the manifest.json deps key that governs a pack's content, so the updater can skip an
    // already-present pack whose dep is unchanged across releases.
    var packs = new List<(string Name, string Dep, string[] Files)>
    {
        ("trt-runtime", "inference_runtime", Directory.GetFiles(inferencePath)
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                bool builderResource = n.Contains("builder_resource");
                bool runtime = n.StartsWith("nvinfer") || n.StartsWith("libnvinfer") ||
                               n.StartsWith("nvonnxparser") || n.StartsWith("libnvonnxparser") ||
                               n.StartsWith("cudart64_") || n.StartsWith("libcudart") ||
                               n == "trtexec.exe" || n == "trtexec";
                // ffmpeg_ and DirectML_LICENSE.txt stay in the core with the libs they cover
                bool license = n.Contains("LICENSE", StringComparison.OrdinalIgnoreCase) &&
                               !n.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase) &&
                               !n.StartsWith("DirectML", StringComparison.OrdinalIgnoreCase);
                return (runtime && !builderResource) || license;
            })
            .Select(f => Path.GetRelativePath(installDirectory, f)).ToArray()),
        ("rife", "rife", new[] { Path.GetRelativePath(installDirectory, rifePath) }),
    };
    foreach (var f in Directory.GetFiles(inferencePath, "*builder_resource_*"))
    {
        // Windows: nvinfer_builder_resource_sm120_11.dll -> trt-sm120
        // Linux:   libnvinfer_builder_resource_sm120.so.11.4.0 -> trt-sm120
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"builder_resource_([a-z0-9]+)[._]");
        if (m.Success)
        {
            var name = $"trt-{m.Groups[1].Value}";
            var rel = Path.GetRelativePath(installDirectory, f);
            var existing = packs.FindIndex(p => p.Name == name);
            // Linux has a symlink chain per resource (.so.11 -> .so.11.4.0): one pack holds them all
            if (existing >= 0)
                packs[existing] = (name, "inference_runtime", packs[existing].Files.Append(rel).ToArray());
            else
                packs.Add((name, "inference_runtime", new[] { rel }));
        }
    }

    var index = new List<object>();
    var packedFiles = new List<string>();
    foreach (var (name, dep, files) in packs)
    {
        if (files.Length == 0 ||
            !files.Any(f => File.Exists(Path.Combine(installDirectory, f)) || Directory.Exists(Path.Combine(installDirectory, f))))
        {
            Console.WriteLine($"  component-{name}: nothing to pack in this tree, skipped");
            continue;
        }
        var archive = Path.Combine(packsDir, $"component-{name}{plat.PackSuffix}.7z");
        if (File.Exists(archive)) File.Delete(archive);
        var fileArgs = string.Join(' ', files.Select(f => $"\"{f}\""));
        var psi = new ProcessStartInfo
        {
            FileName = sevenZ,
            // -spf2: store the relative paths as given; -snl: store symlinks as links, so the
            // versioned Linux TRT libs stay deduplicated
            Arguments = $"a -spf2 -snl -mx=3 \"{archive}\" {fileArgs}",
            WorkingDirectory = installDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) throw new InvalidOperationException($"{plat.ArchiveTool} failed for pack {name}");

        var allFiles = files.SelectMany(f =>
        {
            var abs = Path.Combine(installDirectory, f);
            return Directory.Exists(abs)
                ? Directory.GetFiles(abs, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(installDirectory, x))
                : new[] { f };
        }).Select(f => f.Replace('\\', '/')).ToArray();
        // installed_bytes: what this pack occupies once extracted. The updater compares it against the
        // files on disk to decide whether an install is genuinely current, instead of trusting
        // components.json alone (a record can claim a component is current while the previous
        // release's files are still there). Measured before SlimInstallTree removes them, the same
        // way the updater measures (PackContentMatches).
        long installedBytes = allFiles.Sum(f =>
        {
            var fi = new FileInfo(Path.Combine(installDirectory, f));
            return fi.Exists ? fi.Length : 0L;
        });
        index.Add(new
        {
            name,
            dep,
            asset = Path.GetFileName(archive),
            bytes = new FileInfo(archive).Length,
            installed_bytes = installedBytes,
            files = allFiles,
        });
        packedFiles.AddRange(allFiles);
        Console.WriteLine($"  component-{name}{plat.PackSuffix}.7z ({new FileInfo(archive).Length / 1048576} MB, {allFiles.Length} files)");
    }
    File.WriteAllText(Path.Combine(packsDir, $"packs{plat.PackSuffix}.json"),
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

// ---------------------------------------------------------------------------
// Platform descriptor: every platform-varying name/source in one place.
// ---------------------------------------------------------------------------
enum TargetOs { Windows, Linux }

record Platform(
    TargetOs Os,
    string Rid,
    string ArchiveTool,
    string[] AjiAssets,
    string[] InferenceRuntimeFiles,
    string[] InferenceRuntimePrefixes)
{
    public bool IsWindows => Os == TargetOs.Windows;
    // "VideoJaNaiUpdater" -> "VideoJaNaiUpdater.exe" / "VideoJaNaiUpdater"
    public string Exe(string stem) => IsWindows ? stem + ".exe" : stem;
    // Release-asset suffix so Windows + Linux assets coexist on one release tag (Windows keeps the
    // legacy unsuffixed names).
    public string PackSuffix => IsWindows ? "" : "-" + Rid;

    public static readonly Platform Win = new(
        Os: TargetOs.Windows,
        Rid: "win-x64",
        ArchiveTool: "7za.exe",
        AjiAssets: new[] { "aji-windows-x64.zip" },
        InferenceRuntimeFiles: new[] { "nvinfer_11.dll", "nvinfer_plugin_11.dll", "nvonnxparser_11.dll", "trtexec.exe" },
        InferenceRuntimePrefixes: new[] { "cudart64_", "nvinfer_builder_resource_" });

    public static readonly Platform Linux = new(
        Os: TargetOs.Linux,
        Rid: "linux-x64",
        ArchiveTool: "7zz",
        // aji_encode + its libav libs are a separate asset from the player-side libaji tarball
        AjiAssets: new[] { "aji-linux-x64.tar.zst", "aji-encode-linux-x64.tar.zst" },
        // Linux harvests TensorRT by glob from the unpacked .debs; these lists are unused there.
        InferenceRuntimeFiles: Array.Empty<string>(),
        InferenceRuntimePrefixes: Array.Empty<string>());
}
