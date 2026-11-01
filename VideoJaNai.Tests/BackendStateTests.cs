using AnimeJaNaiConverterGui.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Splat;
using System;
using System.IO;
using System.Runtime.Serialization;
using VideoJaNai.ViewModels;
using Xunit;

namespace VideoJaNai.Tests
{
    // The backend is persisted as UseDirectMl (null in states that predate the app-wide setting, which
    // MigrateBackend fills from the selected workflow) plus UseTensorRtRtx (absent before it existed,
    // and while the user hasn't picked an NVIDIA backend: then TensorRT-RTX when its component is
    // installed, else TensorRT).
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

        static string Rtx(string value) => $"\"UseTensorRtRtx\": {value},";

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

        // ... with the trt-rtx component installed or not
        static MainWindowViewModel Load(string json, bool rtxInstalled)
        {
            using var _ = rtxInstalled ? null : FakePythonService.WithoutTensorRtRtx();
            return Load(json);
        }

        static string Save(MainWindowViewModel vm) => JsonConvert.SerializeObject(vm, NewtonsoftJsonSuspensionDriver.Settings);

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

        // ... with the trt-rtx component installed or not
        static Backend Selected(MainWindowViewModel vm, bool rtxInstalled)
        {
            using var _ = rtxInstalled ? null : FakePythonService.WithoutTensorRtRtx();
            return Selected(vm);
        }

        // DirectML and TensorRT-RTX are Windows-only; elsewhere those settings run on TensorRT.
        static Backend OnThisOs(Backend b) => OperatingSystem.IsWindows() ? b : Backend.TensorRt;

        // States saved by released versions have no UseTensorRtRtx: they load exactly as before, except
        // that the NVIDIA backend is TensorRT-RTX when its component is installed.
        [Theory]
        [InlineData("false", false, false, Backend.TensorRt)]
        [InlineData("false", false, true, Backend.TensorRtRtx)]
        [InlineData("false", true, false, Backend.TensorRt)]     // the app-wide setting wins over the legacy one
        [InlineData("false", true, true, Backend.TensorRtRtx)]
        [InlineData("true", false, false, Backend.DirectMl)]
        [InlineData("true", false, true, Backend.DirectMl)]
        [InlineData(null, false, false, Backend.TensorRt)]       // per-workflow era: taken from the workflow
        [InlineData(null, false, true, Backend.TensorRtRtx)]
        [InlineData(null, true, false, Backend.DirectMl)]
        [InlineData(null, true, true, Backend.DirectMl)]
        public void OldStatesKeepTheirBackend(string? useDirectMl, bool workflowDirectMl, bool rtxInstalled, Backend expected)
        {
            var vm = Load(State(useDirectMl, workflowDirectMl), rtxInstalled);

            Assert.Null(vm.UseTensorRtRtx);
            Assert.Equal(useDirectMl is null ? workflowDirectMl : useDirectMl == "true", vm.UseDirectMl);
            Assert.Equal(OnThisOs(expected), Selected(vm, rtxInstalled));
        }

        // An explicit choice stands whether or not the component is installed (TensorRT-RTX without it
        // reports the missing components, as before).
        [Theory]
        [InlineData("false", false, Backend.TensorRt)]
        [InlineData("false", true, Backend.TensorRt)]
        [InlineData("true", false, Backend.TensorRtRtx)]
        [InlineData("true", true, Backend.TensorRtRtx)]
        public void ExplicitChoiceStands(string useTensorRtRtx, bool rtxInstalled, Backend expected)
        {
            var vm = Load(State("false", false, Rtx(useTensorRtRtx)), rtxInstalled);

            Assert.Equal(useTensorRtRtx == "true", vm.UseTensorRtRtx);
            Assert.Equal(OnThisOs(expected), Selected(vm, rtxInstalled));
        }

