using NumericMath = global::System.Math;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Assertions;

/// <summary>
/// Compares the rendered script pixels with their raster oracles: row and
/// region equality, ink inside or outside a region, and readable pixel
/// dumps for failure messages.
/// </summary>
internal static class ScriptRasterAssertions
{
    internal static void AssertRasterRowsMatch(byte[] actual, byte[] expected, int width)
    {
        Assert.Equal(expected.Length, actual.Length);
        int bytesPerRow = checked(width * 4);
        int rowCount = actual.Length / bytesPerRow;
        bool sawInk = false;
        for (int y = 0; y < rowCount; y++)
        {
            int actualLeft = width;
            int actualRight = -1;
            int expectedLeft = width;
            int expectedRight = -1;
            int rowStart = y * bytesPerRow;
            for (int x = 0; x < width; x++)
            {
                int alphaIndex = rowStart + (x * 4) + 3;
                if (actual[alphaIndex] != 0)
                {
                    actualLeft = NumericMath.Min(actualLeft, x);
                    actualRight = x;
                }

                if (expected[alphaIndex] != 0)
                {
                    expectedLeft = NumericMath.Min(expectedLeft, x);
                    expectedRight = x;
                }
            }

            bool actualHasInk = actualRight >= 0;
            bool expectedHasInk = expectedRight >= 0;
            Assert.True(
                expectedHasInk == actualHasInk,
                $"Ink presence differs at row {y}. Actual row extents: {FormatInkRows(actual, width)}. " +
                $"Expected row extents: {FormatInkRows(expected, width)}.");
            if (!actualHasInk)
                continue;

            sawInk = true;
            Assert.InRange(NumericMath.Abs(actualLeft - expectedLeft), 0, 1);
            Assert.InRange(NumericMath.Abs(actualRight - expectedRight), 0, 1);
        }

        Assert.True(sawInk, "The independent native text-layout raster contained no ink.");
    }

    internal static void AssertPixelsMatchWithinRegion(
        byte[] actual,
        byte[] expected,
        int width,
        Rect region)
    {
        Assert.Equal(expected.Length, actual.Length);
        int height = actual.Length / checked(width * 4);
        int startX = NumericMath.Clamp((int)NumericMath.Floor(region.Left) - 1, 0, width);
        int endX = NumericMath.Clamp((int)NumericMath.Ceiling(region.Right) + 1, 0, width);
        int startY = NumericMath.Clamp((int)NumericMath.Floor(region.Top) - 1, 0, height);
        int endY = NumericMath.Clamp((int)NumericMath.Ceiling(region.Bottom) + 1, 0, height);
        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                int pixel = checked((y * width + x) * 4);
                for (int channel = 0; channel < 4; channel++)
                {
                    int delta = NumericMath.Abs(actual[pixel + channel] - expected[pixel + channel]);
                    Assert.True(
                        delta <= 1,
                        $"Raster differs at ({x},{y}) channel {channel}: actual={actual[pixel + channel]}, " +
                        $"expected={expected[pixel + channel]}, delta={delta}. " +
                        $"Actual region bytes={FormatRegionPixels(actual, width, region)}; " +
                        $"expected region bytes={FormatRegionPixels(expected, width, region)}.");
                }
            }
        }
    }

    internal static string FormatInkRows(byte[] pixels, int width)
    {
        var rows = new List<string>();
        int bytesPerRow = checked(width * 4);
        for (int y = 0; y < pixels.Length / bytesPerRow; y++)
        {
            int left = width;
            int right = -1;
            for (int x = 0; x < width; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] == 0)
                    continue;
                left = NumericMath.Min(left, x);
                right = x;
            }

            if (right >= 0)
                rows.Add($"{y}:{left}-{right}");
        }

        return string.Join(",", rows);
    }

    internal static string FormatRegionPixels(byte[] pixels, int width, Rect region)
    {
        int height = pixels.Length / checked(width * 4);
        int startX = NumericMath.Clamp((int)NumericMath.Floor(region.Left) - 1, 0, width - 1);
        int endX = NumericMath.Clamp((int)NumericMath.Ceiling(region.Right) + 1, 0, width);
        int startY = NumericMath.Clamp((int)NumericMath.Floor(region.Top) - 1, 0, height - 1);
        int endY = NumericMath.Clamp((int)NumericMath.Ceiling(region.Bottom) + 1, 0, height);
        var values = new List<string>();
        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                int pixel = checked((y * width + x) * 4);
                if (pixels[pixel + 3] == 0)
                    continue;
                values.Add($"{x},{y}={pixels[pixel]}/{pixels[pixel + 1]}/{pixels[pixel + 2]}/{pixels[pixel + 3]}");
            }
        }

        return string.Join(";", values);
    }

    internal static void AssertInkInRegion(byte[] pixels, int width, Rect region)
    {
        int startX = NumericMath.Clamp((int)NumericMath.Floor(region.Left), 0, width - 1);
        int endX = NumericMath.Clamp((int)NumericMath.Ceiling(region.Right), 0, width);
        int startY = NumericMath.Max(0, (int)NumericMath.Floor(region.Top));
        int endY = NumericMath.Max(startY + 1, (int)NumericMath.Ceiling(region.Bottom));
        int pixelHeight = pixels.Length / (width * 4);
        endY = NumericMath.Min(endY, pixelHeight);
        int ink = 0;
        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] != 0)
                    ink++;
            }
        }

        Assert.True(ink > 0, $"Expected rendered ink inside independent region {region}.");
    }
}
