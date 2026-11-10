using System.Drawing;

namespace JitHub.WinUI.Automation.Probes.Theming;

/// <summary>
/// Pixel statistics the theme probes use to compare app chrome colors between screenshots.
/// </summary>
internal static class ThemePalettePixels
{
    internal static (double R, double G, double B) AverageThemePaletteChrome(Bitmap bitmap)
    {
        int left = (int)(bitmap.Width * 0.22);
        int right = (int)(bitmap.Width * 0.28);
        int top = (int)(bitmap.Height * 0.025);
        int bottom = (int)(bitmap.Height * 0.075);
        long red = 0;
        long green = 0;
        long blue = 0;
        long count = 0;

        for (int y = top; y < bottom; y += 3)
        {
            for (int x = left; x < right; x += 3)
            {
                Color color = bitmap.GetPixel(x, y);
                red += color.R;
                green += color.G;
                blue += color.B;
                count++;
            }
        }

        return (red / (double)count, green / (double)count, blue / (double)count);
    }

    internal static int CountSignificantThemePaletteChromeChanges(Bitmap initial, Bitmap live)
    {
        int left = (int)(initial.Width * 0.04);
        int right = (int)(initial.Width * 0.2);
        int top = (int)(initial.Height * 0.2);
        int bottom = (int)(initial.Height * 0.38);
        int changedPixels = 0;

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                Color before = initial.GetPixel(x, y);
                Color after = live.GetPixel(x, y);
                double distance =
                    (Math.Abs(before.R - after.R) +
                     Math.Abs(before.G - after.G) +
                     Math.Abs(before.B - after.B)) / 3d;
                if (distance >= 18)
                {
                    changedPixels++;
                }
            }
        }

        return changedPixels;
    }

    internal static int CountThemePalettePixelsNear(Bitmap bitmap, Color expected)
    {
        int left = (int)(bitmap.Width * 0.04);
        int right = (int)(bitmap.Width * 0.22);
        int top = (int)(bitmap.Height * 0.2);
        int bottom = (int)(bitmap.Height * 0.38);
        int matchingPixels = 0;

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                Color actual = bitmap.GetPixel(x, y);
                if (Math.Abs(actual.R - expected.R) <= 10 &&
                    Math.Abs(actual.G - expected.G) <= 10 &&
                    Math.Abs(actual.B - expected.B) <= 10)
                {
                    matchingPixels++;
                }
            }
        }

        return matchingPixels;
    }

    internal static int CountThemePalettePixelsNearAnySurface(Bitmap bitmap, Color expected)
    {
        int matchingPixels = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color actual = bitmap.GetPixel(x, y);
                if (Math.Abs(actual.R - expected.R) <= 10 &&
                    Math.Abs(actual.G - expected.G) <= 10 &&
                    Math.Abs(actual.B - expected.B) <= 10)
                {
                    matchingPixels++;
                }
            }
        }

        return matchingPixels;
    }

    internal static double AverageRgbDistance(
        (double R, double G, double B) left,
        (double R, double G, double B) right) =>
        (Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B)) / 3d;
}
