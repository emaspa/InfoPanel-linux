using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InfoPanel.Services;
using InfoPanel.Sensors;
using InfoPanel.Designer;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.ViewModels;
using System;

namespace InfoPanel.Views.Pages
{
    public partial class DesignerPage : UserControl
    {
        private DesignerSession? _session;
        private readonly SensorTreeViewModel _sensorTree = new();
        private bool _catalogSubscribed;
        private DispatcherTimer? _sensorTimer;
        private bool _syncingLayerSelection;
        private bool _syncingActiveToggle;

        public DesignerPage()
        {
            InitializeComponent();

            // Sensor-browsing UI: poll all sensors while this page is visible.
            bool sensorViewer = false;
            Loaded += (_, _) => { if (!sensorViewer) { sensorViewer = true; InfoPanel.Models.SensorDemand.AddUiViewer(); } };
            Unloaded += (_, _) => { if (sensorViewer) { sensorViewer = false; InfoPanel.Models.SensorDemand.RemoveUiViewer(); } };

            Canvas.ViewportChanged += (_, _) => UpdateScrollBars();
            CanvasHScroll.Scroll += (_, _) =>
            {
                if (!_syncingScroll)
                {
                    Canvas.Pan = new SkiaSharp.SKPoint((float)(ScrollMargin - CanvasHScroll.Value), Canvas.Pan.Y);
                }
            };
            CanvasVScroll.Scroll += (_, _) =>
            {
                if (!_syncingScroll)
                {
                    Canvas.Pan = new SkiaSharp.SKPoint(Canvas.Pan.X, (float)(ScrollMargin - CanvasVScroll.Value));
                }
            };

            Loaded += (_, _) =>
            {
                if (Avalonia.Application.Current is App app)
                {
                    if (ProfilePicker.ItemsSource == null)
                    {
                        ProfilePicker.ItemsSource = app.Host.Profiles;
                        ProfilePicker.SelectedIndex = app.Host.Profiles.Count > 0 ? 0 : -1;
                    }

                    Canvas.GridSpacing = (int)app.Host.Settings.GridLinesSpacing;
                    Canvas.SnapToGrid = SnapToggle.IsChecked == true;
                }

                if (!_catalogSubscribed)
                {
                    HwmonMonitor.Instance.CatalogChanged += CatalogChanged;
                    _catalogSubscribed = true;
                }
                RebuildSensorTree();

                _sensorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _sensorTimer.Tick += (_, _) => _sensorTree.RefreshValues();
                _sensorTimer.Start();

                Inspector.BindSensorRequested += Inspector_BindSensorRequested;
            };

            Unloaded += (_, _) =>
            {
                HwmonMonitor.Instance.CatalogChanged -= CatalogChanged;
                _catalogSubscribed = false;
                _sensorTimer?.Stop();
                _sensorTimer = null;
                Inspector.BindSensorRequested -= Inspector_BindSensorRequested;
            };
        }

        // ================= canvas scrollbars =================

        private const double ScrollMargin = 48;
        private bool _syncingScroll;

        private void CatalogChanged(object? sender, SensorCatalogSnapshot catalog) => Dispatcher.UIThread.Post(() =>
        {
            if (!_catalogSubscribed) return;
            RebuildSensorTree();
            Inspector.Rebuild();
        });

        private void RebuildSensorTree()
        {
            _sensorTree.Rebuild();
            ApplySensorFilter();
        }

        private void UpdateScrollBars()
        {
            if (Canvas.Session is not { } session || Canvas.Bounds.Width <= 0)
            {
                return;
            }

            _syncingScroll = true;

            var contentWidth = session.Profile.Width * Canvas.Zoom + ScrollMargin * 2;
            var contentHeight = session.Profile.Height * Canvas.Zoom + ScrollMargin * 2;
            var offsetX = ScrollMargin - Canvas.Pan.X;
            var offsetY = ScrollMargin - Canvas.Pan.Y;

            CanvasHScroll.ViewportSize = Canvas.Bounds.Width;
            CanvasHScroll.Minimum = Math.Min(0, offsetX);
            CanvasHScroll.Maximum = Math.Max(contentWidth - Canvas.Bounds.Width, offsetX);
            CanvasHScroll.Value = offsetX;
            CanvasHScroll.IsVisible = CanvasHScroll.Maximum - CanvasHScroll.Minimum > 0.5;

            CanvasVScroll.ViewportSize = Canvas.Bounds.Height;
            CanvasVScroll.Minimum = Math.Min(0, offsetY);
            CanvasVScroll.Maximum = Math.Max(contentHeight - Canvas.Bounds.Height, offsetY);
            CanvasVScroll.Value = offsetY;
            CanvasVScroll.IsVisible = CanvasVScroll.Maximum - CanvasVScroll.Minimum > 0.5;

            _syncingScroll = false;
        }

