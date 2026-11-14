using System;

namespace JitHub.Core.Services.Images.Caching;

public sealed record GitHubImageCacheWriteMetadata(
    string? ETag,
    DateTimeOffset? LastModified,
    string? ContentType);
