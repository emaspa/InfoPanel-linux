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
    /// Enable logging per game, e.g. in Steam launch options:
    ///   MANGOHUD_CONFIG=autostart_log=1,log_interval=500,log_folder=/home/USER/mangohud_logs mangohud %command%
    /// or set the same keys in ~/.config/MangoHud/MangoHud.conf.
    /// </summary>
    public partial class MangoHudPlugin : BasePlugin
    {
        private readonly PluginSensor _fps = new("FPS", 0, " FPS");
        private readonly PluginSensor _fpsAverage = new("FPS Average", 0, " FPS");
        private readonly PluginSensor _fpsOnePercentLow = new("FPS 1% Low", 0, " FPS");
        private readonly PluginSensor _frametime = new("Frametime", 0, " ms");
        private readonly PluginText _game = new("Game", "-");
        private readonly PluginText _status = new("Status", "Waiting for MangoHud log");

        public override string? ConfigFilePath => Config.FilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        private string _logFolder = "";
        private const int FreshnessSeconds = 10;
        private const int AverageWindow = 120; // samples used for average / 1% low

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

                if (newest == null || (DateTime.UtcNow - newest.LastWriteTimeUtc).TotalSeconds > FreshnessSeconds)
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

                var window = samples.Count > AverageWindow ? samples.Skip(samples.Count - AverageWindow).ToList() : samples;
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
