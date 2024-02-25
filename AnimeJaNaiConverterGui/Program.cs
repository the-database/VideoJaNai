using Avalonia;
using Avalonia.ReactiveUI;
using Salaros.Configuration.Logging;
using System;
using Velopack;
using Microsoft.Extensions.Logging;

namespace AnimeJaNaiConverterGui
{
    internal class Program
    {
        public static ILogger Log { get; private set; }

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            VelopackApp.Build().Run(Log);

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