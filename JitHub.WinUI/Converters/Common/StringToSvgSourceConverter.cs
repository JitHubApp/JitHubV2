using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Security.Cryptography;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace JitHub.WinUI.Converters.Common
{
    public partial class StringToSvgSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var svg = new SvgImageSource();
            if (value is not string svgText || string.IsNullOrWhiteSpace(svgText))
            {
                return svg;
            }

            try
            {
                var svgBuffer = CryptographicBuffer.ConvertStringToBinary(svgText, BinaryStringEncoding.Utf8);

                using (var stream = svgBuffer.AsStream())
                {
                    svg.SetSourceAsync(stream.AsRandomAccessStream()).AsTask().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {

            }

            return svg;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}

