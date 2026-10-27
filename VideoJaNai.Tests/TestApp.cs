using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using VideoJaNai.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace VideoJaNai.Tests
{
    // A bare application for headless UI tests: the real App loads the user's saved state.
    public class TestApp : Application
    {
        // Controls need a theme for their templates (an ItemsControl renders no items without one).
        public override void Initialize() => Styles.Add(new FluentTheme());

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
