using InfoPanel.Models;
using SkiaSharp;
using System.Collections.Immutable;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Ambient state the renderer needs from the hosting application: which display items
    /// a profile currently has, whether designer chrome (grid, selection) should draw,
    /// and pacing settings. The app/store layer configures this at startup; the defaults
    /// give a clean headless render (no grid, nothing selected, v1 default rates).
    /// </summary>
    public static class RenderContext
    {
        /// <summary>Returns the current display item list for a profile (a stable snapshot).</summary>
        public static Func<Profile, ImmutableList<DisplayItem>> GetDisplayItems { get; set; } =
            static profile => ConfigPersistenceItemsFallback(profile);

        /// <summary>True when the profile is the one open in the designer (draws selection chrome).</summary>
        public static Func<Profile, bool> IsSelectedProfile { get; set; } = static _ => false;

        public static bool ShowGridLines { get; set; }
        public static int GridLinesSpacing { get; set; } = 20;
        public static string GridLinesColor { get; set; } = "#1A808080";
        public static string SelectedItemColor { get; set; } = "#FF00FF00";

        /// <summary>Graph history sampling interval in ms (v1 Settings.TargetGraphUpdateRate).</summary>
        public static int TargetGraphUpdateRate { get; set; } = 1000;

        /// <summary>Target render frame rate (v1 Settings.TargetFrameRate); used for graph interpolation.</summary>
        public static int TargetFrameRate { get; set; } = 15;

        private static readonly Dictionary<Guid, ImmutableList<DisplayItem>> _fallbackCache = [];
        private static readonly Lock _fallbackLock = new();

        private static ImmutableList<DisplayItem> ConfigPersistenceItemsFallback(Profile profile)
        {
            // Headless fallback: load once from disk. Hosts with live editing replace this.
            lock (_fallbackLock)
            {
                if (!_fallbackCache.TryGetValue(profile.Guid, out var items))
                {
                    items = [.. Persistence.ConfigPersistence.LoadDisplayItems(profile)];
                    _fallbackCache[profile.Guid] = items;
                }

                return items;
            }
        }
    }

    /// <summary>
    /// Wires the rendering implementations into InfoPanel.Core's seams
    /// (text measurement and image cache). Call once at host startup.
    /// </summary>
    public static class RenderingServices
    {
        public static void Register()
        {
            TextMeasure.Configure(static (text, fontScale, fontName, fontStyle, fontSize,
                bold, italic, underline, strikeout, wrap, ellipsis, width, height) =>
                SkiaGraphics.FromEmpty(fontScale).MeasureString(
                    text, fontName, fontStyle, fontSize, bold, italic, underline, strikeout,
                    wrap, ellipsis, width, height));

            ImageCacheHook.GetImageSize = static (item, initialiseIfMissing) =>
                Cache.GetLocalImage(item, initialiseIfMissing) is LockedImage image
                    ? new SKSize(image.Width, image.Height)
                    : null;

            ImageCacheHook.InvalidateImage = static path => Cache.InvalidateImage(path);
        }
    }
}
