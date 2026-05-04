using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Converters.Activities
{
    public partial class RefFullStringToBranchConverter : IValueConverter
    {
        public static string ConvertFromRefToBranch(string? reference)
        {
            if (!string.IsNullOrEmpty(reference) && reference.StartsWith("refs/heads/"))
                return reference.Substring(11);
            return reference ?? string.Empty;
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


