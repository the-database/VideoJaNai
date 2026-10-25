using Avalonia;
using ReactiveUI.Avalonia;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoJaNai
{
    internal class Program
    {
        public static bool WasFirstRun { get; private set; }
        // VIDEOJANAI_STATE_DIR redirects the saved app state (workflows, settings), so test and dev
        // launches never touch a real install's state.
        public static readonly string AppStateFolder =
            Environment.GetEnvironmentVariable("VIDEOJANAI_STATE_DIR") is { Length: > 0 } stateDir
                ? stateDir
                // Create: without it GetFolderPath returns "" when the folder doesn't exist yet
                // (~/.config on a fresh Linux account), making this a relative "VideoJaNai" that
                // collides with the executable of the same name.
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                                                         Environment.SpecialFolderOption.Create), "VideoJaNai");
        public static readonly string AppStateFilename = "appstate2.json";
        public static readonly string AppStatePath = Path.Combine(AppStateFolder, AppStateFilename);

        public static readonly ISuspensionDriver SuspensionDriver = new NewtonsoftJsonSuspensionDriver(AppStatePath);

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // Velopack removed: install/uninstall/update are now handled by the Inno installer +
            // VideoJaNaiUpdater. Uninstall cleanup is the installer's [UninstallDelete].
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