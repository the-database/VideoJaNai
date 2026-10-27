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
//   VideoJaNaiUpdater.exe --recommend    KEY=value hardware summary for the Inno installer
//
// Update size is tiered: if the latest release's heavy deps match what's installed (compared via
// manifest.json) only the small overlay archive is fetched; otherwise the full package.
//
// Component packs are subsets of the install (TensorRT runtime, per-GPU-generation builder
// resources, RIFE models), emitted by the package builder as component-<name>.7z + packs.json
// release assets. Archives are rooted at the install dir, so extraction is installation;
// packs.json carries each pack's file list, so removal is deletion. Installed state lives in
// components.json at the install root (inferred from disk for installs that predate it).
// Dev override: set VIDEOJANAI_PACKS_DIR to a local directory with packs.json + archives.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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

// NVML ships in the NVIDIA driver as nvml.dll on Windows and libnvidia-ml.so.1 on Linux; remap the
// DllImport library name on non-Windows so DetectGpu works without per-OS P/Invoke declarations.
if (!OperatingSystem.IsWindows())
{
    NativeLibrary.SetDllImportResolver(typeof(Nvml).Assembly, (name, asm, search) =>
        name == "nvml.dll" ? NativeLibrary.Load("libnvidia-ml.so.1", asm, search)
                           : IntPtr.Zero);
}

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--check";

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

async Task DownloadWithProgress(Asset asset, string dest)
{
    double last = -10;
    Console.WriteLine($"Downloading {asset.Name}...");
    await DownloadFileAsync(asset.Url, dest, p =>
    {
        if (p >= last + 5)
        {
            Console.WriteLine($"  {asset.Name}: {p}%");
            last = p;
        }
    });
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

async Task<PackIndex> GetPackIndexAsync()
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
            e.TryGetProperty("installed_bytes", out var ib) ? ib.GetInt64() : 0));
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

// Installed state: components.json, else inferred from what's on disk so
// full installs that predate the manager work out of the box.
Dictionary<string, string> ReadInstalledComponents(PackIndex index)
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
    foreach (var pack in index.Packs)
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

void WriteInstalledComponents(Dictionary<string, string> installed)
{
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

// NVIDIA detection via NVML (ships with the driver); its absence means a non-NVIDIA GPU.
// Only the TensorRT engine needs downloadable component packs, and they apply to NVIDIA GPUs
// only; non-NVIDIA GPUs use the built-in DirectML engine, which ships in the base install.
//
// Every NVIDIA GPU is reported (Sm "sm89, sm120", names joined with " + "): NVML enumerates in PCI
// order while CUDA picks the fastest GPU first, so recommending only NVML's device 0 left the GPU
// TensorRT actually builds on without its builder kernels on multi-GPU systems.
static (bool HasNvidia, string Sm, string GpuName) DetectGpu()
{
    try
    {
        if (Nvml.nvmlInit_v2() != 0)
        {
            return (false, "", "");
        }
        try
        {
            if (Nvml.nvmlDeviceGetCount_v2(out uint count) != 0 || count == 0)
            {
                return (false, "", "");
            }
            var sms = new List<string>();
            var names = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                if (Nvml.nvmlDeviceGetHandleByIndex_v2(i, out var dev) != 0)
                {
                    continue;
                }
                Nvml.nvmlDeviceGetCudaComputeCapability(dev, out int major, out int minor);
                var name = new byte[96];
                Nvml.nvmlDeviceGetName(dev, name, (uint)name.Length);
                names.Add(System.Text.Encoding.ASCII.GetString(name).TrimEnd('\0'));
                var sm = $"sm{major}{minor}";
                if (!sms.Contains(sm))
                {
                    sms.Add(sm);
                }
            }
            return sms.Count == 0
                ? (false, "", "")
                : (true, string.Join(", ", sms), string.Join(" + ", names));
        }
        finally { Nvml.nvmlShutdown(); }
    }
    catch
    {
        return (false, "", "");
    }
}

// Hardware-matched packs only. RIFE is a user choice, not a recommendation: it is preselected on
// installs that have never managed components, and once the user has made any component decision
// their choice stands.
List<string> RecommendedPacks(PackIndex index, bool hasNvidia, string sm)
{
    var rec = new List<string>();
    if (hasNvidia)
    {
        rec.Add("trt-runtime");
        // per GPU generation: the exact pack if published, else the PTX fallback pack
        foreach (var s in sm.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var pack = index.Packs.Any(p => p.Name == $"trt-{s}") ? $"trt-{s}" : "trt-ptx";
            if (!rec.Contains(pack))
            {
                rec.Add(pack);
            }
        }
    }
    return rec;
}

