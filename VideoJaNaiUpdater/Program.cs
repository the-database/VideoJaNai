// VideoJaNaiUpdater — keeps an installed VideoJaNai folder up to date in place, preserving user
// files, and manages hardware-specific component packs (the in-app component manager shells this).
// Ships at the install root next to VideoJaNai.exe.
//
//   VideoJaNaiUpdater.exe --check        prints UPDATE_AVAILABLE <ver> | UP_TO_DATE <ver>
//   VideoJaNaiUpdater.exe --download     downloads + unpacks the update into update-staging/, prints UPDATE_READY <ver>
//   VideoJaNaiUpdater.exe --apply        waits for VideoJaNai to close, applies (staged or freshly downloaded), relaunches
//                         [--no-relaunch] the app's install-on-close: no relaunch; failures go to update-error.txt
//   VideoJaNaiUpdater.exe --components   detect GPU, list installed/available packs + recommendation
//   VideoJaNaiUpdater.exe --install X    download + install component pack X
//   VideoJaNaiUpdater.exe --remove X     delete component pack X's files
//   VideoJaNaiUpdater.exe --auto         install everything the detected hardware recommends
//                         [--gpus <ids>|none] with --auto and --components: recommend packs for these
//                         GPUs only (App Settings' chosen GPU, as aji_encode --device ids), or none
//                         [--check] with --auto: list what it would install, install nothing
//                         [--include <packs>] with --auto: also these packs (videojanai setup: rife)
//                         [--exclude <packs>] with --auto: not these packs, even where they'd be
//                         installed by default (videojanai setup --no-rife: rife)
//                         [--progress lines] with --auto: also report on stdout for programs, as
//                         mangajanai setup does (videojanai setup runs this); other text goes to stderr
//   VideoJaNaiUpdater.exe --recommend    KEY=value hardware summary for the Inno installer
//
// Update size is tiered: if the latest release's heavy deps match what's installed (compared via
// manifest.json) only the small overlay archive is fetched; otherwise the full package.
//
// Component packs are subsets of the install (TensorRT runtime, per-GPU-generation builder
// resources, RIFE models, the TensorRT-RTX backend on Windows), emitted by the package
// builder as component-<name>.7z + packs.json
// release assets. Archives are rooted at the install dir, so extraction is installation;
// packs.json carries each pack's file list, so removal is deletion. Installed state lives in
// components.json at the install root (inferred from disk for installs that predate it).
// Dev override: set VIDEOJANAI_PACKS_DIR to a local directory with packs.json + archives.
//
// Shared pack store: when manifest.json pins a shared runtime release ("runtime": its index URL and
// SHA-256) and aji-runtime(.exe) ships beside this updater, the TensorRT runtime, the builder packs and
// TensorRT-RTX's runtime live in the per-user pack store instead (%LOCALAPPDATA%\the-database\aji,
// shared with the other apps; the-database/aji-runtime), as app "videojanai" (the GUI's install) or
// "videojanai-cli" (the command line's folder). Every change to them is one `aji-runtime ensure`
// with the install's full set, which also writes animejanai/inference/aji-runtime.json (libaji and
// the app read it). Files an older version installed into the install folder are taken over by the
// store (matched by SHA-256) and then deleted here. RIFE stays this release's component pack. The pack
// names, --components --json, the SETUP_* lines and the exit codes stay the same, so the app, the
// installer and videojanai setup don't change. The uninstaller runs `aji-runtime unref`. Without the
// runtime index (offline), an install that hasn't moved to the store keeps its own packs.
// VIDEOJANAI_RUNTIME_MIRROR=<dir> installs the store packs from a folder holding the runtime
// release's packs-<platform>.json and archives; AJI_RUNTIME_STORE=<dir> (read by aji-runtime) points
// the store at a test folder.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoJaNai.Core;
using static Downloader;

const string Repo = "the-database/VideoJaNai";
string apiLatest = $"https://api.github.com/repos/{Repo}/releases/latest";

// Single-file exe: BaseDirectory is the folder the exe runs from = the install root.
string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, '/');

// Platform-specific names come from the install's manifest.json. Fallbacks keep things working
// before a manifest is present.
string localManifest = Path.Combine(installDir, "manifest.json");
string playerExe = ReadManifestString(localManifest, "player_executable", "VideoJaNai.exe");
string archiveTool = ReadManifestString(localManifest, "archive_tool", "7za.exe");
// Release assets are shared across platforms on one tag, suffixed by RID on Linux
// (…-linux-x64.tar.zst / packs-linux-x64.json / component-*-linux-x64.7z). Windows keeps its legacy
// unsuffixed names. This RID selects our platform's assets from a release that carries both.
string platformRid = ReadManifestString(localManifest, "platform", "win-x64");
bool isWinRid = platformRid == "win-x64";

// The shared pack store: the runtime release this package pins, and the tool that manages the store.
string ajiRuntime = Path.Combine(installDir, OperatingSystem.IsWindows() ? "aji-runtime.exe" : "aji-runtime");
string? runtimeIndexUrl = ReadManifestNested(localManifest, "runtime", "index_url");
string? runtimeIndexSha = ReadManifestNested(localManifest, "runtime", "index_sha256");
string? runtimeMirror = Environment.GetEnvironmentVariable("VIDEOJANAI_RUNTIME_MIRROR") is { Length: > 0 } mirror ? mirror : null;
bool storeMode = (runtimeIndexUrl is not null && runtimeIndexSha is not null || runtimeMirror is not null) && File.Exists(ajiRuntime);
// The GUI's install folder (the Avalonia app is there) or the command line's (videojanai setup): one
// ref each, kept while that program exists.
bool guiTree = File.Exists(Path.Combine(installDir, "Avalonia.Base.dll"));
string storeAppId = guiTree ? "videojanai" : "videojanai-cli";
string storeAppPath = Path.Combine(installDir, guiTree ? playerExe : (OperatingSystem.IsWindows() ? "videojanai.exe" : "videojanai"));
string inferenceDir = Path.Combine(installDir, "animejanai", "inference");

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--check";

// --auto --progress lines: the SETUP_* lines (see SetupLine) on stdout, everything else on stderr.
bool setupLines = mode == "--auto" && Array.IndexOf(args, "--progress") is int progressAt and >= 0 &&
                  progressAt + 1 < args.Length && args[progressAt + 1] is "lines" or "line";
