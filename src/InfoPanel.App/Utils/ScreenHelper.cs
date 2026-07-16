using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using SkiaSharp;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.Utils
{
    public class ScreenHelper
    {
        public static void MoveWindowPhysical(Window window, int x, int y)
        {
            window.Position = new PixelPoint(x, y);
        }

        public static SKPoint GetWindowPositionPhysical(Window window)
        {
            return new SKPoint(window.Position.X, window.Position.Y);
        }

        public static MonitorInfo? GetWindowScreen(Window window)
        {
            var position = new SKPoint(window.Position.X, window.Position.Y);
            var monitors = GetAllMonitors(window);

            foreach (var monitor in monitors)
            {
                if (monitor.Bounds.Contains(position))
                {
                    return monitor;
                }
            }

            return monitors
                .OrderBy(m => DistanceSquared(position, m.Bounds))
                .FirstOrDefault();
        }

        private static double DistanceSquared(SKPoint point, SKRect rect)
        {
            int centerX = (int)(rect.Left + rect.Width / 2);
            int centerY = (int)(rect.Top + rect.Height / 2);
            int dx = (int)(centerX - point.X);
            int dy = (int)(centerY - point.Y);
            return dx * dx + dy * dy;
        }

        public static Point GetWindowRelativePosition(MonitorInfo screen, SKPoint absolutePosition)
        {
            var relativeX = absolutePosition.X - (int)screen.Bounds.Left;
            var relativeY = absolutePosition.Y - (int)screen.Bounds.Top;

            return new Point(relativeX, relativeY);
        }

        public static List<MonitorInfo> GetAllMonitors(Window window)
        {
            var monitors = new List<MonitorInfo>();

            var screens = window.Screens;
            if (screens == null) return monitors;

            foreach (var screen in screens.All)
            {
                monitors.Add(FromAvaloniaScreen(screen));
            }

            return monitors;
        }

        public static MonitorInfo FromAvaloniaScreen(Screen screen)
        {
            return new MonitorInfo
            {
                DeviceName = screen.DisplayName ?? $"Screen-{screen.GetHashCode()}",
                Bounds = SKRect.Create(
                    screen.Bounds.X,
                    screen.Bounds.Y,
                    screen.Bounds.Width,
                    screen.Bounds.Height),
                WorkingArea = SKRect.Create(
                    screen.WorkingArea.X,
                    screen.WorkingArea.Y,
                    screen.WorkingArea.Width,
                    screen.WorkingArea.Height),
                IsPrimary = screen.IsPrimary
            };
        }
    }

    public class MonitorInfo
    {
        public string? DeviceName { get; set; }
        public SKRect Bounds { get; set; }
        public SKRect WorkingArea { get; set; }
        public bool IsPrimary { get; set; }

        public override string ToString()
        {
            return $"Monitor: {DeviceName}, Bounds={Bounds}, WorkingArea={WorkingArea}, Primary={IsPrimary}";
        }
    }
}
