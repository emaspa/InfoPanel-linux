using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using InfoPanel.Models;

namespace InfoPanel.Designer
{
    /// <summary>
    /// Contextual property inspector for the designer. Sections are rebuilt per
    /// selection; every edit is routed through the session's UndoManager so it
    /// participates in undo/redo (consecutive edits of one property merge).
    /// </summary>
    public sealed class InspectorPanel : UserControl
    {
        private DesignerSession? _session;
        private readonly StackPanel _root;
        private bool _rebuilding;

        public InspectorPanel()
        {
            _root = new StackPanel { Spacing = 12, Margin = new Thickness(12) };
            Content = new ScrollViewer { Content = _root };
        }

        public DesignerSession? Session
        {
            get => _session;
            set
            {
                if (_session != null)
                {
                    _session.SelectionChanged -= OnSelectionChanged;
                }

                _session = value;
                if (_session != null)
                {
                    _session.SelectionChanged += OnSelectionChanged;
                }

                Rebuild();
            }
        }

        private void OnSelectionChanged(object? sender, EventArgs e) => Rebuild();

        /// <summary>Re-reads values into the existing editors (e.g. after canvas move/undo).</summary>
        public void Rebuild()
        {
            _rebuilding = true;
            try
            {
                _root.Children.Clear();

                var session = _session;
                if (session == null || session.Selection.Count == 0)
                {
                    _root.Children.Add(Header("Profile"));
                    if (session != null)
                    {
                        _root.Children.Add(Label($"{session.Profile.Name} — {session.Profile.Width}×{session.Profile.Height}"));
                        _root.Children.Add(Label($"{session.Items.Count} items"));
                    }

                    return;
                }

                if (session.Selection.Count > 1)
                {
                    _root.Children.Add(Header($"{session.Selection.Count} items selected"));
                    return;
                }

                var item = session.Selection[0];

                BuildCommonSection(session, item);
                BuildTransformSection(session, item);

                if (item is SensorDisplayItem sensor)
                {
                    BuildSensorSection(session, sensor);
                }

                if (item is TextDisplayItem text)
                {
                    BuildTextSection(session, text);
                }

                if (item is ChartDisplayItem chart)
                {
                    BuildChartSection(session, chart);
                }

                if (item is ImageDisplayItem imageItem)
                {
                    BuildImageSection(session, imageItem);
                }

                if (item is ShapeDisplayItem shape)
                {
                    BuildShapeSection(session, shape);
                }

                if (item is GaugeDisplayItem gauge)
                {
                    BuildGaugeSection(session, gauge);
                }
            }
            finally
            {
                _rebuilding = false;
            }
        }

        // ---- sections ----

        private void BuildCommonSection(DesignerSession session, DisplayItem item)
        {
            _root.Children.Add(Header(item.Kind));

            var name = new TextBox { Text = item.Name, Watermark = "Name" };
            name.LostFocus += (_, _) => Commit(session, item, nameof(item.Name), v => item.Name = v, item.Name, name.Text ?? "");
            _root.Children.Add(Field("Name", name));

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var hidden = new CheckBox { Content = "Hidden", IsChecked = item.Hidden };
            hidden.IsCheckedChanged += (_, _) => Commit(session, item, nameof(item.Hidden), v => item.Hidden = v, item.Hidden, hidden.IsChecked == true);
            var locked = new CheckBox { Content = "Locked", IsChecked = item.IsLocked };
            locked.IsCheckedChanged += (_, _) => Commit(session, item, nameof(item.IsLocked), v => item.IsLocked = v, item.IsLocked, locked.IsChecked == true);
            row.Children.Add(hidden);
            row.Children.Add(locked);
            _root.Children.Add(row);
        }

        private void BuildTransformSection(DesignerSession session, DisplayItem item)
        {
            _root.Children.Add(Header("Transform"));

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto"),
            };

            grid.Children.Add(WithGrid(Field("X", IntEditor(item.X, v => Commit(session, item, "X", x => item.X = x, item.X, v))), 0, 0));
            grid.Children.Add(WithGrid(Field("Y", IntEditor(item.Y, v => Commit(session, item, "Y", y => item.Y = y, item.Y, v))), 0, 1));

            if (ItemGeometry.IsResizable(item))
            {
                var (w, h) = ItemGeometry.GetSize(item);
                grid.Children.Add(WithGrid(Field("Width", IntEditor(w, v => CommitSize(session, item, width: v))), 1, 0));
                grid.Children.Add(WithGrid(Field("Height", IntEditor(h, v => CommitSize(session, item, height: v))), 1, 1));
            }

            _root.Children.Add(grid);
        }

