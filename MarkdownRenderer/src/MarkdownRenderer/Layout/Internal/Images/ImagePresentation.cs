using System.Threading;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.Loading;

namespace MarkdownRenderer.Layout.Internal.Images;

/// <summary>
/// The whole bitmap an image box presents and the device-cache lease that
/// keeps it alive, the physical viewport the bitmap was rasterized for, the
/// raster's intrinsic size, and whether the box has presented an image in
/// this layout.
/// </summary>
/// <remarks>
/// Bitmap geometry belongs to the lease, not to the latest accepted SVG tile
/// plan: the previous whole bitmap remains visible while a replacement is
/// rendering. Replaced and released leases are disposed through the box's
/// work lifetime, so a failed native release is kept by the retirement
/// barrier.
/// </remarks>
internal sealed class ImagePresentation
{
    private readonly ImageWorkLifetime _lifetime;
    private readonly ImageLoadState _load;
    private CanvasBitmap? _bitmap;
    private SharedCanvasBitmapCache.Lease? _lease;

    internal ImagePresentation(ImageWorkLifetime lifetime, ImageLoadState load)
    {
        _lifetime = lifetime;
        _load = load;
    }

    /// <summary>The presented whole bitmap, if any.</summary>
    internal CanvasBitmap? Bitmap => _bitmap;

    /// <summary>The lease that owns <see cref="Bitmap"/>, if any.</summary>
    internal SharedCanvasBitmapCache.Lease? Lease => Volatile.Read(ref _lease);

    /// <summary>The physical viewport the presented bitmap was rasterized for.</summary>
    internal MarkdownSvgPhysicalViewport? PhysicalViewport { get; private set; }

    /// <summary>The raster's intrinsic size in DIPs, once known.</summary>
    internal Size RasterIntrinsicSize { get; set; }

    /// <summary>True once the box presented an image in this layout.</summary>
    internal bool HasPresentedImage { get; set; }

    /// <summary>True while a whole bitmap or its lease is retained.</summary>
    internal bool HasRetainedBitmap =>
        Volatile.Read(ref _bitmap) is not null ||
        Volatile.Read(ref _lease) is not null;

    /// <summary>An independent lease on the presented bitmap, if it is still live.</summary>
    internal SharedCanvasBitmapCache.Lease? AcquireLease() => Volatile.Read(ref _lease)?.TryClone();

    /// <summary>
    /// Presents <paramref name="bitmap"/> under <paramref name="lease"/> and
    /// disposes the lease it replaces. A raster takes its intrinsic size from
    /// the lease, or from the bitmap when the lease has none.
    /// </summary>
    internal void Replace(
        CanvasBitmap bitmap,
        SharedCanvasBitmapCache.Lease lease,
        MarkdownSvgPhysicalViewport? physicalViewport = null)
    {
        SharedCanvasBitmapCache.Lease? previousLease = _lease;

        if (!_load.IsSvg)
            RasterIntrinsicSize = lease.IntrinsicSize ?? bitmap.Size;
        HasPresentedImage = true;
        _bitmap = bitmap;
        _lease = lease;
        PhysicalViewport = physicalViewport;

        if (previousLease is { } retiredLease)
            _lifetime.TryCleanup(retiredLease.Dispose);
    }

    /// <summary>
    /// Takes a clone's candidate lease and the source's intrinsic size and
    /// physical viewport, without probing the bitmap.
    /// </summary>
    internal void AdoptPlacement(
        SharedCanvasBitmapCache.Lease lease,
        Size rasterIntrinsicSize,
        bool hasPresentedImage,
        MarkdownSvgPhysicalViewport? physicalViewport)
    {
        RasterIntrinsicSize = rasterIntrinsicSize;
        HasPresentedImage = hasPresentedImage;
        _bitmap = lease.Bitmap;
        _lease = lease;
        PhysicalViewport = physicalViewport;
    }

    /// <summary>Stops presenting the bitmap and disposes its lease.</summary>
    internal void Release()
    {
        SharedCanvasBitmapCache.Lease? lease = _lease;

        _bitmap = null;
        _lease = null;
        PhysicalViewport = null;

        if (lease is { } retiredLease)
            _lifetime.TryCleanup(retiredLease.Dispose);
    }
}
