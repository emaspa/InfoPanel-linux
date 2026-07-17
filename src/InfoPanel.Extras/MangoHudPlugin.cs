using InfoPanel.Plugins;
using System.Globalization;
using System.Text.RegularExpressions;

namespace InfoPanel.Extras
{
    /// <summary>
    /// Game FPS sensors from MangoHud's metrics logging (the Linux equivalent of
    /// RTSS/PresentMon). Tails the newest CSV in the MangoHud log folder while a
    /// game is running.
    ///
    /// Recommended ~/.config/MangoHud/MangoHud.conf (verified against MangoHud 0.8.4):
    ///   fps_only
    ///   autostart_log=1
    ///   log_interval=1000
    ///   output_folder=/home/USER/mangohud_logs
    /// Notes: the folder key is 'output_folder' (not 'log_folder') on 0.8.x, and
    /// 'no_display' silently disables autostart_log — use fps_only to minimize the
    /// overlay instead. Enable per game with 'mangohud %command%' or globally for
    /// Vulkan titles with MANGOHUD=1. Flatpak games additionally need the
    /// org.freedesktop.Platform.VulkanLayer.MangoHud extension and, if the app
    /// overrides XDG_DATA_DIRS, an override appending /usr/lib/extensions/vulkan/share.
    /// </summary>
    public partial class MangoHudPlugin : BasePlugin, IPluginConfigurable
    {
        private readonly PluginSensor _fps = new("FPS", 0, " FPS");
        private readonly PluginSensor _fpsAverage = new("FPS Average", 0, " FPS");
        private readonly PluginSensor _fpsOnePercentLow = new("FPS 1% Low", 0, " FPS");
        private readonly PluginSensor _frametime = new("Frametime", 0, " ms");
        private readonly PluginText _game = new("Game", "-");
        private readonly PluginText _status = new("Status", "Waiting for MangoHud log");

        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        private string _logFolder = "";
        private int _freshnessSeconds = 10;
        private int _averageWindow = 120; // samples used for average / 1% low

        private List<PluginConfigProperty>? _configProperties;

        public IReadOnlyList<PluginConfigProperty> ConfigProperties
        {
            get
            {
                _configProperties ??=
                [
                    new() { Key = "LogFolder", DisplayName = "Log folder", Type = PluginConfigType.String,
                            Description = "MangoHud output_folder where metrics CSVs are written.", Value = _logFolder },
                    new() { Key = "FreshnessSeconds", DisplayName = "Freshness timeout", Type = PluginConfigType.Integer,
                            Description = "Seconds without new samples before the game counts as stopped.",
                            MinValue = 2, MaxValue = 120, Step = 1, Value = _freshnessSeconds },
                    new() { Key = "AverageWindow", DisplayName = "Average window", Type = PluginConfigType.Integer,
                            Description = "Samples used for the average and 1% low.",
                            MinValue = 10, MaxValue = 1000, Step = 10, Value = _averageWindow },
                ];
                return _configProperties;
            }
        }

        public void ApplyConfig(string key, object? value)
        {
            switch (key)
            {
                case "LogFolder":
                    if (value is string folder && !string.IsNullOrWhiteSpace(folder)) _logFolder = folder;
                    break;
                case "FreshnessSeconds":
                    if (value is int fresh) _freshnessSeconds = Math.Clamp(fresh, 2, 120);
                    break;
                case "AverageWindow":
                    if (value is int window) _averageWindow = Math.Clamp(window, 10, 1000);
                    break;
            }

            _configProperties = null; // rebuild with current values on next read
        }

        // per-file cached column indices
        private string? _mappedFile;
        private int _fpsColumn = -1;
        private int _frametimeColumn = -1;

        public MangoHudPlugin() : base("mangohud-plugin", "MangoHud", "Game FPS and frametimes from MangoHud metrics logging.")
        {
        }

        public override void Initialize()
        {
            Config.Instance.Load();
            if (Config.Instance.TryGetValue(Config.SECTION_MANGOHUD, "LogFolder", out var folder) && !string.IsNullOrWhiteSpace(folder))
            {
                _logFolder = folder;
            }
            else
            {
                _logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mangohud_logs");
            }
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Game");
            container.Entries.Add(_fps);
            container.Entries.Add(_fpsAverage);
            container.Entries.Add(_fpsOnePercentLow);
            container.Entries.Add(_frametime);
            container.Entries.Add(_game);
            container.Entries.Add(_status);
            containers.Add(container);
        }

