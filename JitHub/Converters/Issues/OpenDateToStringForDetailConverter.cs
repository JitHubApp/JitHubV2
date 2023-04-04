using System;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters.Issues
{
    class OpenDateToStringForDetailConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var dateCreated = (DateTimeOffset)value;
            var param = parameter as string;
            return String.Format("opened this {0} on {1}", param, dateCreated.ToString("MMM dd, yyyy"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
