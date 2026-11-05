using System;
using System.IO;
using System.Linq;
using VideoJaNai.Core;
using VideoJaNai.ViewModels;
using Xunit;

namespace VideoJaNai.Tests
{
    // Folder mode takes the folder's videos and its subfolders' (the output keeps the subfolders),
    // and the status bar counts exactly those (it used to count subfolders but run the top level only).
    public class FolderModeTests : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), $"vj-folder-{Guid.NewGuid():N}");

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        string Touch(string rel)
        {
            var p = Path.Join(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");
            return p;
        }

        MainWindowViewModel Create(bool overwrite = false)
        {
            var vm = new MainWindowViewModel(new FakePythonService(), new FakeUpdateManagerService());
            var wf = new UpscaleWorkflow
            {
                Vm = vm,
                WorkflowIndex = 0,
                WorkflowName = "Workflow 1",
                UpscaleSettings = [new() { OnnxModelPath = Touch("model.onnx") }],
            };
            vm.Workflows = [wf];
            wf.SelectedTabIndex = 1;
            wf.InputFolderPath = Path.Join(_root, "in");
            wf.OutputFolderPath = Path.Join(_root, "out");
            wf.OverwriteExistingVideos = overwrite;
            return vm;
        }

        [Fact]
        public void SubfolderVideosAreCountedAndRunIntoMatchingSubfolders()
        {
            Touch(Path.Join("in", "a.mkv"));
            Touch(Path.Join("in", "season 2", "b.mp4"));
            Touch(Path.Join("in", "notes.txt"));
            var vm = Create();
            vm.CurrentWorkflow!.Validate();

            Assert.True(vm.CurrentWorkflow.Valid, vm.ValidationText);
            Assert.Equal(2, vm.CheckInputs());
            var plan = vm.PlanFolder();
            Assert.Null(plan.Problem);
            Assert.Equal([Path.Join(_root, "out", "a-animejanai.mkv"), Path.Join(_root, "out", "season 2", "b-animejanai.mkv")],
                         plan.Videos.Select(v => v.Output));
        }

        [Fact]
        public void ExistingOutputsAreSkippedUnlessOverwriting()
        {
            Touch(Path.Join("in", "a.mkv"));
            Touch(Path.Join("in", "s", "b.mkv"));
            Touch(Path.Join("out", "s", "b-animejanai.mkv"));

            var vm = Create();
            Assert.Equal(1, vm.CheckInputs());
            Assert.Equal("1 video file (1 video file already exists and will be skipped)", vm.InputStatusText);
            Assert.Equal([PlanAction.Encode, PlanAction.SkipExisting], vm.PlanFolder().Videos.Select(v => v.Action));

            var redo = Create(overwrite: true);
            Assert.Equal(2, redo.CheckInputs());
            Assert.All(redo.PlanFolder().Videos, v => Assert.Equal(PlanAction.Encode, v.Action));
        }

        [Fact]
        public void OnlySubfolderVideosIsStillSomethingToRun()
        {
            Touch(Path.Join("in", "s", "b.mkv"));
            var vm = Create();
            vm.CurrentWorkflow!.Validate();
            Assert.True(vm.CurrentWorkflow.Valid, vm.ValidationText);
            Assert.Equal(1, vm.CheckInputs());
        }

        [Fact]
        public void TwoVideosWritingOneFileStopTheRun()
        {
            Touch(Path.Join("in", "a.mkv"));
            Touch(Path.Join("in", "a.mp4"));
            var vm = Create();
            vm.CurrentWorkflow!.Validate();
            Assert.False(vm.CurrentWorkflow.Valid);
            Assert.Contains("would both be written to", vm.ValidationText);
        }
    }
}
