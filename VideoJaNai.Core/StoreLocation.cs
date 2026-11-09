using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VideoJaNai.Core
{
    // Where the shared pack store is (the TensorRT runtime, its GPU builder packs and TensorRT-RTX's
    // runtime, shared by every JaNai app), through aji-runtime at the install root: `location --json`,
    // `move`, `move --default`, `reset` (the-database/aji-runtime 0.2.0). A moved store leaves a link
    // at the default path, so aji-runtime.json's pack paths and the other apps keep working. While the
    // moved folder is missing (an unplugged drive) nothing in the store loads, so a run stops early
    // with MissingText instead of a missing-runtime error.
    public static class StoreLocation
    {
        public sealed record Info(string Root, bool Moved, string? Target, bool Available)
        {
            // The folder the store's files are in.
            public string Folder => Moved && Target is not null ? Target : Root;

            // Moved to a folder that isn't there.
            public bool Missing => Moved && !Available;
        }

        // aji-runtime(.exe) in a folder (the install root; the command line's own folder).
        public static string ExePath(string folder) => Path.Join(folder, Platform.Exe("aji-runtime"));

        // The first half of what a run says while the store's folder is missing; the GUI and the
        // command line each add how to fix it.
        public static string MissingText(string folder) =>
            $"The shared runtime's folder {folder} isn't available. Is its drive connected?";

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

        // The folder the store was moved to while it's missing, else null.
        public static async Task<string?> MissingFolderAsync(string ajiRuntime) =>
            await GetAsync(ajiRuntime) is { Missing: true } info ? info.Folder : null;

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
                var parts = line.Split(' ');
                if (parts.Length == 3 && parts[0] == "AJI_MOVE" && long.TryParse(parts[1], out var done) && long.TryParse(parts[2], out var total))
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
