using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Utilities.Common
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => (bool)value ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => (Visibility)value == Visibility.Visible;
    }
}
