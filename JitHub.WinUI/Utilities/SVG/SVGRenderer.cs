using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace JitHub.Utilities.SVG
{
    public class SVGRenderer
    {
        public static async Task<Image> SvgToImage(string svgString)
        {
            Image image = new();
            SvgImageSource svgImage = new();

            using (InMemoryRandomAccessStream stream = new())
            {
                using DataWriter writer = new(stream);
                writer.WriteBytes(Encoding.UTF8.GetBytes(svgString));
                await writer.StoreAsync();
                writer.DetachStream();
                stream.Seek(0);
                await svgImage.SetSourceAsync(stream);
            }

            ApplySvgDimensions(image, svgString);
            image.Source = svgImage;
            return image;
        }

        private static void ApplySvgDimensions(Image image, string svgString)
        {
            XElement? root = XDocument.Parse(svgString).Root;
            if (root is null)
            {
                return;
            }

            if (TryParseSvgLength(root.Attribute("width")?.Value, out double width) && width > 0)
            {
                image.Width = width;
            }

            if (TryParseSvgLength(root.Attribute("height")?.Value, out double height) && height > 0)
            {
                image.Height = height;
            }

            if ((image.Width > 0 && image.Height > 0) ||
                !TryParseViewBox(root.Attribute("viewBox")?.Value, out double viewBoxWidth, out double viewBoxHeight))
            {
                return;
            }

            if (image.Width <= 0 && viewBoxWidth > 0)
            {
                image.Width = viewBoxWidth;
            }

            if (image.Height <= 0 && viewBoxHeight > 0)
            {
                image.Height = viewBoxHeight;
            }
        }

        private static bool TryParseSvgLength(string? value, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string normalized = value.Trim();
            while (normalized.Length > 0 && !char.IsDigit(normalized[^1]) && normalized[^1] != '.')
            {
                normalized = normalized[..^1];
            }

            return double.TryParse(
                normalized,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result);
        }

        private static bool TryParseViewBox(string? value, out double width, out double height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string[] parts = value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 4 &&
                   double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out width) &&
                   double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out height);
        }
    }
}

