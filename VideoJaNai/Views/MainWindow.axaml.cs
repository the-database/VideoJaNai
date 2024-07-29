using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Material.Icons.Avalonia;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VideoJaNai.ViewModels;

namespace VideoJaNai.Views
{
    public partial class MainWindow : AppWindow
    {
        private bool _autoScrollConsole = true;
        private bool _userWantsToQuit = false;

        public MainWindow()
        {
            AvaloniaXamlLoader.Load(this);
            Resized += MainWindow_Resized;
            Closing += MainWindow_Closing;
            Opened += MainWindow_Opened;

            var inputFileNameTextBox = this.FindControl<TextBox>("InputFileNameTextBox");
            var inputFolderNameTextBox = this.FindControl<TextBox>("InputFolderNameTextBox");
            var outputFolderNameTextBox = this.FindControl<TextBox>("OutputFolderNameTextBox");

            inputFileNameTextBox?.AddHandler(DragDrop.DropEvent, SetInputFilePath);
            inputFolderNameTextBox?.AddHandler(DragDrop.DropEvent, SetInputFolderPath);
            outputFolderNameTextBox?.AddHandler(DragDrop.DropEvent, SetOutputFolderPath);
        }

        private void MainWindow_Opened(object? sender, EventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.CheckAndExtractBackend();
            }
        }

