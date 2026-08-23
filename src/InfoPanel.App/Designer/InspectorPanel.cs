using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InfoPanel.Models;
using InfoPanel.Utils;

namespace InfoPanel.Designer
{
    /// <summary>
    /// Contextual property inspector for the designer, covering the full v1.4 editor
    /// surface for every item type. Sections are rebuilt per selection; every edit is
    /// routed through the session's UndoManager (consecutive edits of one property merge).
    /// </summary>
    public sealed class InspectorPanel : UserControl
    {
        private DesignerSession? _session;
        private readonly StackPanel _root;
        private bool _rebuilding;

        // gauge frame previews: decoded once per file, refreshed on rebuild
        private readonly Dictionary<string, (Avalonia.Media.Imaging.Bitmap? Bitmap, int Width, int Height)> _gaugeFrameCache = [];
        private Avalonia.Threading.DispatcherTimer? _gaugePreviewTimer;

        public InspectorPanel()
        {
            _root = new StackPanel { Spacing = 10, Margin = new Thickness(12) };
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

        /// <summary>Raised when the user clicks "Bind selected sensor" - the page supplies the sensor tree selection.</summary>
        public event EventHandler<DisplayItem>? BindSensorRequested;

        private void OnSelectionChanged(object? sender, EventArgs e) => Rebuild();

        // subscriptions made by the profile section (e.g. display dropdown following
        // the overlay being dragged to another screen), detached on every rebuild
        private readonly List<Action> _profileSectionCleanups = [];

        public void Rebuild()
        {
            _rebuilding = true;
            try
            {
                _gaugePreviewTimer?.Stop();
                _gaugePreviewTimer = null;

                foreach (var cleanup in _profileSectionCleanups)
                {
                    cleanup();
                }
                _profileSectionCleanups.Clear();

                _root.Children.Clear();
                _gaugeFrameCache.Clear();

                var session = _session;
                if (session == null || session.Selection.Count == 0)
                {
                    _root.Children.Add(Header("Profile"));
                    if (session != null)
                    {
                        _root.Children.Add(Label($"{session.Profile.Name} - {session.Profile.Width}×{session.Profile.Height}"));
                        _root.Children.Add(Label($"{session.Items.Count} items"));
                        _root.Children.Add(Label("Select an item to edit its properties."));

                        var profile = session.Profile;
                        var triggers = new TextBox
                        {
                            Text = profile.TriggerProcessNames ?? "",
                            Watermark = "e.g. Cyberpunk2077.exe, steam",
                        };
                        triggers.LostFocus += (_, _) =>
                        {
                            var value = string.IsNullOrWhiteSpace(triggers.Text) ? null : triggers.Text.Trim();
                            if (value == profile.TriggerProcessNames) return;
                            profile.TriggerProcessNames = value;
                            (Avalonia.Application.Current as App)?.Host.SaveProfiles();
                        };
                        _root.Children.Add(Field("Trigger programs", triggers));
                        _root.Children.Add(Label("Comma-separated process names. When program-specific profiles are enabled in Settings, this overlay only shows while one of these apps is in the foreground."));

                        BuildDisplayAssignment(profile);
                    }

                    return;
                }

                if (session.Selection.Count > 1)
                {
                    _root.Children.Add(Header($"{session.Selection.Count} items selected"));
                    _root.Children.Add(Label("Drag to move together; geometry edits apply per item."));
                    return;
                }

                var item = session.Selection[0];

                BuildCommonSection(session, item);

                switch (item)
                {
                    case GaugeDisplayItem gauge:
                        BuildGaugeSection(session, gauge);
                        break;

                    case ClockDisplayItem clock:
                        BuildTextSection(session, clock);
                        BuildDateTimeSection(session, clock, format => clock.Format = format, () => clock.Format, includeTimeTokens: true);
                        break;

                    case CalendarDisplayItem calendar:
                        BuildTextSection(session, calendar);
                        BuildDateTimeSection(session, calendar, format => calendar.Format = format, () => calendar.Format, includeTimeTokens: false);
                        break;

                    case TableSensorDisplayItem table:
                        BuildTextSection(session, table);
                        BuildTableSection(session, table);
                        break;

                    case SensorDisplayItem sensor:
                        BuildTextSection(session, sensor);
                        BuildSensorSection(session, sensor);
                        break;

                    case BarDisplayItem bar:
                        BuildBarSection(session, bar);
                        BuildChartRangeSection(session, bar);
                        break;

                    case GraphDisplayItem graph:
                        BuildGraphSection(session, graph);
                        BuildChartRangeSection(session, graph);
                        break;

                    case DonutDisplayItem donut:
                        BuildDonutSection(session, donut);
                        BuildChartRangeSection(session, donut);
                        break;

                    case SensorImageDisplayItem sensorImage:
                        BuildImageSection(session, sensorImage);
                        BuildSensorHeaderLine(sensorImage.SensorName);
                        BuildBindButton(sensorImage);
                        break;

                    case ImageDisplayItem image:
                        BuildImageSection(session, image);
                        break;

                    case TextDisplayItem text:
                        BuildTextSection(session, text);
                        break;

                    case ShapeDisplayItem shape:
                        BuildShapeSection(session, shape);
                        break;

                    case GroupDisplayItem group:
                        _root.Children.Add(Header("Group"));
                        _root.Children.Add(Label($"{group.DisplayItemsCount} items in group"));
                        break;
                }
            }
            finally
            {
                _rebuilding = false;
            }
        }

        // ================= sections =================

        /// <summary>
        /// Dropdown assigning the profile's overlay to a monitor. Selecting one sets
        /// Profile.TargetWindow and parks the overlay at that monitor's top-left; a
        /// running overlay moves immediately through DisplayWindow's TargetWindow
        /// reposition path. Dragging the overlay onto another screen updates the
        /// dropdown in return.
        /// </summary>
        private void BuildDisplayAssignment(Profile profile)
        {
            if (TopLevel.GetTopLevel(this) is not Window window) return;
            var monitors = ScreenHelper.GetAllMonitors(window);
            if (monitors.Count == 0) return;

            var combo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "Not assigned",
            };
            foreach (var monitor in monitors)
            {
                combo.Items.Add(monitor.Label);
            }

            var syncing = false;
            void SyncSelection()
            {
                syncing = true;
                try
                {
                    var assigned = FindAssignedMonitor(profile, monitors);
                    combo.SelectedIndex = assigned != null ? monitors.IndexOf(assigned) : -1;
                }
                finally
                {
                    syncing = false;
                }
            }
            SyncSelection();

            combo.SelectionChanged += (_, _) =>
            {
                if (syncing || combo.SelectedIndex < 0 || combo.SelectedIndex >= monitors.Count) return;
                var monitor = monitors[combo.SelectedIndex];

                ScreenHelper.AssignTargetWindow(profile, monitor);
                (Avalonia.Application.Current as App)?.Host.SaveProfiles();
            };

            System.ComponentModel.PropertyChangedEventHandler handler = (_, e) =>
            {
                if (e.PropertyName == nameof(Profile.TargetWindow))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(SyncSelection);
                }
            };
            profile.PropertyChanged += handler;
            _profileSectionCleanups.Add(() => profile.PropertyChanged -= handler);

