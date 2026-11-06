using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>
/// An opened SVG document whose parsed state may be reused for more than one
/// raster size or tile.
/// </summary>
/// <remarks>
/// Implementations must support overlapping <see cref="RenderAsync"/> calls.
/// <see cref="IDisposable.Dispose"/> must be idempotent and may race admitted
/// renders; those renders must either complete normally or fail with
/// <see cref="MarkdownSvgFailureReason.Canceled"/>. Calls admitted after
/// disposal must fail with that same typed cancellation reason.
/// </remarks>
public interface IMarkdownSvgDocument : IDisposable
{
    /// <summary>Gets immutable metadata discovered while opening the document.</summary>
    MarkdownSvgDocumentInfo Info { get; }

    /// <summary>Rasterizes the full output or one physical-pixel tile.</summary>
    /// <param name="request">Requested full output dimensions, tile, and pixel format.</param>
    /// <param name="cancellationToken">Cancels the render operation.</param>
    /// <returns>An owned raster that must remain alive until upload completes.</returns>
    /// <exception cref="MarkdownSvgException">The render failed for a typed provider reason.</exception>
    ValueTask<MarkdownSvgRaster> RenderAsync(
        MarkdownSvgRenderRequest request,
        CancellationToken cancellationToken = default);
}
