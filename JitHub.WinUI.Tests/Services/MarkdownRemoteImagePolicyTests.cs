using System;
using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownRemoteImagePolicyTests
{
    [Theory]
    [InlineData("https://github.com/openai/openai-python/raw/main/logo.png")]
    [InlineData("https://avatars.githubusercontent.com/u/1?v=4")]
    [InlineData("https://raw.githubusercontent.com/openai/openai-python/main/logo.png")]
    [InlineData("https://github.githubassets.com/assets/logo.png")]
    public void TrustedGitHubImages_LoadAutomatically(string source)
    {
        MarkdownRemoteImagePolicy policy = CreatePolicy();

        MarkdownRemoteImageDecision decision = policy.Evaluate(new Uri(source), userInitiated: false);

        Assert.Equal(MarkdownRemoteImageAccess.AllowNetwork, decision.Access);
        Assert.False(decision.IsThirdParty);
        Assert.Equal(MarkdownImageUnavailableReason.None, decision.UnavailableReason);
    }

    [Theory]
    [InlineData("https://images.example.test/tracker.png")]
    [InlineData("https://github.com.evil.test/tracker.png")]
    [InlineData("https://evilgithubusercontent.com/tracker.png")]
    public void ThirdPartyImages_AreBlockedByDefault(string source)
    {
        MarkdownRemoteImagePolicy policy = CreatePolicy();

        MarkdownRemoteImageDecision decision = policy.Evaluate(new Uri(source), userInitiated: false);

        Assert.Equal(MarkdownRemoteImageAccess.CacheOnly, decision.Access);
        Assert.True(decision.IsThirdParty);
        Assert.Equal(MarkdownImageUnavailableReason.RemoteContentBlocked, decision.UnavailableReason);
    }

    [Fact]
    public void ThirdPartyImages_LoadOnlyAfterDocumentConsent()
    {
        Uri source = new("https://images.example.test/readme.png");
        MarkdownRemoteImagePolicy policy = CreatePolicy();

        Assert.Equal(
            MarkdownRemoteImageAccess.AllowNetwork,
            policy.Evaluate(source, userInitiated: true).Access);
        Assert.Equal(
            MarkdownRemoteImageAccess.CacheOnly,
            policy.Evaluate(source, userInitiated: false).Access);
    }

    [Fact]
    public void OfflineAndMeteredConnections_UseCacheOnly()
    {
        Uri source = new("https://images.example.test/readme.png");

        MarkdownRemoteImageDecision offline =
            CreatePolicy(online: false).Evaluate(source, userInitiated: true);
        MarkdownRemoteImageDecision metered =
            CreatePolicy(metered: true).Evaluate(
                new Uri("https://raw.githubusercontent.com/owner/repo/main/image.png"),
                userInitiated: false);

        Assert.Equal(MarkdownRemoteImageAccess.CacheOnly, offline.Access);
        Assert.Equal(MarkdownImageUnavailableReason.Offline, offline.UnavailableReason);
        Assert.Equal(MarkdownRemoteImageAccess.CacheOnly, metered.Access);
        Assert.Equal(MarkdownImageUnavailableReason.MeteredConnection, metered.UnavailableReason);
    }

    [Theory]
    [InlineData("http://github.com/image.png", MarkdownImageUnavailableReason.InsecureRemoteContent)]
    [InlineData("file:///private/image.png", MarkdownImageUnavailableReason.Unavailable)]
    public void NonHttpsRemoteSources_AreNeverLoaded(
        string source,
        MarkdownImageUnavailableReason expectedReason)
    {
        MarkdownRemoteImageDecision decision =
            CreatePolicy().Evaluate(new Uri(source), userInitiated: true);

        Assert.Equal(MarkdownRemoteImageAccess.Block, decision.Access);
        Assert.Equal(expectedReason, decision.UnavailableReason);
    }

    private static MarkdownRemoteImagePolicy CreatePolicy(
        bool online = true,
        bool metered = false) =>
        new(new StubNetworkState(online, metered));

    private sealed class StubNetworkState(bool online, bool metered) : IMarkdownNetworkState
    {
        public bool IsOnline => online;

        public bool IsMetered => metered;
    }
}
