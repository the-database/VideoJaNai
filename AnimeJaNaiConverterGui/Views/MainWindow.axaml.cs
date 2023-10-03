using AnimeJaNaiConverterGui.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;
using System.IO;

namespace AnimeJaNaiConverterGui.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
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
                SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(new Uri(@"C:\mpv-upscale-2x_animejanai\vapoursynth64\plugins\models\animejanai")),
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