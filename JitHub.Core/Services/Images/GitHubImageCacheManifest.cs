namespace JitHub.Services;

internal sealed record GitHubImageCacheManifest(
    string? PayloadFileName,
    GitHubImageCacheWriteMetadata Metadata,
    string? AccountPartition = null);
