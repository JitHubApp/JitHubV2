using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters.Activities
{
    class RefFullStringToBranchConverter : IValueConverter
    {
        public static string ConvertFromRefToBranch(string reference)
        {
            if (!string.IsNullOrEmpty(reference) && reference.StartsWith("refs/heads/"))
                return reference.Substring(11);
            return reference;
        }
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var reference = value as string;
            return ConvertFromRefToBranch(reference);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
