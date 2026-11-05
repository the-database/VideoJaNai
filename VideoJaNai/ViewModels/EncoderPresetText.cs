using System;
using VideoJaNai.Core;

namespace VideoJaNai.ViewModels
{
    // The encoder presets' tooltips: localized UI text, so they live here rather than in Core.
    public static class EncoderPresetText
    {
        public static string Description(this EncoderPreset p) =>
            p == EncoderPresets.NvencLossless ? Lang.Resources.FfmpegPresetsDescriptionNvencLossless
            : p == EncoderPresets.X265 ? Lang.Resources.FfmpegPresetsDescriptionX265
            : p == EncoderPresets.X264 ? Lang.Resources.FfmpegPresetsDescriptionX264
            : p == EncoderPresets.Ffv1 ? Lang.Resources.FfmpegPresetsDescriptionLossless
            : p.Codec.StartsWith("av1", StringComparison.Ordinal) ? Lang.Resources.FfmpegPresetsDescriptionGpuAv1
            : Lang.Resources.FfmpegPresetsDescriptionGpuHevc;
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
