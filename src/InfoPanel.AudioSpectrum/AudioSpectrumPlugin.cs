using InfoPanel.Plugins;
using InfoPanel.Plugins.Graphics;
using Serilog;
using SkiaSharp;
using System.Diagnostics;

namespace InfoPanel.AudioSpectrum
{
    public class AudioSpectrumPlugin : BasePlugin, IPluginConfigurable, IPluginImageProvider
    {
        private static readonly ILogger Logger = Log.ForContext<AudioSpectrumPlugin>();

        private readonly List<PluginContainer> _containers = [];
        private readonly SpectrumConfig _config = new();
        private List<PluginConfigProperty>? _configProperties;

        private AudioCapture? _capture;
        private SpectrumAnalyzer? _analyzer;
        private SpectrumRenderer? _renderer;

        private IPluginImageWriter? _spectrumWriter;

        private PluginSensor[] _bandSensors = [];
        private PluginSensor? _peakSensor;
        private PluginSensor? _averageSensor;
        private PluginSensor? _audioLevelSensor;
        private PluginSensor? _dataCountSensor;
        private PluginText? _deviceText;

        public AudioSpectrumPlugin() : base(
            "audio-spectrum",
            "Audio Spectrum",
            "Real-time audio spectrum visualizer for the system output (PulseAudio/PipeWire).")
        {
        }

        public override TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(33); // ~30fps

        public override void Initialize()
        {
            // Initialize audio capture
            _capture = new AudioCapture();
            _capture.Start(string.IsNullOrEmpty(_config.AudioDevice) ? null : _config.AudioDevice);

            // Initialize analyzer
            _analyzer = new SpectrumAnalyzer(_config.BandCount, _config.Smoothing, _config.PeakDecay, _config.Gain);

            // Initialize renderer
            _renderer = new SpectrumRenderer
            {
                Style = _config.Style,
                Scheme = _config.Scheme,
                CustomColor1 = _config.ParseColor(_config.CustomColor1),
                CustomColor2 = _config.ParseColor(_config.CustomColor2),
                BackgroundColor = _config.ParseColor(_config.BackgroundColor),
                BarSpacing = _config.BarSpacing,
                CornerRadius = _config.Style == SpectrumStyle.Rounded ? MathF.Max(_config.CornerRadius, 10f) : _config.CornerRadius,
                ShowPeaks = _config.ShowPeaks,
                ShowReflection = _config.ShowReflection,
                ShowMirror = _config.ShowMirror,
                Brightness = _config.Brightness,
                Alignment = _config.Alignment,
                ContentWidth = _config.ContentWidth,
                CenterOut = _config.CenterOut,
                EdgeBoost = _config.EdgeBoost,
                NoiseFloor = _config.NoiseFloor,
                TrimBands = _config.TrimBands
            };

            // Create containers and sensors
            var spectrumContainer = new PluginContainer("Spectrum");

            _peakSensor = new PluginSensor("peak", "Peak Level", 0, "%");
            _averageSensor = new PluginSensor("average", "Average Level", 0, "%");
            _audioLevelSensor = new PluginSensor("audio-level", "Audio Input Level", 0, "");
            _dataCountSensor = new PluginSensor("data-count", "Data Callbacks", 0, "");
            _deviceText = new PluginText("device", "Audio Device",
                string.IsNullOrEmpty(_capture.LastError)
                    ? _capture.DeviceName
                    : $"ERROR: {_capture.LastError}");
            spectrumContainer.Entries.Add(_peakSensor);
            spectrumContainer.Entries.Add(_averageSensor);
            spectrumContainer.Entries.Add(_audioLevelSensor);
            spectrumContainer.Entries.Add(_dataCountSensor);
            spectrumContainer.Entries.Add(_deviceText);

            _containers.Add(spectrumContainer);

            // Per-band sensors
            var bandsContainer = new PluginContainer("Bands");
            _bandSensors = new PluginSensor[_config.BandCount];
            for (int i = 0; i < _config.BandCount; i++)
            {
                string name = GetBandLabel(i, _config.BandCount);
                _bandSensors[i] = new PluginSensor($"band_{i}", name, 0, "%");
                bandsContainer.Entries.Add(_bandSensors[i]);
            }
            _containers.Add(bandsContainer);

            Logger.Information("AudioSpectrum initialized: {Bands} bands, {W}x{H}, style={Style}",
                _config.BandCount, _config.ImageWidth, _config.ImageHeight, _config.Style);
        }

        #region IPluginImageProvider

        public IReadOnlyList<PluginImageDescriptor> ImageDescriptors =>
        [
            new("spectrum", "Spectrum", _config.ImageWidth, _config.ImageHeight)
        ];

