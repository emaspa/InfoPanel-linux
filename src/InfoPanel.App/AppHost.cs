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
        public Services.ForegroundAppMonitor? ForegroundApps { get; private set; }

        /// <summary>
        /// The Lian Li Universal Screen 8.8" used to live inside the Turing family
        /// (model LIANLI_88INCH_USB); it now has its own device family covering all
        /// Lian Li panels. Move any previously configured entry across so users keep
        /// their profile assignment and settings.
        /// </summary>
        private void MigrateLianLiDevices()
        {
            var stale = Settings.TuringPanelDevices
                .Where(d => d.Model == "LIANLI_88INCH_USB")
                .ToList();
            if (stale.Count == 0) return;

            foreach (var old in stale)
            {
                Settings.TuringPanelDevices.Remove(old);

                if (!Settings.LianLiPanelDevices.Any(d => d.DeviceId == old.DeviceId))
                {
                    Settings.LianLiPanelDevices.Add(new LianLiPanelDevice
                    {
                        DeviceId = old.DeviceId ?? string.Empty,
                        DeviceLocation = old.DeviceLocation ?? string.Empty,
                        Model = LianLiPanel.LianLiPanelModel.UniversalScreen88Inch,
                        Enabled = old.Enabled,
                        ProfileGuid = old.ProfileGuid,
                        Rotation = old.Rotation,
                        Brightness = old.Brightness,
                        TargetFrameRate = old.TargetFrameRate,
                    });
                }

                // Keep hotkey bindings working across the family change
                foreach (var binding in Settings.HotkeyBindings.Where(b => b.DeviceType == "Turing" && b.DeviceId == old.DeviceId))
                {
                    binding.DeviceType = "LianLi";
                }
            }

            if (Settings.TuringPanelDevices.Count == 0)
                Settings.TuringPanelMultiDeviceMode = false;
            if (Settings.LianLiPanelDevices.Any(d => d.Enabled))
                Settings.LianLiPanelMultiDeviceMode = true;

            Logger.Information("Migrated {Count} Lian Li device(s) from the Turing family", stale.Count);
            SaveSettings();
        }

        public void Initialize()
        {
            // Platform + rendering seams
            LinuxPlatform.Register();
            RenderingServices.Register();

            // Configuration
            Settings = ConfigPersistence.LoadSettings() ?? new Settings();
            MigrateLianLiDevices();
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

            ForegroundApps = new Services.ForegroundAppMonitor(this);
            ForegroundApps.Start();

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
                        case nameof(Settings.VmaxPanelMultiDeviceMode):
                            await VmaxPanelTask.Instance.StopAsync();
                            if (Settings.VmaxPanelMultiDeviceMode) await VmaxPanelTask.Instance.StartAsync();
                            break;
                        case nameof(Settings.JonsboPanelMultiDeviceMode):
                            await JonsboPanelTask.Instance.StopAsync();
                            if (Settings.JonsboPanelMultiDeviceMode) await JonsboPanelTask.Instance.StartAsync();
                            break;
                        case nameof(Settings.LianLiPanelMultiDeviceMode):
                            await LianLiPanelTask.Instance.StopAsync();
                            if (Settings.LianLiPanelMultiDeviceMode) await LianLiPanelTask.Instance.StartAsync();
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

            // Live plugin-rendered images (plugin-image:// display items)
            PluginImageSource.Configure(static (pluginId, imageId) =>
                Monitors.PluginMonitor.IMAGEWRITERS.TryGetValue(pluginId, out var writers)
                    && writers.TryGetValue(imageId, out var writer) ? writer : null);

            // Demand-driven sensor polling (#9): only sensors referenced by consumed
            // profiles (recently rendered for a panel/web, or shown as an overlay) are
            // polled; sensor-browsing pages switch to full polling while visible.
            SensorDemand.DemandProvider = () =>
            {
                var consumed = new HashSet<Profile>();
                var byGuid = new Dictionary<Guid, Profile>();
                foreach (var p in Profiles) byGuid[p.Guid] = p;
                foreach (var guid in SharedFrameCache.RecentlyRendered(TimeSpan.FromSeconds(30)))
                {
                    if (byGuid.TryGetValue(guid, out var p)) consumed.Add(p);
                }
                foreach (var p in Profiles)
                {
                    if (p.Active) consumed.Add(p);
                }
                var snapshot = SensorDemand.Collect(consumed, RenderContext.GetDisplayItems);

                // Stopwatch hotkeys drive the plugin without any display item; keep it
                // running (idle-stopping it would reset a running stopwatch).
                if (Settings.StopwatchHotkeyStartKey != "None"
                    || Settings.StopwatchHotkeyStopKey != "None"
                    || Settings.StopwatchHotkeyResetKey != "None")
                {
                    snapshot.PluginIds.Add(HotkeyManager.StopwatchPluginId);
                }

                return snapshot;
            };
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
            await VmaxPanelTask.Instance.StartAsync(token);
            await JonsboPanelTask.Instance.StartAsync(token);
            await LianLiPanelTask.Instance.StartAsync(token);

            if (Settings.WebServer)
            {
                await WebServerTask.Instance.StartAsync(token);
            }
        }

        public async Task StopDevicesAsync()
        {
            try { Hotkeys?.Stop(); } catch { }
            try { ForegroundApps?.Dispose(); } catch { }
            try { await WebServerTask.Instance.StopAsync(shutdown: true); } catch { }
            await BeadaPanelTask.Instance.StopAsync(shutdown: true);
            await TuringPanelTask.Instance.StopAsync(shutdown: true);
            await ThermalrightPanelTask.Instance.StopAsync(shutdown: true);
            await ThermaltakePanelTask.Instance.StopAsync(shutdown: true);
            await JlPanelTask.Instance.StopAsync(shutdown: true);
            await VmaxPanelTask.Instance.StopAsync(shutdown: true);
            await JonsboPanelTask.Instance.StopAsync(shutdown: true);
            await LianLiPanelTask.Instance.StopAsync(shutdown: true);

            try { await Monitors.PluginMonitor.Instance.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            try { HwmonMonitor.Instance.Stop(); } catch { }
            try { IntelGpuMonitor.Instance.Shutdown(); } catch { }
        }
    }
}
