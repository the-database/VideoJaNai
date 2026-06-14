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
    }
}
