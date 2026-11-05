using JitHub.Markdown.Images;

namespace JitHub.Services;

public sealed record GitHubImageCacheRead(
    GitHubImageCacheEntry Entry,
    byte[] Bytes);