            _root.Children.Add(Field("Display", combo));
            _root.Children.Add(Label("Assigns the overlay to a monitor, parked at its top-left. Dragging the overlay onto another screen updates this automatically."));
        }

        private static MonitorInfo? FindAssignedMonitor(Profile profile, List<MonitorInfo> monitors)
        {
            if (profile.TargetWindow is not TargetWindow target) return null;
            return ScreenHelper.MatchTargetWindow(target, monitors, strict: false);
        }

        private void BuildCommonSection(DesignerSession session, DisplayItem item)
        {
            _root.Children.Add(Header(item.Kind));

            var name = new TextBox { Text = item.Name, Watermark = "Name" };
            name.LostFocus += (_, _) => Commit(session, item, nameof(item.Name), v => item.Name = v, item.Name, name.Text ?? "");
            _root.Children.Add(Field("Name", name));

            var grid = TwoColumns();
            AddCell(grid, 0, 0, Field("X", IntEditor(item.X, v => Commit(session, item, "X", x => item.X = x, item.X, v))));
            AddCell(grid, 0, 1, Field("Y", IntEditor(item.Y, v => Commit(session, item, "Y", y => item.Y = y, item.Y, v))));

            if (ItemGeometry.IsResizable(item))
            {
                var (w, h) = ItemGeometry.GetSize(item);
                AddCell(grid, 1, 0, Field("Width", IntEditor(w, v => CommitSize(session, item, width: v))));
                AddCell(grid, 1, 1, Field("Height", IntEditor(h, v => CommitSize(session, item, height: v))));
            }

            _root.Children.Add(grid);

            // nudge d-pad + step (v1 CommonProperties)
            var step = 1;
            var nudgeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            Button Nudge(string glyph, int dx, int dy)
            {
                var button = new Button { Content = glyph, MinWidth = 34 };
                button.Click += (_, _) =>
                {
                    if (!session.Selection.Contains(item))
                    {
                        session.Select(item);
                    }

                    session.Nudge(dx * step, dy * step);
                    RefreshGeometry();
                };
                return button;
            }

            nudgeRow.Children.Add(Nudge("←", -1, 0));
            nudgeRow.Children.Add(Nudge("→", 1, 0));
            nudgeRow.Children.Add(Nudge("↑", 0, -1));
            nudgeRow.Children.Add(Nudge("↓", 0, 1));
            var stepEditor = new NumericUpDown { Value = 1, Minimum = 1, Maximum = 100, Increment = 1, FormatString = "0", Width = 110 };
            stepEditor.ValueChanged += (_, e) => step = (int)(e.NewValue ?? 1);
            nudgeRow.Children.Add(stepEditor);
            _root.Children.Add(Field("Nudge / step", nudgeRow));

            _root.Children.Add(Field("Rotation", IntEditor(item.Rotation, v => Commit(session, item, nameof(item.Rotation), r => item.Rotation = r, item.Rotation, v), 0, 359)));

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(Check("Hidden", item.Hidden, v => Commit(session, item, nameof(item.Hidden), x => item.Hidden = x, item.Hidden, v)));
            row.Children.Add(Check("Locked", item.IsLocked, v => Commit(session, item, nameof(item.IsLocked), x => item.IsLocked = x, item.IsLocked, v)));
            _root.Children.Add(row);
        }

        private void RefreshGeometry() => Rebuild();

        private void BuildSensorHeaderLine(string sensorName)
        {
            _root.Children.Add(Label(string.IsNullOrEmpty(sensorName) ? "No sensor bound" : $"Sensor: {sensorName}"));
        }

        private void BuildBindButton(DisplayItem item)
        {
            var bind = new Button { Content = "Bind selected sensor" };
            bind.Click += (_, _) => BindSensorRequested?.Invoke(this, item);
            _root.Children.Add(bind);
        }

        private void BuildSensorBindingHeader(DesignerSession session, ChartDisplayItem chart)
        {
            BuildSensorHeaderLine(chart.SensorName);
            BuildBindButton(chart);
        }

