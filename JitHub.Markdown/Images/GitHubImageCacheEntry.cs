using System;

namespace JitHub.Markdown.Images;

public sealed record GitHubImageCacheEntry(
    string CacheKey,
    string FilePath,
    long ByteLength,
    DateTimeOffset CachedAt,
    DateTimeOffset LastAccessedAt,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    string? ContentType = null);
