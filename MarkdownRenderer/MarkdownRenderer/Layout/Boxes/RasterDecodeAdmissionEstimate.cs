using System;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Produces weighted scheduling estimates for owned native WIC raster work.
/// These values coordinate concurrent preparations; they are not RSS promises,
/// native-allocation limits, or decode rejection thresholds.
/// </summary>
internal static class RasterDecodeAdmissionEstimate
{
    private const long SourceSurfaceBytesPerPixel = 4;
    // Oversized WebP probes showed roughly eight total source bytes per pixel
    // across decoder/Fant working surfaces, not just the existing four-byte
    // source term. Allow two extra source-sized surfaces conservatively for
    // native graph work. This is a scheduling weight, not a universal bound.
    private const long OwnedWicScratchBytesPerSourcePixel = 8;
    private const long OutputOverlapBytesPerPixel = 12;

    internal static long ForOwnedStaticWic(long encodedBytes, in RasterImageDecodePlan plan)
    {
        if (plan.SourcePixels <= 0 || plan.OutputPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(plan), "The static raster plan must have positive pixel counts.");

        return CalculateOwnedWicSchedulingWeight(
            encodedBytes,
            sourcePixels: plan.SourcePixels,
            scratchPixels: plan.SourcePixels,
            outputPixels: plan.OutputPixels);
    }

    internal static long ForOwnedGifWic(long encodedBytes, in RasterGifFirstFrameDecodePlan plan)
    {
        if (plan.CanvasWidth <= 0 || plan.CanvasHeight <= 0 ||
            plan.FrameWidth <= 0 || plan.FrameHeight <= 0 || plan.CanvasOutputPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "The GIF plan must have positive canvas, frame, and output dimensions.");
        }

        // Preserve the existing logical-canvas source charge, but scale WIC's
        // extra source-scratch estimate by only the first frame descriptor.
        // Animation frames are not decoded by this first-frame path.
        long canvasPixels = checked((long)plan.CanvasWidth * plan.CanvasHeight);
        long frameSourcePixels = checked((long)plan.FrameWidth * plan.FrameHeight);
        return CalculateOwnedWicSchedulingWeight(
            encodedBytes,
            sourcePixels: canvasPixels,
            scratchPixels: frameSourcePixels,
            outputPixels: plan.CanvasOutputPixels);
    }

    internal static long CalculateOwnedWicSchedulingWeight(
        long encodedBytes,
        long sourcePixels,
        long scratchPixels,
        long outputPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(encodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(sourcePixels);
        ArgumentOutOfRangeException.ThrowIfNegative(scratchPixels);
        ArgumentOutOfRangeException.ThrowIfNegative(outputPixels);

        return checked(
            encodedBytes +
            checked(sourcePixels * SourceSurfaceBytesPerPixel) +
            checked(scratchPixels * OwnedWicScratchBytesPerSourcePixel) +
            checked(outputPixels * OutputOverlapBytesPerPixel));
    }
}
