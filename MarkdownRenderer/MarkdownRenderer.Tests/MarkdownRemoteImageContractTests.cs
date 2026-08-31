using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownRemoteImageContractTests
{
    [Fact]
    public void ResolveContext_DefaultsToNoThirdPartyConsent()
    {
        MarkdownImageResolveContext context = new(new Uri("https://github.com/owner/repo/"));

        Assert.False(context.AllowThirdPartyRemoteImages);
    }

    [Theory]
    [InlineData(MarkdownImageUnavailableReason.RemoteContentBlocked)]
    [InlineData(MarkdownImageUnavailableReason.InsecureRemoteContent)]
    [InlineData(MarkdownImageUnavailableReason.Offline)]
    [InlineData(MarkdownImageUnavailableReason.MeteredConnection)]
    public void BlockedResolution_PreservesUnavailableReason(MarkdownImageUnavailableReason reason)
    {
        MarkdownImageResolution resolution = MarkdownImageResolution.Blocked(reason);

        Assert.True(resolution.IsHandled);
        Assert.Null(resolution.Asset);
        Assert.Equal(reason, resolution.UnavailableReason);
    }

    [Fact]
    public void UnavailableEvent_DoesNotExposeResolvedOrFetchedContent()
    {
        MarkdownImageUnavailableEventArgs args = new(
            "https://images.example.test/tracker.png",
            MarkdownImageUnavailableReason.RemoteContentBlocked);

        Assert.Equal("https://images.example.test/tracker.png", args.Source);
        Assert.Equal(MarkdownImageUnavailableReason.RemoteContentBlocked, args.Reason);
    }
}