bool autoCheckOnly = mode == "--auto" && args.Skip(1).Contains("--check");
// --auto --include rife: packs wanted whatever the hardware recommends (RIFE is only preselected on a
// fresh install, so a later --auto would never add it)
var autoInclude = mode == "--auto" && Array.IndexOf(args, "--include") is int includeAt and >= 0 && includeAt + 1 < args.Length
    ? args[includeAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
    : [];
// --auto --exclude rife: packs left out even where a fresh install would preselect them
var autoExclude = mode == "--auto" && Array.IndexOf(args, "--exclude") is int excludeAt and >= 0 && excludeAt + 1 < args.Length
    ? args[excludeAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
    : [];
TextWriter protocolOut = Console.Out;
if (setupLines)
{
    Console.SetOut(Console.Error);
}

// --gpus: the GPUs to recommend packs for, when the app has one chosen in App Settings: aji_encode
// --device ids (the PCI address), or "none" when the chosen backend needs no packs (DirectML).
// Without it every NVIDIA GPU counts, as for Auto, where TensorRT may build on any of them.
HashSet<ulong>? gpuFilter = null;
if (Array.IndexOf(args, "--gpus") is int gpusAt and >= 0 && gpusAt + 1 < args.Length)
{
    gpuFilter = [];
    foreach (var part in args[gpusAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (ulong.TryParse(part, out var id))
        {
            gpuFilter.Add(id);
        }
    }
}

// A background --download unpacks the update here (inside the install, so each install has its own),
// and --apply installs from it without downloading again. ready.json marks it complete.
string stagingRoot = Path.Combine(installDir, "update-staging");
string stagingMarker = Path.Combine(stagingRoot, "ready.json");
// An update that failed to install, for the app to show on its next start.
string updateErrorFile = Path.Combine(installDir, "update-error.txt");

try
{
    switch (mode)
    {
        case "--check":
            await CheckAsync();
            return 0;
        case "--download":
            return await DownloadAsync();
        case "--apply":
            return await ApplyAsync(args.Contains("--no-relaunch"));
        case "--components":
            await ComponentsAsync(null, args.Contains("--json"));
            return 0;
        case "--install":
            return await InstallComponentAsync(args.Length > 1 ? args[1] : "");
        case "--remove":
            return RemoveComponent(args.Length > 1 ? args[1] : "");
        case "--auto":
            return await AutoComponentsAsync();
        case "--recommend":
            return await RecommendAsync();
        default:
            Console.WriteLine("Usage: VideoJaNaiUpdater.exe [--check|--download|--apply [--no-relaunch]|--components|--install <pack>|--remove <pack>|--auto|--recommend]");
            return 2;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Updater error: {ex.Message}");
    return 1;
}

// ---- modes ---------------------------------------------------------------------------------

async Task CheckAsync()
{
    // Before the network call, so post-update housekeeping happens even offline.
    CleanupLegacy();
    var release = await GetLatestReleaseAsync();
    string local = ReadLocalVersion();
    if (IsNewer(release.Tag, local))
    {
        Console.WriteLine($"UPDATE_AVAILABLE {release.Tag}");
    }
    else
    {
        Console.WriteLine($"UP_TO_DATE {local}");
    }
}

// Old VideoJaNai (Velopack + Python/VapourSynth/vs-mlrt) downloaded its backend to
// %APPDATA%\VideoJaNai (python/, ffmpeg/, ~5.5 GB). The new Inno build installs to
// %LOCALAPPDATA%\Programs\VideoJaNai with the native libaji engine, so that old tree is dead
// weight. Remove it once both a legacy marker (the old VapourSynth python tree) and a current
// marker (the new inference dir) exist — never true on a clean install or a pure 3.x install.
void CleanupLegacy()
{
    if (!OperatingSystem.IsWindows())
    {
        return; // the VapourSynth-era builds were Windows-only
    }
    try
    {
        string oldBackend = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoJaNai");
        bool legacy = File.Exists(Path.Combine(oldBackend, "python", "VSPipe.exe"));
        bool current = File.Exists(Path.Combine(installDir, "animejanai", "inference", "aji_encode.exe")) ||
                       Directory.Exists(Path.Combine(installDir, "animejanai", "inference"));
        if (!legacy || !current)
        {
            return;
        }

        // The big regenerable trees. Leave %APPDATA%\VideoJaNai\onnx (any user-added models) alone.
        string[] dirs = { "python", "ffmpeg" };
        long freed = 0;
        foreach (var rel in dirs)
        {
            string p = Path.Combine(oldBackend, rel);
            if (!Directory.Exists(p))
            {
                continue;
            }
            try
            {
                freed += new DirectoryInfo(p).EnumerateFiles("*", SearchOption.AllDirectories)
                                             .Sum(f => f.Length);
                Directory.Delete(p, true);
            }
            catch { /* locked file etc. - retried on a later start */ }
        }
        if (freed > 0)
        {
            string amount = freed >= 1073741824
                ? $"{freed / 1073741824.0:F1} GB"
                : $"{freed / 1048576} MB";
            Console.WriteLine($"LEGACY_CLEANUP freed {amount} of retired VapourSynth/Python files");
        }
    }
    catch { /* cleanup must never break --check */ }
}

async Task<int> DownloadAsync()
{
    var release = await GetLatestReleaseAsync();
    string local = ReadLocalVersion();
    if (!IsNewer(release.Tag, local))
    {
        Console.WriteLine($"UP_TO_DATE {local}");
        return 0;
    }
    if (ReadStaged(release.Tag) is null)
    {
        await StageAsync(release);
    }
    Console.WriteLine($"UPDATE_READY {release.Tag}");
    return 0;
}

async Task<int> ApplyAsync(bool noRelaunch)
{
    // The app quits right after starting us; wait for it, and anything else running from the install
    // (aji_encode from a preview or a stopped upscale, a background --download), to release file locks.
    if (!WaitForInstallProcesses(TimeSpan.FromSeconds(60)))
    {
        if (noRelaunch)
        {
            // Another VideoJaNai window is still open; installing on its close covers it.
            return 0;
        }
        Console.WriteLine("Close all VideoJaNai windows, then try again.");
        return 1;
    }

    try
    {
        var release = await GetLatestReleaseAsync();
        string local = ReadLocalVersion();
        if (!IsNewer(release.Tag, local))
        {
            Console.WriteLine($"Already up to date (v{local}).");
            TryDelete(() => Directory.Delete(stagingRoot, true));
        }
        else
        {
            Console.WriteLine($"Updating from v{local} to v{release.Tag}...");
            var staged = ReadStaged(release.Tag) ?? await StageAsync(release);

            Console.WriteLine("Applying update (your animejanai.conf, settings and added models are kept)...");
            ApplyOver(staged.Root, installDir, staged.Overlay);

            TryDelete(() => Directory.Delete(stagingRoot, true));
            TryDelete(() => File.Delete(updateErrorFile));
            Console.WriteLine($"Update to v{release.Tag} complete.");
        }
    }
    catch (Exception ex)
    {
        // The staged files are kept, so the next attempt doesn't download again.
        TryDelete(() => File.WriteAllText(updateErrorFile, ex.Message));
        Console.WriteLine($"Updater error: {ex.Message}");
        if (!noRelaunch)
        {
            RelaunchApp();
        }
        return 1;
    }

    if (!noRelaunch)
    {
        RelaunchApp();
    }
    return 0;
}

// Downloads and unpacks the release into update-staging/, marking it complete only at the end.
async Task<Staged> StageAsync(Release release)
{
    TryDelete(() => Directory.Delete(stagingRoot, true));
    string work = Path.Combine(stagingRoot, "download");
    string staging = Path.Combine(stagingRoot, "tree");
    Directory.CreateDirectory(work);

    bool overlay = await IsOverlaySufficientAsync(release, work);
    Console.WriteLine(overlay
        ? "Heavy dependencies unchanged — downloading lightweight overlay update."
        : "Dependencies changed — downloading full package (this is large).");

    string archiveEntry = overlay
        ? await DownloadOverlayAsync(release, work)
        : await DownloadFullAsync(release, work);

    Console.WriteLine("Extracting...");
    Extract(archiveEntry, staging);
    TryDelete(() => Directory.Delete(work, true));

    // For the full package the archive root is the versioned folder; the overlay is flat.
    string sourceRoot = overlay ? staging : FindVersionedRoot(staging);
    File.WriteAllText(stagingMarker, JsonSerializer.Serialize(new
    {
        tag = release.Tag,
        overlay,
        root = Path.GetRelativePath(stagingRoot, sourceRoot),
    }));
    return new Staged(sourceRoot, overlay);
}

// The staged update for this release, if one finished unpacking.
Staged? ReadStaged(string tag)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(stagingMarker));
        var r = doc.RootElement;
        string root = Path.Combine(stagingRoot, r.GetProperty("root").GetString() ?? "");
        return r.GetProperty("tag").GetString() == tag && Directory.Exists(root)
            ? new Staged(root, r.GetProperty("overlay").GetBoolean())
            : null;
    }
    catch
    {
        return null;
    }
}

// ---- update decision -----------------------------------------------------------------------

// Overlay is enough when the latest release's heavy deps match the installed manifest's deps.
async Task<bool> IsOverlaySufficientAsync(Release release, string work)
{
    // VideoJaNai ships a full package only: its slim core carries no heavy deps to skip and no
    // bundled user data, so an overlay would be the same size and the assembler doesn't build one.
    // Only take the overlay path if a release actually publishes an overlay asset (future-proofing);
    // otherwise a deps-unchanged (app-only) update would pick overlay and then fail to download it.
    bool hasOverlay = release.Assets.Any(a => Regex.IsMatch(a.Name, @"overlay-.*\.7z$"));
    if (!hasOverlay)
    {
        return false;
    }

    var asset = release.Assets.FirstOrDefault(a => a.Name == "manifest.json");
    if (asset is null)
    {
        return false; // no manifest published -> play safe with a full update
    }

    string remotePath = Path.Combine(work, "manifest.remote.json");
    await DownloadFileAsync(asset.Url, remotePath, _ => { });

    string? localDeps = ReadDepsRaw(Path.Combine(installDir, "manifest.json"));
    string? remoteDeps = ReadDepsRaw(remotePath);
    return localDeps != null && remoteDeps != null && localDeps == remoteDeps;
}

static string ReadManifestString(string manifestPath, string key, string fallback)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrEmpty(s)) return s;
        }
    }
    catch { /* missing/unreadable manifest -> fallback */ }
    return fallback;
}

static string? ReadDepsRaw(string manifestPath)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.TryGetProperty("deps", out var deps) ? deps.GetRawText() : null;
    }
    catch
    {
        return null;
    }
}

// ---- downloads -----------------------------------------------------------------------------

