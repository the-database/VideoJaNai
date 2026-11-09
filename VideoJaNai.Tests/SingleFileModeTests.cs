using System;
using System.IO;
using VideoJaNai.ViewModels;
using Xunit;

namespace VideoJaNai.Tests
{
    // Single-file mode plans its output as folder mode does, so an output that would replace the
    // input video stops the run.
    public class SingleFileModeTests : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), $"vj-file-{Guid.NewGuid():N}");

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

        MainWindowViewModel Create(string outputFilename)
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
            wf.SelectedTabIndex = 0;
            wf.InputFilePath = Touch(Path.Join("in", "ep.mkv"));
            wf.OutputFolderPath = Path.Join(_root, "in");
            wf.OutputFilename = outputFilename;
            wf.OverwriteExistingVideos = true;
            return vm;
        }

        [Fact]
        public void AnOutputOverTheInputStopsTheRun()
        {
            var vm = Create("%filename%.mkv");
            vm.CurrentWorkflow!.Validate();
            Assert.False(vm.CurrentWorkflow.Valid);
            Assert.Contains("would be written over itself", vm.ValidationText);
        }

        [Fact]
        public void AnotherOutputNameRuns()
        {
            var vm = Create(BatchPlan.DefaultFilename);
            vm.CurrentWorkflow!.Validate();
            Assert.True(vm.CurrentWorkflow.Valid, vm.ValidationText);
            Assert.Equal(1, vm.CheckInputs());
            Assert.Equal(Path.Join(_root, "in", "ep-animejanai.mkv"), Assert.Single(vm.PlanFile().Videos).Output);
        }
    }
}
