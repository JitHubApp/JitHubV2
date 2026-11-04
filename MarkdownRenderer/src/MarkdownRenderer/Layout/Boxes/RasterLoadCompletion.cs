using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The outcome of one raster load, carried to the UI thread. It owns
/// <see cref="Lease"/> and <see cref="OwnedBitmap"/> until the box presents or
/// retires them.
/// </summary>
internal readonly record struct RasterLoadCompletion(
    SharedCanvasBitmapCache.Lease? Lease,
    CanvasBitmap? OwnedBitmap,
    Size IntrinsicSize,
    bool TransformedDecode,
    bool Failed,
    bool DeviceLost,
    bool CacheRasterDecodeFailure,
    string CacheKey,
    string FailureFormatKey,
    long RasterDecodeFailureIdentity);
