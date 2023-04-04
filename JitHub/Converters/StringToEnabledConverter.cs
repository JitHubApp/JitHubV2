using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters
{
    class StringToEnabledConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var stringValue = string.Empty;
            if (value != null)
            {
                stringValue = (string)value;
            }
            return !String.IsNullOrWhiteSpace(stringValue);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
