using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images.Svg;

/// <summary>
/// Opens sanitized, self-contained SVG content for metadata inspection and
/// subsequent rasterization.
/// </summary>
/// <remarks>
/// Implementations must be safe to share between controls and to call
/// concurrently. They must honor cancellation promptly and enforce their own
/// immutable active-operation deadlines; host queue time is deliberately not
/// charged to an active-render deadline. A control borrows this service and
/// never disposes it.
/// </remarks>
public interface IMarkdownSvgRenderer
{
    /// <summary>
    /// Produces inert, bounded SVG bytes for host metadata inspection and
    /// provider opening. Implementations must reject active content, external
    /// references, and over-budget input before returning. This runs off the
    /// UI thread; the provider must still repeat its authoritative checks.
    /// </summary>
    MarkdownSvgSourcePreparation PrepareSource(byte[] source, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a monotonic token that changes when environment state can alter a
    /// rendered result, such as the installed-font generation.
    /// </summary>
    /// <remarks>
    /// This synchronous getter is also queried during UI publication. Return a
    /// thread-safe, inexpensive snapshot; do not perform I/O, wait for worker
    /// startup, or dispatch synchronously to a UI thread from this getter.
    /// </remarks>
    long CacheGeneration => 0;

    /// <summary>
    /// Raised after <see cref="CacheGeneration"/> changes so hosts can discard
    /// affected GPU rasters and schedule replacement renders.
    /// </summary>
    event EventHandler? CacheInvalidated
    {
        add { }
        remove { }
    }

    /// <summary>Opens one sanitized SVG document.</summary>
    /// <param name="request">Immutable source and rendering-environment inputs.</param>
    /// <param name="cancellationToken">Cancels the open operation.</param>
    /// <returns>A disposable document handle owned by the caller.</returns>
    /// <exception cref="MarkdownSvgException">The SVG cannot be opened within the provider's supported policy.</exception>
    ValueTask<IMarkdownSvgDocument> OpenAsync(
        MarkdownSvgOpenRequest request,
        CancellationToken cancellationToken = default);
}
