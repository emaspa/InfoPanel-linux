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

            Loaded += (_, _) => LoadFromSettings();

            AutoStartToggle.IsCheckedChanged += (_, _) => Apply(s => s.AutoStart = AutoStartToggle.IsChecked == true);
            AutoStartDelay.ValueChanged += (_, _) => Apply(s => s.AutoStartDelay = (int)(AutoStartDelay.Value ?? 5));
            StartMinimizedToggle.IsCheckedChanged += (_, _) => Apply(s => s.StartMinimized = StartMinimizedToggle.IsChecked == true);
            MinimizeToTrayToggle.IsCheckedChanged += (_, _) => Apply(s => s.MinimizeToTray = MinimizeToTrayToggle.IsChecked == true);
            FrameRate.ValueChanged += (_, _) => Apply(s => s.TargetFrameRate = (int)(FrameRate.Value ?? 15));
            GraphRate.ValueChanged += (_, _) => Apply(s => s.TargetGraphUpdateRate = (int)(GraphRate.Value ?? 1000));
            WebServerToggle.IsCheckedChanged += (_, _) => Apply(s => s.WebServer = WebServerToggle.IsChecked == true);
            WebServerIp.LostFocus += (_, _) => Apply(s => s.WebServerListenIp = WebServerIp.Text ?? "127.0.0.1");
            WebServerPort.ValueChanged += (_, _) => Apply(s => s.WebServerListenPort = (int)(WebServerPort.Value ?? 80));
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
                FrameRate.Value = settings.TargetFrameRate;
                GraphRate.Value = settings.TargetGraphUpdateRate;
                WebServerToggle.IsChecked = settings.WebServer;
                WebServerIp.Text = settings.WebServerListenIp;
                WebServerPort.Value = settings.WebServerListenPort;
                UpdateWebLink(settings);

                var version = Assembly.GetExecutingAssembly().GetName().Version;
                VersionText.Text = $"InfoPanel v2 — {version?.ToString(3) ?? "dev"} (rebuild)";
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

        private void UpdateWebLink(Models.Settings settings)
        {
            WebServerLink.Text = settings.WebServer
                ? $"Serving at http://{settings.WebServerListenIp}:{settings.WebServerListenPort}/"
                : "";
        }

        private void OpenDataFolder_Click(object? sender, RoutedEventArgs e) => OpenFolder(ConfigPersistence.BaseFolder);

        private void OpenLogFolder_Click(object? sender, RoutedEventArgs e) => OpenFolder(Path.Combine(ConfigPersistence.BaseFolder, "logs"));

        private static void OpenFolder(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
            }
            catch
            {
            }
        }
    }
}
