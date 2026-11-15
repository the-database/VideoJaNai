using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VideoJaNai.Core
{
    // Where this copy keeps the TensorRT runtime, its GPU builder packs and TensorRT-RTX's runtime (its
    // pack store), and moving it.
    //
    // The choice (the updater's `--storage --json` / `--storage inside|shared|<folder>`, aji-runtime
    // 0.3.0): inside this copy's folder (a portable copy's default), the per-user store every JaNai app
    // shares (the installed copy's default), or a folder the user picks (apps set to the same folder
    // share it). The updater moves this copy's packs when the choice changes.
    //
    // The shared store itself moves through aji-runtime at the install root: `location --json`, `move`,
    // `move --default`, `reset` (the-database/aji-runtime 0.2.0). A moved store leaves a link at the
    // default path, so aji-runtime.json's pack paths and the other apps keep working. While the store's
    // folder is missing (an unplugged drive) nothing in it loads, so a run stops early with MissingText
    // instead of a missing-runtime error.
    public static class StoreLocation
    {
        public enum StoreChoice { Inside, Shared, Custom }

        // Root: the store folder this copy uses. Moved/Target: the shared store was moved and links to
        // Target. Available: false while the folder's drive isn't connected.
        public sealed record Info(string Root, bool Moved, string? Target, bool Available)
        {
            // null: the updater can't say (one from before the choice); the store is the shared one.
            public StoreChoice? Choice { get; init; }

            // The folders "inside" and "shared" mean for this copy (only with Choice).
            public string? Inside { get; init; }
            public string? Shared { get; init; }

            // This copy has TensorRT in its store (only with Choice).
            public bool Installed { get; init; }

            // The size of this copy's TensorRT packs in its store, which is what a switch moves (only
            // with Choice; null when the updater doesn't say).
            public long? Bytes { get; init; }

            public bool IsShared => Choice is null or StoreChoice.Shared;

            // The folder the store's files are in.
            public string Folder => IsShared && Moved && Target is not null ? Target : Root;

            // Where the shared store's files are, whichever store this copy uses.
            public string SharedFolder => Moved && Target is not null ? Target : Shared ?? (IsShared ? Root : "");

            // The shared store was moved, and the folder is there: Move Back to Default.
            public bool MovedHere => IsShared && Moved && Available;

            // The folder isn't there (an unplugged drive).
            public bool Missing => !Available;
        }

        // aji-runtime(.exe) in a folder (the install root; the command line's own folder).
        public static string ExePath(string folder) => Path.Join(folder, Platform.Exe("aji-runtime"));

        // The first half of what a run says while the store's folder is missing; the GUI and the
        // command line each add how to fix it.
        public static string MissingText(string folder) =>
            $"The TensorRT runtime's folder {folder} isn't available. Is its drive connected?";

        // Whether App Settings > Storage shows: this copy uses a store that could be read, and TensorRT
        // is, or can be, installed here (it is, or the PC has an NVIDIA GPU that can run it; nvidia is
        // null until the updater has said).
        public static bool Shows(Info? storage, bool hasTrt, bool? nvidia) =>
            storage is not null && (hasTrt || nvidia != false);

        // `VideoJaNaiUpdater --storage --json`: null unless it names a choice and a folder.
        public static Info? ParseChoice(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var o = doc.RootElement;
                if (o.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                string? Str(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool Bool(string k, bool fallback) => o.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
                StoreChoice? choice = Str("choice") switch
                {
                    "inside" => StoreChoice.Inside,
                    "shared" => StoreChoice.Shared,
                    "custom" => StoreChoice.Custom,
                    _ => null,
                };
                if (choice is null || Str("root") is not { Length: > 0 } root)
                {
                    return null;
                }
                return new Info(root, Bool("moved", false), Str("target"), Bool("available", true))
                {
                    Choice = choice,
                    Inside = Str("inside"),
                    Shared = Str("shared"),
                    Installed = Bool("installed", false),
                    Bytes = o.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var n) && n >= 0 ? n : null,
                };
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        // A folder that already is a store (another app keeps its packs there).
        public static bool IsStore(string folder) => File.Exists(Path.Join(folder, "store.json"));

        // "A folder I choose": a store is used as it is (that is how two apps share one), as is a new or
        // empty folder; any other folder gets one of its own inside (which may be the store another app
        // made there the same way).
        public static string ChoiceTarget(string folder) =>
            IsStore(folder) ? folder : MoveTarget(folder);

        // The updater's argument for switching to folder: inside or shared when it is one of those.
        public static string ChoiceArgument(Info storage, string folder) =>
            StoreCopy.SamePath(folder, storage.Inside) ? "inside"
            : StoreCopy.SamePath(folder, storage.Shared) || StoreCopy.SamePath(folder, storage.SharedFolder) ? "shared"
            : Path.GetFullPath(folder);

        // (copied, total) bytes from an AJI_MOVE line.
        public static bool TryProgress(string line, out long done, out long total)
        {
            done = total = 0;
            var parts = line.Trim().Split(' ');
            return parts.Length == 3 && parts[0] == "AJI_MOVE" && long.TryParse(parts[1], out done) && long.TryParse(parts[2], out total);
        }

        public static Info? Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var o = doc.RootElement;
                string? Str(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool? Bool(string k) => o.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
                return new Info(Str("root") ?? "", Bool("moved") ?? false, Str("target"), Bool("available") ?? true);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        // `aji-runtime location --json`: null without that aji-runtime, or one older than 0.2.0.
        public static async Task<Info?> GetAsync(string ajiRuntime)
        {
            if (!File.Exists(ajiRuntime))
            {
                return null;
            }
            try
            {
                var (rc, stdout, _) = await RunAsync(ajiRuntime, ["location", "--json"], null);
                return rc == 0 ? Parse(stdout) : null;
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return null;
            }
        }

        // The store while its folder is missing, else null.
        public static async Task<Info?> MissingAsync(string ajiRuntime) =>
            await GetAsync(ajiRuntime) is { Missing: true } info ? info : null;

        // The store's folder while it's missing, else null.
        public static async Task<string?> MissingFolderAsync(string ajiRuntime) =>
            (await MissingAsync(ajiRuntime))?.Folder;

        // Moving into folder: aji-runtime needs a new or empty folder, so a folder with files in it
        // gets one of its own inside.
        public static string MoveTarget(string folder)
        {
            var empty = !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();
            return empty ? folder : Path.Join(folder, "JaNai shared runtime");
        }

        // The bytes in the store's folder, for a confirmation (0 when it can't be read).
        public static long Size(string folder)
        {
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                return new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(f => f.Length);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        // `move <folder>`, `move --default` or `reset`, with progress: (copied, total) bytes from the
        // AJI_MOVE lines. Null on success, else aji-runtime's error.
        public static async Task<string?> ChangeAsync(string ajiRuntime, string[] args, Action<long, long>? progress)
        {
            var (rc, _, stderr) = await RunAsync(ajiRuntime, [.. args, "--progress", "lines"], line =>
            {
                if (TryProgress(line, out var done, out var total))
                {
                    progress?.Invoke(done, total);
                }
            });
            if (rc == 0)
            {
                return null;
            }
            var error = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("error: ", StringComparison.Ordinal));
            return error?["error: ".Length..] ?? $"aji-runtime exited with {rc}";
        }

        private static async Task<(int, string, string)> RunAsync(string ajiRuntime, string[] args, Action<string>? onLine)
        {
            var psi = new ProcessStartInfo(ajiRuntime)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var p = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }
                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }
                onLine?.Invoke(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (stderr)
                    {
                        stderr.AppendLine(e.Data);
                    }
                }
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            return (p.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }
}
