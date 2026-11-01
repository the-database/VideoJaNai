using AnimeJaNaiConverterGui.Services;
using Avalonia.Collections;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VideoJaNai.Tests
{
    class FakePythonService : IPythonService
    {
        // Whether the trt-rtx component is installed, per test (the fake is one shared instance and
        // test classes run in parallel): installed unless a test scopes it out with WithoutTensorRtRtx.
        static readonly AsyncLocal<bool?> _tensorRtRtxInstalled = new();

        public static IDisposable WithoutTensorRtRtx()
        {
            _tensorRtRtxInstalled.Value = false;
            return new Restore();
        }

        sealed class Restore : IDisposable
        {
            public void Dispose() => _tensorRtRtxInstalled.Value = null;
        }

        public bool IsInferenceInstalled() => true;
        public bool IsTensorRtInstalled() => true;
        public bool IsTensorRtRtxInstalled() => _tensorRtRtxInstalled.Value ?? true;
        public bool AreModelsInstalled() => true;
        public bool IsFfmpegInstalled() => true;
        public string ModelsDirectory => "";
        public string BackendDirectory => "";
        public string FfmpegDirectory => "";
        public string FfmpegPath => "";
        public string AnimeJaNaiDirectory => "";
        public string InferenceDirectory => "";
        public string RifeModelsDirectory => "";
        public string AjiEncodePath => "";
        public string TrtexecPath => "";
        public string ConfPath => "";
        public void ExtractZip(string archivePath, string outFolder, ProgressChanged progressChanged) { }
        public AvaloniaList<string> AllModels { get; } = [];
    }

    class FakeUpdateManagerService : IUpdateManagerService
    {
        public bool IsInstalled => false;
        public string AppVersion => "0.0.0";
        public string InstallDir => "";
        public string UpdaterPath => "";
        public Task<string?> CheckForUpdateAsync() => Task.FromResult<string?>(null);
        public void ApplyUpdateAndRestart() { }
        public Task<string?> DownloadUpdateAsync(Action<string>? onLine, CancellationToken ct) => Task.FromResult<string?>(null);
        public void ApplyUpdateOnExit() { }
        public string? TakeLastUpdateError() => null;
        public Task<string> GetComponentsJsonAsync() => Task.FromResult("[]");
        public Task<int> RunUpdaterStreamingAsync(string arguments, Action<string>? onLine, CancellationToken ct = default) => Task.FromResult(0);
    }
}