        // ================= profile =================

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
            InfoPanel.Drawing.RenderContext.IsSelectedProfile = p => p.Guid == profile.Guid;
            _session.Undo.StateChanged += Undo_StateChanged;
            _session.SelectionChanged += Session_SelectionChanged;
            Canvas.Session = _session;
            Inspector.Session = _session;
            RefreshLayerList();
            Canvas.ZoomChanged += (_, _) => ZoomLabel.Text = $"{Canvas.Zoom * 100:0}%";
            ZoomLabel.Text = $"{Canvas.Zoom * 100:0}%";

            _syncingActiveToggle = true;
            ActiveToggle.IsChecked = profile.Active;
            _syncingActiveToggle = false;
        }

        private void ActiveToggle_Changed(object? sender, RoutedEventArgs e)
        {
            if (_syncingActiveToggle || _session == null || Avalonia.Application.Current is not App app)
            {
                return;
            }

            var profile = _session.Profile;
            var active = ActiveToggle.IsChecked == true;
            if (profile.Active == active) return;

            profile.Active = active;
            app.Host.SaveProfiles();

            if (active)
            {
                DisplayWindowManager.Instance.ShowDisplayWindow(profile);
            }
            else
            {
                DisplayWindowManager.Instance.CloseDisplayWindow(profile.Guid);
            }
        }

        private void Undo_StateChanged(object? sender, EventArgs e)
        {
            UndoButton.IsEnabled = _session?.Undo.CanUndo == true;
            RedoButton.IsEnabled = _session?.Undo.CanRedo == true;

            // Don't tear down the inspector while one of its own editors is mid-commit:
            // rebuilding closed the color picker flyout on the first slider tick (#6)
            // and broke slider drags. The editors keep their own displayed values in
            // sync; canvas edits and undo/redo still rebuild.
            if (!Inspector.IsCommitting)
            {
                Inspector.Rebuild();
            }

            RefreshLayerList();
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

        // ================= layers panel =================

        private void RefreshLayerList()
        {
            if (_session == null) return;

            // Front-most item (drawn last) at the top of the list, like every design
            // tool - so the up/down/top/bottom buttons match what the eye expects (#8).
            var query = LayerSearch.Text?.Trim() ?? "";
            var items = query.Length == 0
                ? _session.Items
                : _session.Items.Where(i =>
                    i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || i.Kind.Contains(query, StringComparison.OrdinalIgnoreCase));
            LayersTree.ItemsSource = items.Reverse().ToList();
        }

        private void LayerSearch_TextChanged(object? sender, TextChangedEventArgs e) => RefreshLayerList();

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

        private void LayerDelete_Click(object? sender, RoutedEventArgs e)
        {
            _session?.DeleteSelection();
            AfterListEdit();
        }

        private void LayerDuplicate_Click(object? sender, RoutedEventArgs e)
        {
            _session?.Duplicate();
            AfterListEdit();
        }

        private void LayerUp_Click(object? sender, RoutedEventArgs e)
        {
            _session?.PushBy(1);
            AfterListEdit();
        }

        private void LayerDown_Click(object? sender, RoutedEventArgs e)
        {
            _session?.PushBy(-1);
            AfterListEdit();
        }

        private void LayerTop_Click(object? sender, RoutedEventArgs e)
        {
            _session?.PushToEnd(front: true);
            AfterListEdit();
        }

        private void LayerBottom_Click(object? sender, RoutedEventArgs e)
        {
            _session?.PushToEnd(front: false);
            AfterListEdit();
        }

        private void AfterListEdit()
        {
            RefreshLayerList();
            Canvas.InvalidateVisual();
        }

        // ================= sensors tab =================

        private void RefreshSensors_Click(object? sender, RoutedEventArgs e)
        {
            HwmonMonitor.Instance.RequestRescan();
            RebuildSensorTree();
        }

        private void SensorTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            _sensorTree.SelectedItem = SensorTree.SelectedItem as SensorTreeItem;
        }

