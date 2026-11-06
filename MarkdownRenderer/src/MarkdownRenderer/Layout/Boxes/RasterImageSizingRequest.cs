using System;
using System.Globalization;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Detached sizing policy for one raster preparation. The same request plans
/// raw/oriented dimensions and validates cached output, even after async waits.
/// </summary>
internal readonly struct RasterImageSizingRequest
{
    internal RasterImageSizingRequest(
        double availableWidth,
        double rasterizationScale,
        SafeHtmlLength? requestedWidth,
        SafeHtmlLength? requestedHeight,
        long maximumPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPixels);
        AvailableWidth = double.IsFinite(availableWidth)
            ? Math.Clamp(availableWidth, 1d, float.MaxValue) : 1d;
        RasterizationScale = double.IsFinite(rasterizationScale) && rasterizationScale > 0
            ? rasterizationScale : 1d;
        RequestedWidth = Normalize(requestedWidth);
        RequestedHeight = requestedHeight is { IsPercent: false }
            ? Normalize(requestedHeight) : null;
        MaximumPixels = maximumPixels;
    }

    internal readonly double AvailableWidth;
    internal readonly double RasterizationScale;
    internal readonly SafeHtmlLength? RequestedWidth;
    internal readonly SafeHtmlLength? RequestedHeight;
    internal readonly long MaximumPixels;

    internal (int Width, int Height) GetSize(int intrinsicWidth, int intrinsicHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intrinsicWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intrinsicHeight);
        double aspect = intrinsicWidth / (double)intrinsicHeight;
        double width = Math.Min(intrinsicWidth, AvailableWidth);
        double height = width / aspect;
        double? requestedWidth = RequestedWidth?.Resolve((float)AvailableWidth);
        double? requestedHeight = RequestedHeight?.Resolve((float)AvailableWidth);
        // Match ImageBox's authored-size containment before converting DIPs to
        // pixels. Oversized width/height attributes cannot make a table-cell
        // decode larger than the image's actual constrained display rectangle.
        if (requestedWidth is > 0 && requestedHeight is > 0)
        {
            double containment = Math.Min(1d, AvailableWidth / requestedWidth.Value);
            width = Math.Max(1d, requestedWidth.Value * containment);
            height = Math.Max(1d, requestedHeight.Value * containment);
        }
        else if (requestedWidth is > 0)
        {
            width = Math.Max(1d, Math.Min(AvailableWidth, requestedWidth.Value));
            height = Math.Max(1d, width / Math.Max(0.001d, aspect));
        }
        else if (requestedHeight is > 0)
        {
            height = Math.Max(1d, requestedHeight.Value);
            width = Math.Max(1d, height * aspect);
            if (width > AvailableWidth)
            {
                height = Math.Max(1d, height * (AvailableWidth / width));
                width = AvailableWidth;
            }
        }

        width = Math.Clamp(Math.Ceiling(width * RasterizationScale), 1, intrinsicWidth);
        height = Math.Clamp(Math.Ceiling(height * RasterizationScale), 1, intrinsicHeight);
        double pixels = width * height;
        if (pixels > MaximumPixels)
        {
            double scale = Math.Sqrt(MaximumPixels / pixels);
            width = Math.Max(1, Math.Floor(width * scale));
            height = Math.Max(1, Math.Floor(height * scale));
            // A one-pixel short edge cannot shrink any further. Reconcile
            // after that clamp so very thin/tall images still honor the cap.
            width = Math.Min(width, Math.Max(1, Math.Floor(MaximumPixels / height)));
            height = Math.Min(height, Math.Max(1, Math.Floor(MaximumPixels / width)));
        }
        return (checked((int)width), checked((int)height));
    }

    // Decoder creation precedes authoritative EXIF orientation. Charge the
    // larger of both possible axis plans rather than the output pixel ceiling:
    // ordinary display-sized previews rarely fill that ceiling. This remains
    // a scheduling estimate; source scratch is charged separately and native
    // codec peak memory is not implied by either estimate.
    internal long GetMaximumOrientedOutputPixels(
        int intrinsicWidth, int intrinsicHeight, bool maySwapAxes)
    {
        var raw = GetSize(intrinsicWidth, intrinsicHeight);
        long rawPixels = (long)raw.Width * raw.Height;
        if (!maySwapAxes)
            return rawPixels;

        var oriented = GetSize(intrinsicHeight, intrinsicWidth);
        return Math.Max(rawPixels, (long)oriented.Width * oriented.Height);
    }

    // A family preserves preview reuse across viewport widths and DPI. The
    // consumer's ceiling and authored sizing rules partition incompatible work.
    internal string GetCacheFamily(string sourceIdentity, bool displaySized) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{sourceIdentity}\u001Fraster-v2:{(displaySized ? 1 : 0)}:{MaximumPixels}:" +
            $"{RequestedWidth?.Value:R}:{(RequestedWidth?.IsPercent == true ? 1 : 0)}:{RequestedHeight?.Value:R}");

    // Raw-axis clamping can hide different requests before EXIF is known. Keep
    // the detached viewport inputs in the variant as well as the provisional size.
    internal string GetCacheKey(string family, (int Width, int Height) provisionalSize) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{family}\u001Fdisplay:{provisionalSize.Width}x{provisionalSize.Height}" +
            $"\u001Frequest:{AvailableWidth:R}@{RasterizationScale:R}");

    private static SafeHtmlLength? Normalize(SafeHtmlLength? length)
    {
        if (length is not { } value || !float.IsFinite(value.Value))
            return null;
        float normalized = value.IsPercent
            ? Math.Clamp(value.Value, 0, 100) : Math.Max(0, value.Value);
        // Layout treats nonpositive authored dimensions as unspecified.
        return normalized > 0 ? new SafeHtmlLength(normalized, value.IsPercent) : null;
    }
}
