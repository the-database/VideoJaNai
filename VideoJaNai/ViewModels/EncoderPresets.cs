using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoJaNai.ViewModels
{
    // Where a video is encoded: the first row of the Output section's encoder choice.
    public enum EncoderGroup { Nvidia, Amd, Intel, Cpu }

    // A preset of the second row: an encoder's codec with its FFmpeg settings (what aji_encode gets
    // as --vcodec / --vquality, see MapFfmpegSettings).
    public sealed record EncoderPreset(
        EncoderGroup Group,
        string Label,
        string SummaryLabel,
        string Settings,
        Func<string> Description)
    {
        // the encoder name, the settings' first word (hevc_nvenc, libx265, ...)
        public string Codec => EncoderPresets.CodecOf(Settings);
        public bool IsGpu => Group != EncoderGroup.Cpu;
    }

    public static class EncoderPresets
    {
        // Every GPU HEVC/AV1 preset targets the ~50 Mbps the NVENC HEVC preset always has, so a
        // file's size doesn't depend on which GPU made it.
        public static readonly EncoderPreset NvencHevc = new(EncoderGroup.Nvidia, "HEVC", "NVENC HEVC",
            MainWindowViewModel._ffmpegHevcNvenc, () => Lang.Resources.FfmpegPresetsDescriptionGpuHevc);
        public static readonly EncoderPreset NvencAv1 = new(EncoderGroup.Nvidia, "AV1", "NVENC AV1",
            "av1_nvenc -preset p7 -b:v 50M -max_interleave_delta 0", () => Lang.Resources.FfmpegPresetsDescriptionGpuAv1);
        public static readonly EncoderPreset NvencLossless = new(EncoderGroup.Nvidia, "Lossless", "NVENC Lossless",
            "hevc_nvenc -preset p7 -tune lossless -max_interleave_delta 0", () => Lang.Resources.FfmpegPresetsDescriptionNvencLossless);
        public static readonly EncoderPreset AmfHevc = new(EncoderGroup.Amd, "HEVC", "AMF HEVC",
            "hevc_amf -quality quality -rc vbr_peak -b:v 50M -maxrate 75M -max_interleave_delta 0", () => Lang.Resources.FfmpegPresetsDescriptionGpuHevc);
        public static readonly EncoderPreset AmfAv1 = new(EncoderGroup.Amd, "AV1", "AMF AV1",
            "av1_amf -quality quality -rc vbr_peak -b:v 50M -maxrate 75M -max_interleave_delta 0", () => Lang.Resources.FfmpegPresetsDescriptionGpuAv1);
        public static readonly EncoderPreset QsvHevc = new(EncoderGroup.Intel, "HEVC", "QSV HEVC",
            "hevc_qsv -preset medium -b:v 50M -maxrate 75M -max_interleave_delta 0", () => Lang.Resources.FfmpegPresetsDescriptionGpuHevc);
        public static readonly EncoderPreset QsvAv1 = new(EncoderGroup.Intel, "AV1", "QSV AV1",
            "av1_qsv -preset medium -b:v 50M -maxrate 75M -max_interleave_delta 0", () => Lang.Resources.FfmpegPresetsDescriptionGpuAv1);
        public static readonly EncoderPreset X265 = new(EncoderGroup.Cpu, "x265 (HEVC)", "x265",
            MainWindowViewModel._ffmpegX265, () => Lang.Resources.FfmpegPresetsDescriptionX265);
        public static readonly EncoderPreset X264 = new(EncoderGroup.Cpu, "x264 (H.264)", "x264",
            MainWindowViewModel._ffmpegX264, () => Lang.Resources.FfmpegPresetsDescriptionX264);
        public static readonly EncoderPreset Ffv1 = new(EncoderGroup.Cpu, "FFV1 (lossless)", "FFV1 Lossless",
            MainWindowViewModel._ffmpegLossless, () => Lang.Resources.FfmpegPresetsDescriptionLossless);

        public static readonly IReadOnlyList<EncoderPreset> All =
            [NvencHevc, NvencAv1, NvencLossless, AmfHevc, AmfAv1, QsvHevc, QsvAv1, X265, X264, Ffv1];

        // What workflows saved before encoder detection hold when nobody picked an encoder: the
        // bundled default state's NVENC preset (the NVENC button writes _ffmpegHevcNvenc instead).
        // Treated like an empty setting, so it follows this PC's default encoder.
        public const string LegacyDefaultSettings = "hevc_nvenc -preset p7 -profile:v main10 -b:v 50M";

        public static EncoderPreset? Find(string? settings) =>
            All.FirstOrDefault(p => string.Equals(p.Settings, settings?.Trim(), StringComparison.Ordinal));

        public static IEnumerable<EncoderPreset> For(EncoderGroup group) => All.Where(p => p.Group == group);

        public static string CodecOf(string? settings)
        {
            var s = (settings ?? "").Trim();
            var space = s.IndexOf(' ');
            return space < 0 ? s : s[..space];
        }

        // The group a codec name runs on; any non-GPU encoder is the CPU's.
        public static EncoderGroup GroupOf(string codec) =>
            codec.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ? EncoderGroup.Nvidia
            : codec.EndsWith("_amf", StringComparison.OrdinalIgnoreCase) ? EncoderGroup.Amd
            : codec.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase) ? EncoderGroup.Intel
            : EncoderGroup.Cpu;

        public static string GroupLabel(EncoderGroup group) => group switch
        {
            EncoderGroup.Nvidia => "NVIDIA GPU",
            EncoderGroup.Amd => "AMD GPU",
            EncoderGroup.Intel => "Intel GPU",
            _ => "CPU",
        };

        // the GPU vendor a probe's GPU line names
        public static EncoderGroup? GroupOfVendor(string vendor) => vendor switch
        {
            "nvidia" => EncoderGroup.Nvidia,
            "amd" => EncoderGroup.Amd,
            "intel" => EncoderGroup.Intel,
            _ => null,
        };

        // A preset of `to` standing in for `from` when the encoder changes: the same kind of codec
        // (HEVC for HEVC and x265, lossless for lossless, AV1 for AV1), else the group's first.
        public static string Family(EncoderPreset p) =>
            p == NvencLossless || p == Ffv1 ? "lossless"
            : p.Codec.StartsWith("av1", StringComparison.Ordinal) ? "av1"
            : p == X264 ? "h264"
            : "hevc";
    }

    // What `aji_encode --probe-encoders` found on this PC:
    //   GPU index=0 vendor=nvidia default=1 integrated=0 id=9223372036854776064 name=NVIDIA GeForce RTX 5090
    //   ENCODER name=hevc_qsv yuv420p=1 yuv420p10=1
    // default=1 is the GPU DirectML runs on without --device. id= is what --device takes (the
    // card's PCI address); aji_encode before animejanai-inference 0.22.0 prints no id= and lists
    // GPUs on Windows only. integrated=1 marks integrated graphics (0.22.2+; null when unknown).
    public sealed class EncoderProbe
    {
        public sealed record Gpu(EncoderGroup? Vendor, bool IsDefault, string Name, string? Id = null,
                                 bool? Integrated = null);

        public IReadOnlyList<Gpu> Gpus { get; }
        private readonly Dictionary<string, (bool Bit8, bool Bit10)> _encoders;

        private EncoderProbe(List<Gpu> gpus, Dictionary<string, (bool, bool)> encoders)
        {
            Gpus = gpus;
            _encoders = encoders;
        }

        // null unless the output has at least one ENCODER line (an aji_encode without the probe
        // prints its usage instead)
        public static EncoderProbe? Parse(string? output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }
            var gpus = new List<Gpu>();
            var encoders = new Dictionary<string, (bool, bool)>(StringComparer.Ordinal);
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("GPU ", StringComparison.Ordinal))
                {
                    var nameAt = line.IndexOf(" name=", StringComparison.Ordinal);
                    var fields = Fields(nameAt < 0 ? line : line[..nameAt]);
                    gpus.Add(new Gpu(
                        EncoderPresets.GroupOfVendor(fields.GetValueOrDefault("vendor", "")),
                        fields.GetValueOrDefault("default") == "1",
                        nameAt < 0 ? "" : line[(nameAt + 6)..],
                        fields.GetValueOrDefault("id"),
                        fields.GetValueOrDefault("integrated") switch { "1" => true, "0" => false, _ => null }));
                }
                else if (line.StartsWith("ENCODER ", StringComparison.Ordinal))
                {
                    var fields = Fields(line);
                    if (fields.TryGetValue("name", out var name))
                    {
                        encoders[name] = (fields.GetValueOrDefault("yuv420p") == "1",
                                          fields.GetValueOrDefault("yuv420p10") == "1");
                    }
                }
            }
            return encoders.Count == 0 ? null : new EncoderProbe(gpus, encoders);
        }

        private static Dictionary<string, string> Fields(string line) =>
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .GroupBy(kv => kv[0])
                .ToDictionary(g => g.Key, g => g.First()[1], StringComparer.Ordinal);

        // Whether the probe tested this encoder; untested ones (software encoders, codecs it
        // doesn't list) are assumed to work.
        public bool Tested(string codec) => _encoders.ContainsKey(codec);

        public bool Opens(string codec) =>
            !_encoders.TryGetValue(codec, out var e) || e.Bit8 || e.Bit10;

        public bool Opens(string codec, bool tenBit) =>
            !_encoders.TryGetValue(codec, out var e) || (tenBit ? e.Bit10 : e.Bit8);

        // the GPU DirectML runs on without --device, when the probe listed GPUs
        public Gpu? DefaultGpu => Gpus.FirstOrDefault(g => g.IsDefault);
        public EncoderGroup? DefaultGpuVendor => DefaultGpu?.Vendor;

        // The saved GPU among `gpus`: by id; if that's gone, the GPU at the saved position when it
        // has the saved name (which tells identical cards apart), else the first with that name;
        // else none, which is Auto. MangaJaNai GUI finds its saved device the same way.
        public static Gpu? Find(IReadOnlyList<Gpu> gpus, string? id, string? name, int position)
        {
            if (id is null)
            {
                return null;
            }
            return gpus.FirstOrDefault(g => g.Id == id)
                ?? (position >= 0 && position < gpus.Count && gpus[position].Name == name ? gpus[position] : null)
                ?? gpus.FirstOrDefault(g => g.Name == name);
        }

        // A GPU's name in the GPU list. GPUs that share a name (two of the same card) get the end of
        // their id, just long enough to tell them apart: "NVIDIA GeForce RTX 3060 (#6512)".
        public static string Label(IReadOnlyList<Gpu> gpus, Gpu gpu)
        {
            var twins = gpus.Where(g => g.Name == gpu.Name).ToList();
            var id = gpu.Id ?? "";
            if (twins.Count < 2 || id.Length == 0)
            {
                return gpu.Name;
            }
            var longest = twins.Max(g => (g.Id ?? "").Length);
            var n = Math.Min(4, longest);
            while (n < longest && twins.Select(g => Tail(g.Id ?? "", n)).Distinct().Count() < twins.Count)
            {
                n++;
            }
            return $"{gpu.Name} (#{Tail(id, n)})";
        }

        private static string Tail(string id, int n) => id.Length <= n ? id : id[^n..];
    }

    // One toggle of the encoder or codec row.
    public sealed class EncoderChoice
    {
        private readonly Action _select;

        public EncoderChoice(string label, string? tooltip, bool isSelected, Action select)
        {
            Label = label;
            Tooltip = tooltip;
            IsSelected = isSelected;
            _select = select;
        }

        public string Label { get; }
        public string? Tooltip { get; }
        public bool IsSelected { get; }

        public void Select() => _select();
    }
}
