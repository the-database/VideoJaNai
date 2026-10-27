using System;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConverterGui.Services
{
    public interface IUpdateManagerService
    {
        bool IsInstalled { get; }
        string AppVersion { get; }
        string InstallDir { get; }
        string UpdaterPath { get; }

        // Returns the available update version string, or null if up to date / not installed.
        Task<string?> CheckForUpdateAsync();

        // Launches the bundled updater (--apply) detached; the caller exits the app so it can
        // replace files in place and relaunch.
        void ApplyUpdateAndRestart();

        // Auto update: DownloadUpdateAsync runs `--download`, which unpacks the update into the
        // install's update-staging/ and returns its version (null if none or on failure; progress
        // lines go to onLine). ApplyUpdateOnExit installs it after the app exits, with no window and
        // no relaunch. TakeLastUpdateError returns (and clears) the error of an install that failed.
        Task<string?> DownloadUpdateAsync(Action<string>? onLine, CancellationToken ct);
        void ApplyUpdateOnExit();
        string? TakeLastUpdateError();

        // Component manager: shells the bundled updater. GetComponentsJsonAsync runs
        // `--components --json`; RunUpdaterStreamingAsync runs --auto/--install/--remove with each
        // stdout/stderr line delivered to onLine, returning the exit code.
        Task<string> GetComponentsJsonAsync();
        Task<int> RunUpdaterStreamingAsync(string arguments, Action<string>? onLine, CancellationToken ct = default);
    }
}
