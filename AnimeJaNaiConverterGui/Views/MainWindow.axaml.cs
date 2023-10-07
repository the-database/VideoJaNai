using AnimeJaNaiConverterGui.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.ReactiveUI;
using ReactiveUI;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AnimeJaNaiConverterGui.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {
        public MainWindow()
        {
            //InitializeComponent();
            AvaloniaXamlLoader.Load(this);
            this.WhenActivated(disposable => { });
            Resized += MainWindow_Resized;

            var inputFileNameTextBox = this.FindControl<TextBox>("InputFileNameTextBox");
            var outputFileNameTextBox = this.FindControl<TextBox>("OutputFileNameTextBox");
            var inputFolderNameTextBox = this.FindControl<TextBox>("InputFolderNameTextBox");
            var outputFolderNameTextBox = this.FindControl<TextBox>("OutputFolderNameTextBox");

            inputFileNameTextBox?.AddHandler(DragDrop.DropEvent, SetInputFilePath);
            outputFileNameTextBox?.AddHandler(DragDrop.DropEvent, SetOutputFilePath);
            inputFolderNameTextBox?.AddHandler(DragDrop.DropEvent, SetInputFolderPath);
            outputFolderNameTextBox?.AddHandler(DragDrop.DropEvent, SetOutputFolderPath);
        }

        private void ConsoleTextBlock_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name == "Text")
            {
                var consoleScrollViewer = this.FindControl<ScrollViewer>("ConsoleScrollViewer");
                if (consoleScrollViewer != null)
                {
                    consoleScrollViewer.ScrollToEnd();
                }
            }
        }

        private void MainWindow_Resized(object? sender, WindowResizedEventArgs e)
        {
            // Set the ScrollViewer width based on the new parent window's width
            var consoleScrollViewer = this.FindControl<ScrollViewer>("ConsoleScrollViewer");
            if (consoleScrollViewer != null)
            {
                consoleScrollViewer.Width = Width - 40; // Adjust the width as needed
            }
            
        }

        private async void OpenInputFileButtonClick(object? sender, RoutedEventArgs e)
        {
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
                if (DataContext is MainWindowViewModel vm) {
                    vm.InputFilePath = files[0].TryGetLocalPath() ?? "";
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
                        vm.InputFilePath = filePath;
                    }
                }
            }
        }

        public void SetOutputFilePath(object? sender, DragEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                var files = e.Data.GetFiles().ToList();


                if (files.Count > 0)
                {
                    var filePath = files[0].TryGetLocalPath();
                    if (File.Exists(filePath))
                    {
                        vm.OutputFilePath = filePath;
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
                        vm.InputFolderPath = filePath;
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
                        vm.OutputFolderPath = filePath;
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

            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open ONNX Model File",
                AllowMultiple = false,
                FileTypeFilter = new FilePickerFileType[] { new("ONNX Model File") { Patterns = new[] { "*.onnx" }, MimeTypes = new[] { "*/*" } }, FilePickerFileTypes.All },
                SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(@".\mpv-upscale-2x_animejanai\vapoursynth64\plugins\models\animejanai"))),
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
                    // TODO 
                    //vm.InputFilePath = files[0].TryGetLocalPath() ?? "";
                    if (sender is Button button && button.DataContext is UpscaleModel item)
                    {
                        int index = vm.UpscaleSettings.IndexOf(item);
                        // 'index' now contains the index of the clicked item in the ItemsControl
                        // You can use it as needed
                        vm.UpscaleSettings[index].OnnxModelPath = files[0].TryGetLocalPath() ?? string.Empty;
                        vm.Validate();
                    }
                    
                }
            }
        }

        private async void OpenOutputFileButtonClick(object? sender, RoutedEventArgs e)
        {
            // Get top level from the current control. Alternatively, you can use Window reference instead.
            var topLevel = TopLevel.GetTopLevel(this);

            // Start async operation to open the dialog.
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Video File",
                DefaultExtension = "mkv",
                FileTypeChoices = new FilePickerFileType[]
                {
                    new("MKV file (*.mkv)") { Patterns = new[] { "*.mkv" } },
                    new("MP4 file (*.mp4)") { Patterns = new[] { "*.mp4" } },
                    new("All files (*.*)") { Patterns = new[] { "*" } },
                },
            });

            if (file is not null)
            {
                //// Open reading stream from the first file.
                //await using var stream = await files[0].OpenReadAsync();
                //using var streamReader = new StreamReader(stream);
                //// Reads all the content of file as a text.
                //var fileContent = await streamReader.ReadToEndAsync();
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.OutputFilePath = file.TryGetLocalPath() ?? "";
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
                    vm.InputFolderPath = files[0].TryGetLocalPath() ?? "";
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
                    vm.OutputFolderPath = files[0].TryGetLocalPath() ?? "";
                }
            }
        }
    }
}