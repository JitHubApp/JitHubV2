using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace JitHub.WinUI.Converters.Common
{
    public partial class StringToImageSourceConverter : IValueConverter
    {
        private static readonly BitmapImage FallbackImage = new(new Uri("ms-appx:///Assets/Octocat.png"));

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string url && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                return new BitmapImage(uri);
            }

            return FallbackImage;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}


