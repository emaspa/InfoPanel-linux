using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InfoPanel.Designer;
using InfoPanel.Models;
using InfoPanel.ViewModels;
using SkiaSharp;

namespace InfoPanel.Views.Pages
{
    public partial class DesignerPage : UserControl
    {
        private DesignerSession? _session;
        private readonly SensorTreeViewModel _sensorTree = new();
        private DispatcherTimer? _sensorTimer;
        private bool _syncingLayerSelection;

        public DesignerPage()
        {
            InitializeComponent();

            Loaded += (_, _) =>
            {
                if (Avalonia.Application.Current is App app && ProfilePicker.ItemsSource == null)
                {
                    ProfilePicker.ItemsSource = app.Host.Profiles;
                    ProfilePicker.SelectedIndex = app.Host.Profiles.Count > 0 ? 0 : -1;
                }

                if (_sensorTree.Roots.Count == 0)
                {
                    _sensorTree.Rebuild();
                    SensorTree.ItemsSource = _sensorTree.Roots;
                }

                _sensorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _sensorTimer.Tick += (_, _) =>
                {
                    _sensorTree.RefreshValues();
                    Inspector.Rebuild();
                };
                _sensorTimer.Start();

                Inspector.BindSensorRequested += Inspector_BindSensorRequested;
            };

            Unloaded += (_, _) =>
            {
                _sensorTimer?.Stop();
                _sensorTimer = null;
            };
        }

        private void ProfilePicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ProfilePicker.SelectedItem is not Profile profile)
            {
                return;
            }

            if (_session != null)
            {
                _session.SaveNow();
                _session.Undo.StateChanged -= Undo_StateChanged;
                _session.SelectionChanged -= Session_SelectionChanged;
            }

