using System;
using System.IO;
using SkiaSharp;
using Svg.Skia;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Rasterizes SVG documents to BGRA pixel buffers using <see cref="Svg.Skia"/>.
/// Encapsulates all SkiaSharp interop so the rest of the project never imports
/// SkiaSharp types directly. The output buffer is wrapped into a
/// <c>CanvasBitmap</c> by the caller for blitting through the existing image
/// draw path. Public for unit-test access (mirrors <see cref="SvgIntrinsics"/>).
/// </summary>
public static class SvgSkiaRasterizer
{
    /// <summary>
    /// Loaded SVG result: BGRA premultiplied pixel buffer plus dimensions.
    /// The buffer is the exact size required by
    /// <c>CanvasBitmap.CreateFromBytes</c>: <c>WidthPx * HeightPx * 4</c>.
    /// </summary>
    public readonly record struct Raster(byte[] Bgra, int WidthPx, int HeightPx);

    /// <summary>
    /// Parses <paramref name="svgBytes"/> and rasterizes it to a bitmap sized
    /// <paramref name="targetWidthPx"/> by <paramref name="targetHeightPx"/>.
    /// Preserves aspect ratio against the SVG's intrinsic / viewBox dimensions
    /// by scaling uniformly; centers the result inside the target box if the
    /// aspect ratios disagree (no spec-breaking distortion).
    /// </summary>
    /// <returns>
    /// The rasterized BGRA buffer, or <c>null</c> if the SVG could not be
    /// parsed (caller falls back to the alt-text placeholder).
    /// </returns>
    public static Raster? Rasterize(byte[] svgBytes, int targetWidthPx, int targetHeightPx)
    {
        if (svgBytes is null || svgBytes.Length == 0) return null;
        if (targetWidthPx <= 0 || targetHeightPx <= 0) return null;

        try
        {
            using var stream = new MemoryStream(svgBytes, writable: false);
            using var svg = new SKSvg();
            var picture = svg.Load(stream);
            if (picture is null) return null;

            var cull = picture.CullRect;
            if (cull.Width <= 0 || cull.Height <= 0) return null;

            // Uniform fit (preserve aspect). The caller has already chosen the
            // target size from the SVG's intrinsic dimensions, so in the
            // common case the aspect ratios match and scaleX == scaleY.
            float scaleX = targetWidthPx / cull.Width;
            float scaleY = targetHeightPx / cull.Height;
            float scale = MathF.Min(scaleX, scaleY);
            float drawW = cull.Width * scale;
            float drawH = cull.Height * scale;
            float offsetX = (targetWidthPx - drawW) * 0.5f - cull.Left * scale;
            float offsetY = (targetHeightPx - drawH) * 0.5f - cull.Top * scale;

            var info = new SKImageInfo(targetWidthPx, targetHeightPx, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate(offsetX, offsetY);
                canvas.Scale(scale, scale);
                canvas.DrawPicture(picture);
            }

            // Copy the pixels out of the SKBitmap before it is disposed.
            // SKBitmap.Bytes returns a reference into native memory.
            int byteCount = info.BytesSize;
            var bgra = new byte[byteCount];
            System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), bgra, 0, byteCount);
            return new Raster(bgra, targetWidthPx, targetHeightPx);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SvgSkiaRasterizer] rasterize failed: {ex.Message}");
            return null;
        }
    }
}
