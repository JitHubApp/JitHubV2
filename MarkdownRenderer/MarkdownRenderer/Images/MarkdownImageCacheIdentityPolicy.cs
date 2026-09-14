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
        if (!string.IsNullOrWhiteSpace(asset.CacheKey))
            return asset.CacheKey;

        return asset.ResolvedUri is { IsAbsoluteUri: true } resolvedUri
            ? resolvedUri.AbsoluteUri
            : string.Empty;
    }

    private static bool IsSelfContainedDataUri(string? source) =>
        source?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true;
}
