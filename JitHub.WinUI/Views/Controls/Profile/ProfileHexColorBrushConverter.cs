using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace JitHub.WinUI.Views.Controls.Profile;

public sealed partial class ProfileHexColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        ProfileColorBrush.Create(value as string, Color.FromArgb(255, 116, 190, 167));

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        Microsoft.UI.Xaml.DependencyProperty.UnsetValue;
}

internal static class ProfileColorBrush
{
    public static SolidColorBrush Create(string? hex, Color fallback) =>
        new(CreateColor(hex, fallback));

    public static Color CreateColor(string? hex, Color fallback)
    {
        string clean = (hex ?? string.Empty).Trim().TrimStart('#');
        if (clean.Length != 6
            || !byte.TryParse(clean.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte red)
            || !byte.TryParse(clean.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte green)
            || !byte.TryParse(clean.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte blue))
        {
            return fallback;
        }

        return Color.FromArgb(255, red, green, blue);
    }
}
