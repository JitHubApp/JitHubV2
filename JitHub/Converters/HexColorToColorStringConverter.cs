using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace JitHub.Converters
{
    class HexColorToColorStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var colorString = (string)value + "FF";
            var r = (byte)System.Convert.ToUInt32(colorString.Substring(0, 2), 16);
            var g = (byte)System.Convert.ToUInt32(colorString.Substring(2, 2), 16);
            var b = (byte)System.Convert.ToUInt32(colorString.Substring(4, 2), 16);
            var a = (byte)System.Convert.ToUInt32(colorString.Substring(6, 2), 16);
            var myBrush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            return myBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
