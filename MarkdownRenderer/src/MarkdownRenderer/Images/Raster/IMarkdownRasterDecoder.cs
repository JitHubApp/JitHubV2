using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images.Raster;

/// <summary>Decodes bounded static raster images independently of installed image codecs.</summary>
/// <remarks>
/// A control borrows this service and never disposes it. Implementations must support concurrent
/// calls, enforce resource limits and active-operation deadlines, and perform expensive work off
/// the UI thread. A failed decode must not return partially initialized pixels. The output is sRGB,
/// with embedded color profiles and EXIF orientation applied before publication.
/// Controls route only containers for which the host can construct a validated static decode
/// plan. Animated images and GIF frame/canvas composition retain their existing host paths;
/// advertising a format does not bypass those admission and composition rules.
/// </remarks>
public interface IMarkdownRasterDecoder
{
    /// <summary>Reports whether this decoder implements a static image format.</summary>
    /// <remarks>This must be a cheap, thread-safe capability check, with no I/O or decoding.</remarks>
    bool SupportsFormat(MarkdownRasterFormat format);

    /// <summary>Decodes an admitted image to exact, oriented physical-pixel dimensions.</summary>
    /// <returns>Owned premultiplied sRGB pixels, kept alive by the caller until GPU upload completes.</returns>
    /// <exception cref="MarkdownRasterException">The decode failed for a typed provider reason.</exception>
    ValueTask<MarkdownRaster> DecodeAsync(
        MarkdownRasterDecodeRequest request,
        CancellationToken cancellationToken = default);
}
