using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Performance;

internal interface IMarkdownPerformanceDocumentScope : IMarkdownImageResolver, IDisposable
{
    ValueTask<IMarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
        string source,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken);
    /// <summary>
    /// Returns only scalar dimensions already derived from a prefetched,
    /// source-cache-resident raster. Implementations must not resolve sources,
    /// expose bytes, promote cache recency, or acquire source admission here.
    /// </summary>
    bool TryGetPrefetchedRasterIntrinsicMetadata(
        string source,
        out RasterIntrinsicMetadata metadata)
    {
        metadata = default;
        return false;
    }
    bool IsDisposed { get; }
    CancellationToken CancellationToken { get; }
    bool Matches(
        IMarkdownPerformanceSessionInternal session,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context);
    Task PrefetchAsync(IReadOnlyList<string> sources, CancellationToken cancellationToken);
}
