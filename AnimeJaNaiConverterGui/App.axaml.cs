using AnimeJaNaiConverterGui.ViewModels;
using AnimeJaNaiConverterGui.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using NuGet.Versioning;
using ReactiveUI;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AnimeJaNaiConverterGui
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var suspension = new AutoSuspendHelper(ApplicationLifetime!);
            RxApp.SuspensionHost.CreateNewAppState = () => new MainWindowViewModel();
            RxApp.SuspensionHost.SetupDefaultSuspendResume(new NewtonsoftJsonSuspensionDriver("appstate.json"));
            suspension.OnFrameworkInitializationCompleted();
            
            // Load the saved view model state.
            var state = RxApp.SuspensionHost.GetAppState<MainWindowViewModel>();
            new MainWindow { DataContext = state }.Show();
            base.OnFrameworkInitializationCompleted();
        }
    }
}