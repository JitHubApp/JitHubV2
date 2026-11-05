using System.Threading.Tasks;

namespace JitHub.Services;

public sealed record GitHubCachedImage(
    string FilePath,
    byte[] Bytes,
    string? ContentType,
    bool IsFromCache,
    bool IsStale = false,
    Task<GitHubCachedImage?>? RefreshTask = null);
