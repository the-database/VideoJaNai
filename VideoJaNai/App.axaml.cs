using AnimeJaNaiConverterGui.Services;
using Autofac;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using ReactiveUI;
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
            // Creates and sets the Autofac resolver as the Locator.
            var autofacResolver = builder.UseAutofacDependencyResolver();

            // Register the resolver in Autofac so it can be later resolved.
            builder.RegisterInstance(autofacResolver);

            // Initialize ReactiveUI components.
            autofacResolver.InitializeReactiveUI();

            var container = builder.Build();

            autofacResolver.SetLifetimeScope(container);

            var suspension = new AutoSuspendHelper(ApplicationLifetime!);
            RxApp.SuspensionHost.CreateNewAppState = () => new MainWindowViewModel();
            RxApp.SuspensionHost.SetupDefaultSuspendResume(Program.SuspensionDriver);
            suspension.OnFrameworkInitializationCompleted();

            // Load the saved view model state.
            var state = RxApp.SuspensionHost.GetAppState<MainWindowViewModel>();

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