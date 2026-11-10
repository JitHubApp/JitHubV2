using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using MarkdownRenderer.Layout.Internal.Images.Raster;
using MarkdownRenderer.Layout.Internal.Images.Svg;

namespace MarkdownRenderer.Layout.Internal.Images.Loading;

/// <summary>
/// The image box as seen by its <see cref="ImageSourceLoader"/>: the raster and
/// SVG load hosts the loader's pipeline owners use, plus the box's current
/// representation and cache identity, its built-in source, detached-work
/// tracking, and the UI-thread presentations of cached and failed sources.
/// </summary>
internal interface IImageSourceLoadHost : IImageRasterLoadHost, ISvgLoadHost
{
    /// <summary>Whether the box currently loads an SVG. Resolution replaces the URI hint.</summary>
    bool IsSvg { get; set; }

    /// <summary>The security-partitioned cache identity of the current load.</summary>
    string? ActiveCacheKey { get; set; }

    /// <summary>The source the built-in loader may read without a resolver.</summary>
    MarkdownBuiltInImageSource? BuiltInSource { get; }

    void TrackDetachedImageWork(Task detachedOperation, bool failRetirementOnFault = false);

    /// <summary>Presents a shared cached raster bitmap on the UI thread.</summary>
    void PresentCachedRasterBitmap(SharedCanvasBitmapCache.Lease cachedLease);

    /// <summary>Publishes an exact cached bitmap for a current source-cache entry.</summary>
    bool TryPublishCachedSvgSource(
        SvgCacheEntry entry, string resolvedCacheKey, BitmapOutputWorkContext? expectedWork);

    /// <summary>Presents a failed SVG data URI decode on the UI thread.</summary>
    void PresentSvgDataUriFailure();
}
