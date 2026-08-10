using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace JitHub.WinUI.Converters.Common;

public sealed partial class HexColorToSolidBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (TryParse(value as string, out Color color))
        {
            return new SolidColorBrush(color);
        }

        ResourceDictionary? resources = Application.Current?.Resources;
        if (resources is not null && resources.ContainsKey("AppAccentBrush") &&
            resources["AppAccentBrush"] is Brush accentBrush)
        {
            return accentBrush;
        }

        return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static bool TryParse(string? value, out Color color)
    {
        string hex = (value ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte red) &&
            byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte green) &&
            byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte blue))
        {
            color = Color.FromArgb(255, red, green, blue);
            return true;
        }

        color = default;
        return false;
    }
}
