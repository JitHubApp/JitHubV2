using System;

namespace MarkdownRenderer.Images;

internal static class MarkdownImageCacheIdentityPolicy
{
    public static bool CanUseSourceBeforeResolution(bool hasResolver, string? source = null) =>
        !hasResolver || IsSelfContainedDataUri(source);

    public static bool CanUseSourceAfterResolution(MarkdownImageResolution resolution) =>
        !resolution.IsHandled;

    private static bool IsSelfContainedDataUri(string? source) =>
        source?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true;
}
