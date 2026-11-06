using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Markdown.Images;

public delegate Task<GitHubImageDownload?> GitHubImageFetcher(
    GitHubImageCacheEntry? cachedEntry,
    CancellationToken cancellationToken);