        public override void Update() => throw new NotImplementedException();

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!Directory.Exists(_logFolder))
                {
                    SetIdle($"Log folder not found: {_logFolder}");
                    return Task.CompletedTask;
                }

                var newest = new DirectoryInfo(_logFolder)
                    .EnumerateFiles("*.csv")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (newest == null || (DateTime.UtcNow - newest.LastWriteTimeUtc).TotalSeconds > _freshnessSeconds)
                {
                    SetIdle("Waiting for game (MangoHud logging)");
                    return Task.CompletedTask;
                }

                var samples = ReadTailSamples(newest.FullName);
                if (samples.Count == 0)
                {
                    SetIdle("Log has no samples yet");
                    return Task.CompletedTask;
                }

                var (fps, frametime) = samples[^1];
                _fps.Value = (float)fps;
                _frametime.Value = (float)frametime;

                var window = samples.Count > _averageWindow ? samples.Skip(samples.Count - _averageWindow).ToList() : samples;
                _fpsAverage.Value = (float)window.Average(s => s.Fps);

                var sorted = window.Select(s => s.Fps).OrderBy(v => v).ToList();
                var onePercentIndex = Math.Max(0, (int)(sorted.Count * 0.01) - 1 + 1) - 1;
                _fpsOnePercentLow.Value = (float)sorted[Math.Clamp(onePercentIndex, 0, sorted.Count - 1)];

                _game.Value = GameNameFromFile(newest.Name);
                _status.Value = "Capturing";
            }
            catch (Exception ex)
            {
                SetIdle($"Error: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private void SetIdle(string status)
        {
            _fps.Value = 0;
            _fpsAverage.Value = 0;
            _fpsOnePercentLow.Value = 0;
            _frametime.Value = 0;
            _game.Value = "-";
            _status.Value = status;
        }

        /// <summary>Reads the trailing samples of a (possibly large, still-growing) MangoHud CSV.</summary>
        private List<(double Fps, double Frametime)> ReadTailSamples(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // column mapping from the header (a line containing "fps"), cached per file
            if (_mappedFile != path)
            {
                using var headReader = new StreamReader(stream, leaveOpen: true);
                for (int i = 0; i < 5 && !headReader.EndOfStream; i++)
                {
                    var line = headReader.ReadLine();
                    if (line == null) break;

                    var columns = line.Split(',');
                    var fpsIndex = Array.FindIndex(columns, c => c.Trim().Equals("fps", StringComparison.OrdinalIgnoreCase));
                    if (fpsIndex >= 0)
                    {
                        _fpsColumn = fpsIndex;
                        _frametimeColumn = Array.FindIndex(columns, c => c.Trim().Equals("frametime", StringComparison.OrdinalIgnoreCase));
                        _mappedFile = path;
                        break;
                    }
                }

                if (_mappedFile != path)
                {
                    return [];
                }
            }

            // tail the last ~16KB
            const int tailBytes = 16 * 1024;
            stream.Seek(Math.Max(0, stream.Length - tailBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            var samples = new List<(double, double)>();
            var lines = text.Split('\n');
            // skip the first (possibly partial) line when we seeked mid-file
            for (int i = stream.Length > tailBytes ? 1 : 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split(',');
                if (parts.Length <= _fpsColumn) continue;

                if (double.TryParse(parts[_fpsColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps >= 0)
                {
                    double frametime = 0;
                    if (_frametimeColumn >= 0 && parts.Length > _frametimeColumn)
                    {
                        double.TryParse(parts[_frametimeColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out frametime);
                        // MangoHud logs frametime in nanoseconds in some versions, ms in others
                        if (frametime > 10000)
                        {
                            frametime /= 1_000_000.0;
                        }
                    }

                    samples.Add((fps, frametime));
                }
            }

            return samples;
        }

        private static string GameNameFromFile(string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            // MangoHud names logs <app>_<yyyy-MM-dd_HH-mm-ss>
            return TimestampSuffix().Replace(name, "");
        }

        [GeneratedRegex(@"_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$")]
        private static partial Regex TimestampSuffix();

        public override void Close()
        {
        }
    }
}
