using System;

namespace MarkdownRenderer.Images;

internal static class MarkdownImageCacheIdentityPolicy
{
    public static bool CanUseSourceBeforeResolution(bool hasResolver, string? source = null) =>
        !hasResolver || IsSelfContainedDataUri(source);

    public static bool CanUseSourceAfterResolution(MarkdownImageResolution resolution) =>
        !resolution.IsHandled;

    public static string GetResolvedAssetKey(MarkdownImageAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        // A resolved URI is not a security partition: two authenticated
        // accounts may receive different bytes from the same URL. Only the
        // resolver can supply a process-wide identity safe for GPU reuse.
        return string.IsNullOrWhiteSpace(asset.CacheKey)
            ? string.Empty
            : asset.CacheKey;
    }

    private static bool IsSelfContainedDataUri(string? source) =>
        source?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true;
}
