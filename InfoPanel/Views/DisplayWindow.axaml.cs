using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Utils;
using InfoPanel.Views.Controls;
using Serilog;
using SkiaSharp;
using System;
using System.Linq;
using System.Timers;

namespace InfoPanel.Views
{
    public partial class DisplayWindow : Window
    {
        private static readonly ILogger Logger = Log.ForContext<DisplayWindow>();

        public Profile Profile { get; }

        private Timer? _renderTimer;
        private readonly FpsCounter FpsCounter = new();

        private bool _dragStart;
        private Point _startPosition;

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
            Closing += DisplayWindow_Closing;

            Profile.PropertyChanged += Profile_PropertyChanged;
            ConfigModel.Instance.Settings.PropertyChanged += Config_PropertyChanged;

            PositionChanged += DisplayWindow_PositionChanged;
        }

        private void Window_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            SetWindowPositionRelativeToScreen();
            UpdateSkiaTimer();
            Activate();
        }

        private void UpdateSkiaTimer()
        {
            double interval = (1000.0 / ConfigModel.Instance.Settings.TargetFrameRate) - 1;
            FpsCounter.SetMaxFrames(ConfigModel.Instance.Settings.TargetFrameRate);

            if (_renderTimer == null)
            {
                _renderTimer = new Timer(interval);
                _renderTimer.Elapsed += OnTimerElapsed;
                _renderTimer.AutoReset = true;
                _renderTimer.Start();
            }
            else
            {
                _renderTimer.Interval = interval;
            }
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
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
            PanelDraw.Run(Profile, skiaGraphics, cacheHint: $"DISPLAY-{Profile.Guid}", fpsCounter: FpsCounter);
        }

        private void DisplayWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            ConfigModel.Instance.Settings.PropertyChanged -= Config_PropertyChanged;

            if (_renderTimer != null)
            {
                _renderTimer.Stop();
                _renderTimer.Elapsed -= OnTimerElapsed;
                _renderTimer.Dispose();
                _renderTimer = null;
            }

            Profile.PropertyChanged -= Profile_PropertyChanged;
            PositionChanged -= DisplayWindow_PositionChanged;
        }

        private void Config_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ConfigModel.Instance.Settings.TargetFrameRate))
            {
                UpdateSkiaTimer();
            }
        }

        private bool _ignorePositionChange;

        private void Profile_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Profile.TargetWindow) || e.PropertyName == nameof(Profile.WindowX)
                || e.PropertyName == nameof(Profile.WindowY) || e.PropertyName == nameof(Profile.StrictWindowMatching))
            {
                if (!_ignorePositionChange)
                {
                    Dispatcher.UIThread.Post(SetWindowPositionRelativeToScreen);
                }
            }
            else if (e.PropertyName == nameof(Profile.Resize))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    CanResize = Profile.Resize;
                });
            }
            else if (e.PropertyName == nameof(Profile.Width) || e.PropertyName == nameof(Profile.Height))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Width = Profile.Width;
                    Height = Profile.Height;

                    var skiaElement = this.FindControl<SkiaCanvas>("SkiaElement");
                    if (skiaElement != null)
                    {
                        skiaElement.Width = Profile.Width;
                        skiaElement.Height = Profile.Height;
                    }
                });
            }
            else if (e.PropertyName == nameof(Profile.Topmost))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Topmost = Profile.Topmost;
                });
            }
        }

        private void SetWindowPositionRelativeToScreen()
        {
            var screens = ScreenHelper.GetAllMonitors(this);
            MonitorInfo? targetScreen = null;

            if (Profile.TargetWindow is TargetWindow targetWindow)
            {
                targetScreen ??= screens.FirstOrDefault(s =>
                    s.DeviceName == targetWindow.DeviceName
                    && s.Bounds.Width == targetWindow.Width
                    && s.Bounds.Height == targetWindow.Height);

                if (!Profile.StrictWindowMatching)
                {
                    targetScreen ??= screens.FirstOrDefault(s => s.DeviceName == targetWindow.DeviceName);
                    targetScreen ??= screens.FirstOrDefault(s =>
                        s.Bounds.Width == targetWindow.Width
                        && s.Bounds.Height == targetWindow.Height);
                }
            }

            if (!Profile.StrictWindowMatching)
            {
                targetScreen ??= screens.FirstOrDefault();
            }

            if (targetScreen != null)
            {
                var x = targetScreen.Bounds.Left + Profile.WindowX;
                var y = targetScreen.Bounds.Top + Profile.WindowY;

                Logger.Debug("SetWindowPositionRelativeToScreen targetScreen={DeviceName} x={X} y={Y}",
                    targetScreen.DeviceName, x, y);
                ScreenHelper.MoveWindowPhysical(this, (int)x, (int)y);
            }
            else if (IsVisible)
            {
                Hide();
            }
        }

        private void DisplayWindow_PositionChanged(object? sender, PixelPointEventArgs e)
        {
            // Position tracking for after drag operations
        }

        public void Fullscreen()
        {
            Dispatcher.UIThread.Post(() =>
            {
                var screen = ScreenHelper.GetWindowScreen(this);
                if (screen != null)
                {
                    Profile.WindowX = 0;
                    Profile.WindowY = 0;
                    Profile.Width = (int)screen.Bounds.Width;
                    Profile.Height = (int)screen.Bounds.Height;
                }
            });
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);

            if (SharedModel.Instance.SelectedVisibleItems != null)
            {
                foreach (var displayItem in SharedModel.Instance.SelectedVisibleItems)
                {
                    switch (e.Key)
                    {
                        case Key.Up:
                            displayItem.Y -= SharedModel.Instance.MoveValue;
                            break;
                        case Key.Down:
                            displayItem.Y += SharedModel.Instance.MoveValue;
                            break;
                        case Key.Left:
                            displayItem.X -= SharedModel.Instance.MoveValue;
                            break;
                        case Key.Right:
                            displayItem.X += SharedModel.Instance.MoveValue;
                            break;
                    }
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

            if (!_dragStart)
            {
                var inSelectionBounds = false;
                foreach (var displayItem in SharedModel.Instance.SelectedVisibleItems)
                {
                    if (displayItem.ContainsPoint(skPoint))
                    {
                        inSelectionBounds = true;
                        break;
                    }
                }

                if (!inSelectionBounds)
                {
                    foreach (var selectedItem in SharedModel.Instance.SelectedVisibleItems)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            selectedItem.Selected = false;
                        });
                    }
                }
            }

            if (SharedModel.Instance.SelectedVisibleItems.Count == 0)
            {
                if (Profile.Drag)
                {
                    // Begin window drag
                    BeginMoveDrag(e);

                    // After drag completes, update position relative to screen
                    var screen = ScreenHelper.GetWindowScreen(this);
                    if (screen != null)
                    {
                        var windowPos = ScreenHelper.GetWindowPositionPhysical(this);
                        var relativePosition = ScreenHelper.GetWindowRelativePosition(screen, windowPos);

                        Logger.Debug("SetPosition screen={DeviceName} position={Position} relativePosition={RelativePosition}",
                            screen.DeviceName, windowPos, relativePosition);

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

                foreach (var item in SharedModel.Instance.SelectedVisibleItems)
                {
                    item.MouseOffset = new SKPoint((float)(_startPosition.X - item.X), (float)(_startPosition.Y - item.Y));
                }

                _dragStart = true;
            }
        }

        private void HandleMiddlePress(PointerPressedEventArgs e)
        {
            if (SharedModel.Instance.SelectedProfile != Profile)
            {
                return;
            }

            var position = e.GetPosition(this);
            var skPoint = new SKPoint((float)position.X, (float)position.Y);
            DisplayItem? clickedItem = null;

            var displayItems = SharedModel.Instance.GetProfileDisplayItemsCopy(Profile).ToList();
            displayItems.Reverse();

            foreach (var item in displayItems)
            {
                if (item.Hidden) continue;

                if (item is GroupDisplayItem groupDisplayItem)
                {
                    var groupDisplayItems = groupDisplayItem.DisplayItemsCopy.ToList();
                    groupDisplayItems.Reverse();
                    foreach (var groupItem in groupDisplayItems)
                    {
                        if (groupItem.Hidden) continue;

                        if (groupItem.ContainsPoint(skPoint))
                        {
                            clickedItem = groupItem;
                            break;
                        }
                    }

                    if (clickedItem == null)
                    {
                        continue;
                    }
                }

                if (clickedItem != null)
                {
                    break;
                }

                if (item.ContainsPoint(skPoint))
                {
                    clickedItem = item;
                    break;
                }
            }

            var keyModifiers = e.KeyModifiers;

            if (clickedItem != null)
            {
                if (!keyModifiers.HasFlag(KeyModifiers.Control) && !keyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        SharedModel.Instance.SelectedItem = clickedItem;
                    });
                }

                Dispatcher.UIThread.Post(() =>
                {
                    clickedItem.Selected = true;
                });
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    SharedModel.Instance.SelectedItem = null;
                });
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

            if (_dragStart)
            {
                if (SharedModel.Instance.SelectedVisibleItems.Count == 0)
                {
                    _dragStart = false;
                    return;
                }

                var gridSize = SharedModel.Instance.MoveValue;
                var currentPosition = e.GetPosition(this);

                foreach (var displayItem in SharedModel.Instance.SelectedVisibleItems)
                {
                    if (displayItem.Selected && !displayItem.IsLocked)
                    {
                        int x = (int)(currentPosition.X - displayItem.MouseOffset.X);
                        int y = (int)(currentPosition.Y - displayItem.MouseOffset.Y);

                        x = (int)(Math.Round((double)x / gridSize) * gridSize);
                        y = (int)(Math.Round((double)y / gridSize) * gridSize);

                        displayItem.X = x;
                        displayItem.Y = y;
                    }
                }

                _startPosition = currentPosition;
            }
        }
    }
}
