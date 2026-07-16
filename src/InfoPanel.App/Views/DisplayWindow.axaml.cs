using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Utils;
using InfoPanel.Views.Controls;
using SkiaSharp;
using System.Timers;

namespace InfoPanel.Views
{
    /// <summary>
    /// Borderless transparent desktop overlay rendering one profile via the Skia lease.
    /// Runs on the X11 backend (XWayland under Wayland sessions) — native Wayland cannot
    /// self-position or click-through.
    /// </summary>
    public partial class DisplayWindow : Window
    {
        public Profile Profile { get; }

        private System.Timers.Timer? _renderTimer;
        private readonly FpsCounter _fpsCounter = new();

        public DisplayWindow(Profile profile)
        {
            Profile = profile;
            DataContext = this;

            InitializeComponent();

            Topmost = profile.Topmost;
            CanResize = false;

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
            PointerPressed += Window_PointerPressed;
            PositionChanged += Window_PositionChanged;
            Profile.PropertyChanged += Profile_PropertyChanged;
        }

        private void Window_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Position = new PixelPoint(Profile.WindowX, Profile.WindowY);
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
            PanelDraw.Run(Profile, skiaGraphics, cacheHint: $"DISPLAY-{Profile.Guid}", fpsCounter: _fpsCounter);
        }

        private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (Profile.Drag && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        private void Window_PositionChanged(object? sender, PixelPointEventArgs e)
        {
            Profile.WindowX = e.Point.X;
            Profile.WindowY = e.Point.Y;
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
