using System;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters.Activities
{
    class CommitShaToShortConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var sha = value as string;
            return sha?.Substring(0, 7);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
