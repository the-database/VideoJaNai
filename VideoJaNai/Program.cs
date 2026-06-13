using Avalonia;
using ReactiveUI.Avalonia;
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
                .OnBeforeUninstallFastCallback((v) =>
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
                .OnFirstRun(_ =>
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
                // UseReactiveUI runs WithAvalonia() (which configures a SuspensionHost<Unit>) and then
                // invokes this callback before committing the suspension host singleton. We override it
                // with a plain non-generic SuspensionHost (AppState starts null, like the old
                // RxApp.SuspensionHost) so the app state can be loaded/typed as MainWindowViewModel.
                // Otherwise GetAppState<MainWindowViewModel>() in App throws InvalidCastException on the
                // seeded Unit state. This singleton is initialized once, so it must be set here (the
                // earliest init), not in App.OnFrameworkInitializationCompleted.
                .UseReactiveUI(b => b.WithSuspensionHost());
    }
}