using SkiaSharp;

namespace InfoPanel.AudioSpectrum
{
    /// <summary>
    /// Runtime settings holder. The Windows build persists these itself as an ini
    /// sidecar; here persistence is host-managed through IPluginConfigurable
    /// (plugins/&lt;id&gt;.config.json), so this is just the in-memory state.
    /// </summary>
    internal class SpectrumConfig
    {
        // Audio device (empty = monitor of the default sink, or partial source name match)
        public string AudioDevice { get; set; } = "";

        // Spectrum settings
        public int BandCount { get; set; } = 32;
        public int ImageWidth { get; set; } = 400;
        public int ImageHeight { get; set; } = 150;
        public SpectrumStyle Style { get; set; } = SpectrumStyle.Bars;
        public ColorScheme Scheme { get; set; } = ColorScheme.Neon;
        public string CustomColor1 { get; set; } = "#00FF80";
        public string CustomColor2 { get; set; } = "#0080FF";
        public string BackgroundColor { get; set; } = "#FF000000";
        public float BarSpacing { get; set; } = 0.3f;
        public float CornerRadius { get; set; } = 4f;
        public bool ShowPeaks { get; set; } = true;
        public bool ShowReflection { get; set; } = false;
        public bool ShowMirror { get; set; } = false;
        public float Brightness { get; set; } = 1.0f;
        public float Smoothing { get; set; } = 0.05f;
        public float PeakDecay { get; set; } = 0.007f;
        public SpectrumAlignment Alignment { get; set; } = SpectrumAlignment.Left;
        public float ContentWidth { get; set; } = 1.0f;
        public bool CenterOut { get; set; } = false;
        public float Gain { get; set; } = 1.5f;
        public float EdgeBoost { get; set; } = 5f;
        public float NoiseFloor { get; set; } = 0f;
        public int TrimBands { get; set; } = 0;

        public SKColor ParseColor(string colorStr)
        {
            if (string.Equals(colorStr, "Transparent", StringComparison.OrdinalIgnoreCase))
                return SKColors.Transparent;

            if (SKColor.TryParse(colorStr, out var color))
                return color;

            return SKColors.Transparent;
        }
    }
}
