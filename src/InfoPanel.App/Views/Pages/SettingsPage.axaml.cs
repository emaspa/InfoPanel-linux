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
                FrameRate.Value = settings.TargetFrameRate;
                GraphRate.Value = settings.TargetGraphUpdateRate;
                WebServerToggle.IsChecked = settings.WebServer;
                WebServerIp.Text = settings.WebServerListenIp;
                WebServerPort.Value = settings.WebServerListenPort;
                WebRefreshRate.Value = settings.WebServerRefreshRate;
                GridLinesToggle.IsChecked = settings.ShowGridLines;
                GridSpacing.Value = (decimal)settings.GridLinesSpacing;
                UpdateWebLink(settings);

                if (ContributorsList.Children.Count == 0)
                {
                    foreach (var (name, text) in new (string, string)[]
                    {
                        ("habibrehmansg", "Creator of the original InfoPanel for Windows."),
                        ("emaspa", "Linux port and Thermalright panel support."),
                        ("F3NN3X", "For the countless support and awesome plugins."),
                        ("/u/ME5ER", "Special thanks for patiently troubleshooting the early and buggy software iterations over extended periods."),
                        ("/u/DRA6N", "Better known as RobOnTwoWheels our CM on Discord, without whom it would not have existed."),
                        ("Everyone else", "For those that messaged or posted questions, feedback and panel designs on Reddit, HWiNFO forums and Discord."),
                    })
                    {
                        ContributorsList.Children.Add(new TextBlock { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = $"{name} — {text}" });
                    }

                    foreach (var (name, license) in new (string, string)[]
                    {
                        ("Avalonia + FluentAvalonia", "MIT License."),
                        ("SkiaSharp / Svg.Skia / RichTextKit", "MIT License."),
                        ("CommunityToolkit.Mvvm", "MIT License. (c) .NET Foundation."),
                        ("LibUsbDotNet", "LGPL v2 / GPL v2 (dual licensed)."),
                        ("HidSharp", "Apache License 2.0. (c) James F. Bellinger."),
                        ("TuringSmartScreenLib", "MIT License. (c) machi_pon."),
                        ("BouncyCastle.Cryptography", "MIT X Consortium License."),
                        ("ini-parser-netstandard", "MIT License. (c) Ricardo Amores Hernandez."),
                        ("Serilog", "Apache License 2.0."),
                        ("ffmpeg (runtime dependency)", "LGPL/GPL — system binary, not distributed."),
                    })
                    {
                        LicensesList.Children.Add(new TextBlock { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = $"{name} — {license}" });
                    }
                }

                var version = Assembly.GetExecutingAssembly().GetName().Version;
                VersionText.Text = $"InfoPanel Linux — v{version?.ToString(3) ?? "0.0.1"} alpha";
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

        private void OpenLink_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is string url)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
                }
                catch
                {
                }
            }
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
