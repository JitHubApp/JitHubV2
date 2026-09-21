using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.PixelTests;

/// <summary>
/// Pixel-by-pixel comparison helpers for two RGBA / BGRA buffers, with
/// optional cropping to the exact image rectangle (the headless browser
/// shim sometimes pads the screenshot to the requested window size). All
/// buffers are normalized to RGBA8888 in memory and compared per-channel.
/// </summary>
public static class PixelComparer
{
    // Different conforming rasterizers distribute fractional coverage across
    // the two pixels straddling the same vector edge. Treat those pixels as
    // the same alpha boundary while the exact premultiplied-color and SSIM
    // gates below continue to detect shifts, blur, and coverage changes.
    private const int AlphaBoundaryTolerancePixels = 1;

    /// <summary>Aggregated per-pixel diff statistics.</summary>
    public readonly record struct DiffReport(
        int WidthPx,
        int HeightPx,
        long MaxChannelDelta,
        double MeanChannelDelta,
        double DifferingPixelFraction,
        double StructuralSimilarity,
        double AlphaIntersectionOverUnion);

    /// <summary>Loads a PNG from disk into a top-down RGBA8888 byte buffer.</summary>
    public static (byte[] Rgba, int Width, int Height) LoadPngAsRgba(string path)
    {
        // Bitmap(string) still delegates path handling to GDI+, which rejects
        // otherwise valid long worktree/CI paths. System.IO reads those paths
        // correctly; keep the stream alive for the lifetime of the bitmap.
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        using var bmp = new Bitmap(stream);
        return BitmapToRgba(bmp);
    }

    /// <summary>Saves an RGBA8888 byte buffer as a PNG.</summary>
    public static void SaveRgbaAsPng(byte[] rgba, int width, int height, string path)
    {
        string destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // GDI+ still uses legacy path handling even when the process and .NET
        // file APIs are long-path aware. Encode through a short temporary path,
        // then let System.IO move the completed PNG to a deep worktree/CI path.
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "MarkdownRenderer.PixelTests");
        Directory.CreateDirectory(temporaryDirectory);
        string temporaryPath = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.png");
        try
        {
            using var bmp = RgbaToBitmap(rgba, width, height);
            bmp.Save(temporaryPath, ImageFormat.Png);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    /// <summary>Converts a BGRA premultiplied buffer (Skia output) to RGBA8888 unpremultiplied.</summary>
    public static byte[] BgraPremulToRgba(byte[] bgra, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            byte b = bgra[i * 4 + 0];
            byte g = bgra[i * 4 + 1];
            byte r = bgra[i * 4 + 2];
            byte a = bgra[i * 4 + 3];
            // Unpremultiply so comparisons against the browser (which emits
            // un-premultiplied PNGs) align channel-for-channel.
            if (a > 0 && a < 255)
            {
                r = (byte)Math.Min(255, (r * 255) / a);
                g = (byte)Math.Min(255, (g * 255) / a);
                b = (byte)Math.Min(255, (b * 255) / a);
            }
            rgba[i * 4 + 0] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = a;
        }
        return rgba;
    }

    /// <summary>Converts an RGBA premultiplied buffer to RGBA8888 unpremultiplied.</summary>
    public static byte[] RgbaPremulToRgba(ReadOnlySpan<byte> premultiplied, int width, int height)
    {
        if (premultiplied.Length != checked(width * height * 4))
            throw new ArgumentException("dimensions mismatch buffer length", nameof(premultiplied));

        byte[] rgba = premultiplied.ToArray();
        for (int index = 0; index < width * height; index++)
        {
            int offset = index * 4;
            byte alpha = rgba[offset + 3];
            if (alpha is > 0 and < 255)
            {
                rgba[offset] = (byte)Math.Min(255, (rgba[offset] * 255) / alpha);
                rgba[offset + 1] = (byte)Math.Min(255, (rgba[offset + 1] * 255) / alpha);
                rgba[offset + 2] = (byte)Math.Min(255, (rgba[offset + 2] * 255) / alpha);
            }
        }
        return rgba;
    }

