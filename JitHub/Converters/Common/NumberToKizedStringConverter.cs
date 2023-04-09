using System;
using Microsoft.UI.Xaml.Data;

namespace JitHub.Converters.Common
{
    public class NumberToKizedStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var num = (int)value;
            return $"{(num >= 1000 ? num / 1000 : num)}{(num >= 1000 ? "k" : "")}";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