        private void BuildSensorSection(DesignerSession session, SensorDisplayItem sensor)
        {
            _root.Children.Add(Header("Sensor"));

            var id = sensor.SensorType == Enums.SensorType.Plugin ? sensor.PluginSensorId : sensor.LibreSensorId;
            _root.Children.Add(Label(string.IsNullOrEmpty(id) ? "No sensor bound" : $"{sensor.SensorType}: {id}"));
            BuildBindButton(sensor);

            _root.Children.Add(Field("Value type", EnumCombo(sensor.ValueType,
                v => Commit(session, sensor, nameof(sensor.ValueType), x => sensor.ValueType = x, sensor.ValueType, v))));

            var showRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            showRow.Children.Add(Check("Show name", sensor.ShowName, v => Commit(session, sensor, nameof(sensor.ShowName), x => sensor.ShowName = x, sensor.ShowName, v)));
            showRow.Children.Add(Check("Show unit", sensor.ShowUnit, v => Commit(session, sensor, nameof(sensor.ShowUnit), x => sensor.ShowUnit = x, sensor.ShowUnit, v)));
            showRow.Children.Add(Check("1,000s separator", sensor.ShowThousandsSeparator, v => Commit(session, sensor, nameof(sensor.ShowThousandsSeparator), x => sensor.ShowThousandsSeparator = x, sensor.ShowThousandsSeparator, v)));
            _root.Children.Add(showRow);

            var multiplyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            multiplyRow.Children.Add(DoubleEditor(sensor.MultiplicationModifier, v => Commit(session, sensor, nameof(sensor.MultiplicationModifier), x => sensor.MultiplicationModifier = x, sensor.MultiplicationModifier, v), 0.1m, "0.00"));
            multiplyRow.Children.Add(Check("Divide", sensor.DivisionToggle, v => Commit(session, sensor, nameof(sensor.DivisionToggle), x => sensor.DivisionToggle = x, sensor.DivisionToggle, v)));
            _root.Children.Add(Field("Multiply", multiplyRow));

            _root.Children.Add(Field("Add", DoubleEditor(sensor.AdditionModifier, v => Commit(session, sensor, nameof(sensor.AdditionModifier), x => sensor.AdditionModifier = x, sensor.AdditionModifier, v), 1m, "0.00")));

            var precisionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            precisionRow.Children.Add(Check("Override precision", sensor.OverridePrecision, v => Commit(session, sensor, nameof(sensor.OverridePrecision), x => sensor.OverridePrecision = x, sensor.OverridePrecision, v)));
            precisionRow.Children.Add(IntEditor(sensor.Precision, v => Commit(session, sensor, nameof(sensor.Precision), x => sensor.Precision = x, sensor.Precision, v), 0, 3));
            _root.Children.Add(precisionRow);

            var unitRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            unitRow.Children.Add(Check("Override unit", sensor.OverrideUnit, v => Commit(session, sensor, nameof(sensor.OverrideUnit), x => sensor.OverrideUnit = x, sensor.OverrideUnit, v)));
            var unitBox = new TextBox { Text = sensor.Unit, Width = 90 };
            unitBox.LostFocus += (_, _) => Commit(session, sensor, nameof(sensor.Unit), v => sensor.Unit = v, sensor.Unit, unitBox.Text ?? "");
            unitRow.Children.Add(unitBox);
            _root.Children.Add(unitRow);

            _root.Children.Add(Header("Thresholds"));
            var threshold1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            threshold1.Children.Add(DoubleEditor(sensor.Threshold1, v => Commit(session, sensor, nameof(sensor.Threshold1), x => sensor.Threshold1 = x, sensor.Threshold1, v), 1m, "0.0"));
            threshold1.Children.Add(ColorEditor(session, sensor, nameof(sensor.Threshold1Color), sensor.Threshold1Color, v => sensor.Threshold1Color = v));
            _root.Children.Add(Field("Level 1", threshold1));

            var threshold2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            threshold2.Children.Add(DoubleEditor(sensor.Threshold2, v => Commit(session, sensor, nameof(sensor.Threshold2), x => sensor.Threshold2 = x, sensor.Threshold2, v), 1m, "0.0"));
            threshold2.Children.Add(ColorEditor(session, sensor, nameof(sensor.Threshold2Color), sensor.Threshold2Color, v => sensor.Threshold2Color = v));
            _root.Children.Add(Field("Level 2", threshold2));
        }

        private void BuildTextSection(DesignerSession session, TextDisplayItem text)
        {
            _root.Children.Add(Header("Text"));

            var fonts = SkiaSharp.SKFontManager.Default.FontFamilies.OrderBy(f => f).ToList();
            if (!string.IsNullOrEmpty(text.Font) && !fonts.Contains(text.Font))
            {
                fonts.Insert(0, text.Font); // embedded fonts (e.g. Inter) aren't in SKFontManager
            }

            var fontPicker = new ComboBox { ItemsSource = fonts, SelectedItem = text.Font, MaxDropDownHeight = 400 };
            fontPicker.SelectionChanged += (_, _) =>
            {
                if (!_rebuilding && fontPicker.SelectedItem is string font && font != text.Font)
                {
                    Commit(session, text, nameof(text.Font), v => text.Font = v, text.Font, font);
                }
            };
            _root.Children.Add(Field("Font", fontPicker));

            var sizeColorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            sizeColorRow.Children.Add(IntEditor(text.FontSize, v => Commit(session, text, nameof(text.FontSize), s => text.FontSize = s, text.FontSize, v), 1, 500));
            sizeColorRow.Children.Add(ColorEditor(session, text, nameof(text.Color), text.Color, v => text.Color = v));
            _root.Children.Add(Field("Size / color", sizeColorRow));

            var styleRow = new WrapPanel();
            styleRow.Children.Add(Check("Bold", text.Bold, v => Commit(session, text, nameof(text.Bold), x => text.Bold = x, text.Bold, v)));
            styleRow.Children.Add(Check("Italic", text.Italic, v => Commit(session, text, nameof(text.Italic), x => text.Italic = x, text.Italic, v)));
            styleRow.Children.Add(Check("Underline", text.Underline, v => Commit(session, text, nameof(text.Underline), x => text.Underline = x, text.Underline, v)));
            styleRow.Children.Add(Check("Strikeout", text.Strikeout, v => Commit(session, text, nameof(text.Strikeout), x => text.Strikeout = x, text.Strikeout, v)));
            _root.Children.Add(styleRow);

            var alignRow = new WrapPanel();
            alignRow.Children.Add(Check("Right align", text.RightAlign, v => Commit(session, text, nameof(text.RightAlign), x => text.RightAlign = x, text.RightAlign, v)));
            alignRow.Children.Add(Check("Center", text.CenterAlign, v => Commit(session, text, nameof(text.CenterAlign), x => text.CenterAlign = x, text.CenterAlign, v)));
            alignRow.Children.Add(Check("Uppercase", text.Uppercase, v => Commit(session, text, nameof(text.Uppercase), x => text.Uppercase = x, text.Uppercase, v)));
            _root.Children.Add(alignRow);

            var flowRow = new WrapPanel();
            flowRow.Children.Add(Check("Wrap", text.Wrap, v => Commit(session, text, nameof(text.Wrap), x => text.Wrap = x, text.Wrap, v)));
            flowRow.Children.Add(Check("Ellipsis", text.Ellipsis, v => Commit(session, text, nameof(text.Ellipsis), x => text.Ellipsis = x, text.Ellipsis, v)));
            flowRow.Children.Add(Check("Marquee", text.Marquee, v =>
            {
                Commit(session, text, nameof(text.Marquee), x => text.Marquee = x, text.Marquee, v);
                Rebuild();
            }));
            _root.Children.Add(flowRow);

            if (text.Marquee)
            {
                var marqueeGrid = TwoColumns();
                AddCell(marqueeGrid, 0, 0, Field("Speed", IntEditor(text.MarqueeSpeed, v => Commit(session, text, nameof(text.MarqueeSpeed), x => text.MarqueeSpeed = x, text.MarqueeSpeed, v), 1, 200)));
                AddCell(marqueeGrid, 0, 1, Field("Spacing", IntEditor(text.MarqueeSpacing, v => Commit(session, text, nameof(text.MarqueeSpacing), x => text.MarqueeSpacing = x, text.MarqueeSpacing, v), 0, 500)));
                _root.Children.Add(marqueeGrid);
            }

            var glowRow = new WrapPanel();
            glowRow.Children.Add(Check("Glow", text.GlowEnabled, v =>
            {
                Commit(session, text, nameof(text.GlowEnabled), x => text.GlowEnabled = x, text.GlowEnabled, v);
                Rebuild();
            }));
            _root.Children.Add(glowRow);

            if (text.GlowEnabled)
            {
                var glowDetail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                glowDetail.Children.Add(IntEditor(text.GlowRadius, v => Commit(session, text, nameof(text.GlowRadius), x => text.GlowRadius = x, text.GlowRadius, v), 2, 20));
                glowDetail.Children.Add(ColorEditor(session, text, nameof(text.GlowColor), text.GlowColor, v => text.GlowColor = v));
                var blendModes = new[] { "SrcOver", "Screen", "Plus", "Lighten", "Multiply" };
                var blendPicker = new ComboBox { ItemsSource = blendModes, SelectedItem = blendModes.Contains(text.GlowBlendMode) ? text.GlowBlendMode : "SrcOver" };
                blendPicker.SelectionChanged += (_, _) =>
                {
                    if (!_rebuilding && blendPicker.SelectedItem is string mode && mode != text.GlowBlendMode)
                    {
                        Commit(session, text, nameof(text.GlowBlendMode), v => text.GlowBlendMode = v, text.GlowBlendMode, mode);
                    }
                };
                glowDetail.Children.Add(blendPicker);
                _root.Children.Add(Field("Radius / color / blend", glowDetail));
            }
        }

