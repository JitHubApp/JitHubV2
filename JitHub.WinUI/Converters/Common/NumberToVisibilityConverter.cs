using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Converters.Common;

public sealed partial class NumberToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isVisible = value is int number && parameter is bool enabled && number > 0 && enabled;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
