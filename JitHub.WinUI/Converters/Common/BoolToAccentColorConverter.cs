using System;
using Windows.UI.ViewManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Converters.Common
{
    public partial class BoolToAccentColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not bool isTrue)
            {
                return DependencyProperty.UnsetValue;
            }

            var uiSettings = new UISettings();
            var accentColor = uiSettings.GetColorValue(UIColorType.Accent);
            return isTrue ? new SolidColorBrush(accentColor) : DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}


