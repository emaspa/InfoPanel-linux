using InfoPanel.Plugins;
using System.Text.Json;

namespace InfoPanel.Extras
{
    /// <summary>
    /// Drive health sensors (the LibreHardwareMonitor storage equivalent): temperature,
    /// SMART status, wear, spare, power-on hours and lifetime data written/read.
    ///
    /// SMART reads need root, so a tiny systemd timer dumps smartctl JSON to
    /// /run/infopanel/smart.json every minute (see packaging/infopanel-smart.*,
    /// installed by install.sh). This plugin just parses that file.
    /// </summary>
    public class SmartPlugin : BasePlugin
    {
        private const string DefaultDumpFile = "/run/infopanel/smart.json";

        private string _dumpFile = DefaultDumpFile;
        private readonly PluginText _status = new("Status", "-");
        private readonly List<DriveSensors> _drives = [];

        private sealed class DriveSensors
        {
            public required string Device;
            public readonly PluginSensor Temperature = new("Temperature", 0, " °C");
            public readonly PluginText Health = new("Health", "-");
            public readonly PluginSensor PercentageUsed = new("Percentage Used", 0, " %");
            public readonly PluginSensor AvailableSpare = new("Available Spare", 0, " %");
            public readonly PluginSensor PowerOnHours = new("Power On Hours", 0, " h");
            public readonly PluginSensor PowerCycles = new("Power Cycles", 0, "");
            public readonly PluginSensor DataWritten = new("Data Written", 0, " TB");
            public readonly PluginSensor DataRead = new("Data Read", 0, " TB");
        }

        public override string? ConfigFilePath => Config.FilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(30);

        public SmartPlugin() : base("smart-plugin", "Drive Health", "SMART drive health via the InfoPanel smart-dump timer.")
        {
        }

        public override void Initialize()
        {
            Config.Instance.Load();
            if (Config.Instance.TryGetValue(Config.SECTION_SMART, "DumpFile", out var path) && !string.IsNullOrWhiteSpace(path))
            {
                _dumpFile = path;
            }
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var drives = TryReadDump();

            if (drives == null)
            {
                var container = new PluginContainer("Drives");
                _status.Value = "smart.json not found — install the infopanel-smart timer (see install.sh)";
                container.Entries.Add(_status);
                containers.Add(container);
                return;
            }

            foreach (var drive in drives.Value.EnumerateArray())
            {
                var deviceName = drive.TryGetProperty("device", out var dev) && dev.TryGetProperty("name", out var name)
                    ? name.GetString() ?? "?" : "?";
                var model = drive.TryGetProperty("model_name", out var m) ? m.GetString() ?? deviceName : deviceName;

                var sensors = new DriveSensors { Device = deviceName };
                _drives.Add(sensors);

                var container = new PluginContainer(model);
                container.Entries.Add(sensors.Temperature);
                container.Entries.Add(sensors.Health);
                container.Entries.Add(sensors.PercentageUsed);
                container.Entries.Add(sensors.AvailableSpare);
                container.Entries.Add(sensors.PowerOnHours);
                container.Entries.Add(sensors.PowerCycles);
                container.Entries.Add(sensors.DataWritten);
                container.Entries.Add(sensors.DataRead);
                containers.Add(container);
            }
        }

        public override void Update() => throw new NotImplementedException();

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            var drives = TryReadDump();
            if (drives == null)
            {
                return Task.CompletedTask;
            }

            foreach (var drive in drives.Value.EnumerateArray())
            {
                var deviceName = drive.TryGetProperty("device", out var dev) && dev.TryGetProperty("name", out var name)
                    ? name.GetString() ?? "?" : "?";

                var sensors = _drives.FirstOrDefault(d => d.Device == deviceName);
                if (sensors == null) continue;

                if (drive.TryGetProperty("temperature", out var temp) && temp.TryGetProperty("current", out var current))
                {
                    sensors.Temperature.Value = current.GetSingle();
                }

                if (drive.TryGetProperty("smart_status", out var status) && status.TryGetProperty("passed", out var passed))
                {
                    sensors.Health.Value = passed.GetBoolean() ? "PASSED" : "FAILED";
                }

                if (drive.TryGetProperty("power_on_time", out var pot) && pot.TryGetProperty("hours", out var hours))
                {
                    sensors.PowerOnHours.Value = hours.GetSingle();
                }

                if (drive.TryGetProperty("power_cycle_count", out var cycles))
                {
                    sensors.PowerCycles.Value = cycles.GetSingle();
                }

                // NVMe health log (SATA drives won't have this section)
                if (drive.TryGetProperty("nvme_smart_health_information_log", out var nvme))
                {
                    if (nvme.TryGetProperty("percentage_used", out var used)) sensors.PercentageUsed.Value = used.GetSingle();
                    if (nvme.TryGetProperty("available_spare", out var spare)) sensors.AvailableSpare.Value = spare.GetSingle();
                    if (nvme.TryGetProperty("power_cycles", out var pc)) sensors.PowerCycles.Value = pc.GetSingle();

                    // data units are 512,000-byte units per the NVMe spec
                    if (nvme.TryGetProperty("data_units_written", out var written))
                    {
                        sensors.DataWritten.Value = (float)(written.GetDouble() * 512_000 / 1e12);
                    }

                    if (nvme.TryGetProperty("data_units_read", out var read))
                    {
                        sensors.DataRead.Value = (float)(read.GetDouble() * 512_000 / 1e12);
                    }
                }
            }

            return Task.CompletedTask;
        }

        private JsonElement? TryReadDump()
        {
            try
            {
                if (!File.Exists(_dumpFile))
                {
                    return null;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(_dumpFile));
                return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.Clone() : null;
            }
            catch
            {
                return null;
            }
        }

        public override void Close()
        {
        }
    }
}