        private void BuildSensorSection(DesignerSession session, SensorDisplayItem sensor)
        {
            _root.Children.Add(Header("Sensor"));

            var id = sensor.SensorType == Enums.SensorType.Plugin ? sensor.PluginSensorId : sensor.LibreSensorId;
            _root.Children.Add(Label(string.IsNullOrEmpty(id) ? "No sensor bound" : $"{sensor.SensorType}: {id}"));

            var bind = new Button { Content = "Bind selected sensor" };
            bind.Click += (_, _) => BindSensorRequested?.Invoke(this, sensor);
            _root.Children.Add(bind);

            var showUnit = new CheckBox { Content = "Show unit", IsChecked = sensor.ShowUnit };
            showUnit.IsCheckedChanged += (_, _) => Commit(session, sensor, nameof(sensor.ShowUnit), v => sensor.ShowUnit = v, sensor.ShowUnit, showUnit.IsChecked == true);
            _root.Children.Add(showUnit);

            var showName = new CheckBox { Content = "Show name", IsChecked = sensor.ShowName };
            showName.IsCheckedChanged += (_, _) => Commit(session, sensor, nameof(sensor.ShowName), v => sensor.ShowName = v, sensor.ShowName, showName.IsChecked == true);
            _root.Children.Add(showName);
        }

        private void BuildTextSection(DesignerSession session, TextDisplayItem text)
        {
            _root.Children.Add(Header("Text"));

            var fonts = SkiaSharp.SKFontManager.Default.FontFamilies.OrderBy(f => f).ToList();
            var fontPicker = new ComboBox { ItemsSource = fonts, SelectedItem = text.Font, MaxDropDownHeight = 400 };
            fontPicker.SelectionChanged += (_, _) =>
            {
                if (!_rebuilding && fontPicker.SelectedItem is string font && font != text.Font)
                {
                    Commit(session, text, nameof(text.Font), v => text.Font = v, text.Font, font);
                }
            };
            _root.Children.Add(Field("Font", fontPicker));

            _root.Children.Add(Field("Font size", IntEditor(text.FontSize, v => Commit(session, text, nameof(text.FontSize), s => text.FontSize = s, text.FontSize, v))));

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var bold = new CheckBox { Content = "Bold", IsChecked = text.Bold };
            bold.IsCheckedChanged += (_, _) => Commit(session, text, nameof(text.Bold), v => text.Bold = v, text.Bold, bold.IsChecked == true);
            var italic = new CheckBox { Content = "Italic", IsChecked = text.Italic };
            italic.IsCheckedChanged += (_, _) => Commit(session, text, nameof(text.Italic), v => text.Italic = v, text.Italic, italic.IsChecked == true);
            row.Children.Add(bold);
            row.Children.Add(italic);
            _root.Children.Add(row);

            _root.Children.Add(Field("Color", ColorEditor(session, text, nameof(text.Color), text.Color, v => text.Color = v)));
        }

        private void BuildChartSection(DesignerSession session, ChartDisplayItem chart)
        {
            _root.Children.Add(Header("Chart"));

            _root.Children.Add(Field("Min", IntEditor(chart.MinValue, v => Commit(session, chart, nameof(chart.MinValue), x => chart.MinValue = x, chart.MinValue, v))));
            _root.Children.Add(Field("Max", IntEditor(chart.MaxValue, v => Commit(session, chart, nameof(chart.MaxValue), x => chart.MaxValue = x, chart.MaxValue, v))));

            var auto = new CheckBox { Content = "Auto range", IsChecked = chart.AutoValue };
            auto.IsCheckedChanged += (_, _) => Commit(session, chart, nameof(chart.AutoValue), v => chart.AutoValue = v, chart.AutoValue, auto.IsChecked == true);
            _root.Children.Add(auto);

            _root.Children.Add(Field("Color", ColorEditor(session, chart, nameof(chart.Color), chart.Color, v => chart.Color = v)));
        }

