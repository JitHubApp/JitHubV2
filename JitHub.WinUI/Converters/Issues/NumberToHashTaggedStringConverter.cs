using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Converters.Issues
{
    public partial class NumberToHashTaggedStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int intNumber)
            {
                return $"#{intNumber}";
            }

            if (value is long longNumber)
            {
                return $"#{longNumber}";
            }

            return "#0";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}


