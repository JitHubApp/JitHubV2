using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubPublicMarkdownImageStageTests
{
    private const string PublicSource = "https://assets.example.test/badge.png";
    private const string CamoSource = "https://camo.githubusercontent.com/0123456789abcdef0123456789abcdef01234567/";

    private static readonly MarkdownDocumentSource Readme = new(
        "readme:octo/repo",
        Owner: "octo",
        Repository: "repo",
        Ref: "main",
        Path: "README.md");

    private readonly IGitHubImageService _images = Substitute.For<IGitHubImageService>();
    private readonly IGitHubClientService _client = Substitute.For<IGitHubClientService>();
    private readonly IMarkdownRemoteImagePolicy _policy = Substitute.For<IMarkdownRemoteImagePolicy>();

    [Theory]
    [InlineData("docs/badge.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("ftp://assets.example.test/badge.png")]
    public async Task SourceTheHostDoesNotOwn_IsLeftToTheRenderer(string source)
    {
        using GitHubMarkdownImageCandidate candidate = await CreateStage().ResolveAsync(
            source, Context(), null, useLeasedSources: false, CancellationToken.None);

        Assert.Equal(MarkdownImageResolution.NotHandled, candidate.Resolution);
        _policy.DidNotReceiveWithAnyArgs().Evaluate(default!, default);
    }

    [Fact]
    public async Task BlockedByPolicy_ReportsThePolicyReasonWithoutFetching()
    {
        Decide(MarkdownRemoteImageAccess.Block, MarkdownImageUnavailableReason.RemoteContentBlocked);

        using GitHubMarkdownImageCandidate candidate = await CreateStage().ResolveAsync(
            PublicSource, Context(), null, useLeasedSources: false, CancellationToken.None);

        Assert.Equal(
            MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.RemoteContentBlocked),
            candidate.Resolution);
        Assert.Empty(_images.ReceivedCalls());
    }

    [Theory]
    [InlineData(true, GitHubImageFetchScope.UserApprovedHttps)]
    [InlineData(false, GitHubImageFetchScope.TrustedGitHub)]
    public async Task AllowedNetworkImage_IsFetchedUnderItsPolicyScope(bool isThirdParty, GitHubImageFetchScope scope)
    {
        Decide(MarkdownRemoteImageAccess.AllowNetwork, isThirdParty: isThirdParty);
        _images.GetAsync(PublicSource, scope, Arg.Any<CancellationToken>()).Returns(Image("public"));

        using GitHubMarkdownImageCandidate candidate = await CreateStage().ResolveAsync(
            PublicSource, Context(), null, useLeasedSources: false, CancellationToken.None);

        AssertResolved(candidate, "public", PublicSource);
    }

    [Fact]
    public async Task HttpSource_IsUpgradedToHttpsBeforeFetching()
    {
        Decide(MarkdownRemoteImageAccess.AllowNetwork, isThirdParty: true);
        _images.GetAsync(PublicSource, GitHubImageFetchScope.UserApprovedHttps, Arg.Any<CancellationToken>())
            .Returns(Image("upgraded"));

        using GitHubMarkdownImageCandidate candidate = await CreateStage().ResolveAsync(
            "http://assets.example.test/badge.png", Context(), null, useLeasedSources: false, CancellationToken.None);

        AssertResolved(candidate, "upgraded", PublicSource);
    }

    [Fact]
    public async Task CacheOnlyHit_IsServedFromTheCacheWithoutANetworkRead()
    {
        Decide(MarkdownRemoteImageAccess.CacheOnly, MarkdownImageUnavailableReason.MeteredConnection, isThirdParty: true);
        _images.TryGetCachedAsync(PublicSource, GitHubImageFetchScope.UserApprovedHttps, Arg.Any<CancellationToken>())
            .Returns(Image("cached"));

        using GitHubMarkdownImageCandidate candidate = await CreateStage().ResolveAsync(
            PublicSource, Context(), null, useLeasedSources: false, CancellationToken.None);

        AssertResolved(candidate, "cached", PublicSource);
        await _images.DidNotReceiveWithAnyArgs().GetAsync(default!, default, default);
    }

    [Fact]
    public async Task CacheOnlyMiss_ReportsThePolicyReasonWithoutFallbacks()
    {
        Decide(MarkdownRemoteImageAccess.CacheOnly, MarkdownImageUnavailableReason.Offline, isThirdParty: true);

        using GitHubMarkdownImageCandidate candidate = await CreateStage().ResolveAsync(
            PublicSource, Context(Readme), null, useLeasedSources: false, CancellationToken.None);

        Assert.Equal(MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Offline), candidate.Resolution);
        await _client.DidNotReceiveWithAnyArgs().GetRenderedReadmeHtmlAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task NetworkMiss_FallsBackToTheRenderedReadmeCamoMap()
    {
        const string camo = "https://camo.githubusercontent.com/mapped";
        Decide(MarkdownRemoteImageAccess.AllowNetwork, isThirdParty: true);
        _client.GetRenderedReadmeHtmlAsync("token", "octo", "repo", "main", Arg.Any<CancellationToken>())
            .Returns($"<img data-canonical-src='{PublicSource}' src='{camo}'>");
        _images.GetAsync(camo, GitHubImageFetchScope.TrustedGitHub, Arg.Any<CancellationToken>())
            .Returns(Image("camo"));

        using GitHubMarkdownImageCandidate candidate = await CreateStage().ResolveAsync(
            PublicSource, Context(Readme), null, useLeasedSources: false, CancellationToken.None);

        AssertResolved(candidate, "camo", camo);
    }

    [Fact]
    public async Task NetworkMissWithNoFallback_IsUnavailable()
    {
        Decide(MarkdownRemoteImageAccess.AllowNetwork, isThirdParty: true);

        using GitHubMarkdownImageCandidate candidate = await CreateStage().ResolveAsync(
            PublicSource, Context(), null, useLeasedSources: false, CancellationToken.None);

        Assert.Equal(MarkdownImageResolution.Unavailable, candidate.Resolution);
    }

    [Fact]
    public async Task FailedFetch_StillTriesTheCamoFallbacks()
    {
        const string camo = "https://camo.githubusercontent.com/mapped";
        Decide(MarkdownRemoteImageAccess.AllowNetwork, isThirdParty: true);
        _images.GetAsync(PublicSource, Arg.Any<GitHubImageFetchScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubCachedImage?>(
                new HttpRequestException("reset", null, HttpStatusCode.BadGateway)));
        _client.GetRenderedReadmeHtmlAsync("token", "octo", "repo", "main", Arg.Any<CancellationToken>())
            .Returns($"<img data-canonical-src='{PublicSource}' src='{camo}'>");
        _images.GetAsync(camo, GitHubImageFetchScope.TrustedGitHub, Arg.Any<CancellationToken>())
            .Returns(Image("camo"));

        using GitHubMarkdownImageCandidate candidate = await CreateStage().ResolveAsync(
            PublicSource, Context(Readme), null, useLeasedSources: false, CancellationToken.None);

        AssertResolved(candidate, "camo", camo);
    }

    [Fact]
    public async Task CanonicalCamoUrl_IsServedByCamoWhenItAnswers()
    {
        string camo = CanonicalCamo(PublicSource);
        Decide(MarkdownRemoteImageAccess.AllowNetwork);
        _images.GetAsync(camo, GitHubImageFetchScope.TrustedGitHub, Arg.Any<CancellationToken>())
            .Returns(Image("camo"));

        using GitHubMarkdownImageCandidate candidate = await CreateStage().ResolveAsync(
            camo, Context(), null, useLeasedSources: false, CancellationToken.None);

        AssertResolved(candidate, "camo", camo);
    }

    [Fact]
    public async Task CanonicalCamoUrl_FallsBackToItsOriginAsAnUntrustedRequest()
    {
        string camo = CanonicalCamo(PublicSource);
        Decide(MarkdownRemoteImageAccess.AllowNetwork);
        _images.GetAsync(PublicSource, GitHubImageFetchScope.UserApprovedHttps, Arg.Any<CancellationToken>())
            .Returns(Image("origin"));

        using GitHubMarkdownImageCandidate candidate = await CreateStage().ResolveAsync(
            camo, Context(), null, useLeasedSources: false, CancellationToken.None);

        AssertResolved(candidate, "origin", PublicSource);
        await _images.DidNotReceive().GetAsync(
            PublicSource, GitHubImageFetchScope.TrustedGitHub, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        Decide(MarkdownRemoteImageAccess.AllowNetwork, isThirdParty: true);
        _images.GetAsync(PublicSource, Arg.Any<GitHubImageFetchScope>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<GitHubCachedImage?>(cancellation.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateStage().ResolveAsync(
            PublicSource, Context(Readme), null, useLeasedSources: false, cancellation.Token));
        await _client.DidNotReceiveWithAnyArgs().GetRenderedReadmeHtmlAsync(default!, default!, default!, default, default);
    }

    private GitHubPublicMarkdownImageStage CreateStage()
    {
        var fetch = new GitHubMarkdownImageFetch(_images);
        var maps = new GitHubRenderedReadmeImageMaps(_client, static () => "token");
        return new GitHubPublicMarkdownImageStage(
            _policy,
            fetch,
            new GitHubMarkdownCamoImageFallbacks(_policy, maps, fetch));
    }

    private void Decide(
        MarkdownRemoteImageAccess access,
        MarkdownImageUnavailableReason reason = MarkdownImageUnavailableReason.None,
        bool isThirdParty = false) =>
        _policy.Evaluate(Arg.Any<Uri>(), Arg.Any<bool>())
            .Returns(new MarkdownRemoteImageDecision(access, reason, isThirdParty));

    private static MarkdownImageResolveContext Context(MarkdownDocumentSource? document = null) =>
        new(new Uri("https://github.com/octo/repo/blob/main/"), "README.md", DocumentSource: document);

    private static GitHubCachedImage Image(string text) =>
        new($"cache://{text}", Encoding.UTF8.GetBytes(text), "image/png", IsFromCache: false);

    private static string CanonicalCamo(string origin) =>
        CamoSource + Convert.ToHexString(Encoding.UTF8.GetBytes(origin)).ToLowerInvariant();

    private static void AssertResolved(GitHubMarkdownImageCandidate candidate, string text, string resolvedUri)
    {
        MarkdownImageAsset asset = Assert.IsType<MarkdownImageAsset>(candidate.Resolution.Asset);
        Assert.Equal(text, Encoding.UTF8.GetString(asset.Bytes));
        Assert.Equal(new Uri(resolvedUri), asset.ResolvedUri);
        Assert.Equal($"cache://{text}", asset.CacheKey);
    }
}
