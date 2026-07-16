using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using InfoPanel.Views.Pages;

namespace InfoPanel.Views
{
    public partial class MainWindow : Window
    {
        private readonly Dictionary<string, Control> _pages = [];
        private DispatcherTimer? _statusTimer;

        public MainWindow()
        {
            InitializeComponent();

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Title = $"InfoPanel Linux - v{version?.ToString(3) ?? "0.0.1"}";

            if (Avalonia.Application.Current is App sizeApp)
            {
                var settings = sizeApp.Host.Settings;
                if (settings.UiWidth > 400) Width = settings.UiWidth;
                if (settings.UiHeight > 300) Height = settings.UiHeight;
            }

            PropertyChanged += (_, args) =>
            {
                if (args.Property == BoundsProperty && Avalonia.Application.Current is App app)
                {
                    app.Host.Settings.UiWidth = (int)Bounds.Width;
                    app.Host.Settings.UiHeight = (int)Bounds.Height;
                    app.Host.SaveSettings();
                }
                else if (args.Property == WindowStateProperty
                    && WindowState == WindowState.Minimized
                    && Avalonia.Application.Current is App trayApp
                    && trayApp.Host.Settings.MinimizeToTray)
                {
                    WindowState = WindowState.Normal;
                    Hide();
                }
            };

            _pages["dashboard"] = new DashboardPage();
            _pages["designer"] = new DesignerPage();
            _pages["devices"] = new DevicesPage();
            _pages["sensors"] = new SensorsPage();
            _pages["settings"] = new SettingsPage();

            Loaded += (_, _) =>
            {
                // Dev/testing hook: INFOPANEL_START_PAGE=designer opens that page directly
                var startPage = Environment.GetEnvironmentVariable("INFOPANEL_START_PAGE");
                if (startPage == "settings")
                {
                    PageHost.Content = _pages["settings"]; // dev hook; footer item resolves lazily
                }
                else
                {
                    NavView.SelectedItem = NavView.MenuItems.OfType<FANavigationViewItem>()
                        .FirstOrDefault(item => (string?)item.Tag == startPage)
                        ?? NavView.MenuItems.OfType<FANavigationViewItem>().First();
                }

                _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _statusTimer.Tick += (_, _) => UpdateStatus();
                _statusTimer.Start();
                UpdateStatus();
            };

            Closing += (_, e) =>
            {
                // Close-to-tray: hide instead of exiting; Exit lives in the tray menu
                if (Avalonia.Application.Current is App app && app.Host.Settings.MinimizeToTray)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
        }

        private void UpdateStatus()
        {
            var sensorCount = Services.HwmonMonitor.SENSORHASH.Count + Monitors.PluginMonitor.SENSORHASH.Count;
            var app = Avalonia.Application.Current as App;
            var deviceCount = app?.Host.Settings.ThermalrightPanelDevices.Count(d => d.Enabled)
                + app?.Host.Settings.BeadaPanelDevices.Count(d => d.Enabled)
                + app?.Host.Settings.TuringPanelDevices.Count(d => d.Enabled);
            StatusText.Text = $"{deviceCount} devices · {sensorCount} sensors";
        }

        /// <summary>Selects a nav destination by tag (used by the tray menu).</summary>
        public void NavigateTo(string tag)
        {
            if (tag == "settings")
            {
                PageHost.Content = _pages["settings"];
                return;
            }

            var item = NavView.MenuItems.OfType<FANavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);
            if (item != null)
            {
                NavView.SelectedItem = item;
            }
        }

        private void LogsToggle_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            LogsDrawer.IsVisible = LogsToggle.IsChecked == true;
            if (LogsDrawer.IsVisible)
            {
                LogsList.ItemsSource ??= Utils.UiLogSink.Instance.Lines;
                ScrollLogsToEnd();
                Utils.UiLogSink.Instance.Lines.CollectionChanged += Logs_CollectionChanged;
            }
            else
            {
                Utils.UiLogSink.Instance.Lines.CollectionChanged -= Logs_CollectionChanged;
            }
        }

        private void Logs_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ScrollLogsToEnd();

        private void ScrollLogsToEnd()
        {
            if (Utils.UiLogSink.Instance.Lines.Count > 0)
            {
                LogsList.ScrollIntoView(Utils.UiLogSink.Instance.Lines[^1]);
            }
        }

        private async void LogsCopy_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(string.Join(Environment.NewLine, Utils.UiLogSink.Instance.Lines));
            }
        }

        private void LogsClear_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Utils.UiLogSink.Instance.Lines.Clear();

        private void LogsFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "xdg-open", System.IO.Path.Combine(Persistence.ConfigPersistence.BaseFolder, "logs"))
                { UseShellExecute = false });
            }
            catch
            {
            }
        }

        private void NavView_SelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
        {
            var tag = e.IsSettingsSelected
                ? "settings"
                : (e.SelectedItem as FANavigationViewItem)?.Tag as string;

            if (tag != null && _pages.TryGetValue(tag, out var page))
            {
                PageHost.Content = page;
            }
        }
    }
}