            _session = new DesignerSession(profile);
            _session.Undo.StateChanged += Undo_StateChanged;
            _session.SelectionChanged += Session_SelectionChanged;
            Canvas.Session = _session;
            Inspector.Session = _session;
            LayersTree.ItemsSource = _session.Items;
            Canvas.ZoomChanged += (_, _) => ZoomLabel.Text = $"{Canvas.Zoom * 100:0}%";
            ZoomLabel.Text = $"{Canvas.Zoom * 100:0}%";
        }

        private void Undo_StateChanged(object? sender, EventArgs e)
        {
            UndoButton.IsEnabled = _session?.Undo.CanUndo == true;
            RedoButton.IsEnabled = _session?.Undo.CanRedo == true;
            Inspector.Rebuild();
        }

        private void Session_SelectionChanged(object? sender, EventArgs e)
        {
            var count = _session?.Selection.Count ?? 0;
            SelectionLabel.Text = count switch
            {
                0 => "",
                1 => _session!.Selection[0].Name,
                _ => $"{count} items selected"
            };

            // reflect canvas selection into the layers tree
            _syncingLayerSelection = true;
            try
            {
                LayersTree.SelectedItem = count == 1 ? _session!.Selection[0] : null;
            }
            finally
            {
                _syncingLayerSelection = false;
            }
        }

        private void LayersTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_syncingLayerSelection || _session == null)
            {
                return;
            }

            if (LayersTree.SelectedItem is DisplayItem item)
            {
                _session.Select(item);
                Canvas.InvalidateVisual();
            }
        }

        // ---- sensors tab ----

        private void SensorTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            _sensorTree.SelectedItem = SensorTree.SelectedItem as SensorTreeItem;
        }

        private void SensorTree_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (_session == null || SensorTree.SelectedItem is not SensorTreeItem { IsSensor: true } leaf)
            {
                return;
            }

            _session.AddItem(CreateSensorItem(leaf, _session.Profile));
            Canvas.InvalidateVisual();
        }

        private void Inspector_BindSensorRequested(object? sender, SensorDisplayItem target)
        {
            if (_session == null || _sensorTree.SelectedItem is not { IsSensor: true } leaf)
            {
                return;
            }

            var oldType = target.SensorType;
            var oldLibre = target.LibreSensorId;
            var oldPlugin = target.PluginSensorId;

            _session.Undo.Execute(new SetPropertyAction<(Enums.SensorType, string, string)>(
                target, "Sensor binding",
                v =>
                {
                    target.SensorType = v.Item1;
                    target.LibreSensorId = v.Item2;
                    target.PluginSensorId = v.Item3;
                },
                (oldType, oldLibre, oldPlugin),
                leaf.SensorType == Enums.SensorType.Plugin
                    ? (Enums.SensorType.Plugin, "", leaf.SensorId!)
                    : (Enums.SensorType.Hwmon, leaf.SensorId!, "")));

            Inspector.Rebuild();
        }

        private static SensorDisplayItem CreateSensorItem(SensorTreeItem leaf, Profile profile)
        {
            var item = new SensorDisplayItem
            {
                Name = leaf.Name,
                SensorName = leaf.Name,
                X = profile.Width / 3,
                Y = profile.Height / 3,
                Font = profile.Font,
                FontSize = profile.FontSize,
                Color = profile.Color,
            };

            if (leaf.SensorType == Enums.SensorType.Plugin)
            {
                item.SensorType = Enums.SensorType.Plugin;
                item.PluginSensorId = leaf.SensorId!;
            }
            else
            {
                item.SensorType = Enums.SensorType.Hwmon;
                item.LibreSensorId = leaf.SensorId!;
            }

            return item;
        }

        private void SensorSearch_TextChanged(object? sender, TextChangedEventArgs e)
        {
            var query = SensorSearch.Text?.Trim() ?? "";
            if (query.Length == 0)
            {
                SensorTree.ItemsSource = _sensorTree.Roots;
                return;
            }

            // flat filtered list of matching leaves
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

            foreach (var root in _sensorTree.Roots)
            {
                Walk(root);
            }

            SensorTree.ItemsSource = matches;
        }

        // ---- toolbar ----

        private void AddText_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            _session.AddItem(new TextDisplayItem
            {
                Name = "New text",
                X = _session.Profile.Width / 3,
                Y = _session.Profile.Height / 3,
                Font = _session.Profile.Font,
                FontSize = _session.Profile.FontSize,
                Color = _session.Profile.Color,
            });
            Canvas.InvalidateVisual();
        }

        private void AddSensor_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            if (_sensorTree.SelectedItem is { IsSensor: true } leaf)
            {
                _session.AddItem(CreateSensorItem(leaf, _session.Profile));
            }
            else
            {
                _session.AddItem(new SensorDisplayItem
                {
                    Name = "New sensor",
                    X = _session.Profile.Width / 3,
                    Y = _session.Profile.Height / 3,
                    Font = _session.Profile.Font,
                    FontSize = _session.Profile.FontSize,
                    Color = _session.Profile.Color,
                });
            }

            Canvas.InvalidateVisual();
        }

        private void AddShape_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            _session.AddItem(new ShapeDisplayItem
            {
                Name = "New shape",
                X = _session.Profile.Width / 3,
                Y = _session.Profile.Height / 3,
                Width = 120,
                Height = 80,
            });
            Canvas.InvalidateVisual();
        }

        private void Undo_Click(object? sender, RoutedEventArgs e)
        {
            _session?.Undo.Undo();
            Canvas.InvalidateVisual();
        }

        private void Redo_Click(object? sender, RoutedEventArgs e)
        {
            _session?.Undo.Redo();
            Canvas.InvalidateVisual();
        }

        private void Snap_Click(object? sender, RoutedEventArgs e)
        {
            Canvas.SnapToGrid = SnapToggle.IsChecked == true;
        }

        private void Fit_Click(object? sender, RoutedEventArgs e)
        {
            Canvas.ZoomToFit();
        }

        private void Save_Click(object? sender, RoutedEventArgs e)
        {
            _session?.SaveNow();
        }
    }
}
