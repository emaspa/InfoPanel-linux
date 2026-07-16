using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Platform.Linux;
using InfoPanel.Services;
using Serilog;

namespace InfoPanel
{
    /// <summary>
    /// Composition root shared by the headless CLI and (later) the Avalonia UI.
    /// Owns configuration state and the background services, and wires the
    /// Core/Rendering/Devices seams.
    /// </summary>
    public sealed class AppHost
    {
        private static readonly ILogger Logger = Log.ForContext<AppHost>();

        public Settings Settings { get; private set; } = new();
        public List<Profile> Profiles { get; private set; } = [];

        public void Initialize()
        {
            // Platform + rendering seams
            LinuxPlatform.Register();
            RenderingServices.Register();

            // Configuration
            Settings = ConfigPersistence.LoadSettings() ?? new Settings();
            Profiles = ConfigPersistence.LoadProfiles() ?? [];
            Logger.Information("Loaded {ProfileCount} profiles, {DeviceCount} Thermalright devices",
                Profiles.Count, Settings.ThermalrightPanelDevices.Count);

            // Device runtime seams
            DeviceRuntime.Settings = Settings;
            DeviceRuntime.GetProfile = guid => Profiles.FirstOrDefault(p => p.Guid == guid);

            // Render pacing from settings
            RenderContext.TargetFrameRate = Settings.TargetFrameRate;
            RenderContext.TargetGraphUpdateRate = Settings.TargetGraphUpdateRate;

            // Sensor sources for display items and graphs
            SensorReader.ConfigureHwmonSource(static id =>
                HwmonMonitor.SENSORHASH.TryGetValue(id, out var reading) ? reading : null);
            SensorReader.ConfigurePluginSource(Sensors.PluginSensorReader.Read);
        }

        public async Task StartSensorsAsync()
        {
            try
            {
                await Monitors.PluginMonitor.Instance.StartAsync();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Plugin monitor initialization failed");
            }

            try
            {
                // HwmonMonitor's poll also drives LinuxSystemSensors and the GPU monitors
                HwmonMonitor.Instance.Start(Settings.TargetGraphUpdateRate);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "HwmonMonitor initialization failed");
            }
        }

        public async Task StartDevicesAsync(CancellationToken token)
        {
            await ThermalrightPanelTask.Instance.StartAsync(token);
        }

        public async Task StopDevicesAsync()
        {
            await ThermalrightPanelTask.Instance.StopAsync(shutdown: true);

            try { await Monitors.PluginMonitor.Instance.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            try { HwmonMonitor.Instance.Stop(); } catch { }
            try { IntelGpuMonitor.Instance.Shutdown(); } catch { }
        }
    }
}
