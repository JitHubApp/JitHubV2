using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Optional image-resolver capability that returns source bytes together with the admission
/// reservation covering their lifetime.
/// </summary>
/// <remarks>
/// Implementations must reserve capacity before allocating, reading, or otherwise retaining
/// source bytes, and must keep every covered byte buffer within the reservation's lifetime.
/// The renderer cannot discover or account for arbitrary hidden allocations made inside a
/// resolver. Resolver-owned caches have separate lifetimes and must be bounded independently;
/// this capability only carries ownership for the returned resolution and its retained handles.
/// </remarks>
public interface IMarkdownLeasedImageResolver : IMarkdownImageResolver
{
    /// <summary>
    /// Resolves an image while transferring the source-byte reservation into a disposable
    /// resolution lease.
    /// </summary>
    /// <param name="source">The Markdown image source.</param>
    /// <param name="context">The document and image-policy context.</param>
    /// <param name="admission">Admission used before allocating or retaining source bytes.</param>
    /// <param name="cancellationToken">Cancellation for resolution and admission.</param>
    /// <returns>
    /// A lease that must remain alive for the entire time returned asset bytes are read. The
    /// resolver transfers reservation ownership only after constructing the lease successfully.
    /// </returns>
    ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
        string source,
        MarkdownImageResolveContext context,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken);
}
