using System.Drawing;
using MarkdownRenderer.Sample.Automation.Uia;
using Xunit;

namespace MarkdownRenderer.Sample.Automation.Tests.Uia;

public sealed class HighContrastVectorPixelsTests
{
    [Fact]
    public void FocusBorderBandIsExcludedFromTheInterior()
    {
        // A cyan 4-pixel border around a black interior: the border alone must
        // not count as Hotlight glyph paint.
        using var bitmap = Filled(20, 20, Color.Black);
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                if (x < 4 || y < 4 || x >= 16 || y >= 16)
                    bitmap.SetPixel(x, y, Color.FromArgb(0, 255, 255));
            }
        }

        var pixels = HighContrastVectorPixels.Measure(bitmap, expectHotlight: true);

        Assert.Equal(4, pixels.Inset);
        Assert.Equal(12, pixels.Width);
        Assert.Equal(12, pixels.Height);
        Assert.Equal(144, pixels.PixelCount);
        Assert.Equal(144, pixels.SurfacePixels);
        Assert.Equal(0, pixels.SemanticPixels);
    }

    [Fact]
    public void SmallCapturesUseANarrowerInset()
    {
        using var bitmap = Filled(5, 5, Color.Black);

        var pixels = HighContrastVectorPixels.Measure(bitmap, expectHotlight: false);

        Assert.Equal(1, pixels.Inset);
        Assert.Equal(3, pixels.Width);
        Assert.Equal(9, pixels.SurfacePixels);
    }

    [Fact]
    public void GlyphPixelsMatchTheExpectedSystemColorOnly()
    {
        using var bitmap = Filled(12, 12, Color.Black);
        bitmap.SetPixel(5, 5, Color.FromArgb(0, 250, 250));
        bitmap.SetPixel(6, 5, Color.FromArgb(250, 250, 250));
        bitmap.SetPixel(7, 5, Color.FromArgb(200, 40, 40));

        var hotlight = HighContrastVectorPixels.Measure(bitmap, expectHotlight: true);
        var windowText = HighContrastVectorPixels.Measure(bitmap, expectHotlight: false);

        Assert.Equal(1, hotlight.SemanticPixels);
        Assert.Equal(Color.FromArgb(0, 250, 250).ToArgb(), hotlight.AverageSemantic.ToArgb());
        Assert.Equal(1, windowText.SemanticPixels);
        Assert.Equal(Color.FromArgb(250, 250, 250).ToArgb(), windowText.AverageSemantic.ToArgb());
        Assert.Equal(hotlight.PixelCount - 3, hotlight.SurfacePixels);
        Assert.Equal(Color.Black.ToArgb(), hotlight.AverageSurface.ToArgb());
    }

    private static Bitmap Filled(int width, int height, Color color)
    {
        var bitmap = new Bitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, color);
        }

        return bitmap;
    }
}