async Task<string> DownloadOverlayAsync(Release release, string work)
{
    var asset = release.Assets.FirstOrDefault(a => Regex.IsMatch(a.Name, @"overlay-.*\.7z$"))
        ?? throw new InvalidOperationException("No overlay asset found on the latest release.");
    string dest = Path.Combine(work, asset.Name);
    await DownloadWithProgress(asset, dest);
    return dest;
}

async Task<string> DownloadFullAsync(Release release, string work)
{
    if (!isWinRid)
    {
        // Linux full package is a single zstd tarball (…-<rid>.tar.zst).
        var tar = release.Assets.FirstOrDefault(a =>
                Regex.IsMatch(a.Name, $@"{Regex.Escape(platformRid)}\.tar\.zst$"))
            ?? throw new InvalidOperationException("No full-package tarball found on the latest release.");
        string tarDest = Path.Combine(work, tar.Name);
        await DownloadWithProgress(tar, tarDest);
        return tarDest;
    }

    // Windows full package is a multi-volume .7z (full-package-*.7z.001/.002/...).
    var parts = release.Assets
        .Where(a => a.Name.Contains("full-package-") && Regex.IsMatch(a.Name, @"\.7z\.\d+$"))
        .OrderBy(a => a.Name)
        .ToList();
    if (parts.Count == 0)
    {
        throw new InvalidOperationException("No full-package assets found on the latest release.");
    }

    foreach (var part in parts)
    {
        await DownloadWithProgress(part, Path.Combine(work, part.Name));
    }
    // 7z auto-discovers the remaining volumes from the first part.
    return Path.Combine(work, parts[0].Name);
}

async Task DownloadWithProgress(Asset asset, string dest, string? setupName = null)
{
    double last = -10;
    double lastLine = -1;
    Console.WriteLine($"Downloading {asset.Name}...");
    await DownloadFileAsync(asset.Url, dest, p =>
    {
        if (p >= last + 5)
        {
            Console.WriteLine($"  {asset.Name}: {p}%");
            last = p;
        }
    }, (got, total) =>
    {
        // SETUP_DOWNLOAD at most once per percent (and at the end)
        var pct = total > 0 ? Math.Floor(got * 100.0 / total) : -1;
        if (setupName != null && (pct > lastLine || got == total))
        {
            lastLine = pct;
            SetupLine($"SETUP_DOWNLOAD {setupName} {got} {total}");
        }
    });
}

// The --auto --progress lines protocol (the same lines as mangajanai setup's):
//   SETUP_PLAN <pack> <bytes> installed|download   each pack this PC needs
//   SETUP_TOTAL <bytes>                            what will be downloaded (0: all installed)
//   SETUP_DOWNLOAD <pack> <got> <total>            download progress, at most once per percent
//   SETUP_EXTRACT <pack>                           the pack's archive is being unpacked
//   SETUP_DONE                                     finished, nothing left to do (not after --check
//                                                  with work left)
// A failure ends the run without SETUP_DONE: the message is on stderr, the exit code non-zero.
void SetupLine(string line)
{
    if (setupLines)
    {
        protocolOut.WriteLine(line);
        protocolOut.Flush();
    }
}

// A downloaded pack against packs.json's sha256 (indexes that predate it have none).
static void VerifySha256(Pack pack, string archive)
{
    if (string.IsNullOrEmpty(pack.Sha256))
    {
        return;
    }
    using var stream = File.OpenRead(archive);
    var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    if (!actual.Equals(pack.Sha256, StringComparison.OrdinalIgnoreCase))
    {
        stream.Dispose();
        File.Delete(archive);
        throw new InvalidOperationException(
            $"{pack.Asset}: the download doesn't match its SHA-256 (got {actual.ToLowerInvariant()}, expected {pack.Sha256.ToLowerInvariant()}); try again");
    }
}

// ---- extraction / apply --------------------------------------------------------------------

void Extract(string archiveEntry, string outDir)
{
    Directory.CreateDirectory(outDir);
    // The Linux full package is a zstd tarball; the Windows archives and the component packs
    // extract with the manifest's archive_tool.
    bool isTar = archiveEntry.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase);
    string tool = isTar ? "tar" : Path.Combine(installDir, archiveTool);
    string toolArgs = isTar
        ? $"--zstd -xf \"{archiveEntry}\" -C \"{outDir}\""
        : $"x \"{archiveEntry}\" -o\"{outDir}\" -snl -y";   // -snl: restore symlinks as links
    var psi = new ProcessStartInfo
    {
        FileName = tool,
        Arguments = toolArgs,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {tool}");
    string err = p.StandardError.ReadToEnd();
    p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        throw new InvalidOperationException($"7z extraction failed (exit {p.ExitCode}): {err}");
    }
}

string FindVersionedRoot(string staging)
{
    var dir = Directory.GetDirectories(staging)
        .FirstOrDefault(d => Path.GetFileName(d).StartsWith("VideoJaNai-v"));
    return dir ?? staging;
}

// Copy the freshly-extracted tree over the install. user_preserve paths from the local manifest are
// never overwritten (relevant only to a full update — the overlay archive doesn't contain them).
void ApplyOver(string sourceRoot, string targetRoot, bool overlay)
{
    var preserve = overlay ? new HashSet<string>() : ReadUserPreserve(Path.Combine(targetRoot, "manifest.json"));

    // Self-update: a running exe can't be overwritten, but it can be renamed out of the way first.
    string ownExe = Environment.ProcessPath ?? "";
    string ownName = string.IsNullOrEmpty(ownExe) ? "" : Path.GetFileName(ownExe);
    string newUpdater = Path.Combine(sourceRoot, ownName);

    EnsureReplaceable(sourceRoot, targetRoot, preserve, ownName);

    if (!string.IsNullOrEmpty(ownExe) && File.Exists(newUpdater) &&
        Path.GetFullPath(ownExe).Equals(Path.GetFullPath(Path.Combine(targetRoot, ownName)), StringComparison.OrdinalIgnoreCase))
    {
        string old = ownExe + ".old";
        TryDelete(() => File.Delete(old));
        TryDelete(() => File.Move(ownExe, old));
    }

    CopyTree(sourceRoot, targetRoot, sourceRoot, preserve);
}

// Fails before anything is changed when a file the update replaces is in use (another program has it
// open), so a failed install leaves the old version intact instead of half-updated.
static void EnsureReplaceable(string sourceRoot, string targetRoot, HashSet<string> preserve, string ownName)
{
    foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
    {
        string rel = NormalizeRel(Path.GetRelativePath(sourceRoot, file));
        string dst = Path.Combine(targetRoot, rel);
        if (IsPreserved(rel, preserve) || rel.Equals(ownName, StringComparison.OrdinalIgnoreCase) || !File.Exists(dst))
        {
            continue;
        }
        try
        {
            using var _ = new FileStream(dst, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"{rel} is in use by another program. Close it and try again.", e);
        }
    }
}

void CopyTree(string srcDir, string dstDir, string sourceRoot, HashSet<string> preserve)
{
    Directory.CreateDirectory(dstDir);

    foreach (var file in Directory.GetFiles(srcDir))
    {
        string rel = NormalizeRel(Path.GetRelativePath(sourceRoot, file));
        if (IsPreserved(rel, preserve))
        {
            continue;
        }
        string dst = Path.Combine(dstDir, Path.GetFileName(file));
        TryDelete(() => { if (File.Exists(dst)) File.SetAttributes(dst, FileAttributes.Normal); });
        File.Copy(file, dst, true);
    }

    foreach (var sub in Directory.GetDirectories(srcDir))
    {
        string rel = NormalizeRel(Path.GetRelativePath(sourceRoot, sub));
        if (IsPreserved(rel, preserve))
        {
            continue;
        }
        CopyTree(sub, Path.Combine(dstDir, Path.GetFileName(sub)), sourceRoot, preserve);
    }
}

static bool IsPreserved(string rel, HashSet<string> preserve)
{
    foreach (var p in preserve)
    {
        if (rel == p || rel.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }
    return false;
}

static string NormalizeRel(string rel) => rel.Replace('\\', '/');

static HashSet<string> ReadUserPreserve(string manifestPath)
{
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (doc.RootElement.TryGetProperty("user_preserve", out var arr))
        {
            foreach (var e in arr.EnumerateArray())
            {
                var v = e.GetString();
                if (!string.IsNullOrEmpty(v)) set.Add(NormalizeRel(v));
            }
        }
    }
    catch { /* if unreadable, preserve nothing extra — overlay path is unaffected */ }
    return set;
}

// ---- helpers -------------------------------------------------------------------------------

