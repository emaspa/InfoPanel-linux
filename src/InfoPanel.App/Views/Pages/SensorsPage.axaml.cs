using Avalonia.Controls;
using Avalonia.Threading;
using InfoPanel.Services;
using InfoPanel.Sensors;
using InfoPanel.Monitors;
using InfoPanel.ViewModels;

namespace InfoPanel.Views.Pages
{
    public partial class SensorsPage : UserControl
    {
        private readonly SensorTreeViewModel _tree = new();
        private bool _catalogSubscribed;
        private DispatcherTimer? _timer;

        public SensorsPage()
        {
            InitializeComponent();
            Tree.SelectionChanged += (_, _) => _tree.SelectedItem = Tree.SelectedItem as SensorTreeItem;

            // Sensor-browsing UI: poll all sensors while this page is visible.
            bool sensorViewer = false;
            Loaded += (_, _) => { if (!sensorViewer) { sensorViewer = true; InfoPanel.Models.SensorDemand.AddUiViewer(); } };
            Unloaded += (_, _) => { if (sensorViewer) { sensorViewer = false; InfoPanel.Models.SensorDemand.RemoveUiViewer(); } };

            Loaded += (_, _) =>
            {
                if (!_catalogSubscribed)
                {
                    HwmonMonitor.Instance.CatalogChanged += CatalogChanged;
                    _catalogSubscribed = true;
                }
                RebuildSensorTree();

                UpdateCount();

                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _timer.Tick += (_, _) =>
                {
                    _tree.RefreshValues();
                    UpdateCount();
                };
                _timer.Start();
            };

            Unloaded += (_, _) =>
            {
                HwmonMonitor.Instance.CatalogChanged -= CatalogChanged;
                _catalogSubscribed = false;
                _timer?.Stop();
                _timer = null;
            };
        }

        private void CatalogChanged(object? sender, SensorCatalogSnapshot catalog) => Dispatcher.UIThread.Post(() =>
        {
            if (!_catalogSubscribed) return;
            RebuildSensorTree();
            UpdateCount();
        });

        private void RebuildSensorTree()
        {
            _tree.Rebuild();
            ApplySensorFilter();
        }

        private void Tree_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            Utils.TreeViewHelpers.ToggleCategoryOnTap(e);
        }

        private void UpdateCount()
        {
            SensorCount.Text = $"{HwmonMonitor.GetOrderedList().Count} hardware discovered ({Services.HwmonMonitor.SENSORHASH.Count} with readings) · {PluginMonitor.SENSORHASH.Count} plugin sensors, live";
        }

        private void Search_TextChanged(object? sender, TextChangedEventArgs e) => ApplySensorFilter();

        private void ApplySensorFilter()
        {
            var selected = _tree.SelectedItem;
            var query = SearchBox.Text?.Trim() ?? "";
            if (query.Length == 0)
            {
                Tree.ItemsSource = _tree.Roots;
                Tree.SelectedItem = selected;
                return;
            }

            var matches = new List<SensorTreeItem>();
            void Walk(SensorTreeItem node)
            {
                if (node.SensorId != null && node.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(node);
                }

                foreach (var child in node.Children)
                {
                    Walk(child);
                }
            }

            foreach (var root in _tree.Roots)
            {
                Walk(root);
            }

            Tree.ItemsSource = matches;
            Tree.SelectedItem = selected != null && matches.Contains(selected) ? selected : null;
        }

    }
}
