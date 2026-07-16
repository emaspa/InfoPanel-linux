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
        }

        public async Task StartDevicesAsync(CancellationToken token)
        {
            await ThermalrightPanelTask.Instance.StartAsync(token);
        }

        public async Task StopDevicesAsync()
        {
            await ThermalrightPanelTask.Instance.StopAsync(shutdown: true);
        }
    }
}
