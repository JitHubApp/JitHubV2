using JitHub.Markdown.Images.Caching;

namespace JitHub.Services;

public sealed record GitHubImageCacheRead(
    GitHubImageCacheEntry Entry,
    byte[] Bytes);
