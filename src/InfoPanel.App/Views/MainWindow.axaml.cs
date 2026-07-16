using Avalonia.Controls;
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

            _pages["dashboard"] = new DashboardPage();
            _pages["designer"] = new DesignerPage();
            _pages["devices"] = new DevicesPage();
            _pages["sensors"] = new SensorsPage();
            _pages["settings"] = new SettingsPage();

            Loaded += (_, _) =>
            {
                // Dev/testing hook: INFOPANEL_START_PAGE=designer opens that page directly
                var startPage = Environment.GetEnvironmentVariable("INFOPANEL_START_PAGE");
                NavView.SelectedItem = NavView.MenuItems.OfType<FANavigationViewItem>()
                    .FirstOrDefault(item => (string?)item.Tag == startPage)
                    ?? NavView.MenuItems.OfType<FANavigationViewItem>().First();

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
