using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>
/// Optional capability of an <see cref="IMarkdownSvgDocument"/> that moves
/// queued renders ahead when their image becomes visible.
/// </summary>
public interface IMarkdownSvgPrioritizedDocument
{
    /// <summary>
    /// Renders like <see cref="IMarkdownSvgDocument.RenderAsync"/>, and moves
    /// the render ahead in the queue if <paramref name="demand"/> becomes
    /// visible while it waits.
    /// </summary>
    /// <param name="request">The raster size and colors to render.</param>
    /// <param name="demand">The visibility of the image.</param>
    /// <param name="cancellationToken">Cancels the render.</param>
    /// <returns>The rendered raster, which the caller owns.</returns>
    ValueTask<MarkdownSvgRaster> RenderAsync(
        MarkdownSvgRenderRequest request,
        MarkdownSvgRenderDemand demand,
        CancellationToken cancellationToken);
}
