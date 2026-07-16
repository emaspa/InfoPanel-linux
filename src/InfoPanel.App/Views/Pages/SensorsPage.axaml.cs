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

                RebuildPlugins();
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

        private void UpdateCount()
        {
            SensorCount.Text = $"{Services.HwmonMonitor.SENSORHASH.Count} hardware · {PluginMonitor.SENSORHASH.Count} plugin sensors, live";
        }

        private void RebuildPlugins()
        {
            PluginRows.Children.Clear();

            foreach (var descriptor in PluginMonitor.Instance.Plugins)
            {
                var border = new Border
                {
                    CornerRadius = new Avalonia.CornerRadius(8),
                    Padding = new Avalonia.Thickness(12, 8),
                    Background = ThemeBrush("CardBackgroundFillColorDefaultBrush"),
                    BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                    BorderThickness = new Avalonia.Thickness(1),
                };

                var panel = new StackPanel { Spacing = 2 };
                panel.Children.Add(new TextBlock
                {
                    Text = descriptor.PluginInfo?.Name ?? descriptor.FolderName ?? descriptor.FileName,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = $"{descriptor.PluginInfo?.Version ?? "?"} · {descriptor.PluginWrappers.Count} module(s)",
                    FontSize = 11,
                    Opacity = 0.6,
                });
                border.Child = panel;
                PluginRows.Children.Add(border);
            }
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

        private Avalonia.Media.IBrush? ThemeBrush(string key) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) ? value as Avalonia.Media.IBrush : null;
    }
}
