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
using InfoPanel.VmaxPanel;
using InfoPanel.JonsboPanel;
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

        /// <summary>
        /// A device without a resolvable profile renders nothing while the loop idles
        /// at the target frame rate; say so instead of showing a meaningless fps.
        /// </summary>
        private string RunningStatus(Guid profileGuid, int frameRate, long frameTime) =>
            profileGuid == Guid.Empty || _app?.Host.Profiles.Any(p => p.Guid == profileGuid) != true
                ? "running · assign a profile to start streaming"
                : $"running · {frameRate} fps · {frameTime} ms";

        private static string Subtitle(params string?[] parts) =>
            string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        private void RebuildStatusOnly()
        {
            foreach (var (status, get) in _statusBindings)
            {
                status.Text = get();
            }
        }

        // Runtime variant detection can rename a device after the rows were built
        // (e.g. Trofeo 9.16" re-identified as 11.3"); rebuild when Model changes.
        private readonly List<Action> _modelSubscriptionCleanups = [];

        private void WatchModelChanges(System.ComponentModel.INotifyPropertyChanged device)
        {
            System.ComponentModel.PropertyChangedEventHandler handler = (_, e) =>
            {
                if (e.PropertyName == "Model")
                {
                    Dispatcher.UIThread.Post(RebuildRows);
                }
            };
            device.PropertyChanged += handler;
            _modelSubscriptionCleanups.Add(() => device.PropertyChanged -= handler);
        }

        private void RebuildRows()
        {
            DeviceRows.Children.Clear();
            _statusBindings.Clear();
            foreach (var cleanup in _modelSubscriptionCleanups)
            {
                cleanup();
            }
            _modelSubscriptionCleanups.Clear();
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
                        ? RunningStatus(device.ProfileGuid, device.RuntimeProperties.FrameRate, device.RuntimeProperties.FrameTime)
                        : !device.Enabled ? "disabled"
                        : device.RuntimeProperties.ErrorMessage is { Length: > 0 } err ? err : "idle",
                    remove: () => { settings.ThermalrightPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); },
                    rotation: device.Rotation,
                    setRotation: r => { device.Rotation = r; _app.Host.SaveSettings(); });
                AddThermalrightAdvanced(device);
                AddMatchingProfileButton(device.ModelInfo?.Name ?? device.Model.ToString(),
                    device.DisplayWidth, device.DisplayHeight, g => { device.ProfileGuid = g; _app.Host.SaveSettings(); });
                WatchModelChanges(device);
            }

            AddFamilyHeader("Turing Smart Screen", settings.TuringPanelMultiDeviceMode,
                v => { settings.TuringPanelMultiDeviceMode = v; _app.Host.SaveSettings(); });
            foreach (var device in settings.TuringPanelDevices.ToList())
            {
                AddRow(
                    title: device.ModelInfo?.Name ?? device.Model ?? "Turing panel",
                    subtitle: device.ModelInfo is { } mi
                        ? $"{device.DeviceId} · {device.DeviceLocation} · renders at {mi.Width}×{mi.Height}"
                        : $"{device.DeviceId} · {device.DeviceLocation}",
                    isEnabled: device.Enabled,
                    setEnabled: v => { device.Enabled = v; _app.Host.SaveSettings(); },
                    profileGuid: device.ProfileGuid,
                    setProfile: g => { device.ProfileGuid = g; _app.Host.SaveSettings(); },
                    status: () => device.RuntimeProperties.IsRunning
                        ? RunningStatus(device.ProfileGuid, device.RuntimeProperties.FrameRate, device.RuntimeProperties.FrameTime)
                        : !device.Enabled ? "disabled"
                        : device.RuntimeProperties.ErrorMessage is { Length: > 0 } err ? err : "idle",
                    remove: () => { settings.TuringPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); },
                    rotation: device.Rotation,
                    setRotation: r => { device.Rotation = r; _app.Host.SaveSettings(); });
                // Serial models stream raw pixels, so quality only applies to USB models
                if (device.ModelInfo?.IsUsbDevice == true)
                {
                    AddPanelOptions(device.Brightness, b => { device.Brightness = b; _app.Host.SaveSettings(); },
                        device.TargetFrameRate, f => { device.TargetFrameRate = f; _app.Host.SaveSettings(); },
                        device.JpegQuality, q => { device.JpegQuality = q; _app.Host.SaveSettings(); });
                }
                else
                {
                    AddPanelOptions(device.Brightness, b => { device.Brightness = b; _app.Host.SaveSettings(); },
                        device.TargetFrameRate, f => { device.TargetFrameRate = f; _app.Host.SaveSettings(); });
                }
            }

            AddFamilyHeader("BeadaPanel", settings.BeadaPanelMultiDeviceMode,
                v => { settings.BeadaPanelMultiDeviceMode = v; _app.Host.SaveSettings(); });
            foreach (var device in settings.BeadaPanelDevices.ToList())
            {
                var beadaInfo = Enum.TryParse<BeadaPanelModel>(device.Model, out var beadaModel)
                    && BeadaPanelModelDatabase.Models.TryGetValue(beadaModel, out var bi) ? bi : null;
                AddRow(
                    title: beadaInfo?.Name ?? device.Model ?? "BeadaPanel",
                    subtitle: Subtitle(device.DeviceId, device.DeviceLocation,
                        beadaInfo != null ? $"renders at {beadaInfo.Width}×{beadaInfo.Height}" : null),
                    isEnabled: device.Enabled,
                    setEnabled: v => { device.Enabled = v; _app.Host.SaveSettings(); },
                    profileGuid: device.ProfileGuid,
                    setProfile: g => { device.ProfileGuid = g; _app.Host.SaveSettings(); },
                    status: () => device.RuntimeProperties.IsRunning
                        ? RunningStatus(device.ProfileGuid, device.RuntimeProperties.FrameRate, device.RuntimeProperties.FrameTime)
                        : !device.Enabled ? "disabled"
                        : device.RuntimeProperties.ErrorMessage is { Length: > 0 } err ? err : "idle",
                    remove: () => { settings.BeadaPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); },
                    rotation: device.Rotation,
                    setRotation: r => { device.Rotation = r; _app.Host.SaveSettings(); });
                // BeadaPanel streams raw RGB565, so there is no JPEG quality
                AddPanelOptions(device.Brightness, b => { device.Brightness = b; _app.Host.SaveSettings(); },
                    device.TargetFrameRate, f => { device.TargetFrameRate = f; _app.Host.SaveSettings(); });
            }

            AddFamilyHeader("Thermaltake / ASRock LCD", settings.ThermaltakePanelMultiDeviceMode,
                v => { settings.ThermaltakePanelMultiDeviceMode = v; _app.Host.SaveSettings(); });
            foreach (var device in settings.ThermaltakePanelDevices.ToList())
            {
                AddRow(
                    title: device.ModelInfo?.Name ?? device.Model.ToString(),
                    subtitle: Subtitle(device.DeviceId, device.DeviceLocation,
                        device.DisplayWidth > 0 ? $"renders at {device.DisplayWidth}×{device.DisplayHeight}" : null),
                    isEnabled: device.Enabled,
                    setEnabled: v => { device.Enabled = v; _app.Host.SaveSettings(); },
                    profileGuid: device.ProfileGuid,
                    setProfile: g => { device.ProfileGuid = g; _app.Host.SaveSettings(); },
                    status: () => device.RuntimeProperties.IsRunning
                        ? RunningStatus(device.ProfileGuid, device.RuntimeProperties.FrameRate, device.RuntimeProperties.FrameTime)
                        : !device.Enabled ? "disabled"
                        : device.RuntimeProperties.ErrorMessage is { Length: > 0 } err ? err : "idle",
                    remove: () => { settings.ThermaltakePanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); },
                    rotation: device.Rotation,
                    setRotation: r => { device.Rotation = r; _app.Host.SaveSettings(); });
                AddPanelOptions(device.Brightness, b => { device.Brightness = b; _app.Host.SaveSettings(); },
                    device.TargetFrameRate, f => { device.TargetFrameRate = f; _app.Host.SaveSettings(); },
                    device.JpegQuality, q => { device.JpegQuality = q; _app.Host.SaveSettings(); });
            }

            AddFamilyHeader("Jungle Leopard / Hongtai", settings.JlPanelMultiDeviceMode,
                v => { settings.JlPanelMultiDeviceMode = v; _app.Host.SaveSettings(); });
            foreach (var device in settings.JlPanelDevices.ToList())
            {
                AddRow(
                    title: device.ModelInfo?.Name ?? device.Model.ToString(),
                    subtitle: Subtitle(device.DeviceId, device.DeviceLocation,
                        device.DisplayWidth > 0 ? $"renders at {device.DisplayWidth}×{device.DisplayHeight}" : null),
                    isEnabled: device.Enabled,
                    setEnabled: v => { device.Enabled = v; _app.Host.SaveSettings(); },
                    profileGuid: device.ProfileGuid,
                    setProfile: g => { device.ProfileGuid = g; _app.Host.SaveSettings(); },
                    status: () => device.RuntimeProperties.IsRunning
                        ? RunningStatus(device.ProfileGuid, device.RuntimeProperties.FrameRate, device.RuntimeProperties.FrameTime)
                        : !device.Enabled ? "disabled"
                        : device.RuntimeProperties.ErrorMessage is { Length: > 0 } err ? err : "idle",
                    remove: () => { settings.JlPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); },
                    rotation: device.Rotation,
                    setRotation: r => { device.Rotation = r; _app.Host.SaveSettings(); });
                AddPanelOptions(device.Brightness, b => { device.Brightness = b; _app.Host.SaveSettings(); },
                    device.TargetFrameRate, f => { device.TargetFrameRate = f; _app.Host.SaveSettings(); },
                    device.JpegQuality, q => { device.JpegQuality = q; _app.Host.SaveSettings(); });
            }

            AddFamilyHeader("VMAX / AuyiHomu", settings.VmaxPanelMultiDeviceMode,
                v => { settings.VmaxPanelMultiDeviceMode = v; _app.Host.SaveSettings(); });
            foreach (var device in settings.VmaxPanelDevices.ToList())
            {
                AddRow(
                    title: device.ModelInfo?.Name ?? device.Model.ToString(),
                    subtitle: Subtitle(device.DeviceId, device.DeviceLocation,
                        device.DisplayWidth > 0 ? $"renders at {device.DisplayWidth}×{device.DisplayHeight}" : null),
                    isEnabled: device.Enabled,
                    setEnabled: v => { device.Enabled = v; _app.Host.SaveSettings(); },
                    profileGuid: device.ProfileGuid,
                    setProfile: g => { device.ProfileGuid = g; _app.Host.SaveSettings(); },
                    status: () => device.RuntimeProperties.IsRunning
                        ? RunningStatus(device.ProfileGuid, device.RuntimeProperties.FrameRate, device.RuntimeProperties.FrameTime)
                        : !device.Enabled ? "disabled"
                        : device.RuntimeProperties.ErrorMessage is { Length: > 0 } err ? err : "idle",
                    remove: () => { settings.VmaxPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); },
                    rotation: device.Rotation,
                    setRotation: r => { device.Rotation = r; _app.Host.SaveSettings(); });
                AddPanelOptions(device.Brightness, b => { device.Brightness = b; _app.Host.SaveSettings(); },
                    device.TargetFrameRate, f => { device.TargetFrameRate = f; _app.Host.SaveSettings(); },
                    device.JpegQuality, q => { device.JpegQuality = q; _app.Host.SaveSettings(); });
            }

            AddFamilyHeader("Jonsbo", settings.JonsboPanelMultiDeviceMode,
                v => { settings.JonsboPanelMultiDeviceMode = v; _app.Host.SaveSettings(); });
            foreach (var device in settings.JonsboPanelDevices.ToList())
            {
                AddRow(
                    title: device.ModelInfo?.Name ?? device.Model.ToString(),
                    subtitle: Subtitle(device.DeviceId, device.DeviceLocation,
                        device.DisplayWidth > 0 ? $"renders at {device.DisplayWidth}×{device.DisplayHeight}" : null),
                    isEnabled: device.Enabled,
                    setEnabled: v => { device.Enabled = v; _app.Host.SaveSettings(); },
                    profileGuid: device.ProfileGuid,
                    setProfile: g => { device.ProfileGuid = g; _app.Host.SaveSettings(); },
                    status: () => device.RuntimeProperties.IsRunning
                        ? RunningStatus(device.ProfileGuid, device.RuntimeProperties.FrameRate, device.RuntimeProperties.FrameTime)
                        : !device.Enabled ? "disabled"
                        : device.RuntimeProperties.ErrorMessage is { Length: > 0 } err ? err : "idle",
                    remove: () => { settings.JonsboPanelDevices.Remove(device); _app.Host.SaveSettings(); RebuildRows(); },
                    rotation: device.Rotation,
                    setRotation: r => { device.Rotation = r; _app.Host.SaveSettings(); });
                // The DS339 streams raw BGR888, so JPEG quality only applies to the
                // DS916 serial transport.
                int? jpegQuality = device.Model == JonsboPanelModel.DS916 ? device.JpegQuality : null;
                Action<int>? setJpegQuality = device.Model == JonsboPanelModel.DS916
                    ? q => { device.JpegQuality = q; _app.Host.SaveSettings(); }
                    : null;
                AddPanelOptions(device.Brightness, b => { device.Brightness = b; _app.Host.SaveSettings(); },
                    device.TargetFrameRate, f => { device.TargetFrameRate = f; _app.Host.SaveSettings(); },
                    jpegQuality, setJpegQuality);
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
            foreach (var d in settings.VmaxPanelDevices) deviceChoices.Add(new("Vmax", d.DeviceId, d.ModelInfo?.Name ?? d.Model.ToString()));
            foreach (var d in settings.JonsboPanelDevices) deviceChoices.Add(new("Jonsbo", d.DeviceId, d.ModelInfo?.Name ?? d.Model.ToString()));

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

            AddStopwatchHotkeyRow(settings);
        }

        /// <summary>Hotkeys for the bundled Stopwatch plugin (start/stop/reset actions).</summary>
        private void AddStopwatchHotkeyRow(Settings settings)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 10),
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
            };

            var panel = new DockPanel();
            var captures = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            DockPanel.SetDock(captures, Dock.Right);

            captures.Children.Add(MakeStopwatchCapture("Start", settings.StopwatchHotkeyStartModifiers, settings.StopwatchHotkeyStartKey,
                (m, k) => { settings.StopwatchHotkeyStartModifiers = m; settings.StopwatchHotkeyStartKey = k; _app!.Host.SaveSettings(); }));
            captures.Children.Add(MakeStopwatchCapture("Stop", settings.StopwatchHotkeyStopModifiers, settings.StopwatchHotkeyStopKey,
                (m, k) => { settings.StopwatchHotkeyStopModifiers = m; settings.StopwatchHotkeyStopKey = k; _app!.Host.SaveSettings(); }));
            captures.Children.Add(MakeStopwatchCapture("Reset", settings.StopwatchHotkeyResetModifiers, settings.StopwatchHotkeyResetKey,
                (m, k) => { settings.StopwatchHotkeyResetModifiers = m; settings.StopwatchHotkeyResetKey = k; _app!.Host.SaveSettings(); }));

            panel.Children.Add(captures);

            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = "Stopwatch", FontWeight = Avalonia.Media.FontWeight.SemiBold });
            info.Children.Add(new TextBlock { Text = "Global start/stop/reset for the bundled Stopwatch plugin.", FontSize = 11, Opacity = 0.6 });
            panel.Children.Add(info);

            border.Child = panel;
            DeviceRows.Children.Add(border);
        }

        private static string StopwatchHotkeyText(string action, string modifiers, string key)
        {
            if (string.IsNullOrEmpty(key) || key == "None")
            {
                return $"{action}: (not set)";
            }

            var parts = modifiers.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0 && m != "None").ToList();
            parts.Add(key);
            return $"{action}: {string.Join("+", parts)}";
        }

        private Control MakeStopwatchCapture(string action, string modifiers, string key, Action<string, string> apply)
        {
            var button = new ToggleButton { Content = StopwatchHotkeyText(action, modifiers, key), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(button, "Click, then press the key combination. Escape clears the binding.");

            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true) button.Content = "Press keys…";
            };
            button.KeyDown += (_, e) =>
            {
                if (button.IsChecked != true) return;
                e.Handled = true;

                if (e.Key is Avalonia.Input.Key.LeftCtrl or Avalonia.Input.Key.RightCtrl
                    or Avalonia.Input.Key.LeftAlt or Avalonia.Input.Key.RightAlt
                    or Avalonia.Input.Key.LeftShift or Avalonia.Input.Key.RightShift
                    or Avalonia.Input.Key.LWin or Avalonia.Input.Key.RWin)
                {
                    return;
                }

                if (e.Key == Avalonia.Input.Key.Escape)
                {
                    modifiers = "None";
                    key = "None";
                }
                else
                {
                    var parts = new List<string>();
                    if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) parts.Add("Control");
                    if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)) parts.Add("Alt");
                    if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) parts.Add("Shift");
                    if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta)) parts.Add("Windows");
                    modifiers = parts.Count > 0 ? string.Join(", ", parts) : "None";
                    key = e.Key.ToString();
                }

                apply(modifiers, key);
                button.IsChecked = false;
                button.Content = StopwatchHotkeyText(action, modifiers, key);
            };

            return button;
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

                // Ignore presses of bare modifier keys - wait for the real key
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

                // Avalonia's Key enum uses the same member names as WPF's - the settings vocabulary
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

        /// <summary>Collapsible "Panel options" section: brightness, per-device target FPS, and JPEG quality when applicable.</summary>
        private void AddPanelOptions(int brightness, Action<int> setBrightness,
            int targetFps, Action<int> setTargetFps,
            int? jpegQuality = null, Action<int>? setJpegQuality = null)
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
            var brightnessSlider = new Slider { Minimum = 5, Maximum = 100, Value = brightness, Width = 200 };
            brightnessSlider.ValueChanged += (_, _) => setBrightness((int)brightnessSlider.Value);
            brightnessRow.Children.Add(brightnessSlider);
            panel.Children.Add(brightnessRow);

            var fpsRow = new DockPanel { MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
            fpsRow.Children.Add(new TextBlock { Text = "Target FPS", VerticalAlignment = VerticalAlignment.Center, Width = 110 });
            var fps = new NumericUpDown { Minimum = 1, Maximum = 60, Value = targetFps, Increment = 1, FormatString = "0" };
            fps.ValueChanged += (_, _) => setTargetFps((int)(fps.Value ?? 15));
            fpsRow.Children.Add(fps);
            panel.Children.Add(fpsRow);

            if (jpegQuality != null && setJpegQuality != null)
            {
                var qualityRow = new DockPanel { MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
                qualityRow.Children.Add(new TextBlock { Text = "JPEG quality", VerticalAlignment = VerticalAlignment.Center, Width = 110 });
                var quality = new NumericUpDown { Minimum = 50, Maximum = 100, Value = jpegQuality, Increment = 5, FormatString = "0" };
                quality.ValueChanged += (_, _) => setJpegQuality((int)(quality.Value ?? 90));
                qualityRow.Children.Add(quality);
                panel.Children.Add(qualityRow);
            }

            expander.Content = panel;
            DeviceRows.Children.Add(expander);
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
            LCD_ROTATION rotation = LCD_ROTATION.RotateNone, Action<LCD_ROTATION>? setRotation = null)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12),
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
            };

            // Sequential column assignment: optional controls used to shift already
            // placed children while later ones kept hardcoded indexes, stacking the
            // rotation picker on top of the profile picker on brightness rows.
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*") };
            var nextColumn = 0;
            void AddCell(Control control)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                Grid.SetColumn(control, ++nextColumn);
                grid.Children.Add(control);
            }

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
                PlaceholderText = "Assign profile…",
            };
            profilePicker.SelectionChanged += (_, _) =>
            {
                if (profilePicker.SelectedItem is Profile p) setProfile(p.Guid);
            };
            AddCell(profilePicker);

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
                AddCell(rotationPicker);
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
            AddCell(enabled);

            var removeButton = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
            removeButton.Click += (_, _) => remove();
            AddCell(removeButton);

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
                        // The scan only sees the shared VID/PID, so it can neither
                        // identify ChiZhu models (init exchange required) nor Trofeo
                        // variants (byte[20] at connect). Never downgrade a runtime
                        // identification to Unknown or to the base 9.16" guess.
                        var keepExisting = found.Model == ThermalrightPanelModel.Unknown
                            || (found.Model == ThermalrightPanelModel.TrofeoVision916
                                && existing.Model is ThermalrightPanelModel.TrofeoVision916V2 or ThermalrightPanelModel.TrofeoVision113);
                        if (existing.Model != found.Model && !keepExisting)
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

                // Jonsbo AIO (DS916 HLVMAX serial, DS339 MS9132)
                var jonsbo = await Task.Run(JonsboPanelHelper.ScanDevices);
                var ds339Confirmed = jonsbo.Any(f => f.Model == JonsboPanelModel.DS339 && f.Ms9132Confirmed);
                var ms9132Ambiguous = jonsbo.Any(f => f.Model == JonsboPanelModel.DS339 && !f.Ms9132Confirmed);
                foreach (var found in jonsbo)
                {
                    var existing = settings.JonsboPanelDevices.FirstOrDefault(d => d.DeviceId == found.DeviceId);
                    if (existing == null)
                    {
                        // The MS9132 bridge is shared with the VMAX 4.6"; when the EDID
                        // probe could not confirm which panel is attached, don't shadow
                        // an existing VMAX configuration with a Jonsbo entry.
                        if (found.Model == JonsboPanelModel.DS339 && !found.Ms9132Confirmed
                            && settings.VmaxPanelDevices.Count > 0)
                            continue;

                        settings.JonsboPanelDevices.Add(new JonsboPanelDevice
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

                // VMAX / AuyiHomu - shares the MS9132 bridge (345F:9132) with the Jonsbo
                // DS339, so skip when the EDID identified the panel as a DS339, or when
                // it is unreadable but the device is already configured as a DS339.
                var skipVmax = ds339Confirmed
                    || (ms9132Ambiguous && settings.JonsboPanelDevices.Any(d => d.Model == JonsboPanelModel.DS339));
                var vmax = skipVmax ? new List<VmaxPanelDiscoveryInfo>() : await Task.Run(VmaxPanelHelper.ScanDevices);
                foreach (var found in vmax)
                {
                    var existing = settings.VmaxPanelDevices.FirstOrDefault(d => d.DeviceId == found.DeviceId);
                    if (existing == null)
                    {
                        settings.VmaxPanelDevices.Add(new VmaxPanelDevice
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