        private void SensorTree_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            AddSensorAsText_Click(sender, new RoutedEventArgs());
        }

        private void SensorTree_Tapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            InfoPanel.Utils.TreeViewHelpers.ToggleCategoryOnTap(e);
        }

        private void SensorSearch_TextChanged(object? sender, TextChangedEventArgs e) => ApplySensorFilter();

        private void ApplySensorFilter()
        {
            var selected = _sensorTree.SelectedItem;
            var query = SensorSearch.Text?.Trim() ?? "";
            if (query.Length == 0)
            {
                SensorTree.ItemsSource = _sensorTree.Roots;
                SensorTree.SelectedItem = selected;
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

            foreach (var root in _sensorTree.Roots)
            {
                Walk(root);
            }

            SensorTree.ItemsSource = matches;
            SensorTree.SelectedItem = selected != null && matches.Contains(selected) ? selected : null;
        }

        private SensorTreeItem? SelectedSensorLeaf =>
            _sensorTree.SelectedItem is { IsSensor: true } leaf ? leaf : null;

        private void AddBoundItem(DisplayItem item)
        {
            if (_session == null) return;
            _session.AddItem(item);
            AfterListEdit();
        }

        private void AddSensorAsText_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null || SelectedSensorLeaf is not { } leaf) return;
            AddBoundItem(DesignerSensorBinding.CreateSensorItem(leaf, _session.Profile));
        }

        private void AddSensorAsBar_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            var item = new BarDisplayItem { Name = "New bar", Width = 200, Height = 30, X = _session.Profile.Width / 3, Y = _session.Profile.Height / 3 };
            if (SelectedSensorLeaf is { } leaf)
            {
                item.Name = leaf.Name;
                SensorBinding.Apply(item, DesignerSensorBinding.FromLeaf(leaf));
            }
            AddBoundItem(item);
        }

        private void AddSensorAsGraph_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            var item = new GraphDisplayItem { Name = "New graph", Width = 240, Height = 100, X = _session.Profile.Width / 3, Y = _session.Profile.Height / 3 };
            if (SelectedSensorLeaf is { } leaf)
            {
                item.Name = leaf.Name;
                SensorBinding.Apply(item, DesignerSensorBinding.FromLeaf(leaf));
            }
            AddBoundItem(item);
        }

        private void AddSensorAsDonut_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            var item = new DonutDisplayItem { Name = "New donut", Radius = 50, Thickness = 12, X = _session.Profile.Width / 3, Y = _session.Profile.Height / 3 };
            if (SelectedSensorLeaf is { } leaf)
            {
                item.Name = leaf.Name;
                SensorBinding.Apply(item, DesignerSensorBinding.FromLeaf(leaf));
            }
            AddBoundItem(item);
        }

        private void AddSensorAsGauge_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            var item = new GaugeDisplayItem { Name = "New gauge", X = _session.Profile.Width / 3, Y = _session.Profile.Height / 3 };
            if (SelectedSensorLeaf is { } leaf)
            {
                item.Name = leaf.Name;
                SensorBinding.Apply(item, DesignerSensorBinding.FromLeaf(leaf));
            }
            AddBoundItem(item);
        }

        private void AddSensorAsImage_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            // Plugin text sensors carrying an image URL or a live plugin-image:// buffer
            // (e.g. AudioSpectrum's Images entries) become sensor-driven image items.
            if (SelectedSensorLeaf is { SensorType: Enums.SensorType.Plugin, SensorId: { } textSensorId } textLeaf
                && SensorReader.ReadPluginSensor(textSensorId) is { ValueText: { } valueText }
                && (valueText.StartsWith(PluginImageSource.Scheme, StringComparison.Ordinal)
                    || valueText.IsUrl()))
            {
                var httpItem = new HttpImageDisplayItem(textLeaf.Name, _session.Profile)
                {
                    X = _session.Profile.Width / 3,
                    Y = _session.Profile.Height / 3,
                    Width = 100,
                    Height = 100,
                };

                SensorBinding.Apply(httpItem, DesignerSensorBinding.FromLeaf(textLeaf));

                // Live plugin buffers report their true size; use it as the initial box
                if (PluginImageSource.TryParseUri(valueText, out var pluginId, out var imageId)
                    && PluginImageSource.Resolve(pluginId, imageId) is { } writer)
                {
                    httpItem.Width = writer.Width;
                    httpItem.Height = writer.Height;
                }

                AddBoundItem(httpItem);
                return;
            }

            var item = new SensorImageDisplayItem { Name = "New sensor image", X = _session.Profile.Width / 3, Y = _session.Profile.Height / 3 };
            if (SelectedSensorLeaf is { } leaf)
            {
                item.Name = leaf.Name;
                SensorBinding.Apply(item, DesignerSensorBinding.FromLeaf(leaf));
            }
            AddBoundItem(item);
        }

        private void ReplaceSensor_Click(object? sender, RoutedEventArgs e)
        {
            if (_session is { Selection.Count: 1 })
            {
                Inspector_BindSensorRequested(this, _session.Selection[0]);
            }
        }

        private void Inspector_BindSensorRequested(object? sender, DisplayItem target)
        {
            if (_session == null || SelectedSensorLeaf is not { } leaf)
            {
                return;
            }

            DesignerSensorBinding.Replace(_session, target, leaf);

            Inspector.Rebuild();
            Canvas.InvalidateVisual();
        }

        // ================= add toolbar =================

        private void AddItemAtCenter(DisplayItem item)
        {
            if (_session == null) return;
            _session.AddItem(item);
            AfterListEdit();
        }

        private void AddText_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            AddItemAtCenter(new TextDisplayItem
            {
                Name = "New text",
                X = _session.Profile.Width / 3,
                Y = _session.Profile.Height / 3,
                Font = _session.Profile.Font,
                FontSize = _session.Profile.FontSize,
                Color = _session.Profile.Color,
            });
        }

        private void AddSensor_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            if (SelectedSensorLeaf is { } leaf)
            {
                AddItemAtCenter(DesignerSensorBinding.CreateSensorItem(leaf, _session.Profile));
            }
            else
            {
                AddItemAtCenter(new SensorDisplayItem
                {
                    Name = "New sensor",
                    X = _session.Profile.Width / 3,
                    Y = _session.Profile.Height / 3,
                    Font = _session.Profile.Font,
                    FontSize = _session.Profile.FontSize,
                    Color = _session.Profile.Color,
                });
            }
        }

        private void AddClock_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            AddItemAtCenter(new ClockDisplayItem
            {
                Name = "Clock",
                X = _session.Profile.Width / 3,
                Y = _session.Profile.Height / 3,
                Font = _session.Profile.Font,
                FontSize = _session.Profile.FontSize,
                Color = _session.Profile.Color,
            });
        }

        private void AddCalendar_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            AddItemAtCenter(new CalendarDisplayItem
            {
                Name = "Calendar",
                X = _session.Profile.Width / 3,
                Y = _session.Profile.Height / 3,
                Font = _session.Profile.Font,
                FontSize = _session.Profile.FontSize,
                Color = _session.Profile.Color,
            });
        }

        private void AddImage_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            AddItemAtCenter(new ImageDisplayItem
            {
                Name = "New image",
                X = _session.Profile.Width / 3,
                Y = _session.Profile.Height / 3,
            });
        }

        private void AddShape_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            AddItemAtCenter(new ShapeDisplayItem
            {
                Name = "New shape",
                X = _session.Profile.Width / 3,
                Y = _session.Profile.Height / 3,
                Width = 120,
                Height = 80,
            });
        }

        private void AddGroup_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            AddItemAtCenter(new GroupDisplayItem { Name = "New group" });
        }

        // ================= misc toolbar =================

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

        private void Reload_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            _session.ClearSelection();
            Stores.DisplayItemStore.Instance.Reload(_session.Profile);
            _session.Undo.Clear();
            AfterListEdit();
        }

        private async void Restore_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var profile = _session.Profile;
            var backupTime = Persistence.ConfigPersistence.GetDisplayItemsBackupTime(profile);

            var dialog = new FluentAvalonia.UI.Controls.FAContentDialog
            {
                Title = "Restore backup",
                Content = backupTime is { } time
                    ? $"Roll back \"{profile.Name}\" to its layout from {time:g}? The backup is taken automatically before the first change of each session. The current layout will be kept as the new backup."
                    : $"No backup exists yet for \"{profile.Name}\". One is taken automatically before the first change of each session.",
                PrimaryButtonText = backupTime != null ? "Restore" : null,
                CloseButtonText = backupTime != null ? "Cancel" : "OK",
                DefaultButton = FluentAvalonia.UI.Controls.FAContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != FluentAvalonia.UI.Controls.FAContentDialogResult.Primary || backupTime == null)
            {
                return;
            }

            _session.ClearSelection();
            if (Stores.DisplayItemStore.Instance.RestoreFromBackup(profile))
            {
                _session.Undo.Clear();
                AfterListEdit();
            }
        }
    }
}
