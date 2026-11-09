using AnimeJaNaiConverterGui.Services;
using Avalonia.Headless.XUnit;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VideoJaNai.ViewModels;
using Xunit;

namespace VideoJaNai.Tests
{
    // An installed copy, for what only installed copies do.
    sealed class InstalledUpdateManagerService : IUpdateManagerService
    {
        public bool IsInstalled => true;
        public string AppVersion => "3.0.0";
        public string InstallDir => Path.GetTempPath();
        public string UpdaterPath => Path.Combine(InstallDir, "no-such-updater.exe");
        public Task<string?> CheckForUpdateAsync() => Task.FromResult<string?>(null);
        public void ApplyUpdateAndRestart() { }
        public Task<string?> DownloadUpdateAsync(Action<string>? onLine, CancellationToken ct) => Task.FromResult<string?>(null);
        public void ApplyUpdateOnExit() { }
        public string? TakeLastUpdateError() => null;
        public Task<string> GetComponentsJsonAsync(string extraArguments = "") => Task.FromResult("");
        public Task<int> RunUpdaterStreamingAsync(string arguments, Action<string>? onLine, CancellationToken ct = default) => Task.FromResult(0);
    }

    // App Settings > Storage: where the shared pack store is (StoreLocation reads it through aji-runtime;
    // VideoJaNai.Core.Tests has those tests).
    public class StorageTests
    {
        static readonly StoreLocation.Info Default = new(@"C:\aji", false, null, true);
        static readonly StoreLocation.Info Moved = new(@"C:\aji", true, @"F:\aji", true);
        static readonly StoreLocation.Info Missing = new(@"C:\aji", true, @"F:\aji", false);

        static MainWindowViewModel CreateVm(IUpdateManagerService updates)
        {
            var vm = new MainWindowViewModel(new FakePythonService(), updates) { Workflows = [] };
            vm.Workflows.Add(new UpscaleWorkflow { Vm = vm, WorkflowIndex = 0, WorkflowName = "Workflow 1", UpscaleSettings = [new()] });
            return vm;
        }

        [AvaloniaFact]
        public void TheButtonsFollowWhereTheStoreIs()
        {
            var vm = CreateVm(new InstalledUpdateManagerService());
            Assert.False(vm.ShowStorage, "nothing read yet");

            vm.Storage = Default;
            Assert.True(vm.ShowStorage);
            Assert.Equal(@"C:\aji", vm.StorageFolderText);
            Assert.False(vm.StorageMoved || vm.StorageMissing);

            vm.Storage = Moved;
            Assert.Equal(@"F:\aji", vm.StorageFolderText);
            Assert.True(vm.StorageMoved);
            Assert.False(vm.StorageMissing);
            Assert.Contains(@"Moved from C:\aji", vm.StorageText);

            vm.Storage = Missing;
            Assert.False(vm.StorageMoved);
            Assert.True(vm.StorageMissing);
            Assert.Contains("isn't available", vm.StorageText);

            Assert.False(CreateVm(new FakeUpdateManagerService()).ShowStorage, "not in a dev build");
        }

        // Only installed copies whose runtime is in the store (aji-runtime.json beside libaji) show the
        // section: not a dev build or the AppImage (no updater), nor an install from before the store.
        [Fact]
        public void OnlyAnInstallThatUsesTheStoreShowsIt()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"videojanai-storage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                Assert.False(MainWindowViewModel.UsesPackStore(true, dir));
                File.WriteAllText(Path.Combine(dir, RuntimePacks.FileName), "{}");
                Assert.True(MainWindowViewModel.UsesPackStore(true, dir));
                Assert.False(MainWindowViewModel.UsesPackStore(false, dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // While the store's folder is missing, TensorRT can't run: the workflow says why (not that
        // TensorRT isn't installed) and the Upscale button stays off. DirectML needs nothing from it.
        [AvaloniaFact]
        public void AMissingStoreStopsTensorRtButNotDirectMl()
        {
            var vm = CreateVm(new InstalledUpdateManagerService());
            vm.Storage = Missing;
            vm.CurrentWorkflow!.Validate();
            Assert.False(vm.CurrentWorkflow.Valid);
            Assert.Contains(@"F:\aji isn't available. Is its drive connected?", vm.ValidationText);
            Assert.Contains("App Settings > Storage", vm.ValidationText);
            Assert.True(vm.StorageNeededButMissing);

            vm.Storage = Moved;
            vm.CurrentWorkflow.Validate();
            Assert.DoesNotContain("isn't available", vm.ValidationText);
            Assert.False(vm.StorageNeededButMissing);

            if (UpscaleWorkflow.DirectMlAvailable)
            {
                vm.Storage = Missing;
                vm.SetBackendDirectMl();
                vm.CurrentWorkflow.Validate();
                Assert.DoesNotContain("isn't available", vm.ValidationText);
                Assert.False(vm.StorageNeededButMissing);
            }
        }
    }
}
