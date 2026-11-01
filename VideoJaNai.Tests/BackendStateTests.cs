using AnimeJaNaiConverterGui.Services;
using Newtonsoft.Json;
using Splat;
using System;
using System.IO;
using VideoJaNai.ViewModels;
using Xunit;

namespace VideoJaNai.Tests
{
    // The backend is persisted as UseDirectMl (null in states that predate the app-wide setting, which
    // MigrateBackend fills from the selected workflow) plus UseTensorRtRtx (absent before it existed).
    public class BackendStateTests
    {
        static BackendStateTests()
        {
            // The suspension driver builds the view model without arguments; it resolves its
            // services through Splat, as in the app.
            Locator.CurrentMutable.RegisterConstant<IPythonService>(new FakePythonService());
            Locator.CurrentMutable.RegisterConstant<IUpdateManagerService>(new FakeUpdateManagerService());
        }

        static string State(string? useDirectMl, bool workflowDirectMl, string extra = "") => $$"""
            {
              "$type": "VideoJaNai.ViewModels.MainWindowViewModel, VideoJaNai",
              {{(useDirectMl is null ? "" : $"\"UseDirectMl\": {useDirectMl},")}}
              {{extra}}
              "Workflows": {
                "$type": "Avalonia.Collections.AvaloniaList`1[[VideoJaNai.ViewModels.UpscaleWorkflow, VideoJaNai]], Avalonia.Base",
                "$values": [
                  {
                    "$type": "VideoJaNai.ViewModels.UpscaleWorkflow, VideoJaNai",
                    "WorkflowName": "Workflow 1",
                    "WorkflowIndex": 0,
                    "TensorRtSelected": {{(workflowDirectMl ? "false" : "true")}},
                    "DirectMlSelected": {{(workflowDirectMl ? "true" : "false")}}
                  }
                ]
              },
              "SelectedWorkflowIndex": 0
            }
            """;

        // What App does with a loaded state.
        static MainWindowViewModel Load(string json)
        {
            var vm = (MainWindowViewModel)JsonConvert.DeserializeObject<object>(json, NewtonsoftJsonSuspensionDriver.Settings)!;
            foreach (var wf in vm.Workflows)
            {
                wf.Vm = vm;
            }
            vm.MigrateBackend();
            return vm;
        }

        public enum Backend { TensorRt, TensorRtRtx, DirectMl }

        static Backend Selected(MainWindowViewModel vm)
        {
            var wf = vm.CurrentWorkflow!;
            var selected = vm.DirectMlBackendSelected ? Backend.DirectMl
                         : vm.TensorRtRtxBackendSelected ? Backend.TensorRtRtx
                         : Backend.TensorRt;
            // the workflow (conf, aji_encode args, validation) agrees with the App Setting
            Assert.Equal(selected == Backend.DirectMl, wf.DirectMlSelected);
            Assert.Equal(selected != Backend.DirectMl, wf.TensorRtSelected);
            Assert.Equal(selected == Backend.TensorRtRtx, wf.TensorRtRtxSelected);
            Assert.Equal(selected == Backend.TensorRt, vm.TensorRtBackendSelected);
            return selected;
        }

        // DirectML and TensorRT-RTX are Windows-only; elsewhere those settings run on TensorRT.
        static Backend OnThisOs(Backend b) => OperatingSystem.IsWindows() ? b : Backend.TensorRt;

        [Theory]
        [InlineData("false", false, Backend.TensorRt)]
        [InlineData("false", true, Backend.TensorRt)]   // the app-wide setting wins over the legacy one
        [InlineData("true", false, Backend.DirectMl)]
        [InlineData(null, false, Backend.TensorRt)]      // per-workflow era: taken from the workflow
        [InlineData(null, true, Backend.DirectMl)]
        public void OldStatesKeepTheirBackend(string? useDirectMl, bool workflowDirectMl, Backend expected)
        {
            var vm = Load(State(useDirectMl, workflowDirectMl));

            Assert.False(vm.UseTensorRtRtx);
            Assert.Equal(OnThisOs(expected), Selected(vm));
        }

        [Fact]
        public void TensorRtRtxRoundTrips()
        {
            var vm = Load(State("false", false));
            vm.SetBackendTensorRtRtx();
            Assert.Equal(OnThisOs(Backend.TensorRtRtx), Selected(vm));

            var json = JsonConvert.SerializeObject(vm, NewtonsoftJsonSuspensionDriver.Settings);
            var loaded = Load(json);

            Assert.True(loaded.UseTensorRtRtx);
            // an older VideoJaNai, which ignores UseTensorRtRtx, reads this as TensorRT
            Assert.False(loaded.UseDirectMl);
            Assert.Equal(OnThisOs(Backend.TensorRtRtx), Selected(loaded));
        }

        [Fact]
        public void SwitchingAwayFromTensorRtRtxClearsIt()
        {
            var vm = Load(State("false", false, "\"UseTensorRtRtx\": true,"));
            Assert.Equal(OnThisOs(Backend.TensorRtRtx), Selected(vm));

            vm.SetBackendDirectMl();
            Assert.False(vm.UseTensorRtRtx);
            Assert.Equal(OnThisOs(Backend.DirectMl), Selected(vm));

            vm.SetBackendTensorRt();
            Assert.False(vm.UseTensorRtRtx);
            Assert.Equal(Backend.TensorRt, Selected(vm));
        }
    }
}
