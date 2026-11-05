using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public delegate Task<GitHubImageDownload?> GitHubImageFetcher(
    GitHubImageCacheEntry? cachedEntry,
    CancellationToken cancellationToken);
