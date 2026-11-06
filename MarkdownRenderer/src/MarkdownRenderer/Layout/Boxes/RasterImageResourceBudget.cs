using System;

namespace MarkdownRenderer.Layout.Boxes;

internal static class RasterImageResourceBudget
{
    // This is a compressed-source ceiling, not a decoded-memory allowance.
    // The independent pixel/frame/decoded-byte checks below remain authoritative.
    internal const int MaxInputBytes = 32 * 1024 * 1024;
    // D3D feature level 10+ supports 16,384-pixel textures, and a number of
    // real README screenshots are narrow, tall images just above 8K. Pixel
    // and decoded-byte ceilings remain the authoritative memory limits, so a
    // 16K edge does not admit a dimension bomb by itself.
    internal const int MaxDimension = 16_384;
    internal const long MaxPixelsPerFrame = 16_777_216;
    internal const long MaxDownsampleSourcePixels = 40_000_000;
    internal const int MaxFrameCount = 60;
    internal const long MaxDecodedBytes = 64L * 1024 * 1024;
    internal const int BytesPerDecodedPixel = 4;

    public static RasterImageBudgetResult Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return RasterImageBudgetResult.Reject("The raster image is empty.");
        }

        if (bytes.Length > MaxInputBytes)
        {
            return RasterImageBudgetResult.Reject("The compressed raster image exceeds the input budget.");
        }

        RasterImageHeader? header = RasterImageHeaderReader.TryRead(bytes);
        if (header is null)
        {
            return RasterImageBudgetResult.Reject("The raster image header is unsupported or malformed.");
        }

        if (header.Width <= 0 || header.Height <= 0 ||
            header.Width > MaxDimension || header.Height > MaxDimension)
        {
            return RasterImageBudgetResult.Reject("The raster image dimensions exceed the safe budget.");
        }

        if (header.FrameCount <= 0)
        {
            return RasterImageBudgetResult.Reject("The raster animation frame count exceeds the safe budget.");
        }

        return ValidateDecodedSize(header);
    }

    // Applies the pixel, frame and decoded-byte ceilings to a header whose
    // dimensions and frame count are already in range.
    private static RasterImageBudgetResult ValidateDecodedSize(RasterImageHeader header)
    {
        long pixelsPerFrame;
        long totalPixels;
        long decodedBytes;
        try
        {
            pixelsPerFrame = checked((long)header.Width * header.Height);
            totalPixels = header.TotalPixels ?? checked(pixelsPerFrame * header.FrameCount);
            decodedBytes = checked(totalPixels * BytesPerDecodedPixel);
        }
        catch (OverflowException)
        {
            return RasterImageBudgetResult.Reject("The raster image decoded size overflows the safe budget.");
        }

        if (pixelsPerFrame > MaxPixelsPerFrame)
        {
            return header.FrameCount == 1 && pixelsPerFrame <= MaxDownsampleSourcePixels
                ? RasterImageBudgetResult.RequireStaticPreview(
                    header.Format,
                    header.Width,
                    header.Height,
                    header.FrameCount,
                    totalPixels,
                    decodedBytes,
                    "The raster image requires a bounded downsampled preview.", header.FirstFrame)
                : RasterImageBudgetResult.Reject("The raster image pixel count exceeds the safe budget.");
        }

        if (header.FrameCount > MaxFrameCount)
        {
            return RasterImageBudgetResult.RejectAnimation(
                header.Format,
                header.Width,
                header.Height,
                header.FrameCount,
                totalPixels,
                decodedBytes,
                "The raster animation frame count exceeds the safe budget.", header.FirstFrame);
        }

        if (totalPixels < pixelsPerFrame)
        {
            return RasterImageBudgetResult.Reject("The raster image decoded-memory budget is exceeded.");
        }

        if (decodedBytes > MaxDecodedBytes)
        {
            return header.FrameCount > 1
                ? RasterImageBudgetResult.RejectAnimation(
                    header.Format,
                    header.Width,
                    header.Height,
                    header.FrameCount,
                    totalPixels,
                    decodedBytes,
                    "The raster image decoded-memory budget is exceeded.", header.FirstFrame)
                : RasterImageBudgetResult.Reject("The raster image decoded-memory budget is exceeded.");
        }

        return new RasterImageBudgetResult(
            true,
            false,
            header.Format,
            header.Width,
            header.Height,
            header.FrameCount,
            totalPixels,
            decodedBytes,
            null,
            header.FirstFrame);
    }
}
