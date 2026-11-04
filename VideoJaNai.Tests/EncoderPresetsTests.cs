using AnimeJaNaiConverterGui.Services;
using Newtonsoft.Json;
using Splat;
using System.Linq;
using VideoJaNai.ViewModels;
using Xunit;

namespace VideoJaNai.Tests
{
    // The Output section's encoder rows: what `aji_encode --probe-encoders` found decides which
    // encoders are listed, a workflow nobody picked an encoder for follows the upscaling GPU, and a
    // pick is never changed for the person.
    public class EncoderPresetsTests
    {
        static EncoderPresetsTests()
        {
            Locator.CurrentMutable.RegisterConstant<IPythonService>(new FakePythonService());
            Locator.CurrentMutable.RegisterConstant<IUpdateManagerService>(new FakeUpdateManagerService());
        }

        // RTX 5090 + i9-13900K's UHD 770, as probed
        const string NvidiaIntelProbe = """
            GPU index=0 vendor=nvidia default=1 name=NVIDIA GeForce RTX 5090
            GPU index=1 vendor=intel default=0 name=Intel(R) UHD Graphics 770
            ENCODER name=hevc_nvenc yuv420p=1 yuv420p10=1
            ENCODER name=h264_nvenc yuv420p=1 yuv420p10=1
            ENCODER name=av1_nvenc yuv420p=1 yuv420p10=1
            ENCODER name=hevc_amf yuv420p=0 yuv420p10=0
            ENCODER name=h264_amf yuv420p=0 yuv420p10=0
            ENCODER name=av1_amf yuv420p=0 yuv420p10=0
            ENCODER name=hevc_qsv yuv420p=1 yuv420p10=1
            ENCODER name=h264_qsv yuv420p=1 yuv420p10=0
            ENCODER name=av1_qsv yuv420p=0 yuv420p10=0
            """;

        // a Radeon-only PC (DirectML)
        const string AmdProbe = """
            GPU index=0 vendor=amd default=1 name=AMD Radeon RX 7800 XT
            ENCODER name=hevc_nvenc yuv420p=0 yuv420p10=0
            ENCODER name=av1_nvenc yuv420p=0 yuv420p10=0
            ENCODER name=hevc_amf yuv420p=1 yuv420p10=1
            ENCODER name=av1_amf yuv420p=1 yuv420p10=1
            ENCODER name=hevc_qsv yuv420p=0 yuv420p10=0
            ENCODER name=av1_qsv yuv420p=0 yuv420p10=0
            """;

        static MainWindowViewModel Load(string? probe, bool directMl, string? ffmpegSettings = null, string pixFmt = "yuv420p10")
        {
            var json = $$"""
                {
                  "$type": "VideoJaNai.ViewModels.MainWindowViewModel, VideoJaNai",
                  "UseDirectMl": {{(directMl ? "true" : "false")}},
                  "Workflows": {
                    "$type": "Avalonia.Collections.AvaloniaList`1[[VideoJaNai.ViewModels.UpscaleWorkflow, VideoJaNai]], Avalonia.Base",
                    "$values": [
                      {
                        "$type": "VideoJaNai.ViewModels.UpscaleWorkflow, VideoJaNai",
                        "WorkflowName": "Workflow 1",
                        "WorkflowIndex": 0,
                        {{(ffmpegSettings is null ? "" : $"\"FfmpegVideoSettings\": {JsonConvert.ToString(ffmpegSettings)},")}}
                        "OutputPixFmt": "{{pixFmt}}"
                      }
                    ]
                  },
                  "SelectedWorkflowIndex": 0
                }
                """;
            var vm = (MainWindowViewModel)JsonConvert.DeserializeObject<object>(json, NewtonsoftJsonSuspensionDriver.Settings)!;
            foreach (var wf in vm.Workflows)
            {
                wf.Vm = vm;
            }
            vm.EncoderProbeOutput = probe;
            return vm;
        }

        static string[] Labels(System.Collections.Generic.IEnumerable<EncoderChoice> choices) => choices.Select(c => c.Label).ToArray();

