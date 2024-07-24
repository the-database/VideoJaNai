using Avalonia.Collections;

namespace AnimeJaNaiConverterGui.Services
{
    public interface IPythonService
    {
        bool IsPythonInstalled();
        bool AreModelsInstalled();
        string PythonDirectory { get; }
        string ModelsDirectory { get; }
        string BackendDirectory { get; }
        string FfmpegDirectory { get; }
        string FfmpegPath { get; }
        string PythonPath { get; }
        string VspipePath { get; }
        string AnimeJaNaiDirectory { get; }
        string InstallUpdatePythonDependenciesCommand { get; }
        string InstallVapourSynthPluginsCommand { get; }
        void ExtractTgz(string gzArchiveName, string destFolder);
        void ExtractZip(string archivePath, string outFolder, ProgressChanged progressChanged);
        void AddPythonPth(string destFolder);
        AvaloniaList<string> AllModels { get; }
    }
}
