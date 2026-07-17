using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using InfoPanel.Models;
using InfoPanel.BeadaPanel;
using InfoPanel.ThermalrightPanel;
using InfoPanel.ThermaltakePanel;
using InfoPanel.JlPanel;
using LibUsbDotNet.Main;
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
                    subtitle: $"{device.DeviceId} · {device.DeviceLocation} · renders at {device.DisplayWidth}×{device.DisplayHeight}",
                    isEnabled: device.Enabled,
                    setEnabled: v => { device.Enabled = v; _app.Host.SaveSettings(); },
                    profileGuid: device.ProfileGuid,
                    setProfile: g => { device.ProfileGuid = g; _app.Host.SaveSettings(); },
                    status: () => device.RuntimeProperties.IsRunning
                        ? $"running · {device.RuntimeProperties.FrameRate} fps"
                        : device.RuntimeProperties.ErrorMessage is { Length: > 0 } err ? err : "idle",
                    remove: () => { settings.ThermalrightPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); },
                    rotation: device.Rotation,
                    setRotation: r => { device.Rotation = r; _app.Host.SaveSettings(); });
                AddThermalrightAdvanced(device);
                AddMatchingProfileButton(device.ModelInfo?.Name ?? device.Model.ToString(),
                    device.DisplayWidth, device.DisplayHeight, g => { device.ProfileGuid = g; _app.Host.SaveSettings(); });
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
                    remove: () => { settings.TuringPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); },
                    rotation: device.Rotation,
                    setRotation: r => { device.Rotation = r; _app.Host.SaveSettings(); },
                    brightness: device.Brightness,
                    setBrightness: b => { device.Brightness = b; _app.Host.SaveSettings(); });
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
                    remove: () => { settings.BeadaPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); },
                    rotation: device.Rotation,
                    setRotation: r => { device.Rotation = r; _app.Host.SaveSettings(); },
                    brightness: device.Brightness,
                    setBrightness: b => { device.Brightness = b; _app.Host.SaveSettings(); });
            }

            AddFamilyHeader("Thermaltake / ASRock LCD", settings.ThermaltakePanelMultiDeviceMode,
                v => { settings.ThermaltakePanelMultiDeviceMode = v; _app.Host.SaveSettings(); });
            foreach (var device in settings.ThermaltakePanelDevices.ToList())
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
                    remove: () => { settings.ThermaltakePanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); },
                    rotation: device.Rotation,
                    setRotation: r => { device.Rotation = r; _app.Host.SaveSettings(); },
                    brightness: device.Brightness,
                    setBrightness: b => { device.Brightness = b; _app.Host.SaveSettings(); });
            }

            AddFamilyHeader("Jungle Leopard / Hongtai", settings.JlPanelMultiDeviceMode,
                v => { settings.JlPanelMultiDeviceMode = v; _app.Host.SaveSettings(); });
            foreach (var device in settings.JlPanelDevices.ToList())
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
                    remove: () => { settings.JlPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); },
                    rotation: device.Rotation,
                    setRotation: r => { device.Rotation = r; _app.Host.SaveSettings(); },
                    brightness: device.Brightness,
                    setBrightness: b => { device.Brightness = b; _app.Host.SaveSettings(); });
            }

            AddHotkeysSection(settings);
        }

        // ================= profile hotkeys =================

        private sealed record HotkeyDeviceChoice(string Type, string Id, string Display)
        {
            public override string ToString() => Display;
        }

        private void AddHotkeysSection(Settings settings)
        {
            var header = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var addButton = new Button { Content = "Add hotkey" };
            DockPanel.SetDock(addButton, Dock.Right);
            header.Children.Add(addButton);
            header.Children.Add(new TextBlock { Text = "Profile hotkeys", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            DeviceRows.Children.Add(header);

            var caption = Services.HotkeyManager.IsAvailable
                ? Services.HotkeyManager.Limitation ?? "System-wide shortcuts that switch a panel to a profile. Requires at least one modifier."
                : "Global hotkeys are unavailable on this system (no X display).";
            DeviceRows.Children.Add(new TextBlock { Text = caption, FontSize = 11, Opacity = 0.6, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

            var deviceChoices = new List<HotkeyDeviceChoice>();
            foreach (var d in settings.BeadaPanelDevices) deviceChoices.Add(new("Beada", d.DeviceId, $"BeadaPanel {d.Model}"));
            foreach (var d in settings.TuringPanelDevices) deviceChoices.Add(new("Turing", d.DeviceId, d.Model ?? d.DeviceId));
            foreach (var d in settings.ThermalrightPanelDevices) deviceChoices.Add(new("Thermalright", d.DeviceId, d.ModelInfo?.Name ?? d.Model.ToString()));
            foreach (var d in settings.ThermaltakePanelDevices) deviceChoices.Add(new("Thermaltake", d.DeviceId, d.ModelInfo?.Name ?? d.Model.ToString()));
            foreach (var d in settings.JlPanelDevices) deviceChoices.Add(new("Jl", d.DeviceId, d.ModelInfo?.Name ?? d.Model.ToString()));

            addButton.IsEnabled = deviceChoices.Count > 0;
            addButton.Click += (_, _) =>
            {
                var first = deviceChoices.First();
                settings.HotkeyBindings.Add(new HotkeyBinding
                {
                    DeviceType = first.Type,
                    DeviceId = first.Id,
                    ProfileGuid = _app!.Host.Profiles.FirstOrDefault()?.Guid ?? Guid.Empty,
                });
                _app.Host.SaveSettings();
                RebuildRows();
            };

            foreach (var binding in settings.HotkeyBindings.ToList())
            {
                AddHotkeyRow(settings, binding, deviceChoices);
            }
        }

        private void AddHotkeyRow(Settings settings, HotkeyBinding binding, List<HotkeyDeviceChoice> deviceChoices)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 10),
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };

            // Hotkey capture: while toggled, the next key press is recorded
            var captureButton = new ToggleButton
            {
                Content = binding.HotkeyDisplayText,
                MinWidth = 140,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(captureButton, "Click, then press the key combination (needs Ctrl/Alt/Shift/Super)");
            captureButton.IsCheckedChanged += (_, _) =>
            {
                captureButton.Content = captureButton.IsChecked == true ? "Press keys…" : binding.HotkeyDisplayText;
            };
            captureButton.KeyDown += (_, e) =>
            {
                if (captureButton.IsChecked != true) return;
                e.Handled = true;

                // Ignore presses of bare modifier keys — wait for the real key
                if (e.Key is Avalonia.Input.Key.LeftCtrl or Avalonia.Input.Key.RightCtrl
                    or Avalonia.Input.Key.LeftAlt or Avalonia.Input.Key.RightAlt
                    or Avalonia.Input.Key.LeftShift or Avalonia.Input.Key.RightShift
                    or Avalonia.Input.Key.LWin or Avalonia.Input.Key.RWin)
                {
                    return;
                }

                if (e.Key == Avalonia.Input.Key.Escape)
                {
                    captureButton.IsChecked = false;
                    return;
                }

                var parts = new List<string>();
                if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) parts.Add("Control");
                if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)) parts.Add("Alt");
                if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) parts.Add("Shift");
                if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta)) parts.Add("Windows");

                // Avalonia's Key enum uses the same member names as WPF's — the settings vocabulary
                binding.ModifierKeys = parts.Count > 0 ? string.Join(", ", parts) : "None";
                binding.Key = e.Key.ToString();
                _app!.Host.SaveSettings();

                captureButton.IsChecked = false;
                captureButton.Content = binding.HotkeyDisplayText;
            };
            grid.Children.Add(captureButton);

            var devicePicker = new ComboBox
            {
                MinWidth = 170,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = deviceChoices,
                SelectedItem = deviceChoices.FirstOrDefault(c => c.Type == binding.DeviceType && c.Id == binding.DeviceId),
            };
            devicePicker.SelectionChanged += (_, _) =>
            {
                if (devicePicker.SelectedItem is HotkeyDeviceChoice choice)
                {
                    binding.DeviceType = choice.Type;
                    binding.DeviceId = choice.Id;
                    _app!.Host.SaveSettings();
                }
            };
            Grid.SetColumn(devicePicker, 1);
            grid.Children.Add(devicePicker);

            var profilePicker = new ComboBox
            {
                MinWidth = 150,
                Margin = new Thickness(12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = _app!.Host.Profiles,
                SelectedItem = _app.Host.Profiles.FirstOrDefault(p => p.Guid == binding.ProfileGuid),
                DisplayMemberBinding = new Avalonia.Data.Binding(nameof(Profile.Name)),
            };
            profilePicker.SelectionChanged += (_, _) =>
            {
                if (profilePicker.SelectedItem is Profile p)
                {
                    binding.ProfileGuid = p.Guid;
                    _app.Host.SaveSettings();
                }
            };
            Grid.SetColumn(profilePicker, 2);
            grid.Children.Add(profilePicker);

            var removeButton = new Button { Content = "✕", VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(removeButton, "Remove hotkey");
            removeButton.Click += (_, _) =>
            {
                settings.HotkeyBindings.Remove(binding);
                _app.Host.SaveSettings();
                RebuildRows();
            };
            Grid.SetColumn(removeButton, 3);
            grid.Children.Add(removeButton);

            border.Child = grid;
            DeviceRows.Children.Add(border);
        }

        private void AddThermalrightAdvanced(ThermalrightPanelDevice device)
        {
            var expander = new Expander
            {
                Header = "Panel options",
                Margin = new Thickness(24, -8, 0, 0),
                FontSize = 12,
            };

            var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };

            var brightnessRow = new DockPanel { MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
            brightnessRow.Children.Add(new TextBlock { Text = "Brightness", VerticalAlignment = VerticalAlignment.Center, Width = 110 });
            var brightness = new Slider { Minimum = 5, Maximum = 100, Value = device.Brightness, Width = 200 };
            brightness.ValueChanged += (_, _) => { device.Brightness = (int)brightness.Value; _app?.Host.SaveSettings(); };
            brightnessRow.Children.Add(brightness);
            panel.Children.Add(brightnessRow);

            var fpsRow = new DockPanel { MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
            fpsRow.Children.Add(new TextBlock { Text = "Target FPS", VerticalAlignment = VerticalAlignment.Center, Width = 110 });
            var fps = new NumericUpDown { Minimum = 1, Maximum = 60, Value = device.TargetFrameRate, Increment = 1, FormatString = "0" };
            fps.ValueChanged += (_, _) => { device.TargetFrameRate = (int)(fps.Value ?? 15); _app?.Host.SaveSettings(); };
            fpsRow.Children.Add(fps);
            panel.Children.Add(fpsRow);

            if (device.IsJpegQualityConfigurable)
            {
                var qualityRow = new DockPanel { MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
                qualityRow.Children.Add(new TextBlock { Text = "JPEG quality", VerticalAlignment = VerticalAlignment.Center, Width = 110 });
                var quality = new NumericUpDown { Minimum = 50, Maximum = 100, Value = device.JpegQuality, Increment = 5, FormatString = "0" };
                quality.ValueChanged += (_, _) => { device.JpegQuality = (int)(quality.Value ?? 95); _app?.Host.SaveSettings(); };
                qualityRow.Children.Add(quality);
                panel.Children.Add(qualityRow);
            }

            if (device.HasFlickerFix)
            {
                var flicker = new ToggleSwitch
                {
                    OnContent = "Flicker fix (crop to 462 rows)",
                    OffContent = "Flicker fix (crop to 462 rows)",
                    IsChecked = device.FlickerFix,
                };
                flicker.IsCheckedChanged += (_, _) => { device.FlickerFix = flicker.IsChecked == true; _app?.Host.SaveSettings(); };
                panel.Children.Add(flicker);
            }

            if (device.HasDisplayMask)
            {
                var maskRow = new DockPanel { MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
                maskRow.Children.Add(new TextBlock { Text = "Display mask", VerticalAlignment = VerticalAlignment.Center, Width = 110 });
                var mask = new ComboBox
                {
                    ItemsSource = new[] { "None", "Rounded left", "Rounded all" },
                    SelectedIndex = (int)device.DisplayMask,
                };
                mask.SelectionChanged += (_, _) =>
                {
                    if (mask.SelectedIndex >= 0)
                    {
                        device.DisplayMask = (ThermalrightDisplayMask)mask.SelectedIndex;
                        _app?.Host.SaveSettings();
                    }
                };
                maskRow.Children.Add(mask);
                panel.Children.Add(maskRow);
            }

            expander.Content = panel;
            DeviceRows.Children.Add(expander);
        }

        /// <summary>Offers a one-click profile sized exactly to the panel's render resolution (avoids letterboxing).</summary>
        private void AddMatchingProfileButton(string deviceName, int width, int height, Action<Guid> assign)
        {
            if (_app == null || width <= 0 || height <= 0) return;

            // only offer when no existing profile matches the panel size
            if (_app.Host.Profiles.Any(p => p.Width == width && p.Height == height))
            {
                return;
            }

            var button = new Button
            {
                Content = $"Create matching {width}×{height} profile",
                FontSize = 11,
                Margin = new Thickness(24, -6, 0, 0),
            };
            button.Click += (_, _) =>
            {
                var profile = new Models.Profile
                {
                    Guid = Guid.NewGuid(),
                    Name = deviceName,
                    Width = width,
                    Height = height,
                    BackgroundColor = "#FF000000",
                    Color = "#FFFFFFFF",
                };
                _app.Host.Profiles.Add(profile);
                _app.Host.SaveProfiles();
                Stores.DisplayItemStore.Instance.Save(profile);
                assign(profile.Guid);
                RebuildRows();
            };
            DeviceRows.Children.Add(button);
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
            Guid profileGuid, Action<Guid> setProfile, Func<string> status, Action remove,
            LCD_ROTATION rotation = LCD_ROTATION.RotateNone, Action<LCD_ROTATION>? setRotation = null,
            int brightness = 100, Action<int>? setBrightness = null)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12),
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto") };

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

            if (setBrightness != null)
            {
                var brightnessSlider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = brightness,
                    Width = 110,
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTip.SetTip(brightnessSlider, "Brightness");
                brightnessSlider.ValueChanged += (_, _) => setBrightness((int)brightnessSlider.Value);
                Grid.SetColumn(brightnessSlider, 1);
                // shares the cell with the profile picker column shift below
                grid.ColumnDefinitions.Insert(1, new ColumnDefinition(GridLength.Auto));
                foreach (var child in grid.Children)
                {
                    var col = Grid.GetColumn((Control)child);
                    if (col >= 1) Grid.SetColumn((Control)child, col + 1);
                }
                Grid.SetColumn(brightnessSlider, 1);
                grid.Children.Add(brightnessSlider);
            }

            if (setRotation != null)
            {
                var rotationPicker = new ComboBox
                {
                    MinWidth = 90,
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ItemsSource = new[] { "0°", "90°", "180°", "270°" },
                    SelectedIndex = (int)rotation,
                };
                rotationPicker.SelectionChanged += (_, _) =>
                {
                    if (rotationPicker.SelectedIndex >= 0)
                    {
                        setRotation((LCD_ROTATION)rotationPicker.SelectedIndex);
                    }
                };
                Grid.SetColumn(rotationPicker, 2);
                grid.Children.Add(rotationPicker);
            }

            var enabled = new ToggleSwitch
            {
                IsChecked = isEnabled,
                OnContent = "",
                OffContent = "",
                Margin = new Thickness(12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            enabled.IsCheckedChanged += (_, _) => setEnabled(enabled.IsChecked == true);
            Grid.SetColumn(enabled, 3);
            grid.Children.Add(enabled);

            var removeButton = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
            removeButton.Click += (_, _) => remove();
            Grid.SetColumn(removeButton, 4);
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

                // BeadaPanel (StatusLink probe per VID 4E58 PID 1001 device)
                foreach (UsbRegistry deviceReg in LibUsbDotNet.UsbDevice.AllDevices)
                {
                    if (deviceReg.Vid != 0x4e58 || deviceReg.Pid != 0x1001) continue;

                    var deviceId = deviceReg.DeviceProperties.TryGetValue("DeviceID", out var devIdObj) && devIdObj is string devIdStr
                        ? devIdStr : deviceReg.DevicePath ?? $"USB\\VID_{deviceReg.Vid:X4}&PID_{deviceReg.Pid:X4}";
                    var deviceLocation = deviceReg.DeviceProperties.TryGetValue("LocationInformation", out var locObj) && locObj is string locStr
                        ? locStr : deviceReg.DevicePath ?? deviceId;

                    try
                    {
                        var panelInfo = await BeadaPanelHelper.GetPanelInfoAsync(deviceReg);
                        if (panelInfo != null && BeadaPanelModelDatabase.Models.ContainsKey(panelInfo.Model))
                        {
                            var existing = settings.BeadaPanelDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                            if (existing == null)
                            {
                                settings.BeadaPanelDevices.Add(new BeadaPanelDevice
                                {
                                    DeviceId = deviceId,
                                    DeviceLocation = deviceLocation,
                                    Model = panelInfo.Model.ToString(),
                                    ProfileGuid = _app.Host.Profiles.FirstOrDefault()?.Guid ?? Guid.Empty,
                                });
                                added++;
                            }
                            else
                            {
                                existing.DeviceLocation = deviceLocation;
                                updated++;
                            }
                        }
                        else
                        {
                            Logger.Information("BeadaPanel at {Path}: StatusLink unavailable (likely already streaming)", deviceReg.DevicePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Error probing BeadaPanel device");
                    }
                }

                // Thermaltake / ASRock (HID)
                var thermaltake = await Task.Run(ThermaltakePanelHelper.ScanDevices);
                foreach (var found in thermaltake)
                {
                    var existing = settings.ThermaltakePanelDevices.FirstOrDefault(d => d.DeviceId == found.DeviceId);
                    if (existing == null)
                    {
                        settings.ThermaltakePanelDevices.Add(new ThermaltakePanelDevice
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
                        updated++;
                    }
                }

                // Jungle Leopard / Hongtai (CDC serial)
                var jl = await Task.Run(JlPanelHelper.ScanDevices);
                foreach (var found in jl)
                {
                    var existing = settings.JlPanelDevices.FirstOrDefault(d => d.DeviceId == found.DeviceId);
                    if (existing == null)
                    {
                        settings.JlPanelDevices.Add(new JlPanelDevice
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
