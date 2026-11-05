using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services;

public interface IGitHubImageService
{
    Task<GitHubCachedImage?> GetAsync(string sourceUrl, CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> GetAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> GetAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> TryGetCachedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> TryGetCachedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> GetOrFetchAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> GetOrFetchAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken = default);

    Task InvalidateAsync(string sourceUrl, CancellationToken cancellationToken = default);
}
