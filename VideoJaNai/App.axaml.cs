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
                File.Copy(Program.AppStateFilename, Program.AppStatePath);
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
            // ReactiveUI 23 / Splat.Autofac 20. (The suspension host type is configured earlier, in
            // Program.BuildAvaloniaApp's UseReactiveUI callback; that RxSuspension singleton is
            // already initialized by the time this runs.)
            //
            // Statement form (not a fluent chain) avoids the builder's mixed interface/base return
            // types: WithCoreServices() returns the base IAppBuilder, which doesn't expose the full
            // builder method set.
            var rxuiBuilder = RxAppBuilder.CreateReactiveUIBuilder(autofacResolver);
            rxuiBuilder.WithPlatformServices();
            rxuiBuilder.WithAvalonia();
            rxuiBuilder.WithCoreServices();
            rxuiBuilder.Build();

            var container = builder.Build();

            autofacResolver.SetLifetimeScope(container);

            var suspension = new AutoSuspendHelper(ApplicationLifetime!);
            RxSuspension.SuspensionHost.CreateNewAppState = () => new MainWindowViewModel();
            RxSuspension.SuspensionHost.SetupDefaultSuspendResume(Program.SuspensionDriver);
            suspension.OnFrameworkInitializationCompleted();

            // Load the saved view model state.
            var state = RxSuspension.SuspensionHost.GetAppState<MainWindowViewModel>();

            foreach (var wf in state.Workflows)
            {
                wf.Vm = state;
            }

            state.CurrentWorkflow?.Validate();

            new MainWindow { DataContext = state }.Show();
            base.OnFrameworkInitializationCompleted();
        }
    }
}