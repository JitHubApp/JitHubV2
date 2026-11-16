using JitHub.Markdown.Images.Caching;

namespace JitHub.Core.Services.Images.Caching;

public sealed record GitHubImageCacheRead(
    GitHubImageCacheEntry Entry,
    byte[] Bytes);
