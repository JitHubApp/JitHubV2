using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;

/// <summary>
/// Validates a provider's premultiplied SVG raster and uploads it into a
/// device bitmap, compacting a padded stride through a bounded buffer.
/// </summary>
internal static class SvgRasterUpload
{
    private const int MaxPooledUploadBytes = 1024 * 1024;

    /// <summary>
    /// Throws a worker failure unless <paramref name="raster"/> has the
    /// expected dimensions and a buffer large enough for its stride.
    /// </summary>
    internal static void Validate(
        MarkdownSvgRaster raster,
        int expectedWidth,
        int expectedHeight)
    {
        if (raster.WidthPixels != expectedWidth ||
            raster.HeightPixels != expectedHeight)
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG renderer returned unexpected raster dimensions.");
        }

        int minimumStride = checked(expectedWidth * 4);
        int minimumLength = checked(raster.StrideBytes * expectedHeight);
        if (raster.StrideBytes < minimumStride ||
            raster.LengthBytes < minimumLength ||
            raster.Pixels.Length < minimumLength)
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG renderer returned an invalid raster buffer.");
        }
    }

    /// <summary>
    /// Uploads <paramref name="raster"/> on <paramref name="resourceCreator"/>,
    /// committing <paramref name="bitmapReservation"/> immediately before the
    /// native allocation.
    /// </summary>
    internal static CanvasBitmap CreateBitmap(
        ICanvasResourceCreator resourceCreator,
        MarkdownSvgRaster raster,
        long diagnosticWorkId,
        long diagnosticSourceBytes = 0,
        long diagnosticTargetPixels = 0,
        BitmapOutputAdmission.Reservation? bitmapReservation = null)
    {
        int tightStride = checked(raster.WidthPixels * 4);
        int tightLength = checked(tightStride * raster.HeightPixels);
        DirectXPixelFormat format = raster.PixelFormat switch
        {
            MarkdownSvgPixelFormat.Rgba8Premultiplied =>
                DirectXPixelFormat.R8G8B8A8UIntNormalized,
            MarkdownSvgPixelFormat.Bgra8Premultiplied =>
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
            _ => throw new MarkdownSvgException(
                MarkdownSvgFailureReason.WorkerFailure,
                "The SVG renderer returned an unknown pixel format."),
        };
        byte[]? rented = null;
        try
        {
            byte[] bytes = GetTightPixels(
                raster, tightStride, tightLength, diagnosticWorkId, diagnosticSourceBytes, diagnosticTargetPixels, ref rented);

            using var createBitmapTrace = MarkdownSvgStageTrace.Measure(
                diagnosticWorkId,
                MarkdownSvgWorkStage.SvgCanvasBitmapCreateFromBytes,
                diagnosticSourceBytes,
                diagnosticTargetPixels);
            if (bitmapReservation is not null && !bitmapReservation.Commit())
                throw new InvalidOperationException("The SVG output reservation is no longer active.");
            return CanvasBitmap.CreateFromBytes(
                resourceCreator,
                bytes,
                raster.WidthPixels,
                raster.HeightPixels,
                format);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // The provider's own array when it is already tight; otherwise a copy
    // with the stride removed, rented from the shared pool when small.
    private static byte[] GetTightPixels(
        MarkdownSvgRaster raster,
        int tightStride,
        int tightLength,
        long diagnosticWorkId,
        long diagnosticSourceBytes,
        long diagnosticTargetPixels,
        ref byte[]? rented)
    {
        ReadOnlyMemory<byte> pixels = raster.Pixels;
        if (raster.StrideBytes == tightStride &&
            MemoryMarshal.TryGetArray(pixels, out ArraySegment<byte> segment) &&
            segment.Offset == 0 &&
            segment.Array is { } array &&
            segment.Count >= tightLength)
        {
            return array;
        }

        using var bufferCopyTrace = MarkdownSvgStageTrace.Measure(
            diagnosticWorkId,
            MarkdownSvgWorkStage.SvgBitmapBufferCopy,
            diagnosticSourceBytes,
            diagnosticTargetPixels);
        byte[] bytes;
        if (tightLength <= MaxPooledUploadBytes)
        {
            rented = ArrayPool<byte>.Shared.Rent(tightLength);
            bytes = rented;
        }
        else
        {
            // Avoid allowing the shared array pool to retain a second,
            // unbudgeted large raster after the provider's shared-memory
            // buffer has been uploaded and released.
            bytes = GC.AllocateUninitializedArray<byte>(tightLength);
        }
        ReadOnlySpan<byte> source = pixels.Span;
        for (int row = 0; row < raster.HeightPixels; row++)
        {
            source.Slice(row * raster.StrideBytes, tightStride)
                .CopyTo(bytes.AsSpan(row * tightStride, tightStride));
        }

        return bytes;
    }
}