        private void BuildImageSection(DesignerSession session, ImageDisplayItem image)
        {
            _root.Children.Add(Header("Image / Video"));
            _root.Children.Add(Label(image.CalculatedPath ?? "No source selected"));

            var pick = new Button { Content = "Choose file…" };
            pick.Click += async (_, _) =>
            {
                var storage = Avalonia.Controls.TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage == null) return;

                var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Choose image or video",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("Images & videos")
                        {
                            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.svg", "*.bmp", "*.mp4", "*.mkv", "*.webm", "*.avi", "*.mov"]
                        }
                    ]
                });

                if (files.Count == 0 || files[0].Path is not { IsFile: true } fileUri)
                {
                    return;
                }

                var localPath = fileUri.LocalPath;

                // copy into the profile's assets folder like v1 did, then reference relatively
                var fileName = System.IO.Path.GetFileName(localPath);
                var data = await System.IO.File.ReadAllBytesAsync(localPath);
                await InfoPanel.Utils.FileUtil.SaveAsset(session.Profile, fileName, data);

                var oldFile = image.FilePath;
                var oldRelative = image.RelativePath;
                session.Undo.Execute(new SetPropertyAction<(string?, bool)>(image, "Image source",
                    v =>
                    {
                        image.FilePath = v.Item1;
                        image.RelativePath = v.Item2;
                    },
                    (oldFile, oldRelative), (fileName, true)));

                Rebuild();
            };
            _root.Children.Add(pick);

            var url = new TextBox { Text = (image as HttpImageDisplayItem)?.HttpUrl ?? image.HttpUrl, Watermark = "…or http(s):// image URL" };
            url.LostFocus += (_, _) =>
            {
                var text = url.Text ?? "";
                if (!_rebuilding && text != (image.HttpUrl ?? "") && (text.Length == 0 || text.StartsWith("http")))
                {
                    Commit(session, image, nameof(image.HttpUrl), v => image.HttpUrl = v, image.HttpUrl ?? "", text);
                }
            };
            _root.Children.Add(url);

            _root.Children.Add(Field("Scale %", IntEditor(image.Scale, v => Commit(session, image, nameof(image.Scale), x => image.Scale = x, image.Scale, v))));
        }

        private void BuildShapeSection(DesignerSession session, ShapeDisplayItem shape)
        {
            _root.Children.Add(Header("Shape"));
            _root.Children.Add(Field("Fill", ColorEditor(session, shape, nameof(shape.FillColor), shape.FillColor, v => shape.FillColor = v)));
            _root.Children.Add(Field("Frame", ColorEditor(session, shape, nameof(shape.FrameColor), shape.FrameColor, v => shape.FrameColor = v)));
            _root.Children.Add(Field("Corner radius", IntEditor(shape.CornerRadius, v => Commit(session, shape, nameof(shape.CornerRadius), r => shape.CornerRadius = r, shape.CornerRadius, v))));
        }

        private void BuildGaugeSection(DesignerSession session, GaugeDisplayItem gauge)
        {
            _root.Children.Add(Header("Gauge"));
            _root.Children.Add(Label($"{gauge.Images.Count} frame images"));
            _root.Children.Add(Field("Scale %", IntEditor(gauge.Scale, v => Commit(session, gauge, nameof(gauge.Scale), s => gauge.Scale = s, gauge.Scale, v))));
        }

        /// <summary>Raised when the user clicks "Bind selected sensor" — the page supplies the sensor tree selection.</summary>
        public event EventHandler<SensorDisplayItem>? BindSensorRequested;

        // ---- editor helpers ----

        private void Commit<T>(DesignerSession session, DisplayItem item, string property, Action<T> setter, T oldValue, T newValue)
        {
            if (_rebuilding || EqualityComparer<T>.Default.Equals(oldValue, newValue))
            {
                return;
            }

            session.Undo.Execute(new SetPropertyAction<T>(item, property, setter, oldValue, newValue));
        }

        private void CommitSize(DesignerSession session, DisplayItem item, int? width = null, int? height = null)
        {
            if (_rebuilding) return;

            var (w, h) = ItemGeometry.GetSize(item);
            var newW = width ?? w;
            var newH = height ?? h;
            if (newW == w && newH == h) return;

            session.Undo.Execute(new SetPropertyAction<(int, int)>(item, "Size",
                v => ItemGeometry.SetSize(item, v.Item1, v.Item2), (w, h), (newW, newH)));
        }

        private NumericUpDown IntEditor(double value, Action<int> commit)
        {
            var editor = new NumericUpDown
            {
                Value = (decimal)value,
                Increment = 1,
                FormatString = "0",
                MinWidth = 90
            };
            editor.ValueChanged += (_, e) =>
            {
                if (!_rebuilding && e.NewValue.HasValue)
                {
                    commit((int)e.NewValue.Value);
                }
            };
            return editor;
        }

        private Control ColorEditor(DesignerSession session, DisplayItem item, string property, string current, Action<string> setter)
        {
            var box = new TextBox { Text = current, Watermark = "#RRGGBB" };
            box.LostFocus += (_, _) =>
            {
                var text = box.Text ?? "";
                if (!_rebuilding && text != current && (text.StartsWith('#') || text.Length == 0))
                {
                    Commit(session, item, property, setter, current, text);
                }
            };
            return box;
        }

        private static TextBlock Header(string text) => new()
        {
            Text = text,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 4, 0, 0)
        };

        private static TextBlock Label(string text) => new()
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        private static StackPanel Field(string label, Control editor)
        {
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 });
            panel.Children.Add(editor);
            return panel;
        }

        private static Control WithGrid(Control control, int row, int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            control.Margin = new Thickness(0, 0, 8, 8);
            return control;
        }
    }
}
