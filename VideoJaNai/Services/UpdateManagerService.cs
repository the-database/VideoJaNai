using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConverterGui.Services
{
    // Replaces Velopack: drives the bundled VideoJaNaiUpdater.exe (the Inno-installer
    // distribution model). The updater lives at the install root next to VideoJaNai.exe.
    public class UpdateManagerService : IUpdateManagerService
    {
        public string InstallDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, '/');

        public string UpdaterPath => Path.Combine(InstallDir, PythonService.Exe("VideoJaNaiUpdater"));

        // "Installed" = running from an Inno install (the updater ships next to the app).
        public bool IsInstalled => File.Exists(UpdaterPath);

        public string AppVersion
        {
            get
            {
                var versionFile = Path.Combine(InstallDir, "version.txt");
                if (File.Exists(versionFile))
                {
                    return File.ReadAllText(versionFile).Trim();
                }
                return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "";
            }
        }

        public async Task<string?> CheckForUpdateAsync()
        {
            if (!IsInstalled)
            {
                return null;
            }
            var output = await RunUpdaterAsync("--check");
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                const string marker = "UPDATE_AVAILABLE ";
                if (trimmed.StartsWith(marker, StringComparison.Ordinal))
                {
                    return trimmed.Substring(marker.Length).Trim();
                }
            }
            return null;
        }

        public void ApplyUpdateAndRestart()
        {
            if (!IsInstalled)
            {
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = UpdaterPath,
                Arguments = "--apply",
                // Windows: shell-exec gives the updater its own console window. On Linux shell-exec
                // would hand the binary to xdg-open.
                UseShellExecute = OperatingSystem.IsWindows(),
                WorkingDirectory = InstallDir,
            });
        }

        public async Task<string?> DownloadUpdateAsync(Action<string>? onLine, CancellationToken ct)
        {
            string? ready = null;
            const string marker = "UPDATE_READY ";
            int rc = await RunUpdaterStreamingAsync("--download", line =>
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(marker, StringComparison.Ordinal))
                {
                    ready = trimmed.Substring(marker.Length).Trim();
                }
                else
                {
                    onLine?.Invoke(trimmed);
                }
            }, ct);
            return rc == 0 ? ready : null;
        }

        public void ApplyUpdateOnExit()
        {
            if (!IsInstalled)
            {
                return;
            }
            // No window: the user has closed the app, and the next launch shows any failure.
            Process.Start(new ProcessStartInfo
            {
                FileName = UpdaterPath,
                Arguments = "--apply --no-relaunch",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = InstallDir,
            });
        }

        public string? TakeLastUpdateError()
        {
            var path = Path.Combine(InstallDir, "update-error.txt");
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                var error = File.ReadAllText(path).Trim();
                File.Delete(path);
                return error;
            }
            catch
            {
                return null;
            }
        }

        public async Task<string> GetComponentsJsonAsync()
        {
            return IsInstalled ? await RunUpdaterAsync("--components --json") : "";
        }

        public async Task<int> RunUpdaterStreamingAsync(string arguments, Action<string>? onLine, CancellationToken ct = default)
        {
            if (!IsInstalled)
            {
                return -1;
            }
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = UpdaterPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = InstallDir,
                }
            };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { /* already exited */ }
                throw;
            }
            return process.ExitCode;
        }

        private async Task<string> RunUpdaterAsync(string arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = UpdaterPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = InstallDir,
                }
            };
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output;
        }
    }
}
