using System;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters.Code
{
    public class IsExpandToFolderFilledIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var expanded = (bool)value;
            return expanded ? "\ued44" : "\ued42";//closed: e8d5
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
