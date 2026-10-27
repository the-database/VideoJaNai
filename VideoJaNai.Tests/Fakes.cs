using AnimeJaNaiConverterGui.Services;
using Avalonia.Collections;
using System;
using System.Threading.Tasks;

namespace VideoJaNai.Tests
{
    class FakePythonService : IPythonService
    {
        public bool IsInferenceInstalled() => true;
        public bool IsTensorRtInstalled() => true;
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
        public Task<string> GetComponentsJsonAsync() => Task.FromResult("[]");
        public Task<int> RunUpdaterStreamingAsync(string arguments, Action<string>? onLine) => Task.FromResult(0);
    }
}
