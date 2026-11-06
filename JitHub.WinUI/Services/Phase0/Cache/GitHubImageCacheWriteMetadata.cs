using System;

namespace JitHub.Services;

public sealed record GitHubImageCacheWriteMetadata(
    string? ETag,
    DateTimeOffset? LastModified,
    string? ContentType);
