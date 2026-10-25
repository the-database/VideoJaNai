using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;
using VideoJaNai.ViewModels;

namespace VideoJaNai.Views
{
    public partial class PreviewWindow : Window
    {
        public PreviewWindow()
        {
            AvaloniaXamlLoader.Load(this);
            // tunneling: the keys are the player's, whichever control has focus
            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            Closed += OnClosed;
            DataContextChanged += (_, _) =>
            {
                if (DataContext is PreviewWindowViewModel vm)
                {
                    vm.CloseRequested += Close;
                }
            };
            var picture = this.FindControl<Panel>("PicturePanel");
            if (picture != null)
            {
                picture.SizeChanged += (_, e) =>
                {
                    if (DataContext is PreviewWindowViewModel vm)
                    {
                        // frames streamed while playing are made at the size they are shown at
                        vm.ViewWidth = (int)Math.Ceiling(e.NewSize.Width * RenderScaling);
                    }
                };
            }
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not PreviewWindowViewModel vm)
            {
                return;
            }
            switch (e.Key)
            {
                case Key.Space:
                    vm.TogglePlay();
                    break;
                case Key.Left:
                    vm.Step(-1);
                    break;
                case Key.Right:
                    vm.Step(1);
                    break;
                case Key.S:
                    vm.ToggleSource();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            if (DataContext is PreviewWindowViewModel vm)
            {
                vm.CloseRequested -= Close;
                vm.StopSession();
                vm.ReleaseImages();
            }
        }
    }
}
