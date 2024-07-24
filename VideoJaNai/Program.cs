using Avalonia;
using Avalonia.ReactiveUI;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Velopack;

namespace VideoJaNai
{
    internal class Program
    {
        public static bool WasFirstRun { get; private set; }
        public static readonly string AppStateFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoJaNai"
        );
        public static readonly string AppStateFilename = "appstate2.json";
        public static readonly string AppStatePath = Path.Combine(AppStateFolder, AppStateFilename);

        public static readonly ISuspensionDriver SuspensionDriver = new NewtonsoftJsonSuspensionDriver(AppStatePath);

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            VelopackApp.Build()
                .WithBeforeUninstallFastCallback((v) =>
                {
                    // On uninstall, delete backend directories
                    List<string> dirNames = new() { "python", "animejanai", "ffmpeg" };
                    var dirs = dirNames.Select(name => Path.Combine(AppStateFolder, name));

                    foreach (var dir in dirs)
                    {
                        if (Directory.Exists(dir))
                        {
                            Directory.Delete(dir, true);
                        }
                    }
                })
                .WithFirstRun(_ =>
                {
                    WasFirstRun = true;
                })
                .Run();

            BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .UseReactiveUI();
    }
}