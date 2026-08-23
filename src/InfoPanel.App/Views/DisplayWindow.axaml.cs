using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Stores;
using InfoPanel.Utils;
using InfoPanel.Views.Controls;
using Serilog;
using SkiaSharp;

namespace InfoPanel.Views
{
    /// <summary>
    /// Borderless transparent desktop overlay rendering one profile via the Skia lease,
    /// with the v1 interaction model: left-drag moves the window (or the selected items,
    /// snapped to the grid), middle-click selects items, arrow keys nudge.
    /// Runs on the X11 backend (XWayland under Wayland sessions).
    /// </summary>
    public partial class DisplayWindow : Window
    {
        private static readonly ILogger Logger = Log.ForContext<DisplayWindow>();

        public Profile Profile { get; }

        private System.Timers.Timer? _renderTimer;
        private readonly FpsCounter _fpsCounter = new();
        private bool _dragStart;
        private Point _startPosition;
        private bool _ignorePositionChange;

        public DisplayWindow(Profile profile)
        {
            Profile = profile;
            DataContext = this;

            InitializeComponent();

            Topmost = profile.Topmost;
            CanResize = profile.Resize;

            Width = profile.Width;
            Height = profile.Height;

            var skiaElement = this.FindControl<SkiaCanvas>("SkiaElement");
            if (skiaElement != null)
            {
                skiaElement.Width = profile.Width;
                skiaElement.Height = profile.Height;
                skiaElement.RenderAction = PaintSurface;
            }

            Loaded += Window_Loaded;
            Closing += Window_Closing;
            PositionChanged += Window_PositionChanged;
            Profile.PropertyChanged += Profile_PropertyChanged;
        }

        private void Window_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            SetWindowPositionRelativeToScreen();
            StartRenderTimer();
            Activate();
        }

        private void StartRenderTimer()
        {
            double interval = Math.Max(1000.0 / RenderContext.TargetFrameRate - 1, 5);
            _fpsCounter.SetMaxFrames(RenderContext.TargetFrameRate);

            _renderTimer = new System.Timers.Timer(interval);
            _renderTimer.Elapsed += OnTimerElapsed;
            _renderTimer.AutoReset = true;
            _renderTimer.Start();
        }

