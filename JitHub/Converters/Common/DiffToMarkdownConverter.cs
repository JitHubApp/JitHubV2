using System;
using Microsoft.UI.Xaml.Data;

namespace JitHub.Converters.Common
{
    class DiffToMarkdownConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var diff = value as string;
            return "```diff\n" + diff + "\n```";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