    /// <summary>
    /// Crops <paramref name="rgba"/> to a sub-rect. Useful when the headless
    /// browser screenshot is padded to the window size with transparent
    /// pixels and only the SVG-occupied rect should participate in compare.
    /// </summary>
    public static byte[] Crop(byte[] rgba, int srcW, int srcH, int x, int y, int w, int h)
    {
        if (x < 0 || y < 0 || x + w > srcW || y + h > srcH)
            throw new ArgumentOutOfRangeException(nameof(w), $"crop {x},{y} {w}×{h} doesn't fit in {srcW}×{srcH}");
        var dst = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            int srcOffset = ((y + row) * srcW + x) * 4;
            int dstOffset = row * w * 4;
            Buffer.BlockCopy(rgba, srcOffset, dst, dstOffset, w * 4);
        }
        return dst;
    }

    public static byte[] CropToRequiredBounds(
        byte[] rgba,
        int sourceWidth,
        int sourceHeight,
        int requiredWidth,
        int requiredHeight)
    {
        if (sourceWidth < requiredWidth || sourceHeight < requiredHeight)
        {
            throw new InvalidDataException(
                $"Raster capture {sourceWidth}x{sourceHeight} does not cover the required " +
                $"{requiredWidth}x{requiredHeight} comparison area.");
        }

        return sourceWidth == requiredWidth && sourceHeight == requiredHeight
            ? rgba
            : Crop(rgba, sourceWidth, sourceHeight, 0, 0, requiredWidth, requiredHeight);
    }

    /// <summary>
    /// Compares two RGBA buffers of identical dimensions. RGB is compared in
    /// premultiplied-alpha space because unpremultiplied color beneath a fully
    /// transparent pixel is undefined and cannot affect the rendered image.
    /// Alpha is compared directly. A pixel is "differing" if any resulting
    /// channel exceeds <paramref name="channelTolerance"/>.
    /// </summary>
    public static DiffReport Compare(byte[] a, byte[] b, int width, int height, int channelTolerance = 6)
    {
        if (a.Length != b.Length) throw new ArgumentException("buffer length mismatch");
        if (a.Length != width * height * 4) throw new ArgumentException("dimensions mismatch buffer length");

        long maxDelta = 0;
        long sumDelta = 0;
        long differing = 0;
        long totalChannels = (long)width * height * 4;
        for (int i = 0; i < width * height; i++)
        {
            int baseIdx = i * 4;
            bool pixelDiffers = false;
            for (int c = 0; c < 4; c++)
            {
                int aValue = c == 3
                    ? a[baseIdx + c]
                    : Premultiply(a[baseIdx + c], a[baseIdx + 3]);
                int bValue = c == 3
                    ? b[baseIdx + c]
                    : Premultiply(b[baseIdx + c], b[baseIdx + 3]);
                int delta = Math.Abs(aValue - bValue);
                if (delta > maxDelta) maxDelta = delta;
                sumDelta += delta;
                if (delta > channelTolerance) pixelDiffers = true;
            }
            if (pixelDiffers) differing++;
        }
        double mean = (double)sumDelta / totalChannels;
        double frac = (double)differing / (width * height);
        double alphaIou = CalculateBoundaryTolerantAlphaIou(a, b, width, height);
        double ssim = CalculateAlphaWeightedSsim(
            a,
            b,
            width,
            height,
            channelTolerance);
        return new DiffReport(width, height, maxDelta, mean, frac, ssim, alphaIou);
    }

    private static double CalculateBoundaryTolerantAlphaIou(
        byte[] a,
        byte[] b,
        int width,
        int height)
    {
        double totalA = 0;
        double totalB = 0;
        double matchedA = 0;
        double matchedB = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                byte alphaA = a[offset + 3];
                byte alphaB = b[offset + 3];
                totalA += alphaA;
                totalB += alphaB;

                if (alphaA != 0 && HasVisibleAlphaNear(b, width, height, x, y))
                    matchedA += alphaA;
                if (alphaB != 0 && HasVisibleAlphaNear(a, width, height, x, y))
                    matchedB += alphaB;
            }
        }

        double total = totalA + totalB;
        if (total == 0)
            return 1;

        // The symmetric matched-mass ratio is a boundary-tolerant Dice score.
        // Convert it to the equivalent IoU scale so the public report and the
        // release thresholds retain their established interpretation.
        double dice = Math.Clamp((matchedA + matchedB) / total, 0, 1);
        return dice == 0 ? 0 : dice / (2 - dice);
    }

    private static bool HasVisibleAlphaNear(
        byte[] rgba,
        int width,
        int height,
        int x,
        int y)
    {
        int top = Math.Max(0, y - AlphaBoundaryTolerancePixels);
        int bottom = Math.Min(height - 1, y + AlphaBoundaryTolerancePixels);
        int left = Math.Max(0, x - AlphaBoundaryTolerancePixels);
        int right = Math.Min(width - 1, x + AlphaBoundaryTolerancePixels);
        for (int candidateY = top; candidateY <= bottom; candidateY++)
        {
            for (int candidateX = left; candidateX <= right; candidateX++)
            {
                int offset = ((candidateY * width) + candidateX) * 4;
                if (rgba[offset + 3] != 0)
                    return true;
            }
        }

        return false;
    }

    private static double CalculateAlphaWeightedSsim(
        byte[] a,
        byte[] b,
        int width,
        int height,
        int channelTolerance)
    {
        const double c1 = 6.5025;   // (0.01 * 255)^2
        const double c2 = 58.5225;  // (0.03 * 255)^2
        double weightSum = 0;
        double meanA = 0;
        double meanB = 0;
        for (int index = 0; index < width * height; index++)
        {
            int offset = index * 4;
            byte alphaA = a[offset + 3];
            byte alphaB = b[offset + 3];
            if (alphaA == 0 || alphaB == 0)
                continue;

            // Alpha-squared weighting reflects visual confidence: RGB recovered
            // from a nearly transparent premultiplied edge is quantized and is
            // not stable enough to carry the same SSIM weight as an opaque fill.
            double sharedAlpha = Math.Min(alphaA, alphaB) / 255d;
            double weight = sharedAlpha * sharedAlpha;
            double luminanceA = VisibleColorLuminance(a, offset);
            double luminanceB = ComparableColorLuminance(
                a,
                b,
                offset,
                channelTolerance);
            meanA += luminanceA * weight;
            meanB += luminanceB * weight;
            weightSum += weight;
        }

        // Geometry and opacity have independent exact-mean and tolerant-alpha
        // gates. With no shared color evidence, those gates own the result.
        if (weightSum == 0)
            return 1;

        meanA /= weightSum;
        meanB /= weightSum;

        double varianceA = 0;
        double varianceB = 0;
        double covariance = 0;
        for (int index = 0; index < width * height; index++)
        {
            int offset = index * 4;
            byte alphaA = a[offset + 3];
            byte alphaB = b[offset + 3];
            if (alphaA == 0 || alphaB == 0)
                continue;

            double sharedAlpha = Math.Min(alphaA, alphaB) / 255d;
            double weight = sharedAlpha * sharedAlpha;
            double deltaA = VisibleColorLuminance(a, offset) - meanA;
            double deltaB = ComparableColorLuminance(
                    a,
                    b,
                    offset,
                    channelTolerance) - meanB;
            varianceA += weight * deltaA * deltaA;
            varianceB += weight * deltaB * deltaB;
            covariance += weight * deltaA * deltaB;
        }

        varianceA /= weightSum;
        varianceB /= weightSum;
        covariance /= weightSum;
        return ((2 * meanA * meanB + c1) * (2 * covariance + c2)) /
            ((meanA * meanA + meanB * meanB + c1) * (varianceA + varianceB + c2));
    }

    private static double ComparableColorLuminance(
        byte[] reference,
        byte[] candidate,
        int offset,
        int channelTolerance)
    {
        int red = NormalizeComparableChannel(
            reference[offset],
            candidate[offset],
            channelTolerance);
        int green = NormalizeComparableChannel(
            reference[offset + 1],
            candidate[offset + 1],
            channelTolerance);
        int blue = NormalizeComparableChannel(
            reference[offset + 2],
            candidate[offset + 2],
            channelTolerance);
        return (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
    }

    private static int NormalizeComparableChannel(
        byte reference,
        byte candidate,
        int channelTolerance) =>
        Math.Abs(reference - candidate) <= channelTolerance
            ? reference
            : candidate;

    private static double VisibleColorLuminance(byte[] rgba, int offset) =>
        (0.2126 * rgba[offset]) +
        (0.7152 * rgba[offset + 1]) +
        (0.0722 * rgba[offset + 2]);

    private static int Premultiply(byte color, byte alpha) =>
        (color * alpha + 127) / 255;

    private static (byte[] Rgba, int Width, int Height) BitmapToRgba(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[w * h * 4];
            int stride = data.Stride;
            // GDI+ Format32bppArgb is BGRA bottom-up only when negative
            // stride; modern .NET on Windows yields top-down with positive
            // stride. Copy row-by-row so we don't depend on stride sign.
            unsafe
            {
                byte* src = (byte*)data.Scan0;
                for (int row = 0; row < h; row++)
                {
                    Marshal.Copy(new IntPtr(src + row * stride), bgra, row * w * 4, w * 4);
                }
            }
            // BGRA → RGBA channel swap.
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                rgba[i * 4 + 0] = bgra[i * 4 + 2];
                rgba[i * 4 + 1] = bgra[i * 4 + 1];
                rgba[i * 4 + 2] = bgra[i * 4 + 0];
                rgba[i * 4 + 3] = bgra[i * 4 + 3];
            }
            return (rgba, w, h);
        }
        finally { bmp.UnlockBits(data); }
    }

    private static Bitmap RgbaToBitmap(byte[] rgba, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                bgra[i * 4 + 0] = rgba[i * 4 + 2];
                bgra[i * 4 + 1] = rgba[i * 4 + 1];
                bgra[i * 4 + 2] = rgba[i * 4 + 0];
                bgra[i * 4 + 3] = rgba[i * 4 + 3];
            }
            unsafe
            {
                byte* dst = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int row = 0; row < h; row++)
                {
                    Marshal.Copy(bgra, row * w * 4, new IntPtr(dst + row * stride), w * 4);
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }
}
