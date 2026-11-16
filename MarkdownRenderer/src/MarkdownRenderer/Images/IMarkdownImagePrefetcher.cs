using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>
/// Optional capability for hosts that can safely warm image source bytes without decoding
/// or realizing off-screen bitmaps. The renderer supplies only sources admitted by its parsed
/// Markdown and safe-HTML pipeline. Implementations must remain bounded and honor cancellation.
/// </summary>
public interface IMarkdownImagePrefetcher
{
    /// <summary>
    /// Warms a deduplicated set of image sources for a document. Prefetching is best-effort;
    /// normal <see cref="IMarkdownImageResolver.ResolveAsync"/> calls remain authoritative.
    /// </summary>
    ValueTask PrefetchAsync(
        IReadOnlyList<string> sources,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken);
}
