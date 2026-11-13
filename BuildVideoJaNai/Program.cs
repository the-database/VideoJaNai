// Package builder for VideoJaNai (Inno-installer distribution model on Windows; tarball/AppImage on
// Linux).
//
// Assembles the redistributable install tree (the VideoJaNai app + VideoJaNaiUpdater + the libaji
// inference dir + ONNX models) and, under --packs, carves the heavy hardware-specific pieces
// (TensorRT runtime, per-GPU-generation builder resources, RIFE models, the TensorRT-RTX
// backend) into component-*.7z + packs.json release assets, then slims them out of the core. End
// users only ever download the small core + their GPU's component packs.
//
//   BuildVideoJaNai <version> [--target win-x64|linux-x64] [--app-dir <dir>] [--updater <exe>] [--packs [--offline]]
//                   [--cli <videojanai exe>]
//   BuildVideoJaNai <version> --packs-only [dir]   (emit packs from an existing tree)
//
// --offline (with --packs) also writes VideoJaNai-offline-package-<ver>[-<rid>].7z.*: the complete
// tree, every component pack included, for installs without internet access.
//
// --cli (after --packs' slimming) also assembles videojanai-cli-v<ver>/: the command line's standalone
// tree, the slim core without the GUI (videojanai + the updater + animejanai/ + manifest.json), which
// the release workflow archives as videojanai-windows-x64.zip / videojanai-linux-x64.tar.zst.
// `videojanai setup` adds this PC's component packs to it with the updater, from this release.
//
// Target-parameterized like mpv-AnimeJaNai's assembler: every platform-varying name/source lives in
// the Platform descriptor at the bottom. --target defaults to the host RID.
//
// Dev overrides (skip large downloads on local builds):
//   TRT_LOCAL_ZIP          -> a downloaded TensorRT "-external" zip (Windows)
//   TRT_LINUX_ROOT         -> an existing TensorRT install root holding lib/x86_64-linux-gnu + bin (Linux)
//   CUDA_LINUX_LIB         -> a directory holding libcudart.so* (Linux)
//   VIDEOJANAI_FFMPEG_DIR  -> a local extracted gyan ffmpeg shared dir (with bin/) (Windows)
//   AJI_LOCAL_ZIP          -> a locally built aji-windows-x64.zip (Windows; the main aji asset only)
//   AJI_LOCAL_TRTRTX_ZIP   -> a locally built aji-trtrtx-windows-x64.zip (Windows; the TensorRT-RTX
//                             asset). Local zips skip the SHA-256 check the downloads get.
//   AJI_LOCAL_DIR          -> a directory of aji files copied flat into animejanai/inference (both;
//                             on Linux: libaji.so, libaji_trt.so, aji_encode + its libav libs;
//                             on Windows, include the TensorRT-RTX files to get the trt-rtx pack)
//   AJI_RUNTIME_LOCAL      -> a local aji-runtime(.exe), the shared pack store's tool (default: the
//                             pinned the-database/aji-runtime release)
//
// --store (what releases build): the TensorRT packs (runtime, builders, TensorRT-RTX's) are the
// shared pack store's, so they're slimmed out of the core like every pack but not published; RIFE
// stays a component pack. TensorRT is still assembled into the tree, for the offline package.
//
// Shared pack store: the package also carries aji-runtime(.exe) and pins the shared runtime release
// (manifest "runtime"), so the updater installs TensorRT, the builder packs and TensorRT-RTX's runtime
// into the per-user store that other apps share (its store mode), instead of the component packs.
// The component packs are still emitted: older versions' updaters, the AppImage and the offline
// package use them, and RIFE stays one (it isn't in the shared release). aji_trtrtx.dll stays in the
// core as well as in the trt-rtx pack, since in store mode only its runtime comes from the store.
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
// no longer leaves an empty engine behind), and opts out of Windows power throttling. v0.14.0:
// --deinterlace (IVTC / bwdif), --preview (the live upscale preview) and --preview-session (the
// preview window). v0.15.0: --square-pixels, and the final resize working with NVENC again. v0.16.0:
// several temporal models in one chain (a temporal upscaler followed by temporal fix). v0.16.1:
// keeps track dispositions (default/forced subtitles and audio) in the output. v0.17.x: the
// chain_N_upscale / temporal_fix_* conf keys the Manager writes (VJN still lists temporal fix as a
// model), so both apps ship the same engine. v0.18.x: the still-image ABI (aji_image.h) and its
// DirectML device ids; nothing VJN uses, the video path is unchanged. v0.19.0: DirectML ~35-40%
// faster (each session's input dims pinned) and the optional TensorRT-RTX backend (aji_trtrtx.dll),
// published as its own Windows asset, aji-trtrtx-windows-x64.zip, which ships as the trt-rtx pack.
// v0.19.1: TensorRT-RTX engine builds no longer free the builder before the objects it created.
// v0.20.0: CPU encoders (FFV1, x265) use every core instead of one; AMD AMF / Intel QSV encoders;
// --probe-encoders (the GPUs and which GPU encoders open), which the Output section's encoder rows use.
// v0.21.0: video preprocessing fuses the vertical chroma filter into the RGB conversion (bit-identical,
// slightly faster); the rest is the still-image API, which VJN doesn't use. v0.22.0: aji_encode
// --device and GPU ids in --probe-encoders (App Settings' GPU choice), DirectML on the
// high-performance GPU instead of DXGI adapter 0, and a "GPU:" line in the engine log. v0.22.1: AMF and
// QSV encode on the upscaling GPU when it is AMD / Intel (not the first such GPU, an iGPU on laptops).
// v0.22.2: --probe-encoders marks integrated GPUs (integrated=), which the GPU choice labels and warns about.
// v0.23.1: loads TensorRT and TensorRT-RTX from the shared pack store (aji-runtime.json beside aji.dll /
// libaji.so, or the file AJI_RUNTIME_JSON names), with that pack's trtexec and builder kernels.
// A libaji release opens the PR that moves AjiVersion and ajiSha256 (.github/workflows/bump-pins.yml,
// scripts/bump_pins.py libaji <tag>); add its line above by hand.
const string AjiVersion        = "v0.27.0";
// The aji release publishes no checksums; recorded from the release assets (both platforms').
var ajiSha256 = new Dictionary<string, string>
{
    ["aji-windows-x64.zip"]          = "1ae2a0d16be2a660d8197fe4f64e8be7cf7637d7842b19994636e42309ab17b2",
    ["aji-trtrtx-windows-x64.zip"]   = "acdf52d9c63f34b1353f08b0511985ddb755bd61793e9a1ae65cbc3c5ba18625",
    ["aji-linux-x64.tar.zst"]        = "044a9cc652fa7660218597c1faa01b7c0abdd97f10a644b0250a0a42321ac0f5",
    ["aji-encode-linux-x64.tar.zst"] = "aaae0b9b8965e832c2526df1f5178e18b9604f5bd11f76ea994c5376d867fe3e",
};
// The shared runtime packs every libaji app pins (animejanai-inference runtime-<n>): TensorRT, the
// per-GPU builder packs and TensorRT-RTX's runtime, installed once per user into the shared pack store
// by aji-runtime (the updater's store mode). The manifest carries the platform's index URL and
// SHA-256. animejanai-inference's runtime-packs.yml asks bump-pins.yml here for the bump PR.
const string RuntimeRepo       = "the-database/animejanai-inference";
const string RuntimeTag        = "runtime-2";
const string RuntimeIndexSha256Win   = "5b6f0450ba4eed34d6e25dde072ce1298f083d91d142fd858495483e7711aea1";
const string RuntimeIndexSha256Linux = "d441210f7747b255f17483f7c8b921817ad3f997abab5a1ba5d348b51511ab61";
// aji-runtime, the store's tool, from its release (the binary in aji-runtime-<platform>.zip/.tar.gz).
const string AjiRuntimeRepo    = "the-database/aji-runtime";
const string AjiRuntimeVersion = "v0.3.0";
const string AjiRuntimeSha256Win   = "f25b32c9f02e8227a7c33b63e55c88d43de7ec8d1b876f9199169050f17b85b4";
const string AjiRuntimeSha256Linux = "9de51f674534a6b38b8317852b066048aaf4be47e001646fa92197e93a38cc2a";
// 7-Zip "extra" (Windows) / linux-x64 standalone console, from its GitHub release (7-zip.org links
// there from 26.05 on; its own /a/ folder stops at 26.02). Digests from the release assets.
const string SevenZipVersion   = "2605";
const string SevenZipTag       = "26.05";
const string SevenZipSha256Win   = "f27a8f103c84785026edcba1f67faf1357225df9d19ddb0b7760d55947141bc7";
const string SevenZipSha256Linux = "22cf82fcc6eb7474f1d5bb4f02c0f8ebd573a718089d6833dc30f8350a3b232e";
// animejanai-inference RIFE fp16 release. Set 2 only names the inputs' H/W dims (DirectML pins them);
// outputs are bit-identical to set 1 and TensorRT engines carry over (cached by file name).
const string RifeModelsVersion = "models-rife-fp16-2";
const string RifeModelsAsset   = "rife-fp16-2.7z";
const string RifeModelsSha256  = "85fa8aa7408f82636f53a3179998e021708746ff85c7468ae11a0d3157af1451";  // scripts/bump_pins.py rife-models <tag>
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
        var downloadUrl = $"https://github.com/ip7z/7zip/releases/download/{SevenZipTag}/7z{SevenZipVersion}-extra.7z";
        var targetPath = Path.GetFullPath("7z-extra.7z");
        await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading 7-Zip ({p}%)..."));
        VerifySha256(targetPath, SevenZipSha256Win);

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
        var downloadUrl = $"https://github.com/ip7z/7zip/releases/download/{SevenZipTag}/7z{SevenZipVersion}-linux-x64.tar.xz";
        var targetPath = Path.GetFullPath("7z-linux.tar.xz");
        await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading 7-Zip ({p}%)..."));
        VerifySha256(targetPath, SevenZipSha256Linux);
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

    // Windows: each asset has its own local override (the TensorRT-RTX zip extracts over the main one).
    var localZips = new Dictionary<string, string?>
    {
        ["aji-windows-x64.zip"]        = Environment.GetEnvironmentVariable("AJI_LOCAL_ZIP"),
        ["aji-trtrtx-windows-x64.zip"] = Environment.GetEnvironmentVariable("AJI_LOCAL_TRTRTX_ZIP"),
    };
    foreach (var asset in plat.AjiAssets)
    {
        string targetPath;
        bool temp = false;
        if (plat.IsWindows && localZips.GetValueOrDefault(asset) is { Length: > 0 } localZip)
        {
            Console.WriteLine($"Using local aji build for {asset}: {localZip}");
            targetPath = localZip;
        }
        else
        {
            Console.WriteLine($"Downloading {asset}...");
            var downloadUrl = $"https://github.com/the-database/animejanai-inference/releases/download/{AjiVersion}/{asset}";
            targetPath = Path.GetFullPath(asset);
            temp = true;
            await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading {asset} ({p}%)..."));
            VerifySha256(targetPath, ajiSha256[asset]);
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
    var downloadUrl = $"https://github.com/the-database/animejanai-inference/releases/download/{RifeModelsVersion}/{RifeModelsAsset}";
    var targetPath = Path.GetFullPath("rife-fp16.7z");
    await DownloadFileAsync(downloadUrl, targetPath, p => Console.WriteLine($"Downloading RIFE fp16 models ({p}%)..."));
    VerifySha256(targetPath, RifeModelsSha256);

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

// ONNX models are version-controlled in this project (onnx/, ~10 MB, including pifroggi's
// vs_temporalfix models for the temporal fix option) rather than downloaded — they
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
    // Licenses that travel with third-party models (vs_temporalfix, Apache-2.0).
    foreach (var license in Directory.GetFiles(modelsSource, "*_LICENSE.txt"))
    {
        File.Copy(license, Path.Combine(onnxPath, Path.GetFileName(license)), true);
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
    // Windows: the optional trt-rtx component, from the aji release's TensorRT-RTX asset.
    var trtRtx = plat.IsWindows
        ? "NVIDIA TensorRT for RTX runtime (tensorrt_rtx_*.dll, tensorrt_onnxparser_rtx_*.dll;\n" +
          "the optional trt-rtx component), redistributed under the NVIDIA TensorRT for RTX\n" +
          "Software License Agreement; see TensorRT-RTX-NOTICE.txt, with NVIDIA's own\n" +
          "acknowledgements in TensorRT-RTX-Acknowledgements.txt. Both ship with the files\n" +
          "they cover.\n\n"
        : "";
    var ajiNames = plat.IsWindows
        ? "aji.dll / aji_trt.dll / aji_trtrtx.dll / aji_dml.dll / aji_encode.exe /\naji_harness*.exe / aji_kernel_test.exe"
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
        trtRtx +
        $"{ajiNames}:\n" +
        "https://github.com/the-database/animejanai-inference\n";
    File.WriteAllText(Path.Combine(inferencePath, "THIRD_PARTY_NOTICES.txt"), notice);
}

// aji-runtime(.exe), the shared pack store's tool, at the install root beside the updater, which runs it.
async Task InstallAjiRuntime()
{
    string exe = plat.Exe("aji-runtime");
    string dest = Path.Combine(installDirectory, exe);
    var local = Environment.GetEnvironmentVariable("AJI_RUNTIME_LOCAL");
    if (!string.IsNullOrEmpty(local))
    {
        Console.WriteLine($"Using local aji-runtime: {local}");
        File.Copy(local, dest, true);
    }
    else
    {
        string asset = plat.IsWindows ? "aji-runtime-windows-x64.zip" : "aji-runtime-linux-x64.tar.gz";
        string archive = Path.GetFullPath(asset);
        Console.WriteLine($"Downloading {asset} {AjiRuntimeVersion}...");
        await DownloadFileAsync($"https://github.com/{AjiRuntimeRepo}/releases/download/{AjiRuntimeVersion}/{asset}", archive, _ => { });
        VerifySha256(archive, plat.IsWindows ? AjiRuntimeSha256Win : AjiRuntimeSha256Linux);
        if (plat.IsWindows)
        {
            ExtractZipFiltered(archive, entry => Path.GetFileName(entry) == exe ? dest : null);
        }
        else
        {
            var unpack = Path.GetFullPath("aji-runtime-unpack");
            if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
            Directory.CreateDirectory(unpack);
            await RunProcess("tar", $"-xzf \"{archive}\" -C \"{unpack}\"");
            if (Directory.GetFiles(unpack, exe, SearchOption.AllDirectories).FirstOrDefault() is string found)
            {
                File.Copy(found, dest, true);
            }
            Directory.Delete(unpack, true);
        }
        File.Delete(archive);
        if (!File.Exists(dest))
        {
            throw new InvalidOperationException($"{asset} has no {exe}");
        }
    }
    if (!plat.IsWindows)
    {
        await RunProcess("chmod", $"+x \"{dest}\"");
    }
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
            aji_runtime = AjiRuntimeVersion,
            runtime = RuntimeTag,
        },
        // The shared runtime packs (the updater's store mode): this platform's index and its pin.
        runtime = new
        {
            tag = RuntimeTag,
            index_url = $"https://github.com/{RuntimeRepo}/releases/download/{RuntimeTag}/packs-{(plat.IsWindows ? "windows" : "linux")}-x64.json",
            index_sha256 = plat.IsWindows ? RuntimeIndexSha256Win : RuntimeIndexSha256Linux,
        },
        // Runtime-created user data is never in the shipped package, so a full update's tree-copy
        // never touches it; these are listed defensively. aji-runtime.json is written by the store for
        // this install.
        user_preserve = new[]
        {
            "animejanai/animejanai.conf",
            "components.json",
            "animejanai/inference/aji-runtime.json",
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
    await InstallAjiRuntime();

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
        if (args.Contains("--offline"))
        {
            await WriteOfflinePackage();
        }
        SlimInstallTree(packFiles);
    }

    var cli = ArgValue("--cli");
    if (!string.IsNullOrEmpty(cli))
    {
        await WriteCliTree(cli);
    }

    Console.WriteLine($"Built {installDirectory}");
}

// The command line's standalone tree: what the slim core holds for running aji_encode (animejanai/,
// with the inference core and the bundled models), the updater and archive tool that `videojanai
// setup` installs the component packs with, and the manifest that names this release to it. No GUI.
async Task WriteCliTree(string cliExe)
{
    // not videojanai-v<ver>: on Windows that is the package tree itself (VideoJaNai-v<ver>, any case)
    var cliDirectory = Path.Combine(assemblyDirectory, $"videojanai-cli-v{version}");
    Console.WriteLine($"Assembling the command line's tree in {cliDirectory}...");
    if (Directory.Exists(cliDirectory)) Directory.Delete(cliDirectory, true);
    Directory.CreateDirectory(cliDirectory);
    CopyDirectory(Path.Combine(installDirectory, "animejanai"), Path.Combine(cliDirectory, "animejanai"));
    foreach (var name in new[] { "manifest.json", "version.txt", plat.ArchiveTool, plat.Exe("VideoJaNaiUpdater"), plat.Exe("aji-runtime") })
    {
        var src = Path.Combine(installDirectory, name);
        if (!File.Exists(src)) throw new FileNotFoundException($"the command line's tree needs {name} from the package tree", src);
        File.Copy(src, Path.Combine(cliDirectory, name), true);
    }
    var exe = Path.Combine(cliDirectory, plat.Exe("videojanai"));
    File.Copy(cliExe, exe, true);
    if (!plat.IsWindows)
    {
        foreach (var x in new[] { exe, Path.Combine(cliDirectory, "VideoJaNaiUpdater"), Path.Combine(cliDirectory, plat.ArchiveTool),
                                  Path.Combine(cliDirectory, "aji-runtime"),
                                  Path.Combine(cliDirectory, "animejanai", "inference", "aji_encode") })
        {
            if (File.Exists(x)) await RunProcess("chmod", $"+x \"{x}\"");
        }
    }
    long bytes = Directory.GetFiles(cliDirectory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
    Console.WriteLine($"  {bytes / 1048576} MB");
}

// The released full-package is the slim core: TensorRT runtime, per-GPU builder resources, RIFE
// models and TensorRT-RTX ship only as component packs, installed on demand. Keeps the base download
// small.
void SlimInstallTree(List<string> packFiles)
{
    Console.WriteLine("Slimming install tree (component packs ship separately)...");
    long removed = 0;
    // aji_trtrtx.dll is in the trt-rtx pack and the core: in store mode only its runtime is a pack.
    foreach (var rel in packFiles.Where(f => !Path.GetFileName(f).Equals("aji_trtrtx.dll", StringComparison.OrdinalIgnoreCase)))
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

// --offline: the package with every component pack already in it, for machines without internet
// access (or to carry one download to several PCs). Archived from the complete tree before
// SlimInstallTree strips the packs out, with a components.json recording each pack as installed at
// this release's dep versions - exactly what the updater writes after installing them - so neither
// the app's first-launch setup nor the updater's --auto fetches anything. Split volumes like the full
// package: it's over GitHub's 2 GB per-asset limit.
async Task WriteOfflinePackage()
{
    Console.WriteLine("Writing the offline package (all component packs included)...");
    var packsIndex = Path.Combine(assemblyDirectory, $"packs-v{version}", $"packs{plat.PackSuffix}.json");
    using var packsDoc = JsonDocument.Parse(File.ReadAllText(packsIndex));
    using var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(installDirectory, "manifest.json")));
    var deps = manifestDoc.RootElement.GetProperty("deps");
    var installed = new Dictionary<string, string>();
    foreach (var p in packsDoc.RootElement.GetProperty("packs").EnumerateArray())
    {
        var name = p.GetProperty("name").GetString()!;
        var dep = p.TryGetProperty("dep", out var d) ? d.GetString() : null;
        installed[name] = dep is not null && deps.TryGetProperty(dep, out var v) && v.GetString() is string s
            ? s : version;
    }
    var componentsJson = Path.Combine(installDirectory, "components.json");
    File.WriteAllText(componentsJson, JsonSerializer.Serialize(new { installed },
        new JsonSerializerOptions { WriteIndented = true }));

    var sevenZ = Path.Combine(installDirectory, plat.ArchiveTool);
    var archive = Path.Combine(assemblyDirectory, $"VideoJaNai-offline-package-{version}{plat.PackSuffix}.7z");
    foreach (var old in Directory.GetFiles(assemblyDirectory, Path.GetFileName(archive) + ".*"))
    {
        File.Delete(old);
    }
    // -snl (Linux): keep the TensorRT library symlink chains as links
    var psi = new ProcessStartInfo
    {
        FileName = sevenZ,
        Arguments = $"a -t7z -mx=5 -v1900m {(plat.IsWindows ? "" : "-snl ")}\"{archive}\" \"{Path.GetFileName(installDirectory)}\"",
        WorkingDirectory = assemblyDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    using (var proc = Process.Start(psi)!)
    {
        // drain both pipes: a full one would stall the archiver
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"{plat.ArchiveTool} failed for the offline package: {await stderr}");
        }
        await stdout;
    }
    // the slim core keeps its components unmanaged until the updater installs them
    File.Delete(componentsJson);
    foreach (var part in Directory.GetFiles(assemblyDirectory, Path.GetFileName(archive) + ".*").Order())
    {
        Console.WriteLine($"  {Path.GetFileName(part)} ({new FileInfo(part).Length / 1048576} MB)");
    }
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
        // TensorRT-RTX (Windows; recommended on RTX 20 / GTX 16-series and newer): the aji
        // release's aji-trtrtx asset. Governed by the aji dep, not inference_runtime: aji_trtrtx.dll
        // has to match the core's aji.dll, so the pack reinstalls whenever aji moves. Needs
        // trt-runtime too (cudart; and the TensorRT fallback), which the app checks.
        ("trt-rtx", "aji", Directory.GetFiles(inferencePath)
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                return n.Equals("aji_trtrtx.dll", StringComparison.OrdinalIgnoreCase) ||
                       n.StartsWith("tensorrt_rtx_", StringComparison.OrdinalIgnoreCase) ||
                       n.StartsWith("tensorrt_onnxparser_rtx_", StringComparison.OrdinalIgnoreCase) ||
                       n.StartsWith("TensorRT-RTX-", StringComparison.OrdinalIgnoreCase);
            })
            .Select(f => Path.GetRelativePath(installDirectory, f)).ToArray()),
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
        if (args.Contains("--store") && name.StartsWith("trt-", StringComparison.Ordinal))
        {
            // the shared pack store's: out of the core, not published
            packedFiles.AddRange(files.Select(f => f.Replace('\\', '/')));
            Console.WriteLine($"  component-{name}: the shared pack store's (--store), not published");
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
        string sha256;
        using (var stream = File.OpenRead(archive))
        {
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        index.Add(new
        {
            name,
            dep,
            asset = Path.GetFileName(archive),
            bytes = new FileInfo(archive).Length,
            // the updater checks each download against it (indexes before 2.11 have none)
            sha256,
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
        // the TensorRT-RTX asset extracts over the main one; EmitComponentPacks carves it into trt-rtx
        AjiAssets: new[] { "aji-windows-x64.zip", "aji-trtrtx-windows-x64.zip" },
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
