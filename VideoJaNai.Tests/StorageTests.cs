using AnimeJaNaiConverterGui.Services;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VideoJaNai.ViewModels;
using Xunit;

namespace VideoJaNai.Tests
{
    // An installed copy, for what only installed copies do. Updater plays VideoJaNaiUpdater for
    // RunUpdaterAsync (App Settings > Storage); without it the updater isn't there to ask.
    sealed class InstalledUpdateManagerService : IUpdateManagerService
    {
        public Func<IReadOnlyList<string>, Action<string>?, (int, string)>? Updater { get; set; }
        public List<string[]> Calls { get; } = [];

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
        public Task<(int ExitCode, string Output)> RunUpdaterAsync(IReadOnlyList<string> arguments, Action<string>? onLine = null, CancellationToken ct = default)
        {
            lock (Calls)
            {
                Calls.Add([.. arguments]);
            }
            return Task.FromResult(Updater?.Invoke(arguments, onLine) ?? (-1, ""));
        }
    }

    // App Settings > Storage: where this copy keeps TensorRT (the updater's --storage, or with an older
    // updater aji-runtime's shared store; VideoJaNai.Core.Tests has StoreLocation's tests).
    public class StorageTests
    {
        static readonly StoreLocation.Info Default = new(@"C:\aji", false, null, true);
        static readonly StoreLocation.Info Moved = new(@"C:\aji", true, @"F:\aji", true);
        // TensorRT is in it (else its folder being gone doesn't matter)
        static readonly StoreLocation.Info Missing = new(@"C:\aji", true, @"F:\aji", false) { Installed = true };

        static MainWindowViewModel CreateVm(IUpdateManagerService updates)
        {
            var vm = new MainWindowViewModel(new FakePythonService(), updates) { Workflows = [] };
            vm.Workflows.Add(new UpscaleWorkflow { Vm = vm, WorkflowIndex = 0, WorkflowName = "Workflow 1", UpscaleSettings = [new()] });
            return vm;
        }

        // What `--storage --json` prints.
        static string Choice(string choice, string root, bool installed, bool available = true, bool moved = false, string? target = null,
                             long? bytes = null) =>
            JsonSerializer.Serialize(new
            {
                choice, root, moved, target, available, inside = @"D:\vjn\animejanai\runtime", shared = @"C:\aji", installed, bytes,
            });

        // A fake updater: --storage --json prints state; --storage <x> switches it (inside, shared, or a
        // folder), with progress, unless fail names the reason it gives.
        sealed class FakeStorage
        {
            public string State = Choice("inside", @"D:\vjn\animejanai\runtime", installed: true);
            public string? Fail;

            public (int, string) Run(IReadOnlyList<string> args, Action<string>? onLine)
            {
                if (args is ["--storage", "--json"])
                {
                    return (0, State + "\n");
                }
                if (args is ["--storage", var target])
                {
                    onLine?.Invoke("AJI_MOVE 1000000 2000000");
                    if (Fail is string reason)
                    {
                        onLine?.Invoke("warning: something earlier");
                        onLine?.Invoke(reason);
                        onLine?.Invoke("");
                        return (1, "");
                    }
                    onLine?.Invoke("AJI_MOVE 2000000 2000000");
                    State = target switch
                    {
                        "inside" => Choice("inside", @"D:\vjn\animejanai\runtime", true),
                        "shared" => Choice("shared", @"C:\aji", true),
                        _ => Choice("custom", target, true),
                    };
                    onLine?.Invoke("TensorRT is now kept in somewhere.");
                    return (0, "");
                }
                return (2, "Usage: VideoJaNaiUpdater.exe [...]");
            }
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
            Assert.True(vm.NoStorageChoice && vm.StorageIsShared && vm.AllowSharedStorageChanges, "an older updater: the shared folder only");

            vm.Storage = Moved;
            Assert.Equal(@"F:\aji", vm.StorageFolderText);
            Assert.True(vm.StorageMoved);
            Assert.False(vm.StorageMissing);
            Assert.Contains(@"Moved from C:\aji", vm.StorageText);

