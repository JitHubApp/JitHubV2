using System;
using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Bounded frame-zero decoding, deliberately distinct from a static raster's
/// EXIF geometry. The logical screen owns layout; WIC returns only the first
/// image descriptor. Composition retains fractional edges on that screen.
/// </summary>
internal readonly struct RasterGifFirstFrameDecodePlan
{
    private RasterGifFirstFrameDecodePlan(
        RasterImageBudgetResult budget, int outputWidth, int outputHeight,
        int decodeWidth, int decodeHeight)
    {
        var frame = budget.FirstFrame!.Value;
        CanvasWidth = budget.Width;
        CanvasHeight = budget.Height;
        FrameLeft = frame.Left;
        FrameTop = frame.Top;
        FrameWidth = frame.Width;
        FrameHeight = frame.Height;
        FrameCount = budget.FrameCount;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        DecodeWidth = decodeWidth;
        DecodeHeight = decodeHeight;
        _admittedTotalPixels = budget.TotalPixels;
        _admittedDecodedBytes = budget.DecodedBytes;
        _accepted = budget.Accepted;
        _staticPreview = budget.CanRenderStaticPreview;
    }

    private readonly long _admittedTotalPixels;
    private readonly long _admittedDecodedBytes;
    private readonly bool _accepted;
    private readonly bool _staticPreview;
    internal int CanvasWidth { get; }
    internal int CanvasHeight { get; }
    internal int FrameLeft { get; }
    internal int FrameTop { get; }
    internal int FrameWidth { get; }
    internal int FrameHeight { get; }
    internal int FrameCount { get; }
    internal int OutputWidth { get; }
    internal int OutputHeight { get; }
    internal int DecodeWidth { get; }
    internal int DecodeHeight { get; }
    internal long CanvasOutputPixels => (long)OutputWidth * OutputHeight;
    internal long FrameOutputPixels => (long)DecodeWidth * DecodeHeight;
    internal bool RequiresComposition => FrameLeft != 0 || FrameTop != 0 ||
        FrameWidth != CanvasWidth || FrameHeight != CanvasHeight;

    internal static bool TryCreate(
        ReadOnlySpan<byte> bytes, RasterImageBudgetResult validatedBudget,
        RasterImageSizingRequest sizingRequest, out RasterGifFirstFrameDecodePlan plan,
        CancellationToken cancellationToken = default)
    {
        plan = default;
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(validatedBudget);
        if (bytes.IsEmpty || bytes.Length > RasterImageResourceBudget.MaxInputBytes ||
            validatedBudget.Format != "GIF" ||
            (!validatedBudget.Accepted && !validatedBudget.CanRenderStaticPreview) ||
            validatedBudget.FirstFrame is not { } frame ||
            validatedBudget.FrameCount <= 0 || sizingRequest.MaximumPixels <= 0 ||
            validatedBudget.Width <= 0 || validatedBudget.Height <= 0 ||
            validatedBudget.Width > RasterImageResourceBudget.MaxDimension ||
            validatedBudget.Height > RasterImageResourceBudget.MaxDimension ||
            frame.Left < 0 || frame.Top < 0 || frame.Width <= 0 || frame.Height <= 0 ||
            frame.Left > validatedBudget.Width || frame.Top > validatedBudget.Height ||
            frame.Width > validatedBudget.Width - frame.Left ||
            frame.Height > validatedBudget.Height - frame.Top)
        {
            return false;
        }

        long canvasPixels = (long)validatedBudget.Width * validatedBudget.Height;
        if (canvasPixels > RasterImageResourceBudget.MaxDownsampleSourcePixels ||
            validatedBudget.TotalPixels < canvasPixels ||
            validatedBudget.TotalPixels > long.MaxValue / 4 ||
            validatedBudget.DecodedBytes != validatedBudget.TotalPixels * 4)
            return false;

        // More than 60 animation frames may already be admitted as a bounded
        // static preview. Preserve that policy: only frame zero is ever decoded.
        (int width, int height) = sizingRequest.GetSize(validatedBudget.Width, validatedBudget.Height);
        long outputPixels = (long)width * height;
        if (width <= 0 || height <= 0 || width > validatedBudget.Width || height > validatedBudget.Height ||
            outputPixels > sizingRequest.MaximumPixels || outputPixels > RasterImageResourceBudget.MaxPixelsPerFrame)
            return false;

        // Match the established WinRT transform and Win2D composition exactly.
        // Do not snap the destination rectangle or derive it from these ceiled
        // temporary frame dimensions; those are intentionally different spaces.
        int decodeWidth = Math.Max(1, checked((int)Math.Ceiling(frame.Width * (double)width / validatedBudget.Width)));
        int decodeHeight = Math.Max(1, checked((int)Math.Ceiling(frame.Height * (double)height / validatedBudget.Height)));
        if (decodeWidth > frame.Width || decodeHeight > frame.Height ||
            (long)decodeWidth * decodeHeight > outputPixels)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        plan = new RasterGifFirstFrameDecodePlan(validatedBudget, width, height, decodeWidth, decodeHeight);
        return true;
    }

    // The worker repeats full structural admission against its exact borrowed
    // bytes, including later frames and the trailer. A first valid descriptor
    // must not hide malformed, forbidden-size or truncated trailing content.
    internal bool MatchesValidatedBudget(RasterImageBudgetResult budget) =>
        FrameCount > 0 && budget.Format == "GIF" && budget.FirstFrame is { } frame &&
        budget.Accepted == _accepted && budget.CanRenderStaticPreview == _staticPreview &&
        budget.Width == CanvasWidth && budget.Height == CanvasHeight &&
        budget.FrameCount == FrameCount && budget.TotalPixels == _admittedTotalPixels &&
        budget.DecodedBytes == _admittedDecodedBytes &&
        frame == (FrameLeft, FrameTop, FrameWidth, FrameHeight);
}
