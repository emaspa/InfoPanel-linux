using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace InfoPanel.Utils
{
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
