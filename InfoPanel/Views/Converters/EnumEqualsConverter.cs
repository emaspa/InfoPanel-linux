using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace InfoPanel.Views.Converters;

public class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;

        var enumValue = value.ToString();
        var targetValue = parameter.ToString();
        return string.Equals(enumValue, targetValue, StringComparison.OrdinalIgnoreCase);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter != null)
        {
            return Enum.Parse(targetType, parameter.ToString()!);
        }
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
