using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace AnimeJaNaiConverterGui.Services
{
    public class UpdateManagerService : IUpdateManagerService
    {
        private readonly UpdateManager _um;

        public UpdateManagerService()
        {
            _um = new UpdateManager(new GithubSource("https://github.com/the-database/VideoJaNai", null, false));
        }

        public string AppVersion { get => _um?.CurrentVersion?.ToString() ?? ""; }

        public bool IsInstalled { get => _um.IsInstalled; }

        public bool IsUpdatePendingRestart { get => _um.IsUpdatePendingRestart; }

        public void ApplyUpdatesAndRestart(UpdateInfo update)
        {
            _um.ApplyUpdatesAndRestart(update);
        }

        public async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            return await _um.CheckForUpdatesAsync();
        }

        public Task DownloadUpdatesAsync(UpdateInfo update, Action<int>? progress = null)
        {
            return _um.DownloadUpdatesAsync(update, progress);
        }
    }
}
