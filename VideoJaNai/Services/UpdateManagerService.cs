using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace AnimeJaNaiConverterGui.Services
{
    // Replaces Velopack: drives the bundled VideoJaNaiUpdater.exe (the Inno-installer
    // distribution model). The updater lives at the install root next to VideoJaNai.exe.
    public class UpdateManagerService : IUpdateManagerService
    {
        public string InstallDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, '/');

        public string UpdaterPath => Path.Combine(InstallDir, "VideoJaNaiUpdater.exe");

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
                UseShellExecute = true,
                WorkingDirectory = InstallDir,
            });
        }

        public async Task<string> GetComponentsJsonAsync()
        {
            return IsInstalled ? await RunUpdaterAsync("--components --json") : "";
        }

        public async Task<int> RunUpdaterStreamingAsync(string arguments, Action<string>? onLine)
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
            await process.WaitForExitAsync();
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
