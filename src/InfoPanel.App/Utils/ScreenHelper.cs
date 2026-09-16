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
            var name = screen.DisplayName ?? $"Screen-{screen.GetHashCode()}";
            var (nativeW, nativeH) = GetNativeResolution(name);
            var monitor = new MonitorInfo
            {
                DeviceName = name,
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
            // Under XWayland the X11 bounds are a scaled view that shifts with
            // compositor scale settings; the panel's real mode is the stable,
            // user-recognizable identity for labels and matching.
            monitor.NativeWidth = nativeW > 0 ? nativeW : (int)monitor.Bounds.Width;
            monitor.NativeHeight = nativeH > 0 ? nativeH : (int)monitor.Bounds.Height;
            return monitor;
        }

        private static readonly Dictionary<string, (int W, int H)> _nativeResolutionCache = new();

        /// <summary>
        /// Native resolution of a connected output by connector name (e.g. DP-5),
        /// read from DRM sysfs (/sys/class/drm/card*-NAME/modes, preferred mode
        /// first). Returns (0, 0) when unavailable (non-DRM systems, name mismatch).
        /// </summary>
        public static (int Width, int Height) GetNativeResolution(string? name)
        {
            if (string.IsNullOrEmpty(name)) return (0, 0);
            lock (_nativeResolutionCache)
            {
                if (_nativeResolutionCache.TryGetValue(name, out var cached)) return cached;

                var result = (0, 0);
                try
                {
                    foreach (var dir in System.IO.Directory.GetDirectories("/sys/class/drm"))
                    {
                        var baseName = System.IO.Path.GetFileName(dir);
                        var dash = baseName.IndexOf('-');
                        if (dash < 0 || baseName[(dash + 1)..] != name) continue;

                        var statusFile = System.IO.Path.Combine(dir, "status");
                        if (!System.IO.File.Exists(statusFile)
                            || System.IO.File.ReadAllText(statusFile).Trim() != "connected")
                        {
                            continue;
                        }

                        var modes = System.IO.Path.Combine(dir, "modes");
                        var first = System.IO.File.Exists(modes)
                            ? System.IO.File.ReadLines(modes).FirstOrDefault()
                            : null;
                        var parts = first?.Split('x');
                        if (parts is { Length: 2 }
                            && int.TryParse(parts[0], out var w)
                            && int.TryParse(parts[1], out var h))
                        {
                            result = (w, h);
                            break;
                        }
                    }
                }
                catch
                {
                    // sysfs unavailable: fall back to bounds-sized identity
                }

                _nativeResolutionCache[name] = result;
                return result;
            }
        }

        /// <summary>
        /// Resolves a stored TargetWindow to a current monitor. Identity is the
        /// output name plus the native resolution; stored dimensions equal to the
        /// current (possibly scaled) bounds are accepted for assignments saved by
        /// older versions. Non-strict matching falls back to name only, then any
        /// resolution match.
        /// </summary>
        public static MonitorInfo? MatchTargetWindow(Models.TargetWindow target, List<MonitorInfo> monitors, bool strict)
        {
            var match = monitors.FirstOrDefault(m => m.DeviceName == target.DeviceName
                    && m.NativeWidth == target.Width && m.NativeHeight == target.Height)
                ?? monitors.FirstOrDefault(m => m.DeviceName == target.DeviceName
                    && (int)m.Bounds.Width == target.Width && (int)m.Bounds.Height == target.Height);

            if (match != null || strict) return match;

            return monitors.FirstOrDefault(m => m.DeviceName == target.DeviceName)
                ?? monitors.FirstOrDefault(m => m.NativeWidth == target.Width && m.NativeHeight == target.Height)
                ?? monitors.FirstOrDefault(m => (int)m.Bounds.Width == target.Width && (int)m.Bounds.Height == target.Height);
        }

        /// <summary>Assigns a profile's overlay to a monitor, parked at its top-left.</summary>
        public static void AssignTargetWindow(Models.Profile profile, MonitorInfo monitor)
        {
            profile.TargetWindow = new Models.TargetWindow(
                (int)monitor.Bounds.Left, (int)monitor.Bounds.Top,
                monitor.NativeWidth, monitor.NativeHeight,
                monitor.DeviceName ?? string.Empty);
            profile.WindowX = 0;
            profile.WindowY = 0;
        }
    }

    public class MonitorInfo
    {
        public string? DeviceName { get; set; }
        public SKRect Bounds { get; set; }
        public SKRect WorkingArea { get; set; }
        public bool IsPrimary { get; set; }

        /// <summary>Native panel resolution (DRM preferred mode); falls back to bounds.</summary>
        public int NativeWidth { get; set; }
        public int NativeHeight { get; set; }

        /// <summary>User-facing label: output name plus native resolution.</summary>
        public string Label => $"{DeviceName} ({NativeWidth}×{NativeHeight}){(IsPrimary ? " · primary" : "")}";

        public override string ToString()
        {
            return $"Monitor: {DeviceName}, Bounds={Bounds}, WorkingArea={WorkingArea}, Primary={IsPrimary}";
        }
    }
}
