using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Globalization;

namespace InfoPanel.Utils
{
    public static class TreeViewHelpers
    {
        /// <summary>
        /// Makes a single click anywhere on a category row toggle its expansion,
        /// not just the chevron. Wire to the TreeView's Tapped event.
        /// </summary>
        public static void ToggleCategoryOnTap(RoutedEventArgs e)
        {
            if (e.Source is not Control source)
            {
                return;
            }

            // the chevron's ToggleButton already toggles - don't undo it
            if (source.FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>(includeSelf: true) != null)
            {
                return;
            }

            var container = source.FindAncestorOfType<TreeViewItem>(includeSelf: true);
            if (container?.DataContext is ViewModels.SensorTreeItem item && item.Children.Count > 0)
            {
                container.IsExpanded = !container.IsExpanded;
            }
        }
    }

    /// <summary>Two-way #AARRGGBB string ↔ Avalonia Color for ColorPicker bindings.</summary>
    public sealed class ColorHexConverter : IValueConverter
    {
        public static readonly ColorHexConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string hex && Color.TryParse(string.IsNullOrEmpty(hex) ? "#FF000000" : hex, out var color))
            {
                return color;
            }

            return Colors.Black;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Color color)
            {
                return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            }

            return "#FF000000";
        }
    }

    public static class UiCatalog
    {
        public static IReadOnlyList<string> FontFamilies { get; } =
            [.. SkiaSharp.SKFontManager.Default.FontFamilies.OrderBy(f => f)];
    }
}