        [Fact]
        public void ProbeIsParsed()
        {
            var p = EncoderProbe.Parse(NvidiaIntelProbe)!;
            Assert.Equal(EncoderGroup.Nvidia, p.DefaultGpuVendor);
            Assert.Equal("Intel(R) UHD Graphics 770", p.Gpus[1].Name);
            Assert.True(p.Opens("hevc_qsv", tenBit: true));
            Assert.True(p.Opens("h264_qsv", tenBit: false));
            Assert.False(p.Opens("h264_qsv", tenBit: true));
            Assert.False(p.Opens("av1_qsv"));
            Assert.True(p.Opens("libx265"));          // untested: software
            Assert.False(p.Tested("libx265"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("unknown arg: --probe-encoders\naji_encode --input <f> --output <f> ...")]
        public void NoProbeWithoutEncoderLines(string? output) => Assert.Null(EncoderProbe.Parse(output));

        [Fact]
        public void TensorRtListsTheNvidiaGpuAndCpu()
        {
            var wf = Load(NvidiaIntelProbe, directMl: false).Workflows[0];
            Assert.Equal(["NVIDIA GPU", "CPU"], Labels(wf.EncoderGroupChoices));
            Assert.Equal(["HEVC", "AV1", "Lossless"], Labels(wf.CodecChoices));
            Assert.True(wf.ShowOtherGpuEncodersOption);
        }

        [Fact]
        public void OtherGpusAreListedOnRequestAndOnlyIfTheyWork()
        {
            var vm = Load(NvidiaIntelProbe, directMl: false);
            vm.ShowOtherGpuEncoders = true;
            var wf = vm.Workflows[0];
            Assert.Equal(["NVIDIA GPU", "Intel GPU", "CPU"], Labels(wf.EncoderGroupChoices));    // no AMD: AMF failed
            wf.SelectEncoderGroup(EncoderGroup.Intel);
            Assert.Equal(["HEVC"], Labels(wf.CodecChoices));                                     // no AV1 on a UHD 770
        }

        [Fact]
        public void DefaultFollowsTheUpscalingGpu()
        {
            Assert.Same(EncoderPresets.NvencHevc, Load(NvidiaIntelProbe, directMl: false).Workflows[0].SelectedPreset);
            Assert.Same(EncoderPresets.AmfHevc, Load(AmdProbe, directMl: true).Workflows[0].SelectedPreset);
            // DirectML on an AMD GPU whose encoder doesn't open: the CPU
            Assert.Same(EncoderPresets.X265, Load(AmdProbe.Replace("hevc_amf yuv420p=1 yuv420p10=1", "hevc_amf yuv420p=0 yuv420p10=0"),
                                                  directMl: true).Workflows[0].SelectedPreset);
        }

        [Fact]
        public void TheOldBundledDefaultIsADefaultToo()
        {
            var wf = Load(AmdProbe, directMl: true, ffmpegSettings: EncoderPresets.LegacyDefaultSettings).Workflows[0];
            Assert.True(wf.UsesDefaultEncoder);
            Assert.Same(EncoderPresets.AmfHevc, wf.SelectedPreset);
            Assert.StartsWith("hevc_amf ", wf.EffectiveFfmpegVideoSettings);
        }

        [Fact]
        public void WithoutAProbeEncodersAreAsBefore()
        {
            var wf = Load(null, directMl: false).Workflows[0];
            Assert.Same(EncoderPresets.NvencHevc, wf.SelectedPreset);
            Assert.Equal(["NVIDIA GPU", "CPU"], Labels(wf.EncoderGroupChoices));
            Assert.Equal(["HEVC", "Lossless"], Labels(wf.CodecChoices));
        }

        [Fact]
        public void AnUnavailablePickIsKeptAndReported()
        {
            var vm = Load(AmdProbe, directMl: true, ffmpegSettings: EncoderPresets.NvencHevc.Settings);
            var wf = vm.Workflows[0];
            Assert.Same(EncoderPresets.NvencHevc, wf.SelectedPreset);
            Assert.Contains("NVIDIA GPU", Labels(wf.EncoderGroupChoices));                       // still shown, selected
            wf.Validate();
            Assert.False(wf.Valid);
            Assert.Contains("NVENC HEVC isn't available on this PC", vm.ValidationText);
        }

        [Fact]
        public void TenBitTheEncoderCantDoIsReported()
        {
            var vm = Load(NvidiaIntelProbe, directMl: false, ffmpegSettings: "h264_qsv -preset medium");
            var wf = vm.Workflows[0];
            Assert.Null(wf.SelectedPreset);                                                      // custom
            Assert.Equal(EncoderGroup.Intel, wf.SelectedEncoderGroup);
            Assert.False(wf.Output10BitEnabled);
            wf.Validate();
            Assert.Contains("can't encode 10-bit", vm.ValidationText);
        }

        [Fact]
        public void SwitchingEncoderKeepsTheKindOfCodec()
        {
            var vm = Load(NvidiaIntelProbe, directMl: false);
            var wf = vm.Workflows[0];
            wf.SelectPreset(EncoderPresets.NvencLossless);
            wf.SelectEncoderGroup(EncoderGroup.Cpu);
            Assert.Same(EncoderPresets.Ffv1, wf.SelectedPreset);
            wf.SelectEncoderGroup(EncoderGroup.Nvidia);
            Assert.Same(EncoderPresets.NvencLossless, wf.SelectedPreset);
            wf.SelectPreset(EncoderPresets.NvencAv1);
            wf.SelectEncoderGroup(EncoderGroup.Cpu);
            Assert.Same(EncoderPresets.X265, wf.SelectedPreset);                                // no CPU AV1 preset: the first
            Assert.False(wf.UsesDefaultEncoder);
        }

        [Fact]
        public void PickingAGpuEncoderLeaves444()
        {
            var vm = Load(NvidiaIntelProbe, directMl: false, ffmpegSettings: EncoderPresets.X265.Settings, pixFmt: "yuv444p10");
            var wf = vm.Workflows[0];
            Assert.True(wf.Output444Enabled);
            wf.SelectEncoderGroup(EncoderGroup.Nvidia);
            Assert.Same(EncoderPresets.NvencHevc, wf.SelectedPreset);
            Assert.Equal("yuv420p10", wf.OutputPixFmt);
            Assert.False(wf.Output444Enabled);
        }

        // ---- GPU choice ----------------------------------------------------------------------

        const string RtxId = "9223372036854776064", UhdId = "9223372036854775810";

        // the same PC, probed by an aji_encode with --device (animejanai-inference 0.22.0+)
        static readonly string NvidiaIntelProbeWithIds = NvidiaIntelProbe
            .Replace("default=1 name=NVIDIA", $"default=1 id={RtxId} name=NVIDIA")
            .Replace("default=0 name=Intel", $"default=0 id={UhdId} name=Intel");

        static string Auto(string? name = null) => name is null
            ? VideoJaNai.Lang.Resources.GpuAutoLabel
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, VideoJaNai.Lang.Resources.GpuAutoNamedLabel, name);

        [Fact]
        public void ProbeIdsAreParsed()
        {
            var p = EncoderProbe.Parse(NvidiaIntelProbeWithIds)!;
            Assert.Equal(RtxId, p.Gpus[0].Id);
            Assert.Equal("Intel(R) UHD Graphics 770", p.Gpus[1].Name);
            Assert.Null(EncoderProbe.Parse(NvidiaIntelProbe)!.Gpus[0].Id);
        }

        [Fact]
        public void DirectMlOffersEveryGpuAndAutoNamesItsGpu()
        {
            var vm = Load(NvidiaIntelProbeWithIds, directMl: true);
            Assert.True(vm.GpuChoiceVisible);
            Assert.Equal([Auto("NVIDIA GeForce RTX 5090"), "NVIDIA GeForce RTX 5090", "Intel(R) UHD Graphics 770"], vm.GpuChoices);
            Assert.Equal(0, vm.SelectedGpuChoiceIndex);
            Assert.Null(vm.ChosenGpu);
        }

        [Fact]
        public void TheEncodersFollowTheChosenGpu()
        {
            var vm = Load(NvidiaIntelProbeWithIds, directMl: true);
            vm.SelectedGpuChoiceIndex = 2;
            Assert.Equal(UhdId, vm.ChosenGpu?.Id);
            Assert.Equal((UhdId, "Intel(R) UHD Graphics 770", 1), (vm.SelectedGpuId, vm.SelectedGpuName, vm.SelectedGpuPosition));
            Assert.Equal(EncoderGroup.Intel, vm.InferenceGpuVendor);
            Assert.Same(EncoderPresets.QsvHevc, vm.Workflows[0].SelectedPreset);
            vm.SelectedGpuChoiceIndex = 0;
            Assert.Null(vm.SelectedGpuId);
            Assert.Same(EncoderPresets.NvencHevc, vm.Workflows[0].SelectedPreset);
        }

        [Fact]
        public void TensorRtOnlyOffersNvidiaGpus()
        {
            var vm = Load(NvidiaIntelProbeWithIds, directMl: true);
            vm.SelectedGpuChoiceIndex = 2;          // the UHD 770, then a backend it can't run
            vm.SetBackendTensorRt();
            Assert.False(vm.GpuChoiceVisible);      // one NVIDIA GPU: nothing to choose
            Assert.Null(vm.ChosenGpu);              // so Auto, and no --device
            Assert.Equal(EncoderGroup.Nvidia, vm.InferenceGpuVendor);
            vm.SetBackendDirectMl();
            Assert.Equal(UhdId, vm.ChosenGpu?.Id);  // the choice is kept for DirectML
        }

        [Fact]
        public void NoChoiceFromAnAjiEncodeWithoutDevice()
        {
            var vm = Load(NvidiaIntelProbe, directMl: true);
            Assert.False(vm.GpuChoiceVisible);
            Assert.Empty(vm.BackendGpus);
        }

        [Fact]
        public void ASavedGpuWhoseIdIsGoneIsFoundByNameElseAuto()
        {
            var gpus = EncoderProbe.Parse(NvidiaIntelProbeWithIds)!.Gpus;
            Assert.Equal(UhdId, EncoderProbe.Find(gpus, "123", "Intel(R) UHD Graphics 770", 1)?.Id);
            Assert.Equal(UhdId, EncoderProbe.Find(gpus, "123", "Intel(R) UHD Graphics 770", 5)?.Id);
            Assert.Null(EncoderProbe.Find(gpus, "123", "Radeon RX 7800 XT", 0));
            Assert.Null(EncoderProbe.Find(gpus, null, null, -1));
        }

        [Fact]
        public void IdenticalCardsAreToldApartAndFoundAgainByPosition()
        {
            var probe = """
                GPU index=0 vendor=nvidia default=1 id=9223372036854776064 name=NVIDIA GeForce RTX 3060
                GPU index=1 vendor=nvidia default=0 id=9223372036854785280 name=NVIDIA GeForce RTX 3060
                ENCODER name=hevc_nvenc yuv420p=1 yuv420p10=1
                """;
            var vm = Load(probe, directMl: false);
            Assert.Equal([Auto(), "NVIDIA GeForce RTX 3060 (#6064)", "NVIDIA GeForce RTX 3060 (#5280)"], vm.GpuChoices);
            var gpus = vm.Probe!.Gpus;
            // the second card moved slot: its new id is unknown, its position still names it
            Assert.Equal(gpus[1], EncoderProbe.Find(gpus, "1", "NVIDIA GeForce RTX 3060", 1));
        }

        // The App Settings GPU list, bound as MainWindow.axaml binds it: replacing its items when the
        // backend changes must not clear the choice.
        [Avalonia.Headless.XUnit.AvaloniaFact]
        public void TheGpuListKeepsItsChoiceAcrossABackendChange()
        {
            var vm = Load(NvidiaIntelProbeWithIds, directMl: true);
            var list = new Avalonia.Controls.ComboBox { DataContext = vm };
            list.Bind(Avalonia.Controls.ItemsControl.ItemsSourceProperty, new Avalonia.Data.Binding(nameof(vm.GpuChoices)));
            list.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty,
                      new Avalonia.Data.Binding(nameof(vm.SelectedGpuChoiceIndex)) { Mode = Avalonia.Data.BindingMode.TwoWay });
            var window = new Avalonia.Controls.Window { Content = list };
            window.Show();

            list.SelectedIndex = 2;
            Assert.Equal(UhdId, vm.SelectedGpuId);
            vm.SetBackendTensorRt();
            window.UpdateLayout();
            Assert.Equal(UhdId, vm.SelectedGpuId);
            vm.SetBackendDirectMl();
            window.UpdateLayout();
            Assert.Equal(2, list.SelectedIndex);
            Assert.Equal(UhdId, vm.ChosenGpu?.Id);
        }

        [Fact]
        public void TypedSettingsBecomeTheWorkflowsOwn()
        {
            var wf = Load(NvidiaIntelProbe, directMl: false).Workflows[0];
            Assert.Equal(EncoderPresets.NvencHevc.Settings, wf.FfmpegVideoSettingsText);
            wf.FfmpegVideoSettingsText = "libx265 -crf 18 -preset medium";
            Assert.False(wf.UsesDefaultEncoder);
            Assert.Null(wf.SelectedPreset);
            Assert.Equal(EncoderGroup.Cpu, wf.SelectedEncoderGroup);
            Assert.Equal("libx265 -crf 18 -preset medium", wf.EffectiveFfmpegVideoSettings);
        }
    }
}
