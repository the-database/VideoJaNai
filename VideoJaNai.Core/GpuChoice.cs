using System.Collections.Generic;
using System.Linq;

namespace VideoJaNai.Core
{
    // Which GPU upscales and which encoders go with it, from `aji_encode --probe-encoders`. The GUI's
    // App Settings and the command line's --device both go through these.
    public static class GpuChoice
    {
        // The GPUs the backend can upscale on: every GPU on DirectML, the NVIDIA ones on TensorRT
        // and TensorRT-RTX. Only GPUs with an id, so none from an aji_encode without --device.
        // Graphics cards first, integrated graphics last (otherwise in the probe's order): Windows
        // and Direct3D tell those apart, but not which of two graphics cards upscales faster.
        public static IReadOnlyList<EncoderProbe.Gpu> BackendGpus(EncoderProbe? probe, bool directMl) =>
            probe?.Gpus.Where(g => g.Id != null && (directMl || g.Vendor == EncoderGroup.Nvidia))
                .OrderBy(g => g.Integrated == true).ToList()
            ?? [];

        // The saved choice when the backend can use it, else null (Auto: no --device).
        public static EncoderProbe.Gpu? Chosen(EncoderProbe? probe, bool directMl, string? id, string? name, int position) =>
            probe is EncoderProbe p &&
            EncoderProbe.Find(p.Gpus, id, name, position) is EncoderProbe.Gpu g &&
            BackendGpus(probe, directMl).Contains(g)
                ? g : null;

        // The GPU Auto upscales on, when the probe can tell: DirectML's default GPU, or on TensorRT
        // the only NVIDIA GPU (with several, CUDA picks the fastest, which the probe doesn't know).
        public static EncoderProbe.Gpu? Auto(EncoderProbe? probe, bool directMl)
        {
            var gpus = BackendGpus(probe, directMl);
            return directMl
                ? (probe?.DefaultGpu is EncoderProbe.Gpu d && gpus.Contains(d) ? d : null)
                : (gpus.Count == 1 ? gpus[0] : null);
        }

        // The vendor of the GPU that does the upscale: the chosen one, else Auto's, which is an
        // NVIDIA card on TensorRT and the probe's default GPU on DirectML (null when it can't tell).
        public static EncoderGroup? InferenceVendor(EncoderProbe? probe, bool directMl, EncoderProbe.Gpu? chosen) =>
            chosen is EncoderProbe.Gpu g ? g.Vendor
            : !directMl ? EncoderGroup.Nvidia : probe?.DefaultGpuVendor;

        // Works on this PC: CPU presets always; GPU ones when the probe opened their encoder.
        // Without a probe only NVENC HEVC / Lossless are offered, as before detection existed.
        public static bool PresetAvailable(EncoderProbe? probe, EncoderPreset p) =>
            !p.IsGpu ? true
            : probe is EncoderProbe pr ? pr.Opens(p.Codec)
            : p == EncoderPresets.NvencHevc || p == EncoderPresets.NvencLossless;

        // What a workflow nobody picked an encoder for uses: HEVC on the upscaling GPU, else x265.
        public static EncoderPreset DefaultEncoder(EncoderProbe? probe, EncoderGroup? inferenceVendor) =>
            inferenceVendor is EncoderGroup v && EncoderPresets.For(v).FirstOrDefault() is EncoderPreset hevc && PresetAvailable(probe, hevc)
                ? hevc
                : EncoderPresets.X265;
    }

    // The encoder and output format problems that stop a run, worded as the GUI's status bar says
    // them. Components and models are checked by each front end (their wording differs).
    public static class EncodeChecks
    {
        public static IEnumerable<string> EncoderProblems(EncoderProbe? probe, string ffmpegVideoSettings, string pixFmt)
        {
            var preset = EncoderPresets.Find(ffmpegVideoSettings);
            var codec = EncoderPresets.CodecOf(ffmpegVideoSettings);
            var group = preset?.Group ?? EncoderPresets.GroupOf(codec);
            var encoderName = preset?.SummaryLabel ?? codec;
            if (probe is EncoderProbe p && p.Tested(codec) && !p.Opens(codec))
            {
                yield return $"{encoderName} isn't available on this PC (no supported GPU or driver). Choose another encoder.";
            }
            else if (probe is EncoderProbe p10 && pixFmt == "yuv420p10" && !p10.Opens(codec, tenBit: true))
            {
                yield return $"{encoderName} can't encode 10-bit on this PC. Choose 8-bit 4:2:0 or another encoder.";
            }
            if (IsYuv444(pixFmt) && group != EncoderGroup.Cpu)
            {
                yield return $"4:4:4 output needs a CPU encoder (x265 / x264 / FFV1); {encoderName} encodes 4:2:0 only.";
            }
        }

        public static string? DirectMlFormatProblem(Backend backend, string pixFmt) =>
            backend == Backend.DirectMl && IsYuv444(pixFmt)
                ? "DirectML outputs 4:2:0 only; choose a 4:2:0 output pixel format."
                : null;

        public static bool IsYuv444(string pixFmt) => pixFmt.StartsWith("yuv444", System.StringComparison.Ordinal);
    }
}
