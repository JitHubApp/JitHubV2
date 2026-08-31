using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownImageCacheIdentityPolicyTests
{
    [Fact]
    public void ResolverInstalled_DefersSourceIdentityUntilResolution()
    {
        Assert.False(MarkdownImageCacheIdentityPolicy.CanUseSourceBeforeResolution(hasResolver: true));
        Assert.True(MarkdownImageCacheIdentityPolicy.CanUseSourceBeforeResolution(hasResolver: false));
    }

    [Fact]
    public void SelfContainedDataUri_RemainsSafeWithResolverInstalled()
    {
        Assert.True(MarkdownImageCacheIdentityPolicy.CanUseSourceBeforeResolution(
            hasResolver: true,
            "data:image/svg+xml,%3Csvg%3E%3C/svg%3E"));
        Assert.False(MarkdownImageCacheIdentityPolicy.CanUseSourceBeforeResolution(
            hasResolver: true,
            "https://github.com/octocat.png"));
    }

    [Fact]
    public void NotHandledResolution_ReenablesSafeFallbackSourceCache()
    {
        Assert.True(MarkdownImageCacheIdentityPolicy.CanUseSourceAfterResolution(
            MarkdownImageResolution.NotHandled));
        Assert.False(MarkdownImageCacheIdentityPolicy.CanUseSourceAfterResolution(
            MarkdownImageResolution.Unavailable));
        Assert.False(MarkdownImageCacheIdentityPolicy.CanUseSourceAfterResolution(
            MarkdownImageResolution.Resolved(new MarkdownImageAsset([1], "image/png"))));
    }
}
