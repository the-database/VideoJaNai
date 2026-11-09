using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VideoJaNai.Cli;
using VideoJaNai.Core;
using Xunit;

namespace VideoJaNai.Core.Tests
{
    // NVIDIA GPUs older than compute capability 7.5 (GeForce GTX 10-series and older, Volta) can't run
    // TensorRT or TensorRT-RTX, so they count as no NVIDIA GPU there: DirectML on Windows, no TensorRT
    // on Linux, and next to a newer card only that card counts (the-database/VideoJaNai#70).
    public class TensorRtGpusTests
    {
        static readonly ulong Bus1 = Nvml.PciId(0, 1, 0), Bus2 = Nvml.PciId(0, 2, 0), Bus3 = Nvml.PciId(0, 3, 0);
        static readonly NvidiaGpu Gtx1080 = new("NVIDIA GeForce GTX 1080", 6, 1, Bus1);
        static readonly NvidiaGpu Rtx3080 = new("NVIDIA GeForce RTX 3080", 8, 6, Bus2);
        static readonly NvidiaGpu TitanV = new("NVIDIA TITAN V", 7, 0, Bus3);

        // aji_encode --probe-encoders on a PC with these GPUs (NVIDIA on buses 1 and 2, AMD on 3)
        static EncoderProbe Probe(params string[] gpus) => EncoderProbe.Parse(string.Join("\n", gpus.Append("ENCODER name=hevc_nvenc yuv420p=1 yuv420p10=1")))!;
        static string Gpu(int index, string vendor, ulong id, string name, bool isDefault = false) =>
            $"GPU index={index} vendor={vendor} default={(isDefault ? 1 : 0)} integrated=0 id={id} name={name}";
        static readonly string ProbeGtx1080 = Gpu(0, "nvidia", Bus1, Gtx1080.Name, isDefault: true);
        static readonly string ProbeRtx3080 = Gpu(1, "nvidia", Bus2, Rtx3080.Name);
        static readonly string ProbeAmd = Gpu(2, "amd", Bus3, "AMD Radeon RX 7800 XT");

        [Theory]
        [InlineData(6, 1, false)]
        [InlineData(7, 0, false)]
        [InlineData(7, 2, false)]
        [InlineData(7, 5, true)]
        [InlineData(8, 6, true)]
        [InlineData(12, 0, true)]
        [InlineData(13, 0, true)]
        public void TensorRtNeedsComputeCapability75(int major, int minor, bool supported)
        {
            Assert.Equal(supported, TensorRtGpus.Supports(major, minor));
            Assert.Equal(supported, TensorRtGpus.SupportsSm($"sm{major}{minor}"));
            Assert.Equal(supported, new NvidiaGpu("g", major, minor).TensorRtCapable);
        }

        [Fact]
        public void OldGpusGetNoTensorRtPacks()
        {
            var published = new HashSet<string> { "trt-runtime", "trt-sm75", "trt-sm86", "trt-sm120", "trt-ptx", "trt-rtx" };
            List<string> Rec(params string[] sms) => TensorRtGpus.RecommendedPacks(sms, published.Contains);

            // not the PTX fallback: it can't run on an older generation either
            Assert.Empty(Rec("sm61"));
            Assert.Empty(Rec("sm70"));
            Assert.Empty(Rec("sm52", "sm61"));
            // next to a newer card, only that card counts
            Assert.Equal(["trt-runtime", "trt-sm86", "trt-rtx"], Rec("sm61", "sm86"));
            Assert.Equal(["trt-runtime", "trt-sm120", "trt-rtx"], Rec("sm70", "sm120"));
            // the PTX fallback stays for a newer generation than the packs know
            Assert.Equal(["trt-runtime", "trt-ptx", "trt-rtx"], Rec("sm130"));
            Assert.Equal(["trt-runtime", "trt-sm75", "trt-rtx"], Rec("sm75"));
            Assert.Equal(["trt-runtime", "trt-ptx"], TensorRtGpus.RecommendedPacks(["sm80"], published.Contains));
            Assert.Empty(Rec());
        }

        [Fact]
        public void TheMessageSaysWhyAndOnlyWhenNoGpuCanRunIt()
        {
            Assert.Equal("TensorRT needs an NVIDIA GeForce RTX 20 / GTX 16 series or newer GPU (compute capability 7.5+); " +
                         "NVIDIA GeForce GTX 1080 is compute 6.1.", TensorRtGpus.TooOldText([Gtx1080]));
            Assert.StartsWith("TensorRT-RTX needs", TensorRtGpus.TooOldText([Gtx1080], "TensorRT-RTX"));
            Assert.Contains("this PC has NVIDIA GeForce GTX 1080 (compute 6.1) and NVIDIA TITAN V (compute 7.0)",
                            TensorRtGpus.TooOldText([Gtx1080, TitanV]));
            Assert.Null(TensorRtGpus.TooOldText([Gtx1080, Rtx3080]));
            Assert.Null(TensorRtGpus.TooOldText([Rtx3080]));
            Assert.Null(TensorRtGpus.TooOldText([]));   // no NVIDIA GPU: nothing to explain
        }

        [Fact]
        public void TensorRtListsOnlyTheGpusItRunsOn()
        {
            var probe = Probe(ProbeGtx1080, ProbeRtx3080, ProbeAmd).WithNvidia([Gtx1080, Rtx3080]);
            Assert.Equal([Rtx3080.Name], GpuChoice.BackendGpus(probe, directMl: false).Select(g => g.Name));
            Assert.Equal(3, GpuChoice.BackendGpus(probe, directMl: true).Count);
            // the GTX 1080 chosen in App Settings: on TensorRT that's Auto, which names the RTX 3080,
            // since CUDA might otherwise pick the old card
            var gtx = probe.Gpus[0];
            Assert.Null(GpuChoice.Chosen(probe, false, gtx.Id, gtx.Name, 0));
            Assert.Equal(gtx, GpuChoice.Chosen(probe, true, gtx.Id, gtx.Name, 0));
            Assert.Equal(Bus2.ToString(), GpuChoice.Device(probe, false, null));
            Assert.Equal(Rtx3080.Name, GpuChoice.Auto(probe, false)!.Name);
            Assert.Null(GpuChoice.Device(probe, true, null));
            Assert.Equal(gtx.Id, GpuChoice.Device(probe, true, gtx));

            // only the GTX 1080: no GPU for TensorRT
            var old = Probe(ProbeGtx1080, ProbeAmd).WithNvidia([Gtx1080]);
            Assert.Empty(GpuChoice.BackendGpus(old, directMl: false));
            Assert.Null(GpuChoice.Device(old, false, null));

            // NVML not there, or GPUs it can't match: every NVIDIA GPU counts, as before
            var unknown = Probe(ProbeGtx1080, ProbeRtx3080).WithNvidia([]);
            Assert.Equal(2, GpuChoice.BackendGpus(unknown, directMl: false).Count);
            Assert.Null(GpuChoice.Device(unknown, false, null));
            // two newer cards next to the old one: Auto stays CUDA's pick among them
            var two = Probe(ProbeGtx1080, ProbeRtx3080, Gpu(2, "nvidia", Bus3, "NVIDIA GeForce RTX 4090"))
                .WithNvidia([Gtx1080, Rtx3080, new NvidiaGpu("NVIDIA GeForce RTX 4090", 8, 9, Bus3)]);
            Assert.Equal(2, GpuChoice.BackendGpus(two, directMl: false).Count);
            Assert.Null(GpuChoice.Device(two, false, null));
        }

        [Fact]
        public void ProbeGpusMatchNvmlByPciIdElseByName()
        {
            // Windows reports DirectML adapters without the PCI domain; NVML has it
            var withDomain = Gtx1080 with { Id = Nvml.PciId(1, 1, 0) };
            var probe = Probe(ProbeGtx1080, Gpu(1, "nvidia", 12345, "NVIDIA TITAN V")).WithNvidia([withDomain, TitanV with { Id = 0 }]);
            Assert.Equal(withDomain, probe.Gpus[0].Nvidia);
            Assert.Equal("NVIDIA TITAN V", probe.Gpus[1].Nvidia?.Name);
            Assert.All(probe.Gpus, g => Assert.False(g.TensorRtCapable));
            // an AMD GPU is never one TensorRT runs on
            Assert.False(Probe(ProbeAmd).WithNvidia([Gtx1080]).Gpus[0].TensorRtCapable);
        }

        // The command line: Auto takes DirectML on a PC whose NVIDIA GPUs are all too old, and an
        // explicit TensorRT stops before the run with why.
        [Fact]
        public void TheCommandLineDoesNotPickTensorRtForAnOldGpu()
        {
            using var t = new TempDir();
            t.File(Path.Join("animejanai", "inference", Platform.Exe("aji_encode")));
            t.File(Path.Join("animejanai", "inference", Platform.Exe("trtexec")));
            var install = Install.Find(t.Path);
            Assert.True(install.TensorRtInstalled);

            var old = Probe(ProbeGtx1080, ProbeAmd).WithNvidia([Gtx1080]);
            var mixed = Probe(ProbeGtx1080, ProbeRtx3080).WithNvidia([Gtx1080, Rtx3080]);
            var tensorRt = BackendNames.Resolve(false, false, install.TensorRtRtxInstalled);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(Backend.DirectMl, Resolve.PickBackend(null, null, install, old, [Gtx1080]));
                Assert.Equal(Backend.DirectMl, Resolve.PickBackend(null, null, install, null, [Gtx1080]));
            }
            else
            {
                Assert.Contains("TensorRT only", Assert.Throws<RunException>(() => Resolve.PickBackend(null, null, install, old, [Gtx1080])).Message);
            }
            Assert.Equal(tensorRt, Resolve.PickBackend(null, null, install, mixed, [Gtx1080, Rtx3080]));
            Assert.Equal(tensorRt, Resolve.PickBackend(null, null, install, null, [Rtx3080]));

            var e = Assert.Throws<RunException>(() => Resolve.PickBackend("tensorrt", null, install, old, [Gtx1080]));
            Assert.StartsWith("TensorRT needs an NVIDIA GeForce RTX 20 / GTX 16 series or newer GPU (compute capability 7.5+); " +
                              "NVIDIA GeForce GTX 1080 is compute 6.1.", e.Message);
            Assert.Equal(Backend.TensorRt, Resolve.PickBackend("tensorrt", null, install, mixed, [Gtx1080, Rtx3080]));
            // no NVIDIA GPU at all: nothing changes (TensorRT is just missing there)
            Assert.Equal(Backend.TensorRt, Resolve.PickBackend("tensorrt", null, install, null, []));

            // --device naming the old card, on TensorRT
            var gpu = Assert.Throws<RunException>(() => Resolve.PickGpu("0", null, mixed, Backend.TensorRt));
            Assert.StartsWith("TensorRT needs", gpu.Message);
            Assert.Contains("GTX 1080 is compute 6.1", gpu.Message);
            Assert.Equal(Rtx3080.Name, Resolve.PickGpu("1", null, mixed, Backend.TensorRt)!.Name);
            Assert.Equal(Gtx1080.Name, Resolve.PickGpu("0", null, mixed, Backend.DirectMl)!.Name);
        }
    }
}