string ReadLocalVersion()
{
    string path = Path.Combine(installDir, "version.txt");
    return File.Exists(path) ? File.ReadAllText(path).Trim() : "0.0.0";
}

Task<Release> GetLatestReleaseAsync() => GetReleaseAsync(apiLatest);

// Component packs must match this installation (its aji/TensorRT builds), so they come from the
// installed version's release, including prereleases and older versions. The latest endpoint is
// only for updating the application. A test/core-only package may pin component_package_version to
// reuse a release whose inference/runtime/model deps are unchanged.
string ReadComponentVersion() => ReadManifestString(
    localManifest, "component_package_version", ReadLocalVersion()).Trim();

Task<Release> GetInstalledReleaseAsync()
{
    string tag = ReadComponentVersion();
    if (string.IsNullOrWhiteSpace(tag) || tag == "0.0.0")
    {
        throw new InvalidOperationException(
            "Cannot select component packs: the installed component version is missing or empty.");
    }
    return GetReleaseAsync($"https://api.github.com/repos/{Repo}/releases/tags/{Uri.EscapeDataString(tag)}");
}

async Task<Release> GetReleaseAsync(string url)
{
    using var client = NewClient();
    string json = await client.GetStringAsync(url);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    string tag = root.GetProperty("tag_name").GetString() ?? "";
    var assets = new List<Asset>();
    if (root.TryGetProperty("assets", out var arr))
    {
        foreach (var a in arr.EnumerateArray())
        {
            assets.Add(new Asset(
                a.GetProperty("name").GetString() ?? "",
                a.GetProperty("browser_download_url").GetString() ?? ""));
        }
    }
    return new Release(tag, assets);
}

void RelaunchApp()
{
    string app = Path.Combine(installDir, playerExe);
    if (!File.Exists(app))
    {
        return;
    }
    try
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo { FileName = app, UseShellExecute = true });
        }
        else
        {
            // Shell-exec would hand the binary to xdg-open; start it directly, making sure the
            // extraction kept its execute bit.
            try
            {
                File.SetUnixFileMode(app, File.GetUnixFileMode(app)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch { /* best effort */ }
            Process.Start(new ProcessStartInfo { FileName = app, UseShellExecute = false, WorkingDirectory = installDir });
        }
    }
    catch (Exception e) { Console.WriteLine($"Could not relaunch VideoJaNai: {e.Message}"); }
}

// Waits until no other process runs from the install folder; false if some still do at the timeout.
bool WaitForInstallProcesses(TimeSpan timeout)
{
    string root = Path.GetFullPath(installDir) + Path.DirectorySeparatorChar;
    int self = Environment.ProcessId;
    var sw = Stopwatch.StartNew();
    while (true)
    {
        bool busy = Process.GetProcesses().Any(p =>
        {
            try
            {
                return p.Id != self && p.MainModule?.FileName is string f &&
                       Path.GetFullPath(f).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; } // exited, or not ours to inspect
            finally { p.Dispose(); }
        });
        if (!busy)
        {
            return true;
        }
        if (sw.Elapsed >= timeout)
        {
            return false;
        }
        Thread.Sleep(500);
    }
}

static void TryDelete(Action action)
{
    try { action(); } catch { /* best effort */ }
}

// Numeric-dotted semver compare; "3.2.10" > "3.2.9". Falls back to ordinal on non-numeric parts.
static bool IsNewer(string remote, string local)
{
    int[] R = Parse(remote), L = Parse(local);
    for (int i = 0; i < Math.Max(R.Length, L.Length); i++)
    {
        int r = i < R.Length ? R[i] : 0;
        int l = i < L.Length ? L[i] : 0;
        if (r != l) return r > l;
    }
    return false;

    static int[] Parse(string v)
    {
        var parts = v.TrimStart('v', 'V').Split('.');
        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            nums[i] = int.TryParse(new string(parts[i].TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0;
        }
        return nums;
    }
}

// ---- component manager ----------------------------------------------------------------------

Task<PackIndex> GetPackIndexAsync() => storeMode ? GetStorePackIndexAsync() : GetReleasePackIndexAsync();

// This release's component packs (packs.json).
async Task<PackIndex> GetReleasePackIndexAsync()
{
    string? local = Environment.GetEnvironmentVariable("VIDEOJANAI_PACKS_DIR");
    string json;
    List<Asset> assets;
    if (!string.IsNullOrEmpty(local))
    {
        string localIndex = Path.Combine(local, isWinRid ? "packs.json" : $"packs-{platformRid}.json");
        json = File.ReadAllText(File.Exists(localIndex) ? localIndex : Path.Combine(local, "packs.json"));
        assets = Directory.GetFiles(local, "component-*.7z")
            .Select(f => new Asset(Path.GetFileName(f), f)).ToList();
    }
    else
    {
        var release = await GetInstalledReleaseAsync();
        // The packs index is RID-suffixed on Linux (packs-linux-x64.json) so it can coexist with the
        // Windows packs.json on a shared release; its "asset" fields already carry the matching
        // component-*-<rid>.7z names.
        string packsName = isWinRid ? "packs.json" : $"packs-{platformRid}.json";
        var idx = release.Assets.FirstOrDefault(a => a.Name == packsName)
            ?? throw new InvalidOperationException(
                $"Release {release.Tag} publishes no component packs ({packsName} missing).");
        using var client = NewClient();
        json = await client.GetStringAsync(idx.Url);
        assets = release.Assets;
    }

    var packs = new List<Pack>();
    using var doc = JsonDocument.Parse(json);
    foreach (var e in doc.RootElement.GetProperty("packs").EnumerateArray())
    {
        var files = e.GetProperty("files").EnumerateArray()
            .Select(f => f.GetString() ?? "").Where(f => f.Length > 0).ToList();
        string asset = e.GetProperty("asset").GetString() ?? "";
        packs.Add(new Pack(
            e.GetProperty("name").GetString() ?? "",
            asset,
            assets.FirstOrDefault(a => a.Name == asset)?.Url,
            e.GetProperty("bytes").GetInt64(),
            files,
            e.TryGetProperty("dep", out var d) ? d.GetString() : null,
            e.TryGetProperty("installed_bytes", out var ib) ? ib.GetInt64() : 0,
            e.TryGetProperty("sha256", out var sha) ? sha.GetString() : null));
    }
    return new PackIndex(
        doc.RootElement.TryGetProperty("package_version", out var v)
            ? v.GetString() ?? "" : "", packs);
}

// One heavy-dependency version out of a manifest's deps (e.g. "inference_runtime", "rife"). "" if the
// manifest/key is missing - used to decide whether an already-present pack's content is unchanged.
static string ReadManifestDep(string manifestPath, string depKey)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (doc.RootElement.TryGetProperty("deps", out var deps) &&
            deps.TryGetProperty(depKey, out var v) && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString() ?? "";
        }
    }
    catch { /* missing/unreadable manifest -> "" */ }
    return "";
}

// Does the pack's content on disk actually match the published pack? components.json records only
// what the updater BELIEVES it installed, and that record can be wrong - e.g. a component whose
// download was skipped or failed while the record was still written, leaving the previous release's
// files in place. That state is self-perpetuating: the record then says "current" forever and the
// real files are never refreshed (how a TensorRT 11.0 runtime survived an upgrade to an 11.1 build in
// mpv-AnimeJaNai). Comparing the total extracted size against the index catches it - a different
// upstream build is a different size - and costs only a directory stat. Older indexes carry no
// installed_bytes; there is nothing to verify against, so trust the record.
bool PackContentMatches(Pack pack)
{
    if (pack.InstalledBytes <= 0)
    {
        return true;
    }
    long actual = 0;
    foreach (var f in pack.Files)
    {
        var fi = new FileInfo(Path.Combine(installDir, f));
        if (!fi.Exists)
        {
            return false;
        }
        actual += fi.Length;
    }
    if (actual != pack.InstalledBytes)
    {
        Console.WriteLine($"{pack.Name}: on-disk content does not match the published pack " +
                          $"({actual:N0} vs {pack.InstalledBytes:N0} bytes); reinstalling.");
        return false;
    }
    return true;
}

// Installed state: components.json, else inferred from what's on disk so full installs that predate
// the manager work out of the box. In store mode the trt-* packs are the store's for this install.
Dictionary<string, string> ReadInstalledComponents(PackIndex index)
{
    var installed = ReadPrivateComponents(index);
    if (storeMode)
    {
        foreach (var name in installed.Keys.Where(StorePackName).ToList())
        {
            installed.Remove(name);
        }
        foreach (var (name, pack) in ReadStorePacks())
        {
            installed[name] = pack.Version;
        }
    }
    return installed;
}