        private void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var skiaElement = this.FindControl<SkiaCanvas>("SkiaElement");
                skiaElement?.InvalidateVisual();
            });
        }

        private void PaintSurface(SKCanvas canvas, int width, int height)
        {
            if (_renderTimer == null || !_renderTimer.Enabled)
            {
                return;
            }

            canvas.Clear();

            SkiaGraphics skiaGraphics = new(canvas, Profile.FontScale);
            PanelDraw.Run(Profile, skiaGraphics, cacheHint: $"DISPLAY-{Profile.Guid}", fpsCounter: _fpsCounter);
        }

        // ================= interaction (v1 model) =================

        private int GridSize => Math.Max((int)DeviceRuntime.Settings.GridLinesSpacing, 1);

        private List<DisplayItem> SelectedVisibleItems
        {
            get
            {
                var result = new List<DisplayItem>();
                foreach (var item in DisplayItemStore.Instance.GetSnapshot(Profile))
                {
                    if (item is GroupDisplayItem group)
                    {
                        result.AddRange(group.DisplayItems.Where(child => child.Selected && !child.Hidden));
                    }

                    if (item.Selected && !item.Hidden)
                    {
                        result.Add(item);
                    }
                }

                return result;
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);

            foreach (var displayItem in SelectedVisibleItems)
            {
                switch (e.Key)
                {
                    case Key.Up: displayItem.Y -= GridSize; break;
                    case Key.Down: displayItem.Y += GridSize; break;
                    case Key.Left: displayItem.X -= GridSize; break;
                    case Key.Right: displayItem.X += GridSize; break;
                }
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed)
            {
                HandleLeftPress(e);
            }
            else if (point.Properties.IsMiddleButtonPressed)
            {
                HandleMiddlePress(e);
            }
        }

        private void HandleLeftPress(PointerPressedEventArgs e)
        {
            var position = e.GetPosition(this);
            var skPoint = new SKPoint((float)position.X, (float)position.Y);
            var selected = SelectedVisibleItems;

            if (!_dragStart && selected.Count > 0 && !selected.Any(item => item.ContainsPoint(skPoint)))
            {
                foreach (var item in selected)
                {
                    item.Selected = false;
                }

                selected.Clear();
            }

            if (selected.Count == 0)
            {
                if (Profile.Drag)
                {
                    BeginMoveDrag(e);

                    // after the drag completes, persist position relative to the landing screen
                    var screen = ScreenHelper.GetWindowScreen(this);
                    if (screen != null)
                    {
                        var windowPos = ScreenHelper.GetWindowPositionPhysical(this);
                        var relativePosition = ScreenHelper.GetWindowRelativePosition(screen, windowPos);

                        _ignorePositionChange = true;
                        try
                        {
                            Profile.TargetWindow = new TargetWindow(
                                (int)screen.Bounds.Left, (int)screen.Bounds.Top,
                                (int)screen.Bounds.Width, (int)screen.Bounds.Height,
                                screen.DeviceName);
                            Profile.WindowX = (int)relativePosition.X;
                            Profile.WindowY = (int)relativePosition.Y;
                        }
                        finally
                        {
                            _ignorePositionChange = false;
                        }
                    }
                }
            }
            else
            {
                _startPosition = position;
                foreach (var item in selected)
                {
                    item.MouseOffset = new SKPoint((float)(_startPosition.X - item.X), (float)(_startPosition.Y - item.Y));
                }

                _dragStart = true;
            }
        }

        private void HandleMiddlePress(PointerPressedEventArgs e)
        {
            var position = e.GetPosition(this);
            var skPoint = new SKPoint((float)position.X, (float)position.Y);
            DisplayItem? clickedItem = null;

            var displayItems = DisplayItemStore.Instance.GetSnapshot(Profile).Reverse();
            foreach (var item in displayItems)
            {
                if (item.Hidden) continue;

                if (item is GroupDisplayItem group)
                {
                    foreach (var groupItem in group.DisplayItems.Reverse())
                    {
                        if (!groupItem.Hidden && groupItem.ContainsPoint(skPoint))
                        {
                            clickedItem = groupItem;
                            break;
                        }
                    }

                    if (clickedItem == null) continue;
                }

                if (clickedItem != null) break;

                if (item.ContainsPoint(skPoint))
                {
                    clickedItem = item;
                    break;
                }
            }

            var additive = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            if (clickedItem != null)
            {
                if (!additive)
                {
                    foreach (var item in SelectedVisibleItems.Where(i => i != clickedItem))
                    {
                        item.Selected = false;
                    }
                }

                clickedItem.Selected = true;
            }
            else if (!additive)
            {
                foreach (var item in SelectedVisibleItems)
                {
                    item.Selected = false;
                }
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _dragStart = false;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (!_dragStart) return;

            var selected = SelectedVisibleItems;
            if (selected.Count == 0)
            {
                _dragStart = false;
                return;
            }

            var gridSize = GridSize;
            var currentPosition = e.GetPosition(this);

            foreach (var displayItem in selected)
            {
                if (displayItem.IsLocked) continue;

                int x = (int)(currentPosition.X - displayItem.MouseOffset.X);
                int y = (int)(currentPosition.Y - displayItem.MouseOffset.Y);

                displayItem.X = (int)(Math.Round((double)x / gridSize) * gridSize);
                displayItem.Y = (int)(Math.Round((double)y / gridSize) * gridSize);
            }

            _startPosition = currentPosition;
        }

        // ================= placement =================

        /// <summary>
        /// Whether the profile's overlay has a monitor to appear on, using the same
        /// matching rules as <see cref="SetWindowPositionRelativeToScreen"/>. Checked
        /// BEFORE creating the overlay window: showing a window and hiding it after
        /// the fact leaves a ghost taskbar entry on some window managers (KWin).
        /// </summary>
        public static bool HasTargetMonitor(Profile profile, Window reference)
        {
            var screens = ScreenHelper.GetAllMonitors(reference);
            if (profile.TargetWindow is TargetWindow targetWindow
                && ScreenHelper.MatchTargetWindow(targetWindow, screens, profile.StrictWindowMatching) != null)
            {
                return true;
            }

            return !profile.StrictWindowMatching && screens.Count > 0;
        }

        private void SetWindowPositionRelativeToScreen()
        {
            var screens = ScreenHelper.GetAllMonitors(this);
            MonitorInfo? targetScreen = null;

            if (Profile.TargetWindow is TargetWindow targetWindow)
            {
                targetScreen = ScreenHelper.MatchTargetWindow(targetWindow, screens, Profile.StrictWindowMatching);
            }

            if (!Profile.StrictWindowMatching)
            {
                targetScreen ??= screens.FirstOrDefault();
            }

            if (targetScreen != null)
            {
                var x = targetScreen.Bounds.Left + Profile.WindowX;
                var y = targetScreen.Bounds.Top + Profile.WindowY;
                ScreenHelper.MoveWindowPhysical(this, (int)x, (int)y);
            }
            else if (IsVisible)
            {
                Logger.Warning("No matching monitor for profile {Name} (strict matching); hiding overlay", Profile.Name);
                Hide();
            }
        }

        private void Window_PositionChanged(object? sender, PixelPointEventArgs e)
        {
            if (_ignorePositionChange) return;

            var screen = ScreenHelper.GetWindowScreen(this);
            if (screen != null)
            {
                var relative = ScreenHelper.GetWindowRelativePosition(screen, new SKPoint(e.Point.X, e.Point.Y));
                Profile.WindowX = (int)relative.X;
                Profile.WindowY = (int)relative.Y;
            }
        }

        private void Profile_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                switch (e.PropertyName)
                {
                    case nameof(Profile.Width):
                    case nameof(Profile.Height):
                        Width = Profile.Width;
                        Height = Profile.Height;
                        var skiaElement = this.FindControl<SkiaCanvas>("SkiaElement");
                        if (skiaElement != null)
                        {
                            skiaElement.Width = Profile.Width;
                            skiaElement.Height = Profile.Height;
                        }
                        break;
                    case nameof(Profile.Topmost):
                        Topmost = Profile.Topmost;
                        break;
                    case nameof(Profile.Resize):
                        CanResize = Profile.Resize;
                        break;
                    case nameof(Profile.TargetWindow):
                    case nameof(Profile.WindowX):
                    case nameof(Profile.WindowY):
                    case nameof(Profile.StrictWindowMatching):
                        if (!_ignorePositionChange)
                        {
                            SetWindowPositionRelativeToScreen();
                        }
                        break;
                }
            });
        }

        private void Window_Closing(object? sender, WindowClosingEventArgs e)
        {
            if (_renderTimer != null)
            {
                _renderTimer.Stop();
                _renderTimer.Elapsed -= OnTimerElapsed;
                _renderTimer.Dispose();
                _renderTimer = null;
            }

            Profile.PropertyChanged -= Profile_PropertyChanged;
            PositionChanged -= Window_PositionChanged;
        }
    }
}
