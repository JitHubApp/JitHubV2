namespace JitHub.Core.Services.Images.Caching;

internal sealed record GitHubImageCacheManifest(
    string? PayloadFileName,
    GitHubImageCacheWriteMetadata Metadata,
    string? AccountPartition = null);