// What the install folder holds (components.json, else what's on disk), whatever the store has.
Dictionary<string, string> ReadPrivateComponents(PackIndex index)
{
    string path = Path.Combine(installDir, "components.json");
    var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (File.Exists(path))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("installed").EnumerateObject())
            {
                installed[e.Name] = e.Value.GetString() ?? "";
            }
            return installed;
        }
        catch { /* fall through to inference */ }
    }
    foreach (var pack in index.Packs.Where(p => !p.Store).Concat(index.Replaced ?? []))
    {
        // a pack counts as installed when all its files exist
        if (pack.Files.Count > 0 &&
            pack.Files.All(f => File.Exists(Path.Combine(installDir, f))))
        {
            installed[pack.Name] = "(pre-manager install)";
        }
    }
    return installed;
}

// In store mode only this release's packs are recorded (the store keeps its own); the file also marks
// that the hardware setup ran.
void WriteInstalledComponents(Dictionary<string, string> installed)
{
    if (storeMode)
    {
        installed = installed.Where(kv => !StorePackName(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }
    File.WriteAllText(Path.Combine(installDir, "components.json"),
        JsonSerializer.Serialize(new { installed },
            new JsonSerializerOptions { WriteIndented = true }));
}

string? PackVersionMismatch(PackIndex index)
{
    string localVersion = ReadComponentVersion();
    return localVersion != "" && index.PackageVersion != "" && localVersion != index.PackageVersion
        ? $"This installation selects component release v{localVersion} but the published packs are for v{index.PackageVersion}."
        : null;
}

// NVIDIA detection via NVML (ships with the driver; Nvml in VideoJaNai.Core's NvidiaGpus.cs); its
// absence means a non-NVIDIA GPU. Only the TensorRT engine needs downloadable component packs, and
// they apply to NVIDIA GPUs only; non-NVIDIA GPUs use the built-in DirectML engine, which ships in
// the base install.
//
// Every NVIDIA GPU is reported (Sm "sm89, sm120", names joined with " + "): NVML enumerates in PCI
// order while CUDA picks the fastest GPU first, so recommending only NVML's device 0 left the GPU
// TensorRT actually builds on without its builder kernels on multi-GPU systems. With `only` (--gpus),
// just the NVIDIA GPUs among those ids (an empty set: none).
//
// A GPU TensorRT can't run on (compute capability below 7.5, TensorRtGpus.MinComputeCapability:
// GeForce GTX 10-series and older, Volta) counts as no NVIDIA GPU: it upscales on DirectML, so it
// gets no TensorRT packs, and next to a newer card only that one counts. TooOld names those GPUs
// ("NVIDIA GeForce GTX 1080 (compute 6.1)", joined with " + "), for the messages.
static (bool HasNvidia, string Sm, string GpuName, string TooOld) DetectGpu(IReadOnlySet<ulong>? only = null)
{
    var gpus = only is { Count: 0 } ? []
        : Nvml.Detect().Where(g => only == null || only.Any(id => Nvml.SameGpu(id, g.Id))).ToList();
    var usable = gpus.Where(g => g.TensorRtCapable).ToList();
    return (usable.Count > 0,
            string.Join(", ", usable.Select(g => g.Sm).Distinct()),
            string.Join(" + ", usable.Select(g => g.Name)),
            string.Join(" + ", gpus.Where(g => !g.TensorRtCapable).Select(g => $"{g.Name} (compute {g.ComputeCapability})")));
}

// What the updater says about GPUs too old for TensorRT (DetectGpu's TooOld): alone, or next to a
// newer NVIDIA GPU (`others`), which TensorRT runs on instead.
static string TooOldText(string tooOld, bool others = false) =>
    $"GPU: {tooOld} - {TensorRtGpus.NeedsText("TensorRT")}, so " +
    (others ? "TensorRT doesn't use it and it gets no TensorRT components"
     : OperatingSystem.IsWindows() ? "it uses the built-in DirectML engine (no extra components needed)"
     : "VideoJaNai can't upscale on it (on Linux it has no other engine)");

// Hardware-matched packs only (TensorRtGpus.RecommendedPacks: the runtime, each GPU generation's
// builder pack, else the PTX fallback, and trt-rtx where it's the default). RIFE is a user choice,
// not a recommendation: it is preselected on installs that have never managed components, and once
// the user has made any component decision their choice stands. trt-rtx (TensorRT-RTX, Windows
// only) is recommended when a GPU is one it is the default for (TensorRtGpus.TensorRtRtxDefault),
// next to the TensorRT packs: TensorRT stays installed, as TensorRT-RTX's fallback and for cudart,
// which aji_trtrtx.dll needs. Other NVIDIA GPUs can still install it from App Settings. Once
// installed it counts as installed, so --auto keeps it current (its dep is aji: it is refreshed
// whenever the aji release changes).
List<string> RecommendedPacks(PackIndex index, bool hasNvidia, string sm) =>
    hasNvidia
        ? TensorRtGpus.RecommendedPacks(sm.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                                        name => index.Packs.Any(p => p.Name == name))
        : [];

bool ComponentsNeverManaged() => !File.Exists(Path.Combine(installDir, "components.json"));

bool PreselectPack(Pack pack, Dictionary<string, string> installed, List<string> rec) =>
    installed.ContainsKey(pack.Name) || rec.Contains(pack.Name) ||
    (pack.Name == "rife" && ComponentsNeverManaged());

async Task ComponentsAsync(PackIndex? prefetched, bool json = false)
{
    var index = prefetched ?? await GetPackIndexAsync();
    var installed = ReadInstalledComponents(index);
    var (hasNvidia, sm, gpu, tooOld) = DetectGpu(gpuFilter);
    var rec = RecommendedPacks(index, hasNvidia, sm);
    if (json)
    {
        // consumed by VideoJaNai's in-app component manager; keep keys stable. gpu: the GPUs the
        // recommendations are for (--gpus); nvidia_present: whether this PC has an NVIDIA GPU
        // TensorRT runs on at all; nvidia_too_old: the NVIDIA GPUs (of gpu's) TensorRT can't run on,
        // which count as no NVIDIA GPU ("" when none).
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            package_version = index.PackageVersion,
            version_mismatch = PackVersionMismatch(index),
            gpu = new { nvidia = hasNvidia, sm, name = gpu },
            nvidia_present = gpuFilter is null ? hasNvidia : DetectGpu(null).HasNvidia,
            nvidia_too_old = tooOld,
            packs = index.Packs.Select(p => new
            {
                name = p.Name,
                bytes = p.Bytes,
                installed = installed.ContainsKey(p.Name),
                recommended = rec.Contains(p.Name),
                preselect = PreselectPack(p, installed, rec),
            }),
        }));
        return;
    }
    if (PackVersionMismatch(index) is string warn)
    {
        Console.WriteLine(warn);
        Console.WriteLine();
    }
    Console.WriteLine(hasNvidia
        ? $"GPU: {gpu} ({sm}) - {(rec.Contains("trt-rtx") ? "TensorRT-RTX recommended, with TensorRT as its fallback" : "TensorRT recommended")}"
        : tooOld != "" ? TooOldText(tooOld)
        : "GPU: no NVIDIA device detected - using the built-in DirectML engine (no extra components needed)");
    if (hasNvidia && tooOld != "")
    {
        Console.WriteLine(TooOldText(tooOld, others: true));
    }
    Console.WriteLine($"Recommended packs: {string.Join(", ", rec)}");
    Console.WriteLine();
    foreach (var pack in index.Packs)
    {
        string state = installed.ContainsKey(pack.Name) ? "installed" :
                       rec.Contains(pack.Name) ? "RECOMMENDED" :
                       PreselectPack(pack, installed, rec) ? "default on new installs" :
                       "available";
        Console.WriteLine($"  {pack.Name,-14} {pack.Bytes / 1048576,6} MB  {state}");
    }
}

