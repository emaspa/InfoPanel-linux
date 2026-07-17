using Avalonia.Controls;
using Avalonia.Threading;
using InfoPanel.Monitors;
using InfoPanel.ViewModels;

namespace InfoPanel.Views.Pages
{
    public partial class SensorsPage : UserControl
    {
        private readonly SensorTreeViewModel _tree = new();
        private DispatcherTimer? _timer;

        public SensorsPage()
        {
            InitializeComponent();

            Loaded += (_, _) =>
            {
                if (_tree.Roots.Count == 0)
                {
                    _tree.Rebuild();
                    Tree.ItemsSource = _tree.Roots;
                }

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
                _timer?.Stop();
                _timer = null;
            };
        }

        private void Tree_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            Utils.TreeViewHelpers.ToggleCategoryOnTap(e);
        }

        private void UpdateCount()
        {
            SensorCount.Text = $"{Services.HwmonMonitor.SENSORHASH.Count} hardware · {PluginMonitor.SENSORHASH.Count} plugin sensors, live";
        }

        private void Search_TextChanged(object? sender, TextChangedEventArgs e)
        {
            var query = SearchBox.Text?.Trim() ?? "";
            if (query.Length == 0)
            {
                Tree.ItemsSource = _tree.Roots;
                return;
            }

            var matches = new List<SensorTreeItem>();
            void Walk(SensorTreeItem node)
            {
                if (node.IsSensor && node.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
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
        }

    }
}
