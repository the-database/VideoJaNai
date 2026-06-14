using Avalonia.Collections;

namespace AnimeJaNaiConverterGui.Services
{
    public interface IPythonService
    {
        bool IsInferenceInstalled();
        bool AreModelsInstalled();
        bool IsFfmpegInstalled();
        string ModelsDirectory { get; }
        string BackendDirectory { get; }
        string FfmpegDirectory { get; }
        string FfmpegPath { get; }
        string AnimeJaNaiDirectory { get; }

        // libaji native engine paths.
        string InferenceDirectory { get; }
        string RifeModelsDirectory { get; }
        string AjiEncodePath { get; }
        string TrtexecPath { get; }
        string ConfPath { get; }

        void ExtractZip(string archivePath, string outFolder, ProgressChanged progressChanged);
        AvaloniaList<string> AllModels { get; }
    }
}
