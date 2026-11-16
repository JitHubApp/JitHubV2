using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Markdown.Images.Caching;

/// <summary>
/// Optional host capability for resolving GitHub image bytes with ownership that
/// extends through the consumer's use of the returned source buffer.
/// </summary>
/// <remarks>
/// Implementations must reserve source bytes before allocating or materializing
/// them and transfer the reservation into the returned lease. Cache retention is
/// a separate bounded owner and is not covered by this reservation. This capability
/// is intentionally separate from <see cref="IGitHubMarkdownImageCache"/> so legacy
/// providers do not appear to support source-lifetime leases.
/// </remarks>
public interface IGitHubLeasedImageService
{
    Task<GitHubImageSourceLease?> GetLeasedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default);

    Task<GitHubImageSourceLease?> TryGetCachedLeasedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default);

    Task<GitHubImageSourceLease?> GetOrFetchLeasedAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default);
}
