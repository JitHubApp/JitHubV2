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
    /// <summary>Aggregated per-pixel diff statistics.</summary>
    public readonly record struct DiffReport(
        int WidthPx,
        int HeightPx,
        long MaxChannelDelta,
        double MeanChannelDelta,
        double DifferingPixelFraction);

    /// <summary>Loads a PNG from disk into a top-down RGBA8888 byte buffer.</summary>
    public static (byte[] Rgba, int Width, int Height) LoadPngAsRgba(string path)
    {
        using var bmp = new Bitmap(path);
        return BitmapToRgba(bmp);
    }

    /// <summary>Saves an RGBA8888 byte buffer as a PNG.</summary>
    public static void SaveRgbaAsPng(byte[] rgba, int width, int height, string path)
    {
        var bmp = RgbaToBitmap(rgba, width, height);
        bmp.Save(path, ImageFormat.Png);
        bmp.Dispose();
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

    /// <summary>
    /// Compares two RGBA buffers of identical dimensions. Per-channel L1
    /// distance, ignores any difference ≤ <paramref name="channelTolerance"/>
    /// (rasterizers commonly disagree by a few units near antialiased
    /// edges). A pixel is "differing" if any channel exceeds the tolerance.
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
                int delta = Math.Abs(a[baseIdx + c] - b[baseIdx + c]);
                if (delta > maxDelta) maxDelta = delta;
                sumDelta += delta;
                if (delta > channelTolerance) pixelDiffers = true;
            }
            if (pixelDiffers) differing++;
        }
        double mean = (double)sumDelta / totalChannels;
        double frac = (double)differing / (width * height);
        return new DiffReport(width, height, maxDelta, mean, frac);
    }

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
