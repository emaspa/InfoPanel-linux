using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace InfoPanel.Views.Converters;

public class ColorStringToColorConverter : IValueConverter
{
    public static readonly ColorStringToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                return Color.Parse(hex);
            }
            catch
            {
                return Colors.Black;
            }
        }
        return Colors.Black;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color color)
        {
            if (color.A == 255)
                return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        return "#000000";
    }
}
