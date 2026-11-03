using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubMarkdownImageResolverTests
{
    private static readonly MarkdownDocumentSource Readme = new(
        "readme:octo/repo",
        Owner: "octo",
        Repository: "repo",
        Ref: "main",
        Path: "README.md");

    [Fact]
    public async Task CamoFallbackReusesOneAccountsRenderedReadmeMapAndDropsItWhenTheCredentialChanges()
    {
        IGitHubClientService client = Substitute.For<IGitHubClientService>();
        client.GetRenderedReadmeHtmlAsync(
                Arg.Any<string>(), "octo", "repo", "main", Arg.Any<CancellationToken>())
            .Returns("<p>No mapped images.</p>");
        var resolver = new GitHubMarkdownImageResolver(
            client,
            Substitute.For<IGitHubImageService>(),
            Substitute.For<IMarkdownRemoteImagePolicy>());
        var context = new MarkdownImageResolveContext(
            new Uri("https://github.com/octo/repo/blob/main/"),
            "README.md",
            DocumentSource: Readme);

        resolver.SetAccessToken("first-account");
        MarkdownImageResolution first = await resolver.ResolveAsync(
            "http://example.com/badge.png", context, CancellationToken.None);
        await resolver.ResolveAsync("http://example.com/other.png", context, CancellationToken.None);
        resolver.SetAccessToken("second-account");
        await resolver.ResolveAsync("http://example.com/badge.png", context, CancellationToken.None);

        // Nothing serves the image, so every fallback, including the README's
        // server-rendered Camo map, was consulted before reporting it unavailable.
        Assert.Equal(MarkdownImageResolution.Unavailable, first);
        await client.Received(1).GetRenderedReadmeHtmlAsync(
            "first-account", "octo", "repo", "main", Arg.Any<CancellationToken>());
        await client.Received(1).GetRenderedReadmeHtmlAsync(
            "second-account", "octo", "repo", "main", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepositoryImageIsRoutedToTheRepositoryStageAndOtherSourcesToThePublicStage()
    {
        const string rawUrl = "https://raw.githubusercontent.com/octo/repo/main/docs/logo.png";
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        images.GetAsync(rawUrl, GitHubImageFetchScope.TrustedGitHub, Arg.Any<CancellationToken>())
            .Returns(new GitHubCachedImage("cache://raw", [1, 2, 3], "image/png", IsFromCache: false));
        IMarkdownRemoteImagePolicy policy = Substitute.For<IMarkdownRemoteImagePolicy>();
        policy.Evaluate(Arg.Any<Uri>(), Arg.Any<bool>()).Returns(new MarkdownRemoteImageDecision(
            MarkdownRemoteImageAccess.AllowNetwork, MarkdownImageUnavailableReason.None, IsThirdParty: false));
        var resolver = new GitHubMarkdownImageResolver(Substitute.For<IGitHubClientService>(), images, policy);

        MarkdownImageResolution repositoryImage = await resolver.ResolveAsync(
            "docs/logo.png",
            new MarkdownImageResolveContext(null, DocumentSource: Readme),
            CancellationToken.None);
        MarkdownImageResolution unownedRelativeImage = await resolver.ResolveAsync(
            "docs/logo.png",
            new MarkdownImageResolveContext(null),
            CancellationToken.None);

        Assert.Equal(new Uri(rawUrl), repositoryImage.Asset?.ResolvedUri);
        Assert.Equal(MarkdownImageResolution.NotHandled, unownedRelativeImage);
    }

    [Fact]
    public void ProviderReusesOneResolverFacadeForEveryView()
    {
        var resolver = new GitHubMarkdownImageResolver(
            Substitute.For<IGitHubClientService>(),
            Substitute.For<IGitHubImageService>(),
            Substitute.For<IMarkdownRemoteImagePolicy>());

        IMarkdownImageResolver first = resolver.CreateMarkdownImageResolver();

        Assert.Same(resolver, first);
        Assert.Same(first, resolver.CreateMarkdownImageResolver());
    }
}