async Task<int> InstallComponentAsync(string name)
{
    if (string.IsNullOrEmpty(name))
    {
        Console.WriteLine("--install needs a pack name (see --components)");
        return 2;
    }
    var index = await GetPackIndexAsync();
    var pack = index.Packs.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (pack is null)
    {
        Console.WriteLine($"Unknown pack '{name}'. Available: {string.Join(", ", index.Packs.Select(p => p.Name))}");
        return 2;
    }
    if (pack.Store)
    {
        var want = StoreWanted(index);
        want.Add(pack.Name);
        int rc = StoreEnsure(index, want).Item1;
        if (rc == 0)
        {
            Console.WriteLine($"{pack.Name} installed.");
        }
        return rc;
    }
    if (pack.Url is null)
    {
        Console.WriteLine($"Pack '{name}' has no downloadable asset on the selected release.");
        return 1;
    }
    if (PackVersionMismatch(index) is string warn)
    {
        // a pack from a different release can mismatch the installed aji/TensorRT builds
        Console.WriteLine(warn);
        Console.WriteLine("Update first (VideoJaNaiUpdater --apply), then install components.");
        return 1;
    }

    // Skip the (large) download when the identical content is already on disk: every file the pack
    // ships is present, its total size matches the index, AND the upstream version that governs it
    // (the manifest dep the build tagged the pack with: inference_runtime for TensorRT, rife for
    // RIFE) is unchanged - either vs. the pre-upgrade manifest snapshot (manifest.prev.json) or vs.
    // the dep version components.json recorded on a prior managed install. A TensorRT/RIFE bump
    // changes the dep, so this correctly re-downloads then.
    string target = pack.Dep is null ? "" : ReadManifestDep(localManifest, pack.Dep);
    bool filesPresent = pack.Files.Count > 0 &&
        pack.Files.All(f => File.Exists(Path.Combine(installDir, f)));
    if (filesPresent && target != "" && PackContentMatches(pack))
    {
        string prev = pack.Dep is null ? "" :
            ReadManifestDep(Path.Combine(installDir, "manifest.prev.json"), pack.Dep);
        var current = ReadInstalledComponents(index);
        current.TryGetValue(pack.Name, out var recorded);
        if (prev == target || recorded == target)
        {
            current[pack.Name] = target;
            WriteInstalledComponents(current);
            Console.WriteLine($"{pack.Name} already up to date ({target}); skipping download.");
            return 0;
        }
    }

    string work = Path.Combine(Path.GetTempPath(), "videojanai-packs");
    Directory.CreateDirectory(work);
    string archive = Path.Combine(work, pack.Asset);
    if (pack.Url.Contains("://"))
    {
        await DownloadWithProgress(new Asset(pack.Asset, pack.Url), archive, pack.Name);
    }
    else
    {
        File.Copy(pack.Url, archive, true); // VIDEOJANAI_PACKS_DIR dev path
        SetupLine($"SETUP_DOWNLOAD {pack.Name} {pack.Bytes} {pack.Bytes}");
    }
    VerifySha256(pack, archive);
    Console.WriteLine($"Installing {pack.Name}...");
    SetupLine($"SETUP_EXTRACT {pack.Name}");
    Extract(archive, installDir);
    TryDelete(() => File.Delete(archive));

    var installed = ReadInstalledComponents(index);
    // Record the dep version (not the package version) so a later reinstall can tell whether this
    // pack's content actually changed; fall back to the package version for an untagged pack.
    installed[pack.Name] = target != "" ? target : index.PackageVersion;
    WriteInstalledComponents(installed);
    Console.WriteLine($"{pack.Name} installed.");
    return 0;
}

int RemoveComponent(string name)
{
    var index = GetPackIndexAsync().GetAwaiter().GetResult();
    var pack = index.Packs.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (pack is null)
    {
        Console.WriteLine($"Unknown pack '{name}'. Available: {string.Join(", ", index.Packs.Select(p => p.Name))}");
        return 2;
    }
    if (pack.Store)
    {
        var want = StoreWanted(index);
        want.Remove(pack.Name);
        int rc = want.Count == 0 ? StoreUnref() : StoreEnsure(index, want).Item1;
        if (rc == 0)
        {
            WriteInstalledComponents(ReadInstalledComponents(index));
            Console.WriteLine($"{pack.Name} removed. Engine caches and models you added are untouched.");
        }
        return rc;
    }
    int gone = 0;
    foreach (var f in pack.Files)
    {
        string abs = Path.Combine(installDir, f);
        if (File.Exists(abs))
        {
            TryDelete(() => { File.SetAttributes(abs, FileAttributes.Normal); File.Delete(abs); });
            gone++;
        }
    }
    var installed = ReadInstalledComponents(index);
    installed.Remove(pack.Name);
    WriteInstalledComponents(installed);
    Console.WriteLine($"{pack.Name} removed ({gone} files). Engine caches and models you added are untouched.");
    return 0;
}

async Task<int> AutoComponentsAsync()
{
    var index = await GetPackIndexAsync();
    var (hasNvidia, sm, gpu, tooOld) = DetectGpu(gpuFilter);
    var rec = RecommendedPacks(index, hasNvidia, sm);
    if (!hasNvidia && tooOld != "")
    {
        Console.WriteLine(TooOldText(tooOld));
    }
    if (storeMode)
    {
        return await AutoStoreAsync(index, rec, hasNvidia, gpu);
    }
    var installed = ReadInstalledComponents(index);
    // "Needs installing" is not just "absent": a pack already recorded as installed is stale when the
    // dep that governs it moved (a TensorRT/RIFE bump) or when its files on disk are not this pack's
    // content. Without this, --auto reports "everything is installed" after an upgrade and the old
    // runtime is kept forever; InstallComponentAsync re-checks and still skips genuine no-ops.
    var missing = index.Packs
        .Where(p => (PreselectPack(p, installed, rec) || autoInclude.Contains(p.Name)) && !autoExclude.Contains(p.Name))
        .Where(p => !installed.TryGetValue(p.Name, out var recorded) ||
                    (p.Dep is not null && ReadManifestDep(localManifest, p.Dep) is string t &&
                     t != "" && recorded != t) ||
                    !PackContentMatches(p))
        .Select(p => p.Name).ToList();
    var needed = index.Packs.Where(p => (PreselectPack(p, installed, rec) || autoInclude.Contains(p.Name)) && !autoExclude.Contains(p.Name)).ToList();
    foreach (var p in needed)
    {
        SetupLine($"SETUP_PLAN {p.Name} {p.Bytes} {(missing.Contains(p.Name) ? "download" : "installed")}");
    }
    SetupLine($"SETUP_TOTAL {needed.Where(p => missing.Contains(p.Name)).Sum(p => p.Bytes)}");
    if (missing.Count == 0)
    {
        Console.WriteLine("Everything the detected hardware needs is already installed.");
        await ComponentsAsync(index, false);
        SetupLine("SETUP_DONE");
        return 0;
    }
    if (autoCheckOnly)
    {
        Console.WriteLine($"For {(hasNvidia ? gpu : "this GPU")}, would install: " +
            string.Join(", ", needed.Where(p => missing.Contains(p.Name)).Select(p => $"{p.Name} ({p.Bytes / 1048576} MB)")));
        return 0;
    }
    Console.WriteLine($"Installing for {(hasNvidia ? gpu : "this GPU")}: {string.Join(", ", missing)}");
    foreach (var name in missing)
    {
        int rc = await InstallComponentAsync(name);
        if (rc != 0)
        {
            return rc;
        }
    }
    SetupLine("SETUP_DONE");
    return 0;
}

// Hardware summary for the Inno installer's component picker, as trivially parseable KEY=value
// lines. GPU identity is local NVML and always available; TRT_PACKS needs the pack index (network
// or VIDEOJANAI_PACKS_DIR) - if that fails it is emitted empty and the installer falls back to a
// generic TensorRT toggle.
async Task<int> RecommendAsync()
{
    var (hasNvidia, sm, gpu, tooOld) = DetectGpu();
    // NVIDIA: an NVIDIA GPU TensorRT runs on; NVIDIA_TOO_OLD: the ones it can't (DetectGpu's TooOld)
    Console.WriteLine($"NVIDIA={(hasNvidia ? 1 : 0)}");
    Console.WriteLine($"GPU={gpu}");
    Console.WriteLine($"NVIDIA_TOO_OLD={tooOld}");
    string trtPacks = "";
    string rife = "rife";
    try
    {
        var index = await GetPackIndexAsync();
        trtPacks = string.Join(",", RecommendedPacks(index, hasNvidia, sm));
        if (!index.Packs.Any(p => p.Name == "rife"))
        {
            rife = "";
        }
    }
    catch
    {
        // offline / no published packs yet: GPU lines still printed; the installer keeps the
        // TensorRT toggle and resolves packs at install time.
    }
    Console.WriteLine($"TRT_PACKS={trtPacks}");
    Console.WriteLine($"RIFE={rife}");
    return 0;
}

// ---- shared pack store ------------------------------------------------------------------------

