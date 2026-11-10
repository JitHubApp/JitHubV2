using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal.Raster;
using MarkdownRenderer.Performance;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Internal.Images.Raster;

/// <summary>
/// The immutable decode plan of one <see cref="ImageRasterLoader"/> load: the
/// validated budget, the chosen decode path and its geometry, and the bitmap
/// cache identity. The WinRT path's oriented geometry lives in
/// <see cref="RasterLoadState"/>, because it is known only after decoding starts.
/// </summary>
internal readonly record struct RasterLoadPlan
{
    internal RasterImageBudgetResult Budget { get; init; }

    internal string FailureFormatKey { get; init; }

    internal MarkdownPerformanceOptions? Options { get; init; }

    internal bool DisplaySized { get; init; }

    internal bool SessionPixelCapApplies { get; init; }

    internal bool ComposeGifCanvas { get; init; }

    internal IMarkdownRasterDecoder? RasterDecoder { get; init; }

    internal MarkdownRasterFormat RasterFormat { get; init; }

    internal RasterImageSizingRequest SizingRequest { get; init; }

    internal bool TransformedDecode { get; init; }

    internal (int Width, int Height) RasterSize { get; init; }

    internal RasterImageDecodePlan NativePlan { get; init; }

    internal bool PortableRasterDecode { get; init; }

    internal bool NativeDecode { get; init; }

    internal RasterGifFirstFrameDecodePlan NativeGifPlan { get; init; }

    internal bool NativeGifDecode { get; init; }

    internal long RasterDecodeFailureIdentity { get; init; }

    internal string CacheFamily { get; init; }

    internal string BitmapCacheKey { get; init; }

    internal Size IntrinsicSize { get; init; }
}
