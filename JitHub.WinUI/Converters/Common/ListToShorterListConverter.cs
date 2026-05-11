using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Converters.Common
{
    public partial class ListToShorterListConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var arr = value as IEnumerable<object> ?? Array.Empty<object>();
            int amount = 0;
            _ = int.TryParse(parameter?.ToString(), out amount);
            return amount <= 0 ? new List<object>() : arr.Take(amount).ToList();
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}


