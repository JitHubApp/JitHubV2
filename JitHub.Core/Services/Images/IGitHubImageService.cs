using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images.Caching;

namespace JitHub.Services;

/// <summary>
/// The app's GitHub image service: the Markdown cache operations plus the
/// unscoped read used by avatars and other app images, and invalidation.
/// </summary>
public interface IGitHubImageService : IGitHubMarkdownImageCache
{
    Task<GitHubCachedImage?> GetAsync(string sourceUrl, CancellationToken cancellationToken = default);

    Task InvalidateAsync(string sourceUrl, CancellationToken cancellationToken = default);
}