bool ComponentsNeverManaged() => !File.Exists(Path.Combine(installDir, "components.json"));

bool PreselectPack(Pack pack, Dictionary<string, string> installed, List<string> rec) =>
    installed.ContainsKey(pack.Name) || rec.Contains(pack.Name) ||
    (pack.Name == "rife" && ComponentsNeverManaged());

async Task ComponentsAsync(PackIndex? prefetched, bool json = false)
{
    var index = prefetched ?? await GetPackIndexAsync();
    var installed = ReadInstalledComponents(index);
    var (hasNvidia, sm, gpu) = DetectGpu();
    var rec = RecommendedPacks(index, hasNvidia, sm);
    if (json)
    {
        // consumed by VideoJaNai's in-app component manager; keep keys stable
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            package_version = index.PackageVersion,
            version_mismatch = PackVersionMismatch(index),
            gpu = new { nvidia = hasNvidia, sm, name = gpu },
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
        ? $"GPU: {gpu} ({sm}) - TensorRT recommended"
        : "GPU: no NVIDIA device detected - using the built-in DirectML engine (no extra components needed)");
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
        await DownloadWithProgress(new Asset(pack.Asset, pack.Url), archive);
    }
    else
    {
        File.Copy(pack.Url, archive, true); // VIDEOJANAI_PACKS_DIR dev path
    }
    Console.WriteLine($"Installing {pack.Name}...");
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
    var (hasNvidia, sm, gpu) = DetectGpu();
    var rec = RecommendedPacks(index, hasNvidia, sm);
    var installed = ReadInstalledComponents(index);
    // "Needs installing" is not just "absent": a pack already recorded as installed is stale when the
    // dep that governs it moved (a TensorRT/RIFE bump) or when its files on disk are not this pack's
    // content. Without this, --auto reports "everything is installed" after an upgrade and the old
    // runtime is kept forever; InstallComponentAsync re-checks and still skips genuine no-ops.
    var missing = index.Packs
        .Where(p => PreselectPack(p, installed, rec))
        .Where(p => !installed.TryGetValue(p.Name, out var recorded) ||
                    (p.Dep is not null && ReadManifestDep(localManifest, p.Dep) is string t &&
                     t != "" && recorded != t) ||
                    !PackContentMatches(p))
        .Select(p => p.Name).ToList();
    if (missing.Count == 0)
    {
        Console.WriteLine("Everything the detected hardware needs is already installed.");
        await ComponentsAsync(index, false);
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
    return 0;
}

// Hardware summary for the Inno installer's component picker, as trivially parseable KEY=value
// lines. GPU identity is local NVML and always available; TRT_PACKS needs the pack index (network
// or VIDEOJANAI_PACKS_DIR) - if that fails it is emitted empty and the installer falls back to a
// generic TensorRT toggle.
async Task<int> RecommendAsync()
{
    var (hasNvidia, sm, gpu) = DetectGpu();
    Console.WriteLine($"NVIDIA={(hasNvidia ? 1 : 0)}");
    Console.WriteLine($"GPU={gpu}");
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

static HttpClient NewClient()
{
    var client = new HttpClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd("VideoJaNaiUpdater");
    return client;
}

static class Nvml
{
    [System.Runtime.InteropServices.DllImport("nvml.dll")]
    public static extern int nvmlInit_v2();
    [System.Runtime.InteropServices.DllImport("nvml.dll")]
    public static extern int nvmlShutdown();
    [System.Runtime.InteropServices.DllImport("nvml.dll")]
    public static extern int nvmlDeviceGetCount_v2(out uint deviceCount);
    [System.Runtime.InteropServices.DllImport("nvml.dll")]
    public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [System.Runtime.InteropServices.DllImport("nvml.dll")]
    public static extern int nvmlDeviceGetCudaComputeCapability(IntPtr device, out int major, out int minor);
    [System.Runtime.InteropServices.DllImport("nvml.dll")]
    public static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
}

record Release(string Tag, List<Asset> Assets);
// An unpacked update: the folder to copy over the install, and whether it is the overlay archive.
record Staged(string Root, bool Overlay);
record Asset(string Name, string Url);
// Dep: the manifest deps key that governs the pack's content. InstalledBytes: the pack's total size
// once extracted, to verify what is on disk really is this pack (0 for indexes that predate it).
record Pack(string Name, string Asset, string? Url, long Bytes, List<string> Files, string? Dep,
            long InstalledBytes = 0);
record PackIndex(string PackageVersion, List<Pack> Packs);