            vm.Storage = Missing;
            Assert.False(vm.StorageMoved);
            Assert.True(vm.StorageMissing && vm.StorageSharedMissing && !vm.StorageCustomMissing);
            Assert.Contains("isn't available", vm.StorageText);

            Assert.False(CreateVm(new FakeUpdateManagerService()).ShowStorage, "not in a dev build");
        }

        // Only installed copies whose runtime is in the store (aji-runtime.json beside libaji) show the
        // old section (an updater without --storage): not a dev build or the AppImage (no updater), nor an
        // install from before the store.
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
            Assert.Contains("reset the location in App Settings > Storage", vm.ValidationText);
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

        // The updater's choice: which option is current, and the shared folder's buttons only with it.
        [AvaloniaFact]
        public async Task TheUpdatersChoiceIsShown()
        {
            var fake = new FakeStorage();
            var updates = new InstalledUpdateManagerService { Updater = fake.Run };
            var vm = CreateVm(updates);

            fake.State = Choice("inside", @"D:\vjn\animejanai\runtime", installed: true, moved: true, target: @"G:\moved");
            await vm.RefreshStorage();
            Assert.Equal(["--storage", "--json"], updates.Calls[^1]);
            Assert.True(vm.ShowStorage && vm.HasStorageChoice && vm.StorageIsInside && !vm.StorageIsShared && !vm.StorageIsCustom);
            Assert.Equal(@"D:\vjn\animejanai\runtime", vm.StorageInsideFolder);
            Assert.Equal(@"D:\vjn\animejanai\runtime", vm.StorageFolderText);
            Assert.Equal(@"G:\moved", vm.StorageSharedFolder);
            Assert.True(vm.AllowStorageChanges && !vm.AllowSharedStorageChanges && !vm.StorageMoved, "inside: no Move... / Move Back");
            Assert.Equal("No folder chosen", vm.StorageCustomFolder);

            fake.State = Choice("shared", @"C:\aji", installed: true, moved: true, target: @"G:\moved");
            await vm.RefreshStorage();
            Assert.True(vm.StorageIsShared && vm.AllowSharedStorageChanges && vm.StorageMoved);
            Assert.Equal(@"G:\moved", vm.StorageFolderText);

            fake.State = Choice("custom", @"E:\my runtime ランタイム", installed: true);
            await vm.RefreshStorage();
            Assert.True(vm.StorageIsCustom && !vm.AllowSharedStorageChanges);
            Assert.Equal(@"E:\my runtime ランタイム", vm.StorageCustomFolder);

            // installed in a chosen folder whose drive is unplugged: TensorRT can't run, and the fix
            // is another place, not resetting the shared folder
            fake.State = Choice("custom", @"F:\aji2", installed: true, available: false);
            await vm.RefreshStorage();
            Assert.True(vm.StorageMissing && vm.StorageCustomMissing && !vm.StorageSharedMissing);
            Assert.Contains("choose another place in App Settings > Storage", vm.StorageMissingText);
            vm.CurrentWorkflow!.Validate();
            Assert.Contains(@"F:\aji2 isn't available", vm.ValidationText);

            // a fresh copy (nothing installed) whose chosen folder's drive isn't there: nothing is missing
            fake.State = Choice("custom", @"F:\nowhere", installed: false, available: false);
            await vm.RefreshStorage();
            Assert.True(vm.ShowStorage && vm.StorageUnavailable && !vm.StorageMissing && !vm.StorageNeededButMissing);

            fake.State = Choice("inside", @"D:\vjn\animejanai\runtime", installed: false);
            await vm.RefreshStorage();
            Assert.Contains("isn't installed yet", vm.StorageText);

            // an updater from before --storage (and no TensorRT in the shared store): no section
            updates.Updater = (_, _) => (2, "Usage: VideoJaNaiUpdater.exe [--check|...]");
            await vm.RefreshStorage();
            Assert.Null(vm.Storage);
            Assert.False(vm.ShowStorage);
            updates.Updater = (_, _) => (0, "Checking for updates...\n");
            await vm.RefreshStorage();
            Assert.Null(vm.Storage);
        }

