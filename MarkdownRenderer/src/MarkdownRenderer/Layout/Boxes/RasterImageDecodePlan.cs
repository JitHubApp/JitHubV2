using System;
using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Immutable source-to-output geometry for a bounded static raster preview.
/// The output size is in physical pixels. Decode dimensions describe the
/// pre-orientation axes required by WIC's scale-then-EXIF transform order.
/// </summary>
internal readonly struct RasterImageDecodePlan
{
    private RasterImageDecodePlan(
        int sourceWidth,
        int sourceHeight,
        string format,
        int orientation,
        int intrinsicWidth,
        int intrinsicHeight,
        int outputWidth,
        int outputHeight,
        int decodeWidth,
        int decodeHeight,
        long sourcePixels,
        long outputPixels)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Format = format;
        Orientation = orientation;
        IntrinsicWidth = intrinsicWidth;
        IntrinsicHeight = intrinsicHeight;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        DecodeWidth = decodeWidth;
        DecodeHeight = decodeHeight;
        SourcePixels = sourcePixels;
        OutputPixels = outputPixels;
    }

    internal int SourceWidth { get; }
    internal int SourceHeight { get; }
    internal string Format { get; }
    internal int Orientation { get; }
    internal int IntrinsicWidth { get; }
    internal int IntrinsicHeight { get; }
    internal int OutputWidth { get; }
    internal int OutputHeight { get; }
    internal int DecodeWidth { get; }
    internal int DecodeHeight { get; }
    internal long SourcePixels { get; }
    internal long OutputPixels { get; }

    /// <summary>
    /// Creates a plan only from a supported, single-frame raster whose source
    /// budget was already validated for these exact bytes. GIF/ICO/AVIF and
    /// unknown containers deliberately remain on their existing decode path.
    /// The request's MaximumPixels is the caller's effective output admission
    /// ceiling; this method additionally enforces the renderer's fixed per-frame
    /// dimension and pixel ceilings.
    /// </summary>
    internal static bool TryCreate(
        ReadOnlySpan<byte> bytes,
        RasterImageBudgetResult validatedBudget,
        RasterImageSizingRequest sizingRequest,
        out RasterImageDecodePlan plan,
        CancellationToken cancellationToken = default)
    {
        plan = default;
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(validatedBudget);

        if ((!validatedBudget.Accepted && !validatedBudget.CanRenderStaticPreview) ||
            validatedBudget.FrameCount != 1 ||
            validatedBudget.FirstFrame is not null ||
            validatedBudget.Width <= 0 || validatedBudget.Height <= 0 ||
            validatedBudget.Width > RasterImageResourceBudget.MaxDimension ||
            validatedBudget.Height > RasterImageResourceBudget.MaxDimension ||
            validatedBudget.Format is not ("PNG" or "BMP" or "JPEG" or "WEBP"))
        {
            return false;
        }

        if (sizingRequest.MaximumPixels <= 0)
            return false;

        long sourcePixels = (long)validatedBudget.Width * validatedBudget.Height;
        if (sourcePixels <= 0 || sourcePixels > RasterImageResourceBudget.MaxDownsampleSourcePixels ||
            validatedBudget.TotalPixels != sourcePixels ||
            validatedBudget.DecodedBytes != checked(sourcePixels * 4))
        {
            return false;
        }

        if (validatedBudget.Accepted &&
            (sourcePixels > RasterImageResourceBudget.MaxPixelsPerFrame ||
             validatedBudget.DecodedBytes > RasterImageResourceBudget.MaxDecodedBytes))
        {
            return false;
        }

        if (!RasterIntrinsicMetadata.TryReadValidated(
                bytes, validatedBudget, out RasterIntrinsicMetadata intrinsic,
                out int orientation, cancellationToken))
        {
            return false;
        }

        bool swapAxes = orientation is 5 or 6 or 7 or 8;
        int expectedIntrinsicWidth = swapAxes ? validatedBudget.Height : validatedBudget.Width;
        int expectedIntrinsicHeight = swapAxes ? validatedBudget.Width : validatedBudget.Height;
        if (intrinsic.Width != expectedIntrinsicWidth || intrinsic.Height != expectedIntrinsicHeight)
            return false;

        (int outputWidth, int outputHeight) = sizingRequest.GetSize(intrinsic.Width, intrinsic.Height);
        if (outputWidth <= 0 || outputHeight <= 0 ||
            outputWidth > RasterImageResourceBudget.MaxDimension ||
            outputHeight > RasterImageResourceBudget.MaxDimension)
        {
            return false;
        }

        long outputPixels = (long)outputWidth * outputHeight;
        if (outputPixels <= 0 || outputPixels > sizingRequest.MaximumPixels ||
            outputPixels > RasterImageResourceBudget.MaxPixelsPerFrame)
        {
            return false;
        }

        // WIC applies scaling in source axes, then applies EXIF orientation.
        // Orientations 5..8 transpose the axes; mirror-only orientations 2..4
        // do not. Keep both geometries explicit so callers cannot accidentally
        // feed display-axis dimensions to the pre-rotation transform.
        int decodeWidth = swapAxes ? outputHeight : outputWidth;
        int decodeHeight = swapAxes ? outputWidth : outputHeight;
        if (decodeWidth <= 0 || decodeHeight <= 0 ||
            decodeWidth > validatedBudget.Width || decodeHeight > validatedBudget.Height)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        plan = new RasterImageDecodePlan(
            validatedBudget.Width,
            validatedBudget.Height,
            validatedBudget.Format!,
            orientation,
            intrinsic.Width,
            intrinsic.Height,
            outputWidth,
            outputHeight,
            decodeWidth,
            decodeHeight,
            sourcePixels,
            outputPixels);
        return true;
    }
}
