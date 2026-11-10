using System;
using System.Globalization;
using MarkdownRenderer.Images.Internal.Raster;

namespace MarkdownRenderer.Layout.Internal.Images.BitmapCache;

/// <summary>
/// The rules that decide whether a cached display-sized raster variant can
/// stand in as a preview for a sizing request. They read only a cache key's
/// identity text and an entry's immutable metadata, so the cache can apply
/// them under its LRU lock without touching Win2D or live layout.
/// </summary>
internal static class RasterPreviewVariantMatch
{
    /// <summary>
    /// Whether <paramref name="identity"/> is exactly
    /// <paramref name="prefix"/> followed by <c>WxH</c>, a unit separator, and
    /// a <c>request:available@scale</c> suffix with a finite available width
    /// of at least one and a positive finite scale.
    /// </summary>
    internal static bool IsExactDisplayVariantKey(string identity, string prefix)
    {
        if (!identity.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        ReadOnlySpan<char> dimensions = identity.AsSpan(prefix.Length);
        int requestSeparator = dimensions.IndexOf('\u001F');
        if (requestSeparator <= 0)
            return false;
        if (requestSeparator >= 0)
        {
            ReadOnlySpan<char> request = dimensions[(requestSeparator + 1)..];
            const string requestPrefix = "request:";
            if (!request.StartsWith(requestPrefix, StringComparison.Ordinal))
                return false;
            request = request[requestPrefix.Length..];
            int scaleSeparator = request.IndexOf('@');
            if (scaleSeparator <= 0 ||
                !double.TryParse(request[..scaleSeparator], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out double available) ||
                !double.TryParse(request[(scaleSeparator + 1)..], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out double scale) ||
                !double.IsFinite(available) || available < 1 ||
                !double.IsFinite(scale) || scale <= 0)
                return false;
            dimensions = dimensions[..requestSeparator];
        }
        int separator = dimensions.IndexOf('x');
        if (separator is <= 0 || separator == dimensions.Length - 1)
            return false;
        return int.TryParse(dimensions[..separator], NumberStyles.None,
                CultureInfo.InvariantCulture, out int width) && width > 0 &&
            int.TryParse(dimensions[(separator + 1)..], NumberStyles.None,
                CultureInfo.InvariantCulture, out int height) && height > 0;
    }

    /// <summary>
    /// The output size <paramref name="sizingRequest"/> gives the entry's
    /// integral, oriented intrinsic size.
    /// </summary>
    internal static bool TryGetOrientedTarget(
        SharedCanvasBitmapCacheEntry entry, RasterImageSizingRequest sizingRequest,
        out (int Width, int Height) target)
    {
        target = default;
        if (sizingRequest.MaximumPixels <= 0 ||
            entry.IntrinsicSize is not { } intrinsic ||
            !double.IsFinite(intrinsic.Width) || !double.IsFinite(intrinsic.Height) ||
            intrinsic.Width < 1 || intrinsic.Height < 1 ||
            intrinsic.Width > int.MaxValue || intrinsic.Height > int.MaxValue ||
            intrinsic.Width != Math.Truncate(intrinsic.Width) ||
            intrinsic.Height != Math.Truncate(intrinsic.Height))
            return false;
        target = sizingRequest.GetSize((int)intrinsic.Width, (int)intrinsic.Height);
        return true;
    }

    /// <summary>
    /// Whether the entry is a strictly smaller preview of the request's
    /// target within its pixel ceiling.
    /// </summary>
    internal static bool IsCompatibleRasterPreview(SharedCanvasBitmapCacheEntry entry, RasterImageSizingRequest sizingRequest)
    {
        return entry.PixelWidth > 0 && entry.PixelHeight > 0 &&
            (long)entry.PixelWidth * entry.PixelHeight <= sizingRequest.MaximumPixels &&
            TryGetOrientedTarget(entry, sizingRequest, out var target) &&
            entry.PixelWidth <= target.Width && entry.PixelHeight <= target.Height &&
            (entry.PixelWidth < target.Width || entry.PixelHeight < target.Height);
    }
}
