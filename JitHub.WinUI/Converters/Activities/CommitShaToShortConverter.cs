using System;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Converters.Activities
{
    public partial class CommitShaToShortConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var sha = value as string;
            return string.IsNullOrWhiteSpace(sha)
                ? string.Empty
                : sha[..Math.Min(7, sha.Length)];
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}


