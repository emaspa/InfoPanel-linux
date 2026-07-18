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
        private bool _minimizingToTray;

        public MainWindow()
        {
            InitializeComponent();

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Title = $"InfoPanel Linux - v{version?.ToString(3) ?? "0.0.2"} alpha";

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
                    && trayApp.Host.Settings.MinimizeToTray
                    && !_minimizingToTray)
                {
                    // Deferred, guarded, and hide-before-restore: setting WindowState
                    // inside its own change notification recursed with the WM
                    // re-reporting Minimized until the stack overflowed.
                    _minimizingToTray = true;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            Hide();
                            WindowState = WindowState.Normal;
                        }
                        finally
                        {
                            _minimizingToTray = false;
                        }
                    });
                }
            };

            _pages["dashboard"] = new DashboardPage();
            _pages["designer"] = new DesignerPage();
            _pages["devices"] = new DevicesPage();
            _pages["sensors"] = new SensorsPage();
            _pages["plugins"] = new PluginsPage();
            _pages["about"] = new AboutPage();
            _pages["settings"] = new SettingsPage();

            Loaded += (_, _) =>
            {
                // Dev/testing hook: INFOPANEL_START_PAGE=designer opens that page directly
                var startPage = Environment.GetEnvironmentVariable("INFOPANEL_START_PAGE");
                if (startPage is "settings" or "about")
                {
                    PageHost.Content = _pages[startPage]; // dev hook; footer items resolve lazily
                }
                else
                {
                    NavView.SelectedItem = NavView.MenuItems.OfType<FANavigationViewItem>()
                        .Concat(NavView.FooterMenuItems.OfType<FANavigationViewItem>())
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
                // Close-to-tray: hide instead of exiting; Exit lives in the tray menu.
                // Never intercept OS/session or application shutdown, or logout and
                // restart stall waiting for us (issue #2).
                if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
                {
                    return;
                }

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

            var item = NavView.MenuItems.OfType<FANavigationViewItem>()
                .Concat(NavView.FooterMenuItems.OfType<FANavigationViewItem>())
                .FirstOrDefault(i => (string?)i.Tag == tag);
            if (item != null)
            {
                NavView.SelectedItem = item;
            }
        }

        private void NavView_ItemInvoked(object? sender, FANavigationViewItemInvokedEventArgs e)
        {
            if ((e.InvokedItemContainer as FANavigationViewItem)?.Tag as string == "logs")
            {
                SetLogsDrawerVisible(!LogsDrawer.IsVisible);
            }
        }

        private void SetLogsDrawerVisible(bool visible)
        {
            LogsDrawer.IsVisible = visible;
            if (visible)
            {
                LogsList.ItemsSource ??= Utils.UiLogSink.Instance.Lines;
                ScrollLogsToEnd();
                Utils.UiLogSink.Instance.Lines.CollectionChanged -= Logs_CollectionChanged;
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
