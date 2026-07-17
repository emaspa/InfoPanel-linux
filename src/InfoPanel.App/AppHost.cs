using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Platform.Linux;
using InfoPanel.Services;
using Serilog;
using System.Collections.ObjectModel;

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
        public ObservableCollection<Profile> Profiles { get; } = [];
        public HotkeyManager? Hotkeys { get; private set; }

        public void Initialize()
        {
            // Platform + rendering seams
            LinuxPlatform.Register();
            RenderingServices.Register();

            // Configuration
            Settings = ConfigPersistence.LoadSettings() ?? new Settings();
            foreach (var profile in ConfigPersistence.LoadProfiles() ?? [])
            {
                Profiles.Add(profile);
            }
            Logger.Information("Loaded {ProfileCount} profiles, {DeviceCount} Thermalright devices",
                Profiles.Count, Settings.ThermalrightPanelDevices.Count);

            // Device runtime seams
            DeviceRuntime.Settings = Settings;
            DeviceRuntime.GetProfile = guid => Profiles.FirstOrDefault(p => p.Guid == guid);
            DeviceRuntime.RequestSettingsSave = SaveSettings;
            DeviceRuntime.GetProfiles = () => Profiles.ToList();

            Hotkeys = new HotkeyManager(Settings, SaveSettings);
            Hotkeys.Start();

            // React to settings toggles like v1's ConfigModel did: restart the affected
            // service when a mode changes, keep render pacing and autostart in sync.
            Settings.PropertyChanged += async (_, e) =>
            {
                try
                {
                    switch (e.PropertyName)
                    {
                        case nameof(Settings.ThermalrightPanelMultiDeviceMode):
                            await ThermalrightPanelTask.Instance.StopAsync();
                            if (Settings.ThermalrightPanelMultiDeviceMode) await ThermalrightPanelTask.Instance.StartAsync();
                            break;
                        case nameof(Settings.TuringPanelMultiDeviceMode):
                            await TuringPanelTask.Instance.StopAsync();
                            if (Settings.TuringPanelMultiDeviceMode) await TuringPanelTask.Instance.StartAsync();
                            break;
                        case nameof(Settings.BeadaPanelMultiDeviceMode):
                            await BeadaPanelTask.Instance.StopAsync();
                            if (Settings.BeadaPanelMultiDeviceMode) await BeadaPanelTask.Instance.StartAsync();
                            break;
                        case nameof(Settings.ThermaltakePanelMultiDeviceMode):
                            await ThermaltakePanelTask.Instance.StopAsync();
                            if (Settings.ThermaltakePanelMultiDeviceMode) await ThermaltakePanelTask.Instance.StartAsync();
                            break;
                        case nameof(Settings.JlPanelMultiDeviceMode):
                            await JlPanelTask.Instance.StopAsync();
                            if (Settings.JlPanelMultiDeviceMode) await JlPanelTask.Instance.StartAsync();
                            break;
                        case nameof(Settings.WebServer):
                            if (Settings.WebServer) await WebServerTask.Instance.StartAsync();
                            else await WebServerTask.Instance.StopAsync();
                            break;
                        case nameof(Settings.AutoStart):
                        case nameof(Settings.AutoStartDelay):
                            Platform.PlatformServices.Autostart?.Apply(Settings.AutoStart, Settings.AutoStartDelay);
                            break;
                        case nameof(Settings.TargetFrameRate):
                            RenderContext.TargetFrameRate = Settings.TargetFrameRate;
                            break;
                        case nameof(Settings.TargetGraphUpdateRate):
                            RenderContext.TargetGraphUpdateRate = Settings.TargetGraphUpdateRate;
                            break;
                        case nameof(Settings.ShowGridLines):
                            RenderContext.ShowGridLines = Settings.ShowGridLines;
                            break;
                        case nameof(Settings.GridLinesSpacing):
                            RenderContext.GridLinesSpacing = (int)Settings.GridLinesSpacing;
                            break;
                        case nameof(Settings.GridLinesColor):
                            RenderContext.GridLinesColor = Settings.GridLinesColor;
                            break;
                        case nameof(Settings.SelectedItemColor):
                            RenderContext.SelectedItemColor = Settings.SelectedItemColor;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error applying settings change {Property}", e.PropertyName);
                }
            };

            // Render pacing + designer chrome from settings
            RenderContext.TargetFrameRate = Settings.TargetFrameRate;
            RenderContext.TargetGraphUpdateRate = Settings.TargetGraphUpdateRate;
            RenderContext.ShowGridLines = Settings.ShowGridLines;
            RenderContext.GridLinesSpacing = (int)Settings.GridLinesSpacing;
            RenderContext.GridLinesColor = Settings.GridLinesColor;
            RenderContext.SelectedItemColor = Settings.SelectedItemColor;

            // Live display items: renderers see designer edits immediately
            RenderContext.GetDisplayItems = Stores.DisplayItemStore.Instance.GetSnapshot;

            // Sensor sources for display items and graphs
            SensorReader.ConfigureHwmonSource(static id =>
                HwmonMonitor.SENSORHASH.TryGetValue(id, out var reading) ? reading : null);
            SensorReader.ConfigurePluginSource(Sensors.PluginSensorReader.Read);
        }

        public void SaveProfiles() => ConfigPersistence.SaveProfiles([.. Profiles]);

        private readonly Utils.Debouncer _settingsSaveDebouncer = new();

        public void SaveSettings() =>
            _settingsSaveDebouncer.Debounce(() => _ = ConfigPersistence.SaveSettingsAsync(Settings), 500);

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
            await BeadaPanelTask.Instance.StartAsync(token);
            await TuringPanelTask.Instance.StartAsync(token);
            await ThermalrightPanelTask.Instance.StartAsync(token);
            await ThermaltakePanelTask.Instance.StartAsync(token);
            await JlPanelTask.Instance.StartAsync(token);

            if (Settings.WebServer)
            {
                await WebServerTask.Instance.StartAsync(token);
            }
        }

        public async Task StopDevicesAsync()
        {
            try { Hotkeys?.Stop(); } catch { }
            try { await WebServerTask.Instance.StopAsync(shutdown: true); } catch { }
            await BeadaPanelTask.Instance.StopAsync(shutdown: true);
            await TuringPanelTask.Instance.StopAsync(shutdown: true);
            await ThermalrightPanelTask.Instance.StopAsync(shutdown: true);
            await ThermaltakePanelTask.Instance.StopAsync(shutdown: true);
            await JlPanelTask.Instance.StopAsync(shutdown: true);

            try { await Monitors.PluginMonitor.Instance.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            try { HwmonMonitor.Instance.Stop(); } catch { }
            try { IntelGpuMonitor.Instance.Shutdown(); } catch { }
        }
    }
}
