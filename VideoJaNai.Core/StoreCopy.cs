using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoJaNai.Core
{
    // Which copy of VideoJaNai this is, for the pack store (aji-runtime 0.3.0+). The copy the setup
    // installer made keeps the plain ref id (videojanai) and the per-user store shared with the other
    // JaNai apps; any other Windows copy (the portable package, the command line's zip, a copy of the
    // installed folder) uses a per-copy ref (--per-copy --app-path <its exe>), and a fresh one keeps
    // TensorRT inside its own folder (InsideStore). The updater decides with these; it compiles this
    // file in (VideoJaNaiUpdater.csproj), so it stays free of the rest of Core.
    public static class StoreCopy
    {
        // Written by the setup installer (installer/videojanai.iss) before it first runs the updater.
        public const string SetupMarker = "installed-by-setup.txt";

        // A zip copy's default store, relative to aji-runtime.conf at the install root.
        public const string InsideStore = @"animejanai\runtime";

        public static string InsideStoreDir(string installDir) => Path.Combine(installDir, "animejanai", "runtime");

        // installer/videojanai.iss's AppId (Inno's "{{" escape is one brace).
        public static readonly string[] SetupAppIds = ["{B7E4D2A1-3C56-4F89-9A2B-1D3E5F7A9C04}"];

        // Whether dir is the copy the setup installer made: its uninstaller (unins000.exe, unins001.exe,
        // ...) or the marker the installer writes before it first runs the updater is there, AND the
        // installer's own record (setupLocations, SetupInstallLocations) doesn't put the installed copy in
        // another folder. Both files come along when someone copies the installed folder, so a copy
        // elsewhere on the same PC is a zip copy; with no record (a first install before Inno writes it,
        // another PC, an unreadable registry) the files decide.
        public static bool IsSetupInstall(string dir, IReadOnlyCollection<string> setupLocations)
        {
            bool setupFiles;
            try
            {
                setupFiles = File.Exists(Path.Combine(dir, SetupMarker)) ||
                             Directory.EnumerateFiles(dir, "unins*.exe").Any(f => Regex.IsMatch(Path.GetFileName(f), @"^unins\d{3}\.exe$", RegexOptions.IgnoreCase));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            return setupFiles && (setupLocations.Count == 0 || setupLocations.Any(l => SamePath(l, dir)));
        }

        // The folders the setup installer's uninstall entry names: Inno Setup writes InstallLocation
        // ({app}\) and "Inno Setup: App Path" ({app}) under HKCU\...\Uninstall\<AppId>_is1 (the installer
        // is per-user). Empty without an entry, off Windows, or on any registry error.
        // VIDEOJANAI_SETUP_LOCATION (tests) stands in for the registry: a folder, or empty for no entry.
        public static List<string> SetupInstallLocations()
        {
            var folders = new List<string>();
            if (Environment.GetEnvironmentVariable("VIDEOJANAI_SETUP_LOCATION") is { } fake)
            {
                if (fake.Trim().Length > 0)
                {
                    folders.Add(fake.Trim());
                }
                return folders;
            }
            if (!OperatingSystem.IsWindows())
            {
                return folders;
            }
            foreach (var appId in SetupAppIds)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{appId}_is1");
                    foreach (var value in new[] { "InstallLocation", "Inno Setup: App Path" })
                    {
                        if (key?.GetValue(value) is string s && s.Trim().Length > 0)
                        {
                            folders.Add(s.Trim());
                        }
                    }
                }
                catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
            }
            return folders;
        }

        public static bool SamePath(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }
            try
            {
                return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        // This copy's ref in the store: its id and its installed packs (store name -> (version, folder)).
        public sealed record StoreRef(string RefId, Dictionary<string, (string Version, string Dir)> Packs);

        // `aji-runtime show` output -> this copy's ref: only when there is a ref and it names this copy's
        // exe (show's app_path is absolute). A ref naming another path is another copy's.
        public static StoreRef? ParseOwnRef(string showJson, string appPath)
        {
            try
            {
                using var doc = JsonDocument.Parse(showJson);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object ||
                    r.TryGetProperty("has_ref", out var has) && has.ValueKind == JsonValueKind.False)
                {
                    return null;
                }
                string? refPath = r.TryGetProperty("app_path", out var ap) && ap.ValueKind == JsonValueKind.String ? ap.GetString() : null;
                if (!SamePath(refPath, appPath))
                {
                    return null;
                }
                var packs = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
                if (r.TryGetProperty("packs", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in arr.EnumerateArray())
                    {
                        if (p.TryGetProperty("installed", out var i) && i.ValueKind == JsonValueKind.True)
                        {
                            packs[p.GetProperty("name").GetString() ?? ""] =
                                (p.GetProperty("version").GetString() ?? "", p.GetProperty("dir").GetString() ?? "");
                        }
                    }
                }
                string id = r.TryGetProperty("ref_id", out var rid) && rid.ValueKind == JsonValueKind.String ? rid.GetString() ?? "" : "";
                return new StoreRef(id, packs);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                return null;
            }
        }

        // `aji-runtime location --json`. Folder: where the store's files are (a moved per-user store's
        // target). Source (0.3.0+): default, moved, settings (aji-runtime.conf) or env (AJI_RUNTIME_STORE);
        // null from an older aji-runtime.
        public sealed record Location(string Root, bool Moved, string? Target, bool Available, string? Source,
                                      string? Setting, bool Relative, string? SettingsFile, string? DefaultRoot)
        {
            public string Folder => Moved && Target is { Length: > 0 } t ? t : Root;
        }

        public static Location? ParseLocation(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                string? Str(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool Flag(string key, bool fallback) => r.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean() : fallback;
                if (Str("root") is not { Length: > 0 } root)
                {
                    return null;
                }
                return new Location(root, Flag("moved", false), Str("target"), Flag("available", true), Str("source"),
                    Str("setting"), Flag("relative", false), Str("settings_file"), Str("default_root"));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Which of App Settings > Storage's choices a store is: inside this copy, the per-user store
        // shared with the other apps, or another folder.
        public static string Choice(Location location, string insideStoreDir) =>
            SamePath(location.Root, insideStoreDir) ? "inside" :
            SamePath(location.Root, location.DefaultRoot) ? "shared" : "custom";
    }
}
