using System;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media.Imaging;
using Svg.Skia;
using SkiaSharp;
using System.IO;

namespace JitHub.Utilities.SVG
{
    public class SVGRenderer
    {
        public static async Task<Windows.UI.Xaml.Controls.Image> SvgToImage(string svgString)
        {
            var svg = new SKSvg();
            svg.FromSvg(svgString);
            Windows.UI.Xaml.Controls.Image image = new Windows.UI.Xaml.Controls.Image();
            var width = (double)svg.Picture.CullRect.Width;
            var height = (double)svg.Picture.CullRect.Height;
            using (var bitmap = new SKBitmap((int)svg.Picture.CullRect.Width, (int)svg.Picture.CullRect.Height))
            {
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.DrawPicture(svg.Picture);
                }
                using (var skImage = SKImage.FromBitmap(bitmap))
                using (var data = skImage.Encode(SKEncodedImageFormat.Png, 100))
                {
                    using (var stream = data.AsStream())
                    {
                        var bitmapImage = new BitmapImage();
                        await bitmapImage.SetSourceAsync(stream.AsRandomAccessStream());
                        image.Source = bitmapImage;
                    }
                }
            }
            if (width != 0)
            {
                image.Width = width;
            }
            if (height != 0)
            {
                image.Height = height;
            }
            return image;
        }
    }
}
