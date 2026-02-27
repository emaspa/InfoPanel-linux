using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace InfoPanel.Views.Converters;

public class FontFamilyConverter : IValueConverter
{
    public static readonly FontFamilyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string fontName && !string.IsNullOrWhiteSpace(fontName))
        {
            return new FontFamily(fontName);
        }
        return FontFamily.Default;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is FontFamily family)
        {
            return family.Name;
        }
        return "Default";
    }
}