        [Theory]
        [InlineData("")]
        [InlineData("\"UseTensorRtRtx\": true,")]
        [InlineData("\"UseTensorRtRtx\": false,")]
        [InlineData("\"UseTensorRtRtx\": null,")]
        public void DirectMlIsUntouched(string extra)
        {
            foreach (var rtxInstalled in new[] { false, true })
            {
                var vm = Load(State("true", false, extra), rtxInstalled);
                Assert.Equal(OnThisOs(Backend.DirectMl), Selected(vm, rtxInstalled));
            }
        }

        // An unchosen backend follows the component as it is installed or removed while the app runs.
        [Fact]
        public void UnchosenFollowsTheComponent()
        {
            var vm = Load(State("false", false));
            Assert.Equal(OnThisOs(Backend.TensorRt), Selected(vm, rtxInstalled: false));
            Assert.Equal(OnThisOs(Backend.TensorRtRtx), Selected(vm, rtxInstalled: true));
        }

        [Fact]
        public void UnchosenRoundTripsWithoutTheField()
        {
            var json = Save(Load(State("false", false)));

            Assert.Null(JObject.Parse(json).Property("UseTensorRtRtx"));
            var loaded = Load(json);
            Assert.Null(loaded.UseTensorRtRtx);
            Assert.Equal(OnThisOs(Backend.TensorRtRtx), Selected(loaded, rtxInstalled: true));
            Assert.Equal(OnThisOs(Backend.TensorRt), Selected(loaded, rtxInstalled: false));
        }

        // Picking a backend in App Settings makes the choice explicit, saved as a bool.
        [Theory]
        [InlineData(Backend.TensorRt, false)]
        [InlineData(Backend.TensorRtRtx, true)]
        [InlineData(Backend.DirectMl, false)]
        public void PickingABackendRoundTrips(Backend pick, bool savedRtx)
        {
            var vm = Load(State("false", false));
            Action set = pick switch
            {
                Backend.TensorRt => vm.SetBackendTensorRt,
                Backend.TensorRtRtx => vm.SetBackendTensorRtRtx,
                _ => vm.SetBackendDirectMl,
            };
            set();
            Assert.Equal(savedRtx, vm.UseTensorRtRtx);

            var json = Save(vm);
            Assert.Equal(JTokenType.Boolean, JObject.Parse(json)["UseTensorRtRtx"]?.Type);
            foreach (var rtxInstalled in new[] { false, true })
            {
                var loaded = Load(json, rtxInstalled);
                Assert.Equal(savedRtx, loaded.UseTensorRtRtx);
                Assert.Equal(pick == Backend.DirectMl, loaded.UseDirectMl);
                Assert.Equal(OnThisOs(pick), Selected(loaded, rtxInstalled));
            }
        }

        // The backend fields as an older VideoJaNai declares them: UseTensorRtRtx as a plain bool (the
        // first version that had it), which Newtonsoft can't set from null.
        [DataContract]
        public class OlderVersionState
        {
            [DataMember]
            public bool? UseDirectMl { get; set; }

            [DataMember]
            public bool UseTensorRtRtx { get; set; }
        }

        static OlderVersionState LoadAsOlderVersion(string json) =>
            JsonConvert.DeserializeObject<OlderVersionState>(json,
                new JsonSerializerSettings { MetadataPropertyHandling = MetadataPropertyHandling.Ignore })!;

        [Fact]
        public void OlderVersionsLoadTheNewState()
        {
            // why null is never written: it would break that older version's load
            Assert.Throws<JsonSerializationException>(() => LoadAsOlderVersion("{\"UseTensorRtRtx\": null}"));

            var unchosen = LoadAsOlderVersion(Save(Load(State("false", false))));
            Assert.False(unchosen.UseTensorRtRtx);
            Assert.False(unchosen.UseDirectMl);

            var vm = Load(State("false", false));
            vm.SetBackendTensorRtRtx();
            var rtx = LoadAsOlderVersion(Save(vm));
            Assert.True(rtx.UseTensorRtRtx);
            // a version without UseTensorRtRtx ignores it and reads this as TensorRT
            Assert.False(rtx.UseDirectMl);
        }

        [Fact]
        public void SwitchingAwayFromTensorRtRtxClearsIt()
        {
            var vm = Load(State("false", false, Rtx("true")));
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
