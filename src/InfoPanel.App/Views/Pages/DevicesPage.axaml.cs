using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using InfoPanel.Models;
using InfoPanel.ThermalrightPanel;
using InfoPanel.TuringPanel;
using Serilog;

namespace InfoPanel.Views.Pages
{
    public partial class DevicesPage : UserControl
    {
        private static readonly ILogger Logger = Log.ForContext<DevicesPage>();
        private App? _app;
        private DispatcherTimer? _statusTimer;

        public DevicesPage()
        {
            InitializeComponent();

            Loaded += (_, _) =>
            {
                _app ??= Avalonia.Application.Current as App;
                RebuildRows();

                _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _statusTimer.Tick += (_, _) => RebuildStatusOnly();
                _statusTimer.Start();
            };

            Unloaded += (_, _) =>
            {
                _statusTimer?.Stop();
                _statusTimer = null;
            };
        }

        private readonly List<(TextBlock Status, Func<string> Get)> _statusBindings = [];

        private void RebuildStatusOnly()
        {
            foreach (var (status, get) in _statusBindings)
            {
                status.Text = get();
            }
        }

        private void RebuildRows()
        {
            DeviceRows.Children.Clear();
            _statusBindings.Clear();
            if (_app == null) return;

            var settings = _app.Host.Settings;

            AddFamilyHeader("Thermalright / TRCC", settings.ThermalrightPanelMultiDeviceMode,
                v => { settings.ThermalrightPanelMultiDeviceMode = v; _app.Host.SaveSettings(); });
            foreach (var device in settings.ThermalrightPanelDevices.ToList())
            {
                AddRow(
                    title: device.ModelInfo?.Name ?? device.Model.ToString(),
                    subtitle: $"{device.DeviceId} · {device.DeviceLocation}",
                    isEnabled: device.Enabled,
                    setEnabled: v => { device.Enabled = v; _app.Host.SaveSettings(); },
                    profileGuid: device.ProfileGuid,
                    setProfile: g => { device.ProfileGuid = g; _app.Host.SaveSettings(); },
                    status: () => device.RuntimeProperties.IsRunning
                        ? $"running · {device.RuntimeProperties.FrameRate} fps"
                        : device.RuntimeProperties.ErrorMessage is { Length: > 0 } err ? err : "idle",
                    remove: () => { settings.ThermalrightPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); });
            }

            AddFamilyHeader("Turing Smart Screen", settings.TuringPanelMultiDeviceMode,
                v => { settings.TuringPanelMultiDeviceMode = v; _app.Host.SaveSettings(); });
            foreach (var device in settings.TuringPanelDevices.ToList())
            {
                AddRow(
                    title: device.Model ?? "Turing panel",
                    subtitle: $"{device.DeviceId} · {device.DeviceLocation}",
                    isEnabled: device.Enabled,
                    setEnabled: v => { device.Enabled = v; _app.Host.SaveSettings(); },
                    profileGuid: device.ProfileGuid,
                    setProfile: g => { device.ProfileGuid = g; _app.Host.SaveSettings(); },
                    status: () => device.RuntimeProperties.IsRunning
                        ? $"running · {device.RuntimeProperties.FrameRate} fps"
                        : device.RuntimeProperties.ErrorMessage is { Length: > 0 } err ? err : "idle",
                    remove: () => { settings.TuringPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); });
            }