        private void BuildDateTimeSection(DesignerSession session, DisplayItem item, Action<string> setFormat, Func<string> getFormat, bool includeTimeTokens)
        {
            _root.Children.Add(Header(includeTimeTokens ? "Clock format" : "Date format"));

            var preview = Label("");
            void UpdatePreview()
            {
                try
                {
                    preview.Text = "Preview: " + DateTime.Now.ToString(getFormat());
                }
                catch (FormatException)
                {
                    preview.Text = "(invalid format)";
                }
            }

            var formatBox = new TextBox { Text = getFormat() };
            formatBox.TextChanged += (_, _) =>
            {
                if (_rebuilding) return;
                var oldValue = getFormat();
                var newValue = formatBox.Text ?? "";
                if (newValue != oldValue)
                {
                    Commit(session, item, "Format", setFormat, oldValue, newValue);
                    UpdatePreview();
                }
            };
            _root.Children.Add(Field("Format", formatBox));
            _root.Children.Add(preview);
            UpdatePreview();

            string[] templates = includeTimeTokens
                ? ["hh:mm:ss tt", "HH:mm:ss", "hh:mm tt", "HH:mm", "dd MMM yyyy HH:mm"]
                : ["dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "dddd, MMMM dd", "ddd, MMM dd yyyy", "MMMM dd, yyyy"];
            var templateCombo = new ComboBox { ItemsSource = templates, PlaceholderText = "Templates…" };
            templateCombo.SelectionChanged += (_, _) =>
            {
                if (!_rebuilding && templateCombo.SelectedItem is string template)
                {
                    formatBox.Text = template;
                }
            };
            _root.Children.Add(Field("Template", templateCombo));

            void AddTokenExpander(string title, string[] tokens)
            {
                var panel = new WrapPanel();
                foreach (var token in tokens)
                {
                    var button = new Button { Content = token, FontSize = 11, Padding = new Thickness(6, 2), Margin = new Thickness(0, 0, 4, 4) };
                    button.Click += (_, _) =>
                    {
                        var caret = formatBox.CaretIndex;
                        formatBox.Text = (formatBox.Text ?? "").Insert(Math.Clamp(caret, 0, formatBox.Text?.Length ?? 0), token);
                        formatBox.CaretIndex = caret + token.Length;
                    };
                    panel.Children.Add(button);
                }

                _root.Children.Add(new Expander { Header = title, Content = panel, FontSize = 12 });
            }

            AddTokenExpander("Date tokens", ["d", "dd", "ddd", "dddd", "M", "MM", "MMM", "MMMM", "yy", "yyyy"]);
            if (includeTimeTokens)
            {
                AddTokenExpander("Time tokens", ["h", "hh", "H", "HH", "m", "mm", "s", "ss", "tt"]);
            }
        }

        private void BuildTableSection(DesignerSession session, TableSensorDisplayItem table)
        {
            _root.Children.Add(Header("Table"));
            _root.Children.Add(Label($"Sensor: {table.SensorName}"));
            BuildBindButton(table);

            var formatBox = new TextBox { Text = table.TableFormat, Watermark = "e.g. 0:Name|1:Value" };
            formatBox.LostFocus += (_, _) => Commit(session, table, nameof(table.TableFormat), v => table.TableFormat = v, table.TableFormat, formatBox.Text ?? "");
            _root.Children.Add(Field("Format", formatBox));

            _root.Children.Add(Field("Max rows", IntEditor(table.MaxRows, v => Commit(session, table, nameof(table.MaxRows), x => table.MaxRows = x, table.MaxRows, v), 1, 100)));
            _root.Children.Add(Check("Show header", table.ShowHeader, v => Commit(session, table, nameof(table.ShowHeader), x => table.ShowHeader = x, table.ShowHeader, v)));
        }

        private void BuildBarSection(DesignerSession session, BarDisplayItem bar)
        {
            _root.Children.Add(Header("Bar"));
            BuildSensorBindingHeader(session, bar);

            _root.Children.Add(Field("Corner radius", IntEditor(bar.CornerRadius, v => Commit(session, bar, nameof(bar.CornerRadius), x => bar.CornerRadius = x, bar.CornerRadius, v), 0)));
            _root.Children.Add(Field("Bar color", ColorEditor(session, bar, nameof(bar.Color), bar.Color, v => bar.Color = v)));
            _root.Children.Add(Check("Flip horizontally", bar.FlipX, v => Commit(session, bar, nameof(bar.FlipX), x => bar.FlipX = x, bar.FlipX, v)));

            _root.Children.Add(CheckWithColor(session, bar, "Show frame", bar.Frame, v => Commit(session, bar, nameof(bar.Frame), x => bar.Frame = x, bar.Frame, v),
                nameof(bar.FrameColor), bar.FrameColor, v => bar.FrameColor = v));
            _root.Children.Add(CheckWithColor(session, bar, "Show background", bar.Background, v => Commit(session, bar, nameof(bar.Background), x => bar.Background = x, bar.Background, v),
                nameof(bar.BackgroundColor), bar.BackgroundColor, v => bar.BackgroundColor = v));
            _root.Children.Add(CheckWithColor(session, bar, "Gradient", bar.Gradient, v => Commit(session, bar, nameof(bar.Gradient), x => bar.Gradient = x, bar.Gradient, v),
                nameof(bar.GradientColor), bar.GradientColor, v => bar.GradientColor = v));
        }

        private void BuildGraphSection(DesignerSession session, GraphDisplayItem graph)
        {
            _root.Children.Add(Header("Graph"));
            BuildSensorBindingHeader(session, graph);

            _root.Children.Add(Field("Type", EnumCombo(graph.Type, v => Commit(session, graph, nameof(graph.Type), x => graph.Type = x, graph.Type, v))));

            var grid = TwoColumns();
            AddCell(grid, 0, 0, Field("Thickness", IntEditor(graph.Thickness, v => Commit(session, graph, nameof(graph.Thickness), x => graph.Thickness = x, graph.Thickness, v), 1)));
            AddCell(grid, 0, 1, Field("Step", IntEditor(graph.Step, v => Commit(session, graph, nameof(graph.Step), x => graph.Step = x, graph.Step, v), 1)));
            _root.Children.Add(grid);

            _root.Children.Add(Field("Graph color", ColorEditor(session, graph, nameof(graph.Color), graph.Color, v => graph.Color = v)));
            _root.Children.Add(Check("Flip horizontally", graph.FlipX, v => Commit(session, graph, nameof(graph.FlipX), x => graph.FlipX = x, graph.FlipX, v)));

            _root.Children.Add(CheckWithColor(session, graph, "Fill", graph.Fill, v => Commit(session, graph, nameof(graph.Fill), x => graph.Fill = x, graph.Fill, v),
                nameof(graph.FillColor), graph.FillColor, v => graph.FillColor = v));
            _root.Children.Add(CheckWithColor(session, graph, "Show frame", graph.Frame, v => Commit(session, graph, nameof(graph.Frame), x => graph.Frame = x, graph.Frame, v),
                nameof(graph.FrameColor), graph.FrameColor, v => graph.FrameColor = v));
            _root.Children.Add(CheckWithColor(session, graph, "Show background", graph.Background, v => Commit(session, graph, nameof(graph.Background), x => graph.Background = x, graph.Background, v),
                nameof(graph.BackgroundColor), graph.BackgroundColor, v => graph.BackgroundColor = v));
        }

        private void BuildDonutSection(DesignerSession session, DonutDisplayItem donut)
        {
            _root.Children.Add(Header("Donut"));
            BuildSensorBindingHeader(session, donut);

            var grid = TwoColumns();
            AddCell(grid, 0, 0, Field("Radius", IntEditor(donut.Radius, v => Commit(session, donut, nameof(donut.Radius), x => donut.Radius = x, donut.Radius, v), 5)));
            AddCell(grid, 0, 1, Field("Thickness", IntEditor(donut.Thickness, v => Commit(session, donut, nameof(donut.Thickness), x => donut.Thickness = x, donut.Thickness, v), 1)));
            _root.Children.Add(grid);

            _root.Children.Add(Field("Span (°)", SliderEditor(1, 360, donut.Span, v => Commit(session, donut, nameof(donut.Span), x => donut.Span = x, donut.Span, v))));
            _root.Children.Add(Field("Donut color", ColorEditor(session, donut, nameof(donut.Color), donut.Color, v => donut.Color = v)));

            _root.Children.Add(CheckWithColor(session, donut, "Show frame", donut.Frame, v => Commit(session, donut, nameof(donut.Frame), x => donut.Frame = x, donut.Frame, v),
                nameof(donut.FrameColor), donut.FrameColor, v => donut.FrameColor = v));
            _root.Children.Add(CheckWithColor(session, donut, "Show background", donut.Background, v => Commit(session, donut, nameof(donut.Background), x => donut.Background = x, donut.Background, v),
                nameof(donut.BackgroundColor), donut.BackgroundColor, v => donut.BackgroundColor = v));
        }

        private void BuildChartRangeSection(DesignerSession session, ChartDisplayItem chart)
        {
            _root.Children.Add(Check("Auto range", chart.AutoValue, v =>
            {
                Commit(session, chart, nameof(chart.AutoValue), x => chart.AutoValue = x, chart.AutoValue, v);
                Rebuild();
            }));

            if (!chart.AutoValue)
            {
                var grid = TwoColumns();
                AddCell(grid, 0, 0, Field("Min", IntEditor(chart.MinValue, v => Commit(session, chart, nameof(chart.MinValue), x => chart.MinValue = x, chart.MinValue, v))));
                AddCell(grid, 0, 1, Field("Max", IntEditor(chart.MaxValue, v => Commit(session, chart, nameof(chart.MaxValue), x => chart.MaxValue = x, chart.MaxValue, v))));
                _root.Children.Add(grid);
            }
        }

        private void BuildGaugeSection(DesignerSession session, GaugeDisplayItem gauge)
        {
            _root.Children.Add(Header("Gauge"));
            BuildSensorHeaderLine(gauge.SensorName);
            BuildBindButton(gauge);

            var grid = TwoColumns();
            AddCell(grid, 0, 0, Field("Min value", DoubleEditor(gauge.MinValue, v => Commit(session, gauge, nameof(gauge.MinValue), x => gauge.MinValue = x, gauge.MinValue, v), 1m, "0.0")));
            AddCell(grid, 0, 1, Field("Max value", DoubleEditor(gauge.MaxValue, v => Commit(session, gauge, nameof(gauge.MaxValue), x => gauge.MaxValue = x, gauge.MaxValue, v), 1m, "0.0")));
            _root.Children.Add(grid);

            _root.Children.Add(Field("Scale %", SliderEditor(1, 500, gauge.Scale, v => Commit(session, gauge, nameof(gauge.Scale), x => gauge.Scale = x, gauge.Scale, v))));

            _root.Children.Add(Check("Mirror (flip horizontally)", gauge.FlipX, v => Commit(session, gauge, nameof(gauge.FlipX), x => gauge.FlipX = x, gauge.FlipX, v)));

            _root.Children.Add(Header("Image steps"));
            _root.Children.Add(Label("Frames from min to max; the shown frame follows the sensor value."));

            // Live preview of the frame the render loop is currently animating.
            var previewImage = new Image { Height = 130, Stretch = Stretch.Uniform };
            var previewCaption = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Center,
                Text = "No frames",
            };
            var previewStack = new StackPanel { Spacing = 6 };
            previewStack.Children.Add(previewImage);
            previewStack.Children.Add(previewCaption);
            _root.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                Child = previewStack,
            });

            string? shownPath = null;
            void UpdatePreview()
            {
                var current = gauge.CurrentImage;
                var path = current?.CalculatedPath;
                if (path != shownPath)
                {
                    shownPath = path;
                    previewImage.Source = GetGaugeFrame(path).Bitmap;
                }

                if (current == null || gauge.Images.Count == 0)
                {
                    previewCaption.Text = "No frames";
                    return;
                }

                var index = gauge.Images.IndexOf(current);
                var reading = gauge.GetValue();
                if (reading?.ValueNow is double value && gauge.MaxValue > gauge.MinValue)
                {
                    var percent = Math.Clamp((value - gauge.MinValue) / (gauge.MaxValue - gauge.MinValue) * 100, 0, 100);
                    previewCaption.Text = $"Frame {index + 1}/{gauge.Images.Count} - {value:0.#}{reading.Value.Unit} ({percent:0}%)";
                }
                else
                {
                    previewCaption.Text = $"Frame {index + 1}/{gauge.Images.Count}";
                }
            }

            _gaugePreviewTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _gaugePreviewTimer.Tick += (_, _) => UpdatePreview();
            _gaugePreviewTimer.Start();

            var imageList = new ListBox { MaxHeight = 260, SelectionMode = SelectionMode.Single };
            void RefreshImageList()
            {
                var selected = imageList.SelectedIndex;
                var count = gauge.Images.Count;
                var step = count > 1 ? 100.0 / (count - 1) : 100.0;
                var rows = new List<Control>();

                for (int i = 0; i < count; i++)
                {
                    var frame = gauge.Images[i];
                    var (bitmap, width, height) = GetGaugeFrame(frame.CalculatedPath);

                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("56,54,*"), Height = 44 };

                    var thumb = new Image { Source = bitmap, Width = 48, Height = 38, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(thumb, 0);
                    row.Children.Add(thumb);

                    var stepLabel = new TextBlock
                    {
                        Text = i == count - 1 ? "≤100%" : $"<{Math.Round((i + 1) * step)}%",
                        FontSize = 12,
                        Opacity = 0.8,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(stepLabel, 1);
                    row.Children.Add(stepLabel);

                    var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
                    info.Children.Add(new TextBlock
                    {
                        Text = frame.FilePath ?? frame.CalculatedPath ?? "?",
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    info.Children.Add(new TextBlock
                    {
                        Text = bitmap != null ? $"{width}×{height}" : "file missing",
                        FontSize = 11,
                        Opacity = 0.6,
                    });
                    Grid.SetColumn(info, 2);
                    row.Children.Add(info);

                    rows.Add(row);
                }

                imageList.ItemsSource = rows;
                if (selected >= 0 && selected < rows.Count)
                {
                    imageList.SelectedIndex = selected;
                }
            }

            RefreshImageList();
            UpdatePreview();
            _root.Children.Add(imageList);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

            var add = new Button { Content = "Add…" };
            add.Click += async (_, _) =>
            {
                var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage == null) return;

                var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Add gauge images",
                    AllowMultiple = true,
                    FileTypeFilter =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.svg", "*.webp"] }
                    ]
                });

                foreach (var file in files)
                {
                    if (file.Path is not { IsFile: true } uri) continue;
                    var fileName = System.IO.Path.GetFileName(uri.LocalPath);
                    var data = await System.IO.File.ReadAllBytesAsync(uri.LocalPath);
                    await InfoPanel.Utils.FileUtil.SaveAsset(session.Profile, fileName, data);

                    var imageItem = new ImageDisplayItem
                    {
                        Name = fileName,
                        FilePath = fileName,
                        RelativePath = true,
                        PersistentCache = true,
                    };
                    imageItem.SetProfile(session.Profile);
                    gauge.Images.Add(imageItem);
                }

                gauge.TriggerDisplayImageChange();
                RefreshImageList();
            };
            buttons.Children.Add(add);

            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                if (imageList.SelectedIndex >= 0 && imageList.SelectedIndex < gauge.Images.Count)
                {
                    gauge.Images.RemoveAt(imageList.SelectedIndex);
                    gauge.TriggerDisplayImageChange();
                    RefreshImageList();
                }
            };
            buttons.Children.Add(remove);

            var up = new Button { Content = "↑" };
            up.Click += (_, _) =>
            {
                var i = imageList.SelectedIndex;
                if (i > 0)
                {
                    gauge.Images.Move(i, i - 1);
                    RefreshImageList();
                    imageList.SelectedIndex = i - 1;
                }
            };
            buttons.Children.Add(up);

            var down = new Button { Content = "↓" };
            down.Click += (_, _) =>
            {
                var i = imageList.SelectedIndex;
                if (i >= 0 && i < gauge.Images.Count - 1)
                {
                    gauge.Images.Move(i, i + 1);
                    RefreshImageList();
                    imageList.SelectedIndex = i + 1;
                }
            };
            buttons.Children.Add(down);

            _root.Children.Add(buttons);
        }

        /// <summary>Decoded preview + original pixel size for a gauge frame, cached per file.</summary>
        private (Avalonia.Media.Imaging.Bitmap? Bitmap, int Width, int Height) GetGaugeFrame(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return (null, 0, 0);
            }

            if (_gaugeFrameCache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            (Avalonia.Media.Imaging.Bitmap?, int, int) entry;
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    entry = (null, 0, 0);
                }
                else
                {
                    int width = 0, height = 0;
                    using (var codec = SkiaSharp.SKCodec.Create(path))
                    {
                        if (codec != null)
                        {
                            width = codec.Info.Width;
                            height = codec.Info.Height;
                        }
                    }

                    using var stream = System.IO.File.OpenRead(path);
                    entry = (Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 256), width, height);
                }
            }
            catch
            {
                entry = (null, 0, 0);
            }

            _gaugeFrameCache[path] = entry;
            return entry;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _gaugePreviewTimer?.Stop();
            _gaugePreviewTimer = null;
        }

        private void BuildShapeSection(DesignerSession session, ShapeDisplayItem shape)
        {
            _root.Children.Add(Header("Shape"));

            _root.Children.Add(Field("Type", EnumCombo(shape.Type, v => Commit(session, shape, nameof(shape.Type), x => shape.Type = x, shape.Type, v))));
            _root.Children.Add(Field("Corner radius", IntEditor(shape.CornerRadius, v => Commit(session, shape, nameof(shape.CornerRadius), x => shape.CornerRadius = x, shape.CornerRadius, v), 0)));

            _root.Children.Add(Check("Show frame", shape.ShowFrame, v =>
            {
                Commit(session, shape, nameof(shape.ShowFrame), x => shape.ShowFrame = x, shape.ShowFrame, v);
                Rebuild();
            }));
            if (shape.ShowFrame)
            {
                var frameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                frameRow.Children.Add(IntEditor(shape.FrameThickness, v => Commit(session, shape, nameof(shape.FrameThickness), x => shape.FrameThickness = x, shape.FrameThickness, v), 1));
                frameRow.Children.Add(ColorEditor(session, shape, nameof(shape.FrameColor), shape.FrameColor, v => shape.FrameColor = v));
                _root.Children.Add(Field("Thickness / color", frameRow));
            }

            _root.Children.Add(CheckWithColor(session, shape, "Show fill", shape.ShowFill, v => Commit(session, shape, nameof(shape.ShowFill), x => shape.ShowFill = x, shape.ShowFill, v),
                nameof(shape.FillColor), shape.FillColor, v => shape.FillColor = v));

            _root.Children.Add(Check("Show gradient", shape.ShowGradient, v =>
            {
                Commit(session, shape, nameof(shape.ShowGradient), x => shape.ShowGradient = x, shape.ShowGradient, v);
                Rebuild();
            }));
            if (shape.ShowGradient)
            {
                _root.Children.Add(Field("Gradient type", EnumCombo(shape.GradientType, v => Commit(session, shape, nameof(shape.GradientType), x => shape.GradientType = x, shape.GradientType, v))));
                var grid = TwoColumns();
                AddCell(grid, 0, 0, Field("Angle", IntEditor(shape.GradientAngle, v => Commit(session, shape, nameof(shape.GradientAngle), x => shape.GradientAngle = x, shape.GradientAngle, v), 0, 359)));
                AddCell(grid, 0, 1, Field("Speed (ms)", IntEditor(shape.GradientAnimationSpeed, v => Commit(session, shape, nameof(shape.GradientAnimationSpeed), x => shape.GradientAnimationSpeed = x, shape.GradientAnimationSpeed, v), 0)));
                _root.Children.Add(grid);

                var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                colorRow.Children.Add(ColorEditor(session, shape, nameof(shape.GradientColor), shape.GradientColor, v => shape.GradientColor = v));
                colorRow.Children.Add(ColorEditor(session, shape, nameof(shape.GradientColor2), shape.GradientColor2, v => shape.GradientColor2 = v));
                _root.Children.Add(Field("Colors", colorRow));
            }
        }

        private void BuildImageSection(DesignerSession session, ImageDisplayItem image)
        {
            _root.Children.Add(Header("Image / Video"));

            _root.Children.Add(Field("Source type", EnumCombo(image.Type, v =>
            {
                Commit(session, image, nameof(image.Type), x => image.Type = x, image.Type, v);
                Rebuild();
            })));

            switch (image.Type)
            {
                case ImageDisplayItem.ImageType.FILE:
                {
                    _root.Children.Add(Label(image.CalculatedPath ?? "No file selected"));
                    var pick = new Button { Content = "Choose file…" };
                    pick.Click += async (_, _) => await PickImageFile(session, image);
                    _root.Children.Add(pick);
                    break;
                }

                case ImageDisplayItem.ImageType.URL:
                {
                    var url = new TextBox { Text = image.HttpUrl, Watermark = "http(s):// image URL" };
                    url.LostFocus += (_, _) =>
                    {
                        var value = url.Text ?? "";
                        if (!_rebuilding && value != (image.HttpUrl ?? "") && (value.Length == 0 || value.StartsWith("http")))
                        {
                            Commit(session, image, nameof(image.HttpUrl), v => image.HttpUrl = v, image.HttpUrl ?? "", value);
                        }
                    };
                    _root.Children.Add(Field("URL", url));
                    break;
                }

                case ImageDisplayItem.ImageType.RTSP:
                {
                    var rtsp = new TextBox { Text = image.RtspUrl, Watermark = "rtsp:// stream URL" };
                    rtsp.LostFocus += (_, _) =>
                    {
                        var value = rtsp.Text ?? "";
                        if (!_rebuilding && value != (image.RtspUrl ?? "") && (value.Length == 0 || value.StartsWith("rtsp")))
                        {
                            Commit(session, image, nameof(image.RtspUrl), v => image.RtspUrl = v, image.RtspUrl ?? "", value);
                        }
                    };
                    _root.Children.Add(Field("RTSP URL", rtsp));
                    break;
                }
            }

            if (image.Type == ImageDisplayItem.ImageType.URL || image is HttpImageDisplayItem)
            {
                _root.Children.Add(Field("Refresh every (s), 0 = never",
                    IntEditor(image.RefreshIntervalSeconds, v => Commit(session, image, nameof(image.RefreshIntervalSeconds),
                        x => image.RefreshIntervalSeconds = x, image.RefreshIntervalSeconds, v))));
                _root.Children.Add(Label("Re-downloads the image periodically for webcams, rendered dashboards and other changing sources."));
            }

            _root.Children.Add(Field("Scale %", SliderEditor(1, 500, image.Scale, v => Commit(session, image, nameof(image.Scale), x => image.Scale = x, image.Scale, v))));

            _root.Children.Add(CheckWithColor(session, image, "Fill layer", image.Layer, v => Commit(session, image, nameof(image.Layer), x => image.Layer = x, image.Layer, v),
                nameof(image.LayerColor), image.LayerColor, v => image.LayerColor = v));

            var flags = new WrapPanel();
            flags.Children.Add(Check("Enable caching", image.Cache, v => Commit(session, image, nameof(image.Cache), x => image.Cache = x, image.Cache, v)));
            flags.Children.Add(Check("Show panel", image.ShowPanel, v => Commit(session, image, nameof(image.ShowPanel), x => image.ShowPanel = x, image.ShowPanel, v)));
            _root.Children.Add(flags);
        }

        private async Task PickImageFile(DesignerSession session, ImageDisplayItem image)
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
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
        }

        // ================= editor helpers =================

        /// <summary>
        /// True while a commit initiated by one of this panel's own editors is running.
        /// The page's Undo.StateChanged handler skips the full inspector rebuild in
        /// that case: rebuilding would destroy the control mid-interaction (the color
        /// picker flyout closed on the first slider tick, sliders lost their drag).
        /// Canvas-driven edits and undo/redo still rebuild normally.
        /// </summary>
        public bool IsCommitting { get; private set; }

        private void Commit<T>(DesignerSession session, DisplayItem item, string property, Action<T> setter, T oldValue, T newValue)
        {
            if (_rebuilding || EqualityComparer<T>.Default.Equals(oldValue, newValue))
            {
                return;
            }

            IsCommitting = true;
            try
            {
                session.Undo.Execute(new SetPropertyAction<T>(item, property, setter, oldValue, newValue));
            }
            finally
            {
                IsCommitting = false;
            }
        }

        private void CommitSize(DesignerSession session, DisplayItem item, int? width = null, int? height = null)
        {
            if (_rebuilding) return;

            var (w, h) = ItemGeometry.GetSize(item);
            var newW = width ?? w;
            var newH = height ?? h;
            if (newW == w && newH == h) return;

            IsCommitting = true;
            try
            {
                session.Undo.Execute(new SetPropertyAction<(int, int)>(item, "Size",
                    v => ItemGeometry.SetSize(item, v.Item1, v.Item2), (w, h), (newW, newH)));
            }
            finally
            {
                IsCommitting = false;
            }
        }

        private NumericUpDown IntEditor(double value, Action<int> commit, int? min = null, int? max = null)
        {
            var editor = new NumericUpDown
            {
                Value = (decimal)value,
                Increment = 1,
                FormatString = "0",
                MinWidth = 90,
            };
            if (min.HasValue) editor.Minimum = min.Value;
            if (max.HasValue) editor.Maximum = max.Value;
            editor.ValueChanged += (_, e) =>
            {
                if (!_rebuilding && e.NewValue.HasValue)
                {
                    commit((int)e.NewValue.Value);
                }
            };
            return editor;
        }

        private NumericUpDown DoubleEditor(double value, Action<double> commit, decimal increment, string format)
        {
            var editor = new NumericUpDown
            {
                Value = (decimal)value,
                Increment = increment,
                FormatString = format,
                MinWidth = 90,
            };
            editor.ValueChanged += (_, e) =>
            {
                if (!_rebuilding && e.NewValue.HasValue)
                {
                    commit((double)e.NewValue.Value);
                }
            };
            return editor;
        }

        private Control SliderEditor(int min, int max, int value, Action<int> commit)
        {
            var panel = new DockPanel();
            var valueLabel = new TextBlock { Text = value.ToString(), MinWidth = 36, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            DockPanel.SetDock(valueLabel, Dock.Right);
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true };
            slider.ValueChanged += (_, e) =>
            {
                valueLabel.Text = ((int)e.NewValue).ToString();
                if (!_rebuilding)
                {
                    commit((int)e.NewValue);
                }
            };
            panel.Children.Add(valueLabel);
            panel.Children.Add(slider);
            return panel;
        }

        private CheckBox Check(string label, bool value, Action<bool> commit)
        {
            var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 0, 8, 0) };
            check.IsCheckedChanged += (_, _) =>
            {
                if (!_rebuilding)
                {
                    commit(check.IsChecked == true);
                }
            };
            return check;
        }

        private Control CheckWithColor(DesignerSession session, DisplayItem item, string label, bool value, Action<bool> commitCheck,
            string colorProperty, string currentColor, Action<string> setColor)
        {
            var row = new DockPanel();
            var picker = ColorEditor(session, item, colorProperty, currentColor, setColor);
            DockPanel.SetDock(picker, Dock.Right);
            row.Children.Add(picker);
            row.Children.Add(Check(label, value, commitCheck));
            return row;
        }

        private Control ColorEditor(DesignerSession session, DisplayItem item, string property, string current, Action<string> setter)
        {
            var picker = new ColorPicker
            {
                IsAlphaEnabled = true,
                IsAlphaVisible = true,
                Width = 64,
                Height = 28,
            };

            if (Color.TryParse(string.IsNullOrEmpty(current) ? "#FF000000" : current, out var parsed))
            {
                picker.Color = parsed;
            }

            picker.ColorChanged += (_, e) =>
            {
                if (_rebuilding) return;
                var hex = $"#{e.NewColor.A:X2}{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
                if (!hex.Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    Commit(session, item, property, setter, current, hex);
                    current = hex;
                }
            };

            return picker;
        }

        private ComboBox EnumCombo<T>(T current, Action<T> commit) where T : struct, Enum
        {
            var combo = new ComboBox
            {
                ItemsSource = Enum.GetValues<T>(),
                SelectedItem = current,
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (!_rebuilding && combo.SelectedItem is T value && !value.Equals(current))
                {
                    commit(value);
                    current = value;
                }
            };
            return combo;
        }

        private static Grid TwoColumns() => new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };

        private static void AddCell(Grid grid, int row, int column, Control control)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            control.Margin = new Thickness(0, 0, 8, 8);
            grid.Children.Add(control);
        }

        private static TextBlock Header(string text) => new()
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 6, 0, 0),
        };

        private static TextBlock Label(string text) => new()
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };

        private static StackPanel Field(string label, Control editor)
        {
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 });
            panel.Children.Add(editor);
            return panel;
        }
    }
}
