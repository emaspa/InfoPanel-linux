using Avalonia.Controls;
using Avalonia.Interactivity;
using InfoPanel.Persistence;
using System.Diagnostics;
using System.Reflection;

namespace InfoPanel.Views.Pages
{
    public partial class SettingsPage : UserControl
    {
        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();

            Loaded += (_, _) =>
            {
                LoadFromSettings();
                Gpu.GpuRendering.StatusChanged += OnRenderStatusChanged;
            };
            Unloaded += (_, _) => Gpu.GpuRendering.StatusChanged -= OnRenderStatusChanged;
            RenderDeviceBox.SelectionChanged += (_, _) =>
            {
                if (RenderDeviceBox.SelectedItem is ComboBoxItem { Tag: string id })
                {
                    Apply(s => s.RenderDevice = id);
                }
            };

            AutoStartToggle.IsCheckedChanged += (_, _) => Apply(s => s.AutoStart = AutoStartToggle.IsChecked == true);
            AutoStartDelay.ValueChanged += (_, _) => Apply(s => s.AutoStartDelay = (int)(AutoStartDelay.Value ?? 5));
            StartMinimizedToggle.IsCheckedChanged += (_, _) => Apply(s => s.StartMinimized = StartMinimizedToggle.IsChecked == true);
            MinimizeToTrayToggle.IsCheckedChanged += (_, _) => Apply(s => s.MinimizeToTray = MinimizeToTrayToggle.IsChecked == true);
            UpdateCheckToggle.IsCheckedChanged += (_, _) => Apply(s => s.UpdateCheckEnabled = UpdateCheckToggle.IsChecked == true);
            ProgramProfilesToggle.IsCheckedChanged += (_, _) => Apply(s => s.ProgramSpecificPanelsEnabled = ProgramProfilesToggle.IsChecked == true);
            HideOthersToggle.IsCheckedChanged += (_, _) => Apply(s => s.HideOtherProfilesWhenProgramSpecificShown = HideOthersToggle.IsChecked == true);
            FrameRate.ValueChanged += (_, _) => Apply(s => s.TargetFrameRate = (int)(FrameRate.Value ?? 15));
            GraphRate.ValueChanged += (_, _) => Apply(s => s.TargetGraphUpdateRate = (int)(GraphRate.Value ?? 1000));
            HardwareVideoDecodeToggle.IsCheckedChanged += (_, _) => Apply(s => s.HardwareVideoDecode = HardwareVideoDecodeToggle.IsChecked == true);
            WebServerToggle.IsCheckedChanged += (_, _) => Apply(s => s.WebServer = WebServerToggle.IsChecked == true);
            WebServerIp.LostFocus += (_, _) => Apply(s => s.WebServerListenIp = WebServerIp.Text ?? "127.0.0.1");
            WebServerPort.ValueChanged += (_, _) => Apply(s => s.WebServerListenPort = (int)(WebServerPort.Value ?? 80));
            WebRefreshRate.ValueChanged += (_, _) => Apply(s => s.WebServerRefreshRate = (int)(WebRefreshRate.Value ?? 66));
            GridLinesToggle.IsCheckedChanged += (_, _) => Apply(s => s.ShowGridLines = GridLinesToggle.IsChecked == true);
            GridSpacing.ValueChanged += (_, _) => Apply(s => s.GridLinesSpacing = (int)(GridSpacing.Value ?? 20));
        }

        private void LoadFromSettings()
        {
            if (Avalonia.Application.Current is not App app) return;

            _loading = true;
            try
            {
                var settings = app.Host.Settings;
                AutoStartToggle.IsChecked = settings.AutoStart;
                AutoStartDelay.Value = settings.AutoStartDelay;
                StartMinimizedToggle.IsChecked = settings.StartMinimized;
                MinimizeToTrayToggle.IsChecked = settings.MinimizeToTray;
                UpdateCheckToggle.IsChecked = settings.UpdateCheckEnabled;
                ProgramProfilesToggle.IsChecked = settings.ProgramSpecificPanelsEnabled;
                HideOthersToggle.IsChecked = settings.HideOtherProfilesWhenProgramSpecificShown;
                if (Platform.PlatformServices.ForegroundApp is { } fg && (!fg.IsAvailable || fg.Limitation != null))
                {
                    ProgramProfilesLimitation.Text = fg.IsAvailable
                        ? fg.Limitation
                        : "Unavailable: no X display detected.";
                    ProgramProfilesLimitation.IsVisible = true;
                }
                FrameRate.Value = settings.TargetFrameRate;
                GraphRate.Value = settings.TargetGraphUpdateRate;
                LoadRenderDevices(settings.RenderDevice);
                HardwareVideoDecodeToggle.IsChecked = settings.HardwareVideoDecode;
                WebServerToggle.IsChecked = settings.WebServer;
                WebServerIp.Text = settings.WebServerListenIp;
                WebServerPort.Value = settings.WebServerListenPort;
                WebRefreshRate.Value = settings.WebServerRefreshRate;
                GridLinesToggle.IsChecked = settings.ShowGridLines;
                GridSpacing.Value = (decimal)settings.GridLinesSpacing;
                UpdateWebLink(settings);
            }
            finally
            {
                _loading = false;
            }
        }

        private void Apply(Action<Models.Settings> change)
        {
            if (_loading || Avalonia.Application.Current is not App app) return;

            change(app.Host.Settings);
            app.Host.SaveSettings();
            UpdateWebLink(app.Host.Settings);
        }

        private void LoadRenderDevices(string selected)
        {
            var items = new List<ComboBoxItem>
            {
                new() { Content = "CPU", Tag = Gpu.GpuRendering.Cpu },
            };
            foreach (var device in Gpu.GpuDeviceCatalog.Devices)
            {
                items.Add(new ComboBoxItem { Content = device.DisplayName, Tag = device.Id });
            }
            // Keep a saved GPU that is missing right now (unplugged eGPU, driver
            // not loaded) visible instead of silently switching the setting.
            if (items.All(i => !string.Equals((string)i.Tag!, selected, StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new ComboBoxItem { Content = $"{selected} (not found)", Tag = selected });
            }

            RenderDeviceBox.ItemsSource = items;
            RenderDeviceBox.SelectedItem = items.First(i => string.Equals((string)i.Tag!, selected, StringComparison.OrdinalIgnoreCase));
            RenderDeviceStatus.Text = Gpu.GpuRendering.Status;
        }

        private void OnRenderStatusChanged() =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RenderDeviceStatus.Text = Gpu.GpuRendering.Status);

        private void UpdateWebLink(Models.Settings settings)
        {
            WebServerLink.Text = settings.WebServer
                ? $"Serving at http://{settings.WebServerListenIp}:{settings.WebServerListenPort}/"
                : "";
        }

    }
}
