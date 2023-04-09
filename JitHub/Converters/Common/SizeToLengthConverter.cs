using JitHub.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;

namespace JitHub.Converters.Common
{
    class SizeToLengthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                if (value == null) return 32d;
                switch ((UISize)value)
                {
                    case UISize.SMALL:
                        return 24d;
                    case UISize.MEDIUM:
                        return 32d;
                    case UISize.BIG:
                        return 40d;
                    default:
                        return 32d;
                }
            }
            catch
            {
                return 32d;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
