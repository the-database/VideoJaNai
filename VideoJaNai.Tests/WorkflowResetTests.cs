using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using System.IO;
using System.Linq;
using VideoJaNai.ViewModels;
using Xunit;

namespace VideoJaNai.Tests
{
    public class WorkflowResetTests
    {
        static MainWindowViewModel CreateVm(int workflowCount = 3)
        {
            var vm = new MainWindowViewModel(new FakePythonService(), new FakeUpdateManagerService());
            vm.Workflows = [];
            for (var i = 0; i < workflowCount; i++)
            {
                vm.Workflows.Add(new UpscaleWorkflow
                {
                    Vm = vm,
                    WorkflowIndex = i,
                    WorkflowName = $"Workflow {i + 1}",
                    UpscaleSettings = [new()],
                });
            }
            return vm;
        }

        [Fact]
        public void ResetOnlyResetsTheCurrentWorkflow()
        {
            var vm = CreateVm();
            vm.Workflows[1].OutputFilename = "kept.mkv";
            vm.SelectedWorkflowIndex = 2;
            var old = vm.CurrentWorkflow!;
            old.OutputFilename = "reset.mkv";
            old.WorkflowName = "Renamed";

            vm.ResetCurrentWorkflow();

            var reset = vm.CurrentWorkflow!;
            Assert.NotSame(old, reset);
            Assert.Same(reset, vm.Workflows[2]);
            Assert.Equal(new UpscaleWorkflow().OutputFilename, reset.OutputFilename);
            Assert.Equal(2, reset.WorkflowIndex);
            Assert.Equal("Workflow 3", reset.WorkflowName);
            Assert.Same(vm, reset.Vm);
            Assert.Single(reset.UpscaleSettings);
            Assert.Equal(2, vm.SelectedWorkflowIndex);
            Assert.Equal("kept.mkv", vm.Workflows[1].OutputFilename);
            Assert.Equal(3, vm.Workflows.Count);
        }

        [Fact]
        public void ResetDetachesTheReplacedWorkflow()
        {
            var vm = CreateVm();
            var old = vm.CurrentWorkflow!;

            vm.ResetCurrentWorkflow();

            Assert.Null(old.Vm);
        }

        [Fact]
        public void ImportReplacesOnlyTheCurrentWorkflow()
        {
            var vm = CreateVm();
            vm.CurrentWorkflow!.OutputFilename = "exported.mkv";
            var path = Path.Combine(Path.GetTempPath(), $"vjn-test-{Path.GetRandomFileName()}.json");
            try
            {
                vm.WriteCurrentWorkflowToFile(path);
                vm.SelectedWorkflowIndex = 1;
                var old = vm.CurrentWorkflow!;

                vm.ReadWorkflowFileToCurrentWorkflow(path);

                Assert.Equal("exported.mkv", vm.Workflows[1].OutputFilename);
                Assert.Equal(1, vm.Workflows[1].WorkflowIndex);
                Assert.Same(vm, vm.Workflows[1].Vm);
                Assert.Null(old.Vm);
                Assert.Equal("exported.mkv", vm.Workflows[0].OutputFilename);
                Assert.NotSame(vm.Workflows[0], vm.Workflows[1]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ExpandAndCollapseAllSections()
        {
            var wf = CreateVm().CurrentWorkflow!;
            bool[] Sections() =>
            [
                wf.InputSectionExpanded, wf.SourceSectionExpanded, wf.UpscaleSectionExpanded,
                wf.TemporalFixSectionExpanded, wf.InterpolationSectionExpanded,
                wf.PostProcessingSectionExpanded, wf.OutputSectionExpanded,
            ];

            wf.ExpandAllSections();
            Assert.All(Sections(), Assert.True);

            wf.CollapseAllSections();
            Assert.All(Sections(), Assert.False);
        }

        // Regression: Reset swapped the workflow from a background thread while the sidebar was
        // bound to Workflows, which crashed the app. It must work with the list bound to the UI.
        [AvaloniaFact]
        public void ResetWithWorkflowsBoundToTheSidebar()
        {
            var vm = CreateVm();
            vm.SelectedWorkflowIndex = 1;
            vm.CurrentWorkflow!.WorkflowName = "Renamed";
            var sidebar = new ItemsControl
            {
                ItemsSource = vm.Workflows,
                ItemTemplate = new FuncDataTemplate<UpscaleWorkflow>((wf, _) => new TextBlock { Text = wf?.WorkflowName }),
            };
            var window = new Window { Content = sidebar };
            window.Show();

            vm.ResetCurrentWorkflow();
            window.UpdateLayout();

            var names = sidebar.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Equal(["Workflow 1", "Workflow 2", "Workflow 3"], names);
        }
    }
}