        public void OnImageBuffersReady(IReadOnlyDictionary<string, IPluginImageWriter> writers)
        {
            _spectrumWriter = writers["spectrum"];
            Logger.Information("Image buffer ready: {W}x{H}", _spectrumWriter.Width, _spectrumWriter.Height);
        }

        #endregion

        public override void Close()
        {
            _capture?.Dispose();
            _renderer?.Dispose();
            _capture = null;
            _renderer = null;
            _analyzer = null;
            _spectrumWriter = null;

            Logger.Information("AudioSpectrum closed");
        }

        public override void Load(List<IPluginContainer> containers)
        {
            containers.AddRange(_containers);
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            Update();
            return Task.CompletedTask;
        }

        public override void Update()
        {
            if (_capture == null || _analyzer == null || _renderer == null) return;

            try
            {
                var samples = _capture.GetLatestSamples();
                _analyzer.ProcessSamples(samples, _capture.SampleRate > 0 ? _capture.SampleRate : 48000);

                var bands = _analyzer.SmoothedBands;
                var peaks = _analyzer.PeakBands;

                // Update sensors
                float max = 0, sum = 0;
                for (int i = 0; i < _bandSensors.Length && i < bands.Length; i++)
                {
                    _bandSensors[i].Value = MathF.Round(bands[i], 1);
                    if (bands[i] > max) max = bands[i];
                    sum += bands[i];
                }

                if (_peakSensor != null) _peakSensor.Value = MathF.Round(max, 1);
                if (_averageSensor != null) _averageSensor.Value = MathF.Round(sum / _bandSensors.Length, 1);
                if (_audioLevelSensor != null) _audioLevelSensor.Value = MathF.Round(_capture.PeakLevel * 100, 2);
                if (_dataCountSensor != null) _dataCountSensor.Value = _capture.DataReceivedCount;

                // Render directly onto shared memory bitmap
                if (_spectrumWriter != null)
                {
                    _renderer.RenderToBitmap(bands, peaks, _spectrumWriter.Bitmap);
                    _spectrumWriter.Invalidate();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error updating audio spectrum");
            }
        }

        private void UpdateDeviceText()
        {
            if (_deviceText != null && _capture != null)
            {
                _deviceText.Value = string.IsNullOrEmpty(_capture.LastError)
                    ? _capture.DeviceName
                    : $"ERROR: {_capture.LastError}";
            }
        }

        #region IPluginConfigurable

        public IReadOnlyList<PluginConfigProperty> ConfigProperties
        {
            get
            {
                _configProperties ??= BuildConfigProperties();
                return _configProperties;
            }
        }

        public void ApplyConfig(string key, object? value)
        {
            switch (key)
            {
                case "Style":
                    if (value is string styleStr && Enum.TryParse<SpectrumStyle>(styleStr, true, out var style))
                    {
                        _config.Style = style;
                        if (_renderer != null)
                        {
                            _renderer.Style = style;
                            _renderer.CornerRadius = style == SpectrumStyle.Rounded
                                ? MathF.Max(_config.CornerRadius, 10f) : _config.CornerRadius;
                        }
                    }
                    break;
                case "ColorScheme":
                    if (value is string schemeStr && Enum.TryParse<ColorScheme>(schemeStr, true, out var scheme))
                    {
                        _config.Scheme = scheme;
                        if (_renderer != null) _renderer.Scheme = scheme;
                    }
                    break;
                case "CustomColor1":
                    if (value is string c1) { _config.CustomColor1 = c1; if (_renderer != null) _renderer.CustomColor1 = _config.ParseColor(c1); }
                    break;
                case "CustomColor2":
                    if (value is string c2) { _config.CustomColor2 = c2; if (_renderer != null) _renderer.CustomColor2 = _config.ParseColor(c2); }
                    break;
                case "BackgroundColor":
                    if (value is string bg) { _config.BackgroundColor = bg; if (_renderer != null) _renderer.BackgroundColor = _config.ParseColor(bg); }
                    break;
                case "BarSpacing":
                    if (value is double spacing) { _config.BarSpacing = (float)spacing; if (_renderer != null) _renderer.BarSpacing = _config.BarSpacing; }
                    break;
                case "CornerRadius":
                    if (value is double cr) { _config.CornerRadius = (float)cr; if (_renderer != null) _renderer.CornerRadius = _config.CornerRadius; }
                    break;
                case "ShowPeaks":
                    if (value is bool peaks) { _config.ShowPeaks = peaks; if (_renderer != null) _renderer.ShowPeaks = peaks; }
                    break;
                case "ShowReflection":
                    if (value is bool refl) {
                        _config.ShowReflection = refl;
                        if (refl) {
                            _config.ShowMirror = false;
                            if (_renderer != null) _renderer.ShowMirror = false;
                            UpdateConfigPropertyValue("ShowMirror", false);
                        }
                        if (_renderer != null) _renderer.ShowReflection = refl;
                    }
                    break;
                case "ShowMirror":
                    if (value is bool mirr) {
                        _config.ShowMirror = mirr;
                        if (mirr) {
                            _config.ShowReflection = false;
                            if (_renderer != null) _renderer.ShowReflection = false;
                            UpdateConfigPropertyValue("ShowReflection", false);
                        }
                        if (_renderer != null) _renderer.ShowMirror = mirr;
                    }
                    break;
                case "Brightness":
                    if (value is double bright) { _config.Brightness = (float)bright; if (_renderer != null) _renderer.Brightness = _config.Brightness; }
                    break;
                case "Smoothing":
                    if (value is double smooth) { _config.Smoothing = (float)smooth; if (_analyzer != null) _analyzer.Smoothing = _config.Smoothing; }
                    break;
                case "PeakDecay":
                    if (value is double decay) { _config.PeakDecay = (float)decay; if (_analyzer != null) _analyzer.PeakDecay = _config.PeakDecay; }
                    break;
                case "Gain":
                    if (value is double gain) { _config.Gain = (float)gain; if (_analyzer != null) _analyzer.Gain = _config.Gain; }
                    break;
                case "Alignment":
                    if (value is string alignStr && Enum.TryParse<SpectrumAlignment>(alignStr, true, out var align))
                    {
                        _config.Alignment = align;
                        if (_renderer != null) _renderer.Alignment = align;
                    }
                    break;
                case "ContentWidth":
                    if (value is double cw) { _config.ContentWidth = (float)cw; if (_renderer != null) _renderer.ContentWidth = _config.ContentWidth; }
                    break;
                case "CenterOut":
                    if (value is bool center) { _config.CenterOut = center; if (_renderer != null) _renderer.CenterOut = center; }
                    break;
                case "EdgeBoost":
                    if (value is double eb) { _config.EdgeBoost = (float)eb; if (_renderer != null) _renderer.EdgeBoost = _config.EdgeBoost; }
                    break;
                case "NoiseFloor":
                    if (value is double nf) { _config.NoiseFloor = (float)nf; if (_renderer != null) _renderer.NoiseFloor = _config.NoiseFloor; }
                    break;
                case "TrimBands":
                    if (value is int tb) { _config.TrimBands = Math.Clamp(tb, 0, 20); if (_renderer != null) _renderer.TrimBands = _config.TrimBands; }
                    break;
                case "BandCount":
                    if (value is int bc) { _config.BandCount = Math.Clamp(bc, 8, 128); }
                    break;
                case "ImageWidth":
                    if (value is int iw)
                    {
                        _config.ImageWidth = Math.Clamp(iw, 100, 3840);
                        _spectrumWriter?.Resize(_config.ImageWidth, _config.ImageHeight);
                    }
                    break;
                case "ImageHeight":
                    if (value is int ih)
                    {
                        _config.ImageHeight = Math.Clamp(ih, 50, 2160);
                        _spectrumWriter?.Resize(_config.ImageWidth, _config.ImageHeight);
                    }
                    break;
                case "AudioDevice":
                    if (value is string device)
                    {
                        _config.AudioDevice = device;
                        if (_capture != null)
                        {
                            _capture.Start(string.IsNullOrEmpty(device) ? null : device);
                            UpdateDeviceText();
                        }
                    }
                    break;
            }

            _configProperties = null; // rebuild with current values on next read
        }

        private void UpdateConfigPropertyValue(string key, object? newValue)
        {
            if (_configProperties == null) return;
            var prop = _configProperties.Find(p => p.Key == key);
            if (prop != null) prop.Value = newValue;
        }

        private List<PluginConfigProperty> BuildConfigProperties()
        {
            return
            [
                new() { Key = "AudioDevice", DisplayName = "Audio Device", Type = PluginConfigType.String,
                    Value = _config.AudioDevice, Description = "Leave empty for the default output monitor, or set a partial PulseAudio source name" },
                new() { Key = "BandCount", DisplayName = "Band Count", Type = PluginConfigType.Integer,
                    Value = _config.BandCount, MinValue = 8, MaxValue = 128, Step = 1,
                    Description = "Number of frequency bands. Requires plugin restart to apply." },
                new() { Key = "ImageWidth", DisplayName = "Image Width", Type = PluginConfigType.Integer,
                    Value = _config.ImageWidth, MinValue = 100, MaxValue = 3840, Step = 10 },
                new() { Key = "ImageHeight", DisplayName = "Image Height", Type = PluginConfigType.Integer,
                    Value = _config.ImageHeight, MinValue = 50, MaxValue = 2160, Step = 10 },
                new() { Key = "Style", DisplayName = "Style", Type = PluginConfigType.Choice,
                    Value = _config.Style.ToString(),
                    Options = Enum.GetNames<SpectrumStyle>() },
                new() { Key = "ColorScheme", DisplayName = "Color Scheme", Type = PluginConfigType.Choice,
                    Value = _config.Scheme.ToString(),
                    Options = Enum.GetNames<ColorScheme>() },
                new() { Key = "CustomColor1", DisplayName = "Custom Color 1", Type = PluginConfigType.String,
                    Value = _config.CustomColor1, Description = "Hex color for Custom scheme start" },
                new() { Key = "CustomColor2", DisplayName = "Custom Color 2", Type = PluginConfigType.String,
                    Value = _config.CustomColor2, Description = "Hex color for Custom scheme end" },
                new() { Key = "BackgroundColor", DisplayName = "Background Color", Type = PluginConfigType.String,
                    Value = _config.BackgroundColor, Description = "Hex color or Transparent" },
                new() { Key = "BarSpacing", DisplayName = "Bar Spacing", Type = PluginConfigType.Double,
                    Value = (double)_config.BarSpacing, MinValue = 0.0, MaxValue = 0.8, Step = 0.05 },
                new() { Key = "CornerRadius", DisplayName = "Corner Radius", Type = PluginConfigType.Double,
                    Value = (double)_config.CornerRadius, MinValue = 0, MaxValue = 20, Step = 1 },
                new() { Key = "ShowPeaks", DisplayName = "Show Peaks", Type = PluginConfigType.Boolean,
                    Value = _config.ShowPeaks },
                new() { Key = "ShowReflection", DisplayName = "Show Reflection", Type = PluginConfigType.Boolean,
                    Value = _config.ShowReflection },
                new() { Key = "ShowMirror", DisplayName = "Show Mirror", Type = PluginConfigType.Boolean,
                    Value = _config.ShowMirror },
                new() { Key = "Brightness", DisplayName = "Brightness", Type = PluginConfigType.Double,
                    Value = (double)_config.Brightness, MinValue = 0.1, MaxValue = 2.0, Step = 0.1 },
                new() { Key = "Smoothing", DisplayName = "Smoothing", Type = PluginConfigType.Double,
                    Value = (double)_config.Smoothing, MinValue = 0.05, MaxValue = 0.95, Step = 0.05 },
                new() { Key = "PeakDecay", DisplayName = "Peak Decay", Type = PluginConfigType.Double,
                    Value = (double)_config.PeakDecay, MinValue = 0.005, MaxValue = 0.1, Step = 0.005 },
                new() { Key = "Gain", DisplayName = "Gain", Type = PluginConfigType.Double,
                    Value = (double)_config.Gain, MinValue = 0.5, MaxValue = 5.0, Step = 0.1 },
                new() { Key = "Alignment", DisplayName = "Alignment", Type = PluginConfigType.Choice,
                    Value = _config.Alignment.ToString(),
                    Options = Enum.GetNames<SpectrumAlignment>() },
                new() { Key = "ContentWidth", DisplayName = "Content Width", Type = PluginConfigType.Double,
                    Value = (double)_config.ContentWidth, MinValue = 0.1, MaxValue = 1.0, Step = 0.05 },
                new() { Key = "CenterOut", DisplayName = "Center Out", Type = PluginConfigType.Boolean,
                    Value = _config.CenterOut },
                new() { Key = "EdgeBoost", DisplayName = "Edge Boost", Type = PluginConfigType.Double,
                    Value = (double)_config.EdgeBoost, MinValue = 1, MaxValue = 15, Step = 1,
                    Description = "Only applies when Center Out is enabled" },
                new() { Key = "NoiseFloor", DisplayName = "Noise Floor", Type = PluginConfigType.Double,
                    Value = (double)_config.NoiseFloor, MinValue = 0, MaxValue = 1.0, Step = 0.05,
                    Description = "Minimum band level as fraction of average. 0 = off, 0.5 = half of average." },
                new() { Key = "TrimBands", DisplayName = "Trim Bands", Type = PluginConfigType.Integer,
                    Value = _config.TrimBands, MinValue = 0, MaxValue = 20, Step = 1,
                    Description = "Number of bands to cut from each side (removes low-energy edges)" },
            ];
        }

        #endregion

        private static string GetBandLabel(int index, int totalBands)
        {
            const float minFreq = 20f;
            const float maxFreq = 20000f;

            float t1 = (float)index / totalBands;
            float t2 = (float)(index + 1) / totalBands;
            float centerFreq = minFreq * MathF.Pow(maxFreq / minFreq, (t1 + t2) / 2);

            if (centerFreq >= 1000)
                return $"{centerFreq / 1000:F1}kHz";
            else
                return $"{centerFreq:F0}Hz";
        }
    }
}
