using AnimeJaNaiConverterGui.Services;
using Autofac;
using Avalonia;
using Avalonia.Markup.Xaml;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Builder;
using Splat;
using Splat.Autofac;
using System.IO;
using VideoJaNai.ViewModels;
using VideoJaNai.Views;
namespace VideoJaNai
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (!Directory.Exists(Program.AppStateFolder))
            {
                Directory.CreateDirectory(Program.AppStateFolder);
            }

            if (!File.Exists(Program.AppStatePath))
            {
                // The default state ships next to the app; the working directory is wherever the app
                // was launched from (the AppImage's AppRun runs it from the data folder).
                File.Copy(Path.Combine(System.AppContext.BaseDirectory, Program.AppStateFilename),
                          Program.AppStatePath);
            }

            // Create a new Autofac container builder.
            var builder = new ContainerBuilder();
            builder.RegisterType<MainWindowViewModel>().AsSelf();
            builder.RegisterType<PythonService>().As<IPythonService>().SingleInstance();
            builder.RegisterType<UpdateManagerService>().As<IUpdateManagerService>().SingleInstance();
            // etc.

            // Register the Adapter to Splat.
            // Creates and sets the Autofac resolver as the Locator (replaces AppLocator.Current).
            var autofacResolver = builder.UseAutofacDependencyResolver();

            // Register the resolver in Autofac so it can be later resolved.
            builder.RegisterInstance(autofacResolver);

            // Initialize ReactiveUI into the Autofac-backed resolver. This replaces the old
            // autofacResolver.InitializeReactiveUI() extension, which no longer exists in
            // ReactiveUI 23 / Splat.Autofac 20 (since ReactiveUI 26 the builder is an extension on the
            // resolver). (The suspension host type is configured earlier, in
            // Program.BuildAvaloniaApp's UseReactiveUI callback; that RxSuspension singleton is
            // already initialized by the time this runs.)
            //
            // Statement form (not a fluent chain) avoids the builder's mixed interface/base return
            // types: WithCoreServices() returns the base IAppBuilder, which doesn't expose the full
            // builder method set.
            var rxuiBuilder = autofacResolver.CreateReactiveUIBuilder();
            rxuiBuilder.WithPlatformServices();
            rxuiBuilder.WithAvalonia();
            rxuiBuilder.WithCoreServices();
            rxuiBuilder.Build();

            var container = builder.Build();

            autofacResolver.SetLifetimeScope(container);

            var suspension = new AutoSuspendHelper(ApplicationLifetime!);
            // Used when the saved state can't be loaded: start from the bundled defaults (an empty view
            // model has no workflows, which the code below can't run with).
            RxSuspension.SuspensionHost.CreateNewAppState = () =>
                NewtonsoftJsonSuspensionDriver.LoadDefaultState() ?? new MainWindowViewModel();
            RxSuspension.SuspensionHost.SetupDefaultSuspendResume(Program.SuspensionDriver);
            suspension.OnFrameworkInitializationCompleted();

            // Load the saved view model state.
            var state = RxSuspension.SuspensionHost.GetAppState<MainWindowViewModel>();

            foreach (var wf in state.Workflows)
            {
                wf.Vm = state;
                // Migrate engine settings saved by older versions: drop the TensorRT 11 dead flags
                // (no-ops the native engine strips anyway) so the editor doesn't show stale cruft.
                wf.TrtEngineSettings = UpscaleWorkflow.NormalizeTrtEngineSettings(wf.TrtEngineSettings);
            }

            state.MigrateBackend();
            state.CurrentWorkflow?.Validate();

            new MainWindow { DataContext = state }.Show();
            NewtonsoftJsonSuspensionDriver.StartupComplete = true;
            base.OnFrameworkInitializationCompleted();
        }
    }
}