            AddFamilyHeader("BeadaPanel", settings.BeadaPanelMultiDeviceMode,
                v => { settings.BeadaPanelMultiDeviceMode = v; _app.Host.SaveSettings(); });
            foreach (var device in settings.BeadaPanelDevices.ToList())
            {
                AddRow(
                    title: device.Model ?? "BeadaPanel",
                    subtitle: $"{device.DeviceId} · {device.DeviceLocation}",
                    isEnabled: device.Enabled,
                    setEnabled: v => { device.Enabled = v; _app.Host.SaveSettings(); },
                    profileGuid: device.ProfileGuid,
                    setProfile: g => { device.ProfileGuid = g; _app.Host.SaveSettings(); },
                    status: () => device.RuntimeProperties.IsRunning
                        ? $"running · {device.RuntimeProperties.FrameRate} fps"
                        : device.RuntimeProperties.ErrorMessage is { Length: > 0 } err ? err : "idle",
                    remove: () => { settings.BeadaPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); });
            }
        }

        private void AddFamilyHeader(string title, bool multiMode, Action<bool> setMultiMode)
        {
            var panel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var toggle = new ToggleSwitch
            {
                OnContent = "Streaming enabled",
                OffContent = "Streaming disabled",
                IsChecked = multiMode,
            };
            toggle.IsCheckedChanged += (_, _) => setMultiMode(toggle.IsChecked == true);
            DockPanel.SetDock(toggle, Dock.Right);
            panel.Children.Add(toggle);
            panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            DeviceRows.Children.Add(panel);
        }

        private void AddRow(string title, string subtitle, bool isEnabled, Action<bool> setEnabled,
            Guid profileGuid, Action<Guid> setProfile, Func<string> status, Action remove)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12),
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };

            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold });
            info.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Opacity = 0.6 });
            var statusText = new TextBlock { Text = status(), FontSize = 11, Opacity = 0.8, Foreground = Avalonia.Media.Brushes.MediumAquamarine };
            info.Children.Add(statusText);
            _statusBindings.Add((statusText, status));
            grid.Children.Add(info);

            var profilePicker = new ComboBox
            {
                MinWidth = 150,
                Margin = new Thickness(12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = _app!.Host.Profiles,
                SelectedItem = _app.Host.Profiles.FirstOrDefault(p => p.Guid == profileGuid),
                DisplayMemberBinding = new Avalonia.Data.Binding(nameof(Profile.Name)),
            };
            profilePicker.SelectionChanged += (_, _) =>
            {
                if (profilePicker.SelectedItem is Profile p) setProfile(p.Guid);
            };
            Grid.SetColumn(profilePicker, 1);
            grid.Children.Add(profilePicker);

            var enabled = new ToggleSwitch
            {
                IsChecked = isEnabled,
                OnContent = "",
                OffContent = "",
                Margin = new Thickness(12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            enabled.IsCheckedChanged += (_, _) => setEnabled(enabled.IsChecked == true);
            Grid.SetColumn(enabled, 2);
            grid.Children.Add(enabled);

            var removeButton = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
            removeButton.Click += (_, _) => remove();
            Grid.SetColumn(removeButton, 3);
            grid.Children.Add(removeButton);

            border.Child = grid;
            DeviceRows.Children.Add(border);
        }

        private async void Scan_Click(object? sender, RoutedEventArgs e)
        {
            if (_app == null) return;

            ScanButton.IsEnabled = false;
            ScanStatus.IsVisible = true;
            ScanStatus.Text = "Scanning…";

            try
            {
                var settings = _app.Host.Settings;
                int added = 0, updated = 0;

                // Thermalright (HID/bulk/SCSI)
                var thermalright = await Task.Run(ThermalrightPanelHelper.ScanDevices);
                foreach (var found in thermalright)
                {
                    var existing = settings.ThermalrightPanelDevices.FirstOrDefault(d => d.DeviceId == found.DeviceId);
                    if (existing == null)
                    {
                        settings.ThermalrightPanelDevices.Add(new ThermalrightPanelDevice
                        {
                            DeviceId = found.DeviceId,
                            DeviceLocation = found.DeviceLocation,
                            Model = found.Model,
                        });
                        added++;
                    }
                    else
                    {
                        existing.DeviceLocation = found.DeviceLocation;
                        if (existing.Model != found.Model)
                        {
                            Logger.Information("Device {Id} model updated from {Old} to {New}", found.DeviceId, existing.Model, found.Model);
                            existing.Model = found.Model;
                        }

                        updated++;
                    }
                }

                // Turing (USB + serial)
                foreach (var found in (await TuringPanelHelper.GetUsbDevices()).Concat(await TuringPanelHelper.GetSerialDevices()))
                {
                    var existing = settings.TuringPanelDevices.FirstOrDefault(d => d.DeviceId == found.DeviceId);
                    if (existing == null)
                    {
                        settings.TuringPanelDevices.Add(found);
                        added++;
                    }
                    else
                    {
                        existing.DeviceLocation = found.DeviceLocation;
                        if (existing.Model != found.Model)
                        {
                            Logger.Information("Device {Id} model updated from {Old} to {New}", found.DeviceId, existing.Model, found.Model);
                            existing.Model = found.Model;
                        }

                        updated++;
                    }
                }

                _app.Host.SaveSettings();
                ScanStatus.Text = $"Scan complete: {added} new, {updated} known device(s).";
                RebuildRows();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Device scan failed");
                ScanStatus.Text = $"Scan failed: {ex.Message}";
            }
            finally
            {
                ScanButton.IsEnabled = true;
            }
        }

        private Avalonia.Media.IBrush? ThemeBrush(string key) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) ? value as Avalonia.Media.IBrush : null;
    }
}
