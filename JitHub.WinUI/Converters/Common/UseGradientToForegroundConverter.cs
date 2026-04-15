using System;
using Microsoft.UI;
using Windows.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Converters.Common
{
    public partial class UseGradientToForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var useGradient = (bool)value;
            return useGradient ? new SolidColorBrush(Colors.Black) : new SolidColorBrush(Colors.White);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}