        // Switching through the updater: progress, the updater's reason on failure, and never while
        // components change.
        [AvaloniaFact]
        public async Task SwitchingGoesThroughTheUpdater()
        {
            var fake = new FakeStorage();
            var updates = new InstalledUpdateManagerService { Updater = fake.Run };
            var vm = CreateVm(updates);
            await vm.RefreshStorage();
            Assert.True(vm.StorageIsInside);

            Assert.True(await vm.SwitchStorage("shared"));
            var calls = updates.Calls.Select(c => string.Join(" ", c)).ToList();
            Assert.Contains("--storage shared", calls);
            Assert.Equal("--storage --json", calls[^1]);
            Assert.True(vm.StorageIsShared);
            Assert.Equal("Done.", vm.StorageStatusText);
            Assert.True(vm.AllowStorageChanges && !vm.StorageBusy);

            // a folder with spaces and other scripts reaches the updater as one argument
            string mine = @"E:\my runtime ランタイム";
            Assert.True(await vm.SwitchStorage(mine));
            Assert.Contains(updates.Calls, c => c is ["--storage", var t] && t == mine);
            Assert.True(vm.StorageIsCustom);

            fake.Fail = "Stop the upscale and close the preview first: TensorRT can't be moved while it's in use.";
            Assert.False(await vm.SwitchStorage("inside"));
            Assert.Equal(fake.Fail, vm.StorageStatusText);
            Assert.True(vm.StorageIsCustom && vm.AllowStorageChanges, "still where it was, unblocked");
            fake.Fail = null;

            int before = updates.Calls.Count;
            vm.StorageBusy = true; // another storage change
            Assert.False(await vm.SwitchStorage("inside"));
            Assert.Equal(before, updates.Calls.Count);
            vm.StorageBusy = false;
            Assert.True(await vm.SwitchStorage("inside"));
            Assert.True(vm.StorageIsInside);
        }

        // The section's options follow the updater's choice in the window itself.
        [AvaloniaFact]
        public async Task TheWindowShowsTheCurrentOption()
        {
            var fake = new FakeStorage { State = Choice("custom", @"E:\JaNai", installed: true) };
            var vm = CreateVm(new InstalledUpdateManagerService { Updater = fake.Run });
            await vm.RefreshStorage();
            var window = new VideoJaNai.Views.MainWindow { DataContext = vm };
            RadioButton Option(string name) => window.FindControl<RadioButton>(name)!;
            Assert.True(Option("StorageCustomOption").IsChecked == true);
            Assert.False(Option("StorageInsideOption").IsChecked == true || Option("StorageSharedOption").IsChecked == true);
            Assert.True(Option("StorageSharedOption").IsVisible, "with the choice, the shared folder is an option");

            fake.State = Choice("inside", @"D:\vjn\animejanai\runtime", installed: true);
            await vm.RefreshStorage();
            Assert.True(Option("StorageInsideOption").IsChecked == true);
            Assert.False(Option("StorageCustomOption").IsChecked == true);

            vm.Storage = Default; // an updater from before the choice: no options, just the shared folder
            Assert.False(Option("StorageSharedOption").IsVisible);
        }

        // What a switch says it moves: the updater's bytes, else the folder's size.
        [AvaloniaFact]
        public void ASwitchMovesThisCopysPacks()
        {
            var vm = CreateVm(new InstalledUpdateManagerService());
            vm.Storage = StoreLocation.ParseChoice(Choice("inside", @"D:\nowhere-vjn", installed: true, bytes: 99));
            Assert.Equal(99, vm.SwitchBytes());
            vm.Storage = StoreLocation.ParseChoice(Choice("inside", @"D:\nowhere-vjn", installed: true));
            Assert.Equal(0, vm.SwitchBytes());
        }
    }
}
