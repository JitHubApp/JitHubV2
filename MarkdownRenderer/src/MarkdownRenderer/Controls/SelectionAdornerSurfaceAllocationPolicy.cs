using System;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Chooses when the selection-adornment surface may be reused. The surface is
/// allocated grow-only in coarse buckets, so ordinary relayout, source
/// replacement and narrow/wide resizes reuse one XAML-attached surface instead
/// of leaving a stream of replaced surfaces for XAML image-peer cleanup.
/// </summary>
internal static class SelectionAdornerSurfaceAllocationPolicy
{
    internal const int BucketDips = 256;
    internal const int MaximumAreaOversize = 4;
    internal const int MaximumPixelDimension = 16_384;
    private const float DpiTolerance = 0.1f;

    internal static bool TryGetRequired(
        double viewportWidth,
        double viewportHeight,
        float dpi,
        out SelectionAdornerSurfaceAllocation required)
    {
        required = default;
        if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) ||
            viewportWidth <= 0 || viewportHeight <= 0 ||
            !float.IsFinite(dpi) || dpi <= 0)
        {
            return false;
        }

        double width = Math.Ceiling(viewportWidth);
        double height = Math.Ceiling(viewportHeight);
        if (width > int.MaxValue || height > int.MaxValue)
            return false;

        required = new SelectionAdornerSurfaceAllocation((int)width, (int)height, dpi);
        return true;
    }

    internal static bool CanReuse(
        SelectionAdornerSurfaceAllocation current,
        SelectionAdornerSurfaceAllocation required)
        => SameDpi(current, required) &&
           current.Width >= required.Width &&
           current.Height >= required.Height &&
           !IsOversized(current, required);

    /// <summary>
    /// Returns the allocation for a required size. A same-DPI current
    /// allocation keeps its larger dimensions, so alternating narrow and wide
    /// viewports converge on one surface, unless the merged surface would be
    /// oversized for the request.
    /// </summary>
    internal static SelectionAdornerSurfaceAllocation Allocate(
        SelectionAdornerSurfaceAllocation required,
        SelectionAdornerSurfaceAllocation? current)
    {
        SelectionAdornerSurfaceAllocation exact = new(
            RoundUp(required.Width, required.Dpi),
            RoundUp(required.Height, required.Dpi),
            required.Dpi);
        if (current is not { } previous || !SameDpi(previous, required))
            return exact;

        SelectionAdornerSurfaceAllocation merged = new(
            Math.Max(exact.Width, previous.Width),
            Math.Max(exact.Height, previous.Height),
            required.Dpi);
        return IsOversized(merged, required) ? exact : merged;
    }

    private static bool SameDpi(
        SelectionAdornerSurfaceAllocation left,
        SelectionAdornerSurfaceAllocation right)
        => Math.Abs(left.Dpi - right.Dpi) <= DpiTolerance;

    private static bool IsOversized(
        SelectionAdornerSurfaceAllocation current,
        SelectionAdornerSurfaceAllocation required)
    {
        long currentArea = (long)current.Width * current.Height;
        long bucketedArea = (long)RoundUp(required.Width, required.Dpi) * RoundUp(required.Height, required.Dpi);
        return currentArea > MaximumAreaOversize * Math.Max(bucketedArea, (long)BucketDips * BucketDips);
    }

    private static int RoundUp(int dips, float dpi)
    {
        long bucketed = ((long)dips + BucketDips - 1) / BucketDips * BucketDips;
        // Never exceed the largest Win2D surface unless the viewport itself
        // already requires it; that case keeps its exact, previous size.
        long maximum = (long)Math.Floor(MaximumPixelDimension * 96.0 / dpi);
        return (int)Math.Max(Math.Min(bucketed, maximum), dips);
    }
}