        private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Show confirmation dialog
                if (!_userWantsToQuit && vm.Upscaling)
                {
                    // Cancel close to show dialog
                    e.Cancel = true;

                    _userWantsToQuit = await ShowConfirmationDialog("Cancel unfinished upscales?", "If you exit now, all unfinished upscales will be canceled. Are you sure you want to exit?");

                    // Close if the user confirmed
                    if (_userWantsToQuit)
                    {
                        vm.CancelUpscale();
                        Close();
                    }
                }
            }
        }

        private void ConsoleScrollViewer_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name == "Offset" && sender is ScrollViewer consoleScrollViewer)
            {
                if (e.NewValue is Vector newVector)
                {
                    _autoScrollConsole = newVector.Y == consoleScrollViewer?.ScrollBarMaximum.Y;
                }
            }

        }

        private void ConsoleTextBlock_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name == "Text" && sender is TextBlock textBlock)
            {
                if (textBlock.Parent is ScrollViewer consoleScrollViewer)
                {
                    if (consoleScrollViewer != null)
                    {
                        if (_autoScrollConsole)
                        {
                            consoleScrollViewer.ScrollToEnd();
                        }
                    }
                }
            }
        }

        private void MainWindow_Resized(object? sender, WindowResizedEventArgs e)
        {
            // Set the ScrollViewer width based on the new parent window's width
            var consoleScrollViewer = this.FindControl<ScrollViewer>("ConsoleScrollViewer");
            if (consoleScrollViewer != null)
            {
                consoleScrollViewer.Width = Width - 340; // Adjust the width as needed
            }

        }

        private async void OpenInputFileButtonClick(object? sender, RoutedEventArgs e)
        {


            // Get the resources
            var resources = Application.Current.Resources;
            foreach (var resourceKey in resources.Keys)
            {
                var resourceValue = resources[resourceKey];


                //if (resourceValue.HasDynamicResource())
                //{
                //    // This resource has a dynamic reference
                //    Console.WriteLine($"Resource '{resourceKey}' is a dynamic resource.");
                //}
            }


            // Get top level from the current control. Alternatively, you can use Window reference instead.
            var topLevel = TopLevel.GetTopLevel(this);

            // Start async operation to open the dialog.
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Video File",
                AllowMultiple = false
            });

            if (files.Count >= 1)
            {
                //// Open reading stream from the first file.
                //await using var stream = await files[0].OpenReadAsync();
                //using var streamReader = new StreamReader(stream);
                //// Reads all the content of file as a text.
                //var fileContent = await streamReader.ReadToEndAsync();
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.CurrentWorkflow.InputFilePath = files[0].TryGetLocalPath() ?? "";
                }
            }
        }

        public void SetInputFilePath(object? sender, DragEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                var files = e.Data.GetFiles().ToList();


                if (files.Count > 0)
                {
                    var filePath = files[0].TryGetLocalPath();
                    if (File.Exists(filePath))
                    {
                        vm.CurrentWorkflow.InputFilePath = filePath;
                    }
                }
            }
        }

        public void SetInputFolderPath(object? sender, DragEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                var files = e.Data.GetFiles().ToList();


                if (files.Count > 0)
                {
                    var filePath = files[0].TryGetLocalPath();
                    if (Directory.Exists(filePath))
                    {
                        vm.CurrentWorkflow.InputFolderPath = filePath;
                    }
                }
            }
        }

        public void SetOutputFolderPath(object? sender, DragEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                var files = e.Data.GetFiles().ToList();


                if (files.Count > 0)
                {
                    var filePath = files[0].TryGetLocalPath();
                    if (Directory.Exists(filePath))
                    {
                        vm.CurrentWorkflow.OutputFolderPath = filePath;
                    }
                }
            }
        }

        private async void OpenOnnxFileButtonClick(object? sender, RoutedEventArgs e)
        {
            // Get top level from the current control. Alternatively, you can use Window reference instead.
            var topLevel = TopLevel.GetTopLevel(this);

            // Start async operation to open the dialog.
            var storageProvider = topLevel.StorageProvider;

            if (DataContext is MainWindowViewModel vm)
            {

                var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open ONNX Model File",
                    AllowMultiple = false,
                    FileTypeFilter = [new("ONNX Model File") { Patterns = ["*.onnx"], MimeTypes = ["*/*"] }, FilePickerFileTypes.All],
                    SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(new Uri(vm.ModelsDirectory))
                });

                if (files.Count >= 1)
                {


                    // TODO 
                    //vm.InputFilePath = files[0].TryGetLocalPath() ?? "";
                    if (sender is Button button && button.DataContext is UpscaleModel item)
                    {
                        int index = vm.CurrentWorkflow.UpscaleSettings.IndexOf(item);
                        // 'index' now contains the index of the clicked item in the ItemsControl
                        // You can use it as needed
                        vm.CurrentWorkflow.UpscaleSettings[index].OnnxModelPath = files[0].TryGetLocalPath() ?? string.Empty;
                        vm.CurrentWorkflow.Validate();
                    }


                }
            }
        }

        private async void OpenInputFolderButtonClick(object? sender, RoutedEventArgs e)
        {
            // Get top level from the current control. Alternatively, you can use Window reference instead.
            var topLevel = TopLevel.GetTopLevel(this);

            // Start async operation to open the dialog.
            var files = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Open Folder",
                AllowMultiple = false
            });

            if (files.Count >= 1)
            {
                //// Open reading stream from the first file.
                //await using var stream = await files[0].OpenReadAsync();
                //using var streamReader = new StreamReader(stream);
                //// Reads all the content of file as a text.
                //var fileContent = await streamReader.ReadToEndAsync();
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.CurrentWorkflow.InputFolderPath = files[0].TryGetLocalPath() ?? "";
                }
            }
        }

        private async void OpenOutputFolderButtonClick(object? sender, RoutedEventArgs e)
        {
            // Get top level from the current control. Alternatively, you can use Window reference instead.
            var topLevel = TopLevel.GetTopLevel(this);

            // Start async operation to open the dialog.
            var files = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Open Folder",
                AllowMultiple = false
            });

            if (files.Count >= 1)
            {
                //// Open reading stream from the first file.
                //await using var stream = await files[0].OpenReadAsync();
                //using var streamReader = new StreamReader(stream);
                //// Reads all the content of file as a text.
                //var fileContent = await streamReader.ReadToEndAsync();
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.CurrentWorkflow.OutputFolderPath = files[0].TryGetLocalPath() ?? "";
                }
            }
        }

        private async void ImportCurrentWorkflowButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Get top level from the current control. Alternatively, you can use Window reference instead.
                var topLevel = GetTopLevel(this);

                // Start async operation to open the dialog.
                var storageProvider = topLevel.StorageProvider;

                var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import Workflow File",
                    AllowMultiple = false,
                    FileTypeFilter = [new("AnimeJaNai Workflow File") { Patterns = ["*.awf"], MimeTypes = ["*/*"] }, FilePickerFileTypes.All],
                    SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                });

                if (files.Count >= 1)
                {

                    var inPath = files[0].TryGetLocalPath();

                    if (inPath != null)
                    {
                        var td = new TaskDialog
                        {
                            Title = "Confirm Workflow Import",
                            ShowProgressBar = false,
                            Content = $"The following workflow file will be imported to the current workflow {vm.CurrentWorkflow?.WorkflowName}. All configuration settings for the current profile {vm.CurrentWorkflow?.WorkflowName} will be overwritten.\n\n" +
                            inPath,
                            Buttons =
                        {
                            TaskDialogButton.OKButton,
                            TaskDialogButton.CancelButton
                        }
                        };


                        td.Closing += async (s, e) =>
                        {
                            if ((TaskDialogStandardResult)e.Result == TaskDialogStandardResult.OK)
                            {
                                var deferral = e.GetDeferral();

                                td.SetProgressBarState(0, TaskDialogProgressState.Indeterminate);
                                td.ShowProgressBar = true;

                                await Task.Run(() =>
                                {
                                    vm.ReadWorkflowFileToCurrentWorkflow(inPath);
                                });

                                deferral.Complete();
                            }
                        };

                        td.XamlRoot = VisualRoot as Visual;
                        _ = await td.ShowAsync();
                    }
                }
            }
        }

        private async void ResetWorkflow(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {

                var td = new TaskDialog
                {
                    Title = "Confirm Workflow Reset",
                    ShowProgressBar = false,
                    Content = $"The current workflow's settings will be reset to the default settings. Any unsaved settings for the current workflow will be lost.",
                    Buttons =
                {
                    TaskDialogButton.OKButton,
                    TaskDialogButton.CancelButton
                }
                };


                td.Closing += async (s, e) =>
                {
                    if ((TaskDialogStandardResult)e.Result == TaskDialogStandardResult.OK)
                    {
                        var deferral = e.GetDeferral();

                        td.SetProgressBarState(0, TaskDialogProgressState.Indeterminate);
                        td.ShowProgressBar = true;

                        await Task.Run(() =>
                        {
                            vm.ResetCurrentWorkflow();
                        });

                        deferral.Complete();
                    }
                };

                td.XamlRoot = VisualRoot as Visual;
                _ = await td.ShowAsync();

            }
        }

        private async void ExportCurrentWorkflowButtonClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Get top level from the current control. Alternatively, you can use Window reference instead.
                var topLevel = GetTopLevel(this);

                var storageProvider = topLevel.StorageProvider;

                // Start async operation to open the dialog.
                var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export Current Profile Conf File",
                    DefaultExtension = "conf",
                    FileTypeChoices =
                    [
                    new("AnimeJaNai Workflow File (*.awf)") { Patterns = ["*.awf"] },
                    ],
                    SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                    SuggestedFileName = vm.CurrentWorkflow?.WorkflowName,
                });

                if (file is not null)
                {
                    var outPath = file.TryGetLocalPath();

                    if (outPath != null)
                    {
                        vm.WriteCurrentWorkflowToFile(outPath);
                    }
                }
            }
        }

        private async void ReinstallBackendClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                var confirm = await ShowConfirmationDialog("Reinstall Python Backend", "The existing Python backend will be removed and then reinstalled. Your workflow settings will be preserved. This process will take several minutes. Proceed?");

                if (confirm)
                {
                    await vm.ReinstallBackend();
                }
            }
        }

        private async Task<bool> ShowConfirmationDialog(string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 480,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                //Icon = Icon, // TODO
                CanResize = false,
                ShowInTaskbar = false
            };

            var textBlock = new TextBlock
            {
                Text = message,
                Margin = new Thickness(20),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 380,
            };

            var materialIcon = new MaterialIcon
            {
                Kind = Material.Icons.MaterialIconKind.QuestionMarkCircleOutline,
                Width = 48,
                Height = 48,
            };

            var textPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(20),
                Children = { materialIcon, textBlock },
            };

            var yesButton = new Button
            {
                Content = "Yes",
                Width = 100,
                HorizontalAlignment = HorizontalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            yesButton.Click += (sender, e) => dialog.Close(true);

            var noButton = new Button
            {
                Content = "No",
                Width = 100,
                HorizontalAlignment = HorizontalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0)
            };
            noButton.Click += (sender, e) => dialog.Close(false);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { yesButton, noButton },
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(20, 0, 20, 20)
            };

            var mainPanel = new StackPanel
            {
                Children = { textPanel, buttonPanel }
            };

            dialog.Content = mainPanel;
            var result = await dialog.ShowDialog<bool?>(this);

            return result ?? false;
        }
    }
}