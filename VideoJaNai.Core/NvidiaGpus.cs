using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

// Also compiled into VideoJaNaiUpdater (linked, so the updater keeps no reference on Core): keep it
// self-contained.
namespace VideoJaNai.Core
{
    // An NVIDIA GPU as NVML reports it. Id is its PCI location as aji_encode's --device and
    // --probe-encoders give it (Nvml.PciId), 0 when NVML can't say.
    public sealed record NvidiaGpu(string Name, int Major, int Minor, ulong Id = 0)
    {
        // "sm86", the name TensorRT's builder packs use (the CUDA minor is one digit)
        public string Sm => $"sm{Major}{Minor}";
        public string ComputeCapability => $"{Major}.{Minor}";
        public bool TensorRtCapable => TensorRtGpus.Supports(Major, Minor);
    }

    // Which NVIDIA GPUs TensorRT and TensorRT-RTX can run on, and the packs they need.
    public static class TensorRtGpus
    {
        // The oldest GPU generation TensorRT and TensorRT-RTX run on: Turing (GeForce RTX 20 / GTX 16
        // series). The TensorRT 11 VideoJaNai ships (CUDA 13) supports compute capability 7.5 and up,
        // and libaji's CUDA kernels are built from sm75 up. PTX only compiles forward, so the trt-ptx
        // pack can't help an older card: a GTX 10-series (Pascal, 6.x) or Volta (7.0) GPU counts as no
        // NVIDIA GPU for TensorRT, and upscales on DirectML (Windows).
        public static readonly (int Major, int Minor) MinComputeCapability = (7, 5);

        public static bool Supports(int major, int minor) => (major, minor).CompareTo(MinComputeCapability) >= 0;

        // A compute capability as "sm<major><minor>" (sm61 = 6.1, sm120 = 12.0), or null.
        public static (int Major, int Minor)? ParseSm(string sm)
        {
            var digits = sm.Trim();
            if (digits.StartsWith("sm", StringComparison.OrdinalIgnoreCase))
            {
                digits = digits[2..];
            }
            if (digits.Length < 2 || !digits.All(char.IsAsciiDigit) || !int.TryParse(digits[..^1], out int major))
            {
                return null;
            }
            return (major, digits[^1] - '0');
        }

        public static bool SupportsSm(string sm) => ParseSm(sm) is (int major, int minor) && Supports(major, minor);

        // "7.5+"
        public static string MinText => $"{MinComputeCapability.Major}.{MinComputeCapability.Minor}+";

        // What TensorRT (or TensorRT-RTX: `backend`) needs, for a message that then says what to do.
        public static string NeedsText(string backend) =>
            $"{backend} needs an NVIDIA GeForce RTX 20 / GTX 16 series or newer GPU (compute capability {MinText})";

        // Why `gpu` can't run TensorRT, e.g. "TensorRT needs ... (compute capability 7.5+); NVIDIA
        // GeForce GTX 1080 is compute 6.1."
        public static string TooOldText(NvidiaGpu gpu, string backend = "TensorRT") =>
            $"{NeedsText(backend)}; {gpu.Name} is compute {gpu.ComputeCapability}.";

        // Why TensorRT can't run on this PC when it has NVIDIA GPUs and all of them are too old; null
        // when one can run it, or when there's no NVIDIA GPU at all (a PC without one needs no such
        // explanation: TensorRT just isn't installed there).
        public static string? TooOldText(IReadOnlyList<NvidiaGpu> gpus, string backend = "TensorRT")
        {
            if (gpus.Count == 0 || gpus.Any(g => g.TensorRtCapable))
            {
                return null;
            }
            return gpus.Count == 1
                ? TooOldText(gpus[0], backend)
                : $"{NeedsText(backend)}; this PC has {string.Join(" and ", gpus.Select(g => $"{g.Name} (compute {g.ComputeCapability})"))}.";
        }

        // The TensorRT packs for GPUs of these generations ("sm86"; duplicates are fine), given which
        // packs the release publishes: the runtime, each generation's builder pack (the PTX fallback
        // for a newer one than the packs know), and trt-rtx when one of them is a GPU TensorRT-RTX is
        // the default for. Generations older than MinComputeCapability get nothing: TensorRT can't run
        // there, whatever is installed. No GPU it can run on: no packs (DirectML needs none).
        public static List<string> RecommendedPacks(IEnumerable<string> sms, Func<string, bool> published)
        {
            var usable = sms.Where(SupportsSm).ToList();
            var rec = new List<string>();
            if (usable.Count == 0)
            {
                return rec;
            }
            rec.Add("trt-runtime");
            foreach (var s in usable)
            {
                var pack = published($"trt-{s}") ? $"trt-{s}" : "trt-ptx";
                if (!rec.Contains(pack))
                {
                    rec.Add(pack);
                }
            }
            if (usable.Any(TensorRtRtxDefault) && published("trt-rtx"))
            {
                rec.Add("trt-rtx");
            }
            return rec;
        }

        // Whether TensorRT-RTX is the default backend for a GPU, by its "sm<major><minor>" compute
        // capability. TensorRT-RTX 1.7's support matrix lists 7.5, 8.6, 8.9, 12.0 and 12.1, so: sm75
        // (GTX 16 / RTX 20-series), sm86 (RTX 30), sm89 (RTX 40) and sm120 and newer (RTX 50 and
        // later); not the data-center parts it doesn't list (sm80 A100, sm90 H100, sm100/sm103
        // B200/B300, sm110 Thor). Turing is included although TensorRT-RTX supports neither FP32 GEMMs
        // nor 3D convolutions there: VideoJaNai's models (the AnimeJaNai SPAN models, temporalfix,
        // RIFE) use neither, and libaji falls back to TensorRT for a model that does.
        public static bool TensorRtRtxDefault(string sm) =>
            ParseSm(sm) is (int major, int minor) &&
            ((major == 7 && minor == 5) || (major == 8 && (minor == 6 || minor == 9)) || major >= 12);
    }

    // NVIDIA GPU detection through NVML, which ships in the NVIDIA driver (nvml.dll on Windows,
    // libnvidia-ml.so.1 on Linux), loaded at run time: no driver, no NVIDIA GPU.
    public static unsafe class Nvml
    {
        private static IReadOnlyList<NvidiaGpu>? _gpus;

        // This PC's NVIDIA GPUs, detected once per process.
        public static IReadOnlyList<NvidiaGpu> Gpus => _gpus ??= Detect();

        // Every NVIDIA GPU NVML lists (in its order, which is PCI order), or none when there is no
        // NVIDIA driver or NVML fails.
        public static IReadOnlyList<NvidiaGpu> Detect()
        {
            try
            {
                var lib = OperatingSystem.IsWindows() ? "nvml.dll" : "libnvidia-ml.so.1";
                if (!NativeLibrary.TryLoad(lib, out var h))
                {
                    return [];
                }
                // NVML's C entry points; these signatures are the same in every NVML since the _v2
                // ones (2014). nvmlDeviceGetPciInfo_v3 is in NVML 11+ (R450, 2020).
                var init = (delegate* unmanaged<int>)NativeLibrary.GetExport(h, "nvmlInit_v2");
                var shutdown = (delegate* unmanaged<int>)NativeLibrary.GetExport(h, "nvmlShutdown");
                var count = (delegate* unmanaged<uint*, int>)NativeLibrary.GetExport(h, "nvmlDeviceGetCount_v2");
                var handle = (delegate* unmanaged<uint, IntPtr*, int>)NativeLibrary.GetExport(h, "nvmlDeviceGetHandleByIndex_v2");
                var cc = (delegate* unmanaged<IntPtr, int*, int*, int>)NativeLibrary.GetExport(h, "nvmlDeviceGetCudaComputeCapability");
                var name = (delegate* unmanaged<IntPtr, byte*, uint, int>)NativeLibrary.GetExport(h, "nvmlDeviceGetName");
                NativeLibrary.TryGetExport(h, "nvmlDeviceGetPciInfo_v3", out var pciExport);
                var pci = (delegate* unmanaged<IntPtr, PciInfo*, int>)pciExport;
                if (init() != 0)
                {
                    return [];
                }
                try
                {
                    uint n = 0;
                    if (count(&n) != 0)
                    {
                        return [];
                    }
                    var gpus = new List<NvidiaGpu>();
                    var buf = stackalloc byte[96];
                    for (uint i = 0; i < n; i++)
                    {
                        IntPtr dev;
                        int major, minor;
                        if (handle(i, &dev) != 0 || cc(dev, &major, &minor) != 0)
                        {
                            continue;
                        }
                        var label = name(dev, buf, 96) == 0
                            ? Encoding.ASCII.GetString(buf, 96).Split('\0')[0]
                            : "NVIDIA GPU";
                        ulong id = 0;
                        PciInfo info;
                        if (pci != null && pci(dev, &info) == 0)
                        {
                            id = PciId(info.Domain, info.Bus, info.Device);
                        }
                        gpus.Add(new NvidiaGpu(label, major, minor, id));
                    }
                    return gpus;
                }
                finally
                {
                    shutdown();
                }
            }
            catch
            {
                return [];
            }
        }

        // A GPU's id as aji_encode's --device and --probe-encoders give it: its PCI location,
        // (1 << 63) | domain << 32 | bus << 8 | device.
        public static ulong PciId(uint domain, uint bus, uint device) =>
            (1UL << 63) | ((ulong)domain << 32) | ((ulong)bus << 8) | (device & 0xff);

        // Windows reports no PCI domain for DirectML adapters (0), so ids compare without it unless
        // both carry one, as aji_encode compares them.
        public static bool SameGpu(ulong a, ulong b)
        {
            const ulong pci = 1UL << 63, domain = 0x7fffffff00000000UL;
            if ((a & pci) != 0 && (b & pci) != 0 && ((a & domain) == 0 || (b & domain) == 0))
            {
                a &= ~domain;
                b &= ~domain;
            }
            return a == b;
        }

        // nvmlPciInfo_t
        [StructLayout(LayoutKind.Sequential)]
        private struct PciInfo
        {
            public fixed byte BusIdLegacy[16];
            public uint Domain;
            public uint Bus;
            public uint Device;
            public uint PciDeviceId;
            public uint PciSubSystemId;
            public fixed byte BusId[32];
        }
    }
}