// Store mode: TensorRT, the builder packs and TensorRT-RTX's runtime live in the per-user pack store
// (aji-runtime), from the shared runtime release this package pins. VideoJaNai's own component
// packs keep the rest (RIFE). Every trt-* pack name is the store's, except TensorRT-RTX: "trt-rtx" was
// aji_trtrtx.dll and its runtime, and now aji_trtrtx.dll ships in the core and the runtime is the
// store's "trtrtx-runtime".
static bool StorePackName(string pack) => pack.StartsWith("trt-", StringComparison.OrdinalIgnoreCase);
static string StoreName(string pack) => pack == "trt-rtx" ? "trtrtx-runtime" : pack;
static string VjName(string pack) => pack == "trtrtx-runtime" ? "trt-rtx" : pack;

static string? ReadManifestNested(string manifestPath, string key, string field)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (doc.RootElement.TryGetProperty(key, out var o) && o.ValueKind == JsonValueKind.Object &&
            o.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
        {
            return s;
        }
    }
    catch { /* missing/unreadable manifest */ }
    return null;
}

static string Sha256Hex(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

string StoreIndexPath() => Path.Combine(runtimeMirror!, $"packs-{(isWinRid ? "windows" : "linux")}-x64.json");

// The component list in store mode: the runtime release's TensorRT, builder and TensorRT-RTX packs
// (under VideoJaNai's names; Files are paths inside the pack), then this release's own packs that
// aren't the store's (RIFE). This release's trt-* packs are kept aside (Replaced): their file lists
// are what an older version installed into the install folder. Without the runtime index (offline),
// an install that hasn't moved to the store yet keeps using its own release's packs.
async Task<PackIndex> GetStorePackIndexAsync()
{
    List<Pack> store;
    try
    {
        store = await GetStorePacksAsync();
    }
    catch (Exception e) when (!File.Exists(Path.Combine(inferenceDir, "aji-runtime.json")))
    {
        Console.WriteLine($"The shared runtime packs are unavailable ({e.Message}); using this release's component packs.");
        storeMode = false;
        return await GetReleasePackIndexAsync();
    }
    PackIndex own;
    try
    {
        own = await GetReleasePackIndexAsync();
    }
    catch (Exception e)
    {
        Console.WriteLine($"This release's component packs are unavailable ({e.Message}); RIFE can't be installed.");
        own = new PackIndex(ReadComponentVersion(), []);
    }
    return new PackIndex(own.PackageVersion,
        [.. store, .. own.Packs.Where(p => !StorePackName(p.Name))],
        own.Packs.Where(p => StorePackName(p.Name)).ToList());
}

async Task<List<Pack>> GetStorePacksAsync()
{
    string json;
    if (runtimeMirror is not null)
    {
        json = File.ReadAllText(StoreIndexPath());
    }
    else
    {
        using var client = NewClient();
        byte[] data = await client.GetByteArrayAsync(runtimeIndexUrl);
        if (Sha256Hex(data) != runtimeIndexSha!.ToLowerInvariant())
        {
            throw new InvalidOperationException("the runtime packs' index doesn't match the SHA-256 this version pins");
        }
        json = System.Text.Encoding.UTF8.GetString(data);
    }
    var packs = new List<Pack>();
    using var doc = JsonDocument.Parse(json);
    foreach (var e in doc.RootElement.GetProperty("packs").EnumerateArray())
    {
        string kind = e.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
        if (kind is not ("trt" or "trt-builder" or "trtrtx"))
        {
            continue; // DirectML ("private") ships in this app's core; the models are the manga apps'
        }
        var files = e.GetProperty("files").EnumerateArray()
            .Select(f => f.ValueKind == JsonValueKind.Object ? f.GetProperty("path").GetString() ?? "" : f.GetString() ?? "")
            .Where(f => f.Length > 0).ToList();
        packs.Add(new Pack(
            VjName(e.GetProperty("name").GetString() ?? ""),
            e.GetProperty("asset").GetString() ?? "",
            e.GetProperty("url").GetString(),
            e.GetProperty("bytes").GetInt64(),
            files,
            null,
            e.TryGetProperty("installed_bytes", out var ib) ? ib.GetInt64() : 0,
            Store: true));
    }
    return packs;
}

// aji-runtime show: this install's packs in the store (name -> (version, folder)), under VideoJaNai's
// names.
Dictionary<string, (string Version, string Dir)> ReadStorePacks()
{
    var packs = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
    var (rc, stdout) = RunAjiRuntime(["show", "--app-id", storeAppId], quiet: true);
    if (rc != 0)
    {
        return packs;
    }
    try
    {
        using var doc = JsonDocument.Parse(stdout);
        foreach (var p in doc.RootElement.GetProperty("packs").EnumerateArray())
        {
            if (p.GetProperty("installed").GetBoolean())
            {
                packs[VjName(p.GetProperty("name").GetString() ?? "")] =
                    (p.GetProperty("version").GetString() ?? "", p.GetProperty("dir").GetString() ?? "");
            }
        }
    }
    catch { /* unreadable: nothing installed */ }
    return packs;
}

// What this install uses from the store now: the store's packs for it, plus the trt-* packs an older
// version installed into the install folder, which the next ensure moves to the store.
HashSet<string> StoreWanted(PackIndex index)
{
    var want = new HashSet<string>(ReadStorePacks().Keys, StringComparer.OrdinalIgnoreCase);
    foreach (var name in ReadPrivateComponents(index).Keys)
    {
        if (StorePackName(name) && index.Packs.Any(p => p.Store && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            want.Add(name);
        }
    }
    return want;
}

// aji-runtime ensure for exactly `want`: writes the ref and animejanai/inference/aji-runtime.json (libaji
// and the app read it), takes matching files from the inference folder first, then frees what no app
// uses; afterwards this install's private copies of those packs are deleted. With `check`, only the
// plan (its AJI_* lines are returned). With setup lines on, download progress goes out as SETUP_*.
(int, string) StoreEnsure(PackIndex index, HashSet<string> want, bool check = false)
{
    string indexArg = runtimeMirror is not null ? StoreIndexPath() : runtimeIndexUrl!;
    string sha = runtimeMirror is not null ? Sha256Hex(File.ReadAllBytes(indexArg)) : runtimeIndexSha!;
    // install order as the index lists them
    var ordered = index.Packs.Where(p => p.Store && want.Contains(p.Name)).Select(p => StoreName(p.Name)).ToList();
    var arguments = new List<string>
    {
        "ensure", "--app-id", storeAppId, "--app-path", storeAppPath, "--app-version", ReadLocalVersion(),
        "--index", indexArg, "--index-sha256", sha, "--want", string.Join(",", ordered), "--gpus", "none",
        "--aji-dir", inferenceDir, "--adopt-from", inferenceDir,
    };
    if (runtimeMirror is not null)
    {
        arguments.AddRange(["--mirror", runtimeMirror]);
    }
    if (check || setupLines)
    {
        arguments.AddRange(["--progress", "lines"]);
    }
    if (check)
    {
        arguments.Add("--check");
        return RunAjiRuntime(arguments, quiet: true);
    }
    var shown = new Dictionary<string, long>();
    var (rc, output) = RunAjiRuntime(arguments, quiet: false, onLine: line =>
    {
        var f = line.Split(' ');
        string Name(string key) => VjName(key.Split('/')[0]);
        if (f[0] == "AJI_DOWNLOAD" && f.Length == 4 && long.TryParse(f[2], out var got) && long.TryParse(f[3], out var total))
        {
            // at most once per percent, as the release packs' downloads report
            long pct = total > 0 ? got * 100 / total : 0;
            string name = Name(f[1]);
            if (!shown.TryGetValue(name, out var last) || last != pct)
            {
                shown[name] = pct;
                SetupLine($"SETUP_DOWNLOAD {name} {got} {total}");
            }
        }
        else if (f[0] == "AJI_EXTRACT" && f.Length == 2)
        {
            SetupLine($"SETUP_EXTRACT {Name(f[1])}");
        }
    });
    if (rc != 0)
    {
        return (rc, output);
    }
    RemovePrivatePackFiles(index);
    WriteInstalledComponents(ReadInstalledComponents(index));
    return (0, output);
}

int StoreUnref()
{
    var (rc, _) = RunAjiRuntime(["unref", "--app-id", storeAppId], quiet: false);
    TryDelete(() => File.Delete(Path.Combine(inferenceDir, "aji-runtime.json")));
    return rc;
}

// libaji's own files ship in the core and always stay (aji_trtrtx.dll is in the trt-rtx pack too).
static bool IsLibaji(string rel)
{
    string n = Path.GetFileName(rel).ToLowerInvariant();
    return (n.StartsWith("aji") && n.EndsWith(".dll")) || n.StartsWith("libaji");
}

// Where a store pack's file sat in the install folder before the store.
static string PrivatePath(string f) => Path.Combine("animejanai", "inference", f);

// The files an older version installed for the store's packs: this release's trt-* packs' lists, and
// files named like the store packs' in the inference folder. Once the store has TensorRT, also every
// builder kernel in the inference folder: libaji's trtexec loads them from the store's builder packs
// only (an offline package carries them for every GPU generation).
IEnumerable<string> PrivatePackFiles(PackIndex index, Func<string, bool> pack) =>
    (index.Replaced ?? []).Where(p => pack(p.Name)).SelectMany(p => p.Files)
        .Concat(index.Packs.Where(p => p.Store && pack(p.Name)).SelectMany(p => p.Files.Select(PrivatePath)))
        .Concat(pack("trt-runtime") && Directory.Exists(inferenceDir)
            ? Directory.GetFiles(inferenceDir, "*builder_resource_*").Select(f => PrivatePath(Path.GetFileName(f)))
            : [])
        .Where(f => !IsLibaji(f)).Distinct(StringComparer.OrdinalIgnoreCase);

bool PrivatePackFilesLeft(PackIndex index)
{
    var have = ReadStorePacks();
    return PrivatePackFiles(index, have.ContainsKey).Any(f => File.Exists(Path.Combine(installDir, f)));
}

// Once the store holds a pack, its copy in the install folder is unused (libaji loads the store's by
// full path), so it goes.
void RemovePrivatePackFiles(PackIndex index)
{
    var have = ReadStorePacks();
    int gone = 0;
    foreach (var f in PrivatePackFiles(index, have.ContainsKey))
    {
        string abs = Path.Combine(installDir, f);
        if (File.Exists(abs) || new FileInfo(abs).LinkTarget is not null)
        {
            TryDelete(() => { File.SetAttributes(abs, FileAttributes.Normal); File.Delete(abs); });
            gone++;
        }
    }
    if (gone > 0)
    {
        Console.WriteLine($"Removed {gone} file(s) from the install folder that the shared pack store now holds.");
    }
}

// Runs aji-runtime. Its human output (stderr) is passed on line by line, as the app and videojanai
// setup show this updater's output; `quiet` shows only its errors. stdout (JSON, or AJI_* lines) is
// returned, and each of its lines goes to onLine as it arrives.
(int, string) RunAjiRuntime(IEnumerable<string> arguments, bool quiet, Action<string>? onLine = null)
{
    var psi = new ProcessStartInfo
    {
        FileName = ajiRuntime,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        WorkingDirectory = installDir,
    };
    foreach (var a in arguments)
    {
        psi.ArgumentList.Add(a);
    }
    using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start aji-runtime");
    var stderr = Task.Run(() =>
    {
        string? line;
        while ((line = p.StandardError.ReadLine()) != null)
        {
            if (!quiet || line.StartsWith("error:", StringComparison.Ordinal))
            {
                Console.WriteLine(line.TrimEnd());
            }
        }
    });
    var stdout = new System.Text.StringBuilder();
    string? outLine;
    while ((outLine = p.StandardOutput.ReadLine()) != null)
    {
        stdout.AppendLine(outLine);
        onLine?.Invoke(outLine);
    }
    stderr.Wait();
    p.WaitForExit();
    return (p.ExitCode, stdout.ToString());
}

// --auto in store mode: one ensure for the store's packs (if anything changed), then this release's
// packs (RIFE) as before. The plan, total and progress lines cover both.
async Task<int> AutoStoreAsync(PackIndex index, List<string> rec, bool hasNvidia, string gpu)
{
    var installed = ReadInstalledComponents(index);
    var storeWant = StoreWanted(index);
    foreach (var p in index.Packs.Where(p => p.Store && (rec.Contains(p.Name) || autoInclude.Contains(p.Name)) && !autoExclude.Contains(p.Name)))
    {
        storeWant.Add(p.Name);
    }
    var ownNeeded = index.Packs.Where(p => !p.Store && (PreselectPack(p, installed, rec) || autoInclude.Contains(p.Name)) && !autoExclude.Contains(p.Name)).ToList();
    var ownMissing = ownNeeded
        .Where(p => !installed.TryGetValue(p.Name, out var recorded) ||
                    (p.Dep is not null && ReadManifestDep(localManifest, p.Dep) is string t && t != "" && recorded != t) ||
                    !PackContentMatches(p))
        .ToList();

    // The store's plan for this install: what's there (or adoptable from the install folder) and what
    // it would download. Up to date only when ensure --check says everything is installed.
    var plan = new List<(string Name, long Bytes, bool Download)>();
    long storeBytes = 0;
    bool storeCurrent;
    if (storeWant.Count > 0)
    {
        var (rc, lines) = StoreEnsure(index, storeWant, check: true);
        if (rc != 0)
        {
            return rc;
        }
        bool done = false;
        foreach (var l in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = l.Split(' ');
            if (f[0] == "AJI_PLAN" && f.Length == 4 && long.TryParse(f[2], out var bytes))
            {
                plan.Add((VjName(f[1].Split('/')[0]), bytes, f[3] == "download"));
            }
            else if (f[0] == "AJI_TOTAL" && f.Length == 2)
            {
                long.TryParse(f[1], out storeBytes);
            }
            else if (f[0] == "AJI_DONE")
            {
                done = true;
            }
        }
        storeCurrent = done && File.Exists(Path.Combine(inferenceDir, "aji-runtime.json")) && !PrivatePackFilesLeft(index);
    }
    else
    {
        // nothing wanted from the store: current unless this install still holds a ref there
        storeCurrent = ReadStorePacks().Count == 0;
    }
    foreach (var (name, bytes, download) in plan)
    {
        SetupLine($"SETUP_PLAN {name} {bytes} {(download ? "download" : "installed")}");
    }
    foreach (var p in ownNeeded)
    {
        SetupLine($"SETUP_PLAN {p.Name} {p.Bytes} {(ownMissing.Contains(p) ? "download" : "installed")}");
    }
    SetupLine($"SETUP_TOTAL {storeBytes + ownMissing.Sum(p => p.Bytes)}");
    if (storeCurrent && ownMissing.Count == 0)
    {
        WriteInstalledComponents(ReadInstalledComponents(index));
        Console.WriteLine("Everything the detected hardware needs is already installed.");
        await ComponentsAsync(index, false);
        SetupLine("SETUP_DONE");
        return 0;
    }
    if (autoCheckOnly)
    {
        var would = plan.Where(p => p.Download).Select(p => $"{p.Name} ({p.Bytes / 1048576} MB)")
            .Concat(ownMissing.Select(p => $"{p.Name} ({p.Bytes / 1048576} MB)")).ToList();
        Console.WriteLine($"For {(hasNvidia ? gpu : "this GPU")}, would install: " +
            (would.Count > 0 ? string.Join(", ", would) : "nothing new (the shared pack store's set changes)"));
        return 0;
    }
    Console.WriteLine($"Installing for {(hasNvidia ? gpu : "this GPU")}: " +
        string.Join(", ", storeWant.Concat(ownMissing.Select(p => p.Name))));
    if (!storeCurrent)
    {
        int rc = storeWant.Count == 0 ? StoreUnref() : StoreEnsure(index, storeWant).Item1;
        if (rc != 0)
        {
            return rc;
        }
    }
    foreach (var p in ownMissing)
    {
        int rc = await InstallComponentAsync(p.Name);
        if (rc != 0)
        {
            return rc;
        }
    }
    WriteInstalledComponents(ReadInstalledComponents(index));
    SetupLine("SETUP_DONE");
    return 0;
}

static HttpClient NewClient()
{
    var client = new HttpClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd("VideoJaNaiUpdater");
    return client;
}

record Release(string Tag, List<Asset> Assets);
// An unpacked update: the folder to copy over the install, and whether it is the overlay archive.
record Staged(string Root, bool Overlay);
record Asset(string Name, string Url);
// Dep: the manifest deps key that governs the pack's content. InstalledBytes: the pack's total size
// once extracted, to verify what is on disk really is this pack (0 for indexes that predate it).
// Store: one of the shared runtime release's packs (aji-runtime installs it into the pack store; Files
// are paths inside the pack).
record Pack(string Name, string Asset, string? Url, long Bytes, List<string> Files, string? Dep,
            long InstalledBytes = 0, string? Sha256 = null, bool Store = false);
// Replaced (store mode): this release's own packs that the store's replace, for the files an older
// version installed.
record PackIndex(string PackageVersion, List<Pack> Packs, List<Pack>? Replaced = null);
