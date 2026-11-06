using System.Threading.Tasks;

namespace JitHub.Markdown.Images;

public sealed record GitHubCachedImage(
    string FilePath,
    byte[] Bytes,
    string? ContentType,
    bool IsFromCache,
    bool IsStale = false,
    Task<GitHubCachedImage?>? RefreshTask = null);
