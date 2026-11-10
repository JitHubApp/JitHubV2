using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images;
using JitHub.Markdown.Images.Caching;
using JitHub.Markdown.Images.Internal;
using JitHub.Markdown.Images.RemoteContent;
using JitHub.Markdown.Images.Repository;
using JitHub.Markdown.Images.Repository.Internal;
using MarkdownRenderer.Images;
using NSubstitute;
using Xunit;

namespace JitHub.Markdown.Tests.Images.Repository.Internal;

public sealed class GitHubRepositoryMarkdownImageStageTests
{
    private const string RelativeSource = "docs/logo.png";
    private const string RawUrl = "https://raw.githubusercontent.com/octo/repo/main/docs/logo.png";
    private const string LfsRouteUrl = "https://github.com/octo/repo/raw/main/docs/logo.png";

    private const string LfsPointer =
        "version https://git-lfs.github.com/spec/v1\n" +
        "oid sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n" +
        "size 1234\n";

    private static readonly MarkdownDocumentSource Readme = new(
        "readme:octo/repo",
        Owner: "octo",
        Repository: "repo",
        Ref: "main",
        Path: "README.md");

    private readonly IGitHubMarkdownImageCache _images = Substitute.For<IGitHubMarkdownImageCache>();
    private readonly IGitHubMarkdownContentClient _client = Substitute.For<IGitHubMarkdownContentClient>();
    private readonly IMarkdownRemoteImagePolicy _policy = Substitute.For<IMarkdownRemoteImagePolicy>();
    private string? _token = "token";

    public GitHubRepositoryMarkdownImageStageTests()
    {
        // The cache runs the fetcher it is handed and stores what it returns.
        _images.GetOrFetchAsync(Arg.Any<string>(), Arg.Any<GitHubImageFetcher>(), Arg.Any<CancellationToken>())
            .Returns(call => RunFetcherAsync(call.ArgAt<string>(0), call.ArgAt<GitHubImageFetcher>(1)));
    }

    [Fact]
    public async Task BlockedByPolicy_ReportsThePolicyReasonWithoutAnyRead()
    {
        Decide(MarkdownRemoteImageAccess.Block, MarkdownImageUnavailableReason.RemoteContentBlocked);

        using GitHubMarkdownImageCandidate candidate = await ResolveAsync(RelativeSource);

        Assert.Equal(
            MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.RemoteContentBlocked),
            candidate.Resolution);
        Assert.Empty(_images.ReceivedCalls());
        Assert.Empty(_client.ReceivedCalls());
    }

    [Fact]
    public async Task RawCdnHit_IsServedWithoutAnAuthenticatedRead()
    {
        Decide(MarkdownRemoteImageAccess.AllowNetwork);
        _images.GetAsync(RawUrl, GitHubImageFetchScope.TrustedGitHub, Arg.Any<CancellationToken>())
            .Returns(Image("raw"));

        using GitHubMarkdownImageCandidate candidate = await ResolveAsync(RelativeSource);

        AssertResolved(candidate, "raw", "image/png", RawUrl);
        Assert.Empty(_client.ReceivedCalls());
        await _images.DidNotReceiveWithAnyArgs().GetOrFetchAsync(default!, default(GitHubImageFetcher)!, default);
    }

    [Fact]
    public async Task RawCdnMiss_ReadsTheRepositoryContentsWithTheAccountCredential()
    {
        Decide(MarkdownRemoteImageAccess.AllowNetwork);
        _images.GetAsync(RawUrl, GitHubImageFetchScope.TrustedGitHub, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubCachedImage?>(
                new HttpRequestException("private", null, HttpStatusCode.NotFound)));
        Contents(Base64("contents"));

        using GitHubMarkdownImageCandidate candidate = await ResolveAsync(RelativeSource);

        AssertResolved(candidate, "contents", "image/png", RawUrl);
        await _client.Received(1).GetRepositoryFileAsync(
            "token", "octo", "repo", "docs/logo.png", "main", Arg.Any<CancellationToken>());
        await _client.DidNotReceiveWithAnyArgs().GetBlobAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task ContentsWithoutInlineBytes_AreReadThroughTheBlobApi()
    {
        Decide(MarkdownRemoteImageAccess.AllowNetwork);
        Contents(content: null, sha: "abc123");
        _client.GetBlobAsync("token", "octo", "repo", "abc123", Arg.Any<CancellationToken>())
            .Returns(new GitHubMarkdownEncodedContent(Base64("blob"), "base64", "abc123"));

        using GitHubMarkdownImageCandidate candidate = await ResolveAsync(RelativeSource);

        AssertResolved(candidate, "blob", "image/png", RawUrl);
    }

    [Fact]
    public async Task LfsPointer_IsFollowedThroughTheRepositoryRawRoute()
    {
        Decide(MarkdownRemoteImageAccess.AllowNetwork);
        Contents(Base64(LfsPointer));
        _images.GetAsync(LfsRouteUrl, GitHubImageFetchScope.TrustedGitHub, Arg.Any<CancellationToken>())
            .Returns(new GitHubCachedImage("cache://lfs", Encoding.UTF8.GetBytes("lfs media"), "image/png", false));

        using GitHubMarkdownImageCandidate candidate = await ResolveAsync(RelativeSource);

        AssertResolved(candidate, "lfs media", "image/png", RawUrl);
    }

    [Fact]
    public async Task EmptyRepositoryImage_IsUnavailable()
    {
        Decide(MarkdownRemoteImageAccess.AllowNetwork);
        Contents(content: null);

        using GitHubMarkdownImageCandidate candidate = await ResolveAsync(RelativeSource);

        Assert.Equal(MarkdownImageResolution.Unavailable, candidate.Resolution);
    }

    [Fact]
    public async Task SignedOut_RelativeImageIsUnavailable()
    {
        _token = null;
        Decide(MarkdownRemoteImageAccess.AllowNetwork);

        using GitHubMarkdownImageCandidate candidate = await ResolveAsync(RelativeSource);

        Assert.Equal(MarkdownImageResolution.Unavailable, candidate.Resolution);
        Assert.Empty(_client.ReceivedCalls());
    }

    [Fact]
    public async Task AuthoredRawUrlThatTheRepositoryCannotRead_FallsBackToTheRawCdnDirectly()
    {
        const string directRaw = RawUrl + "?jithub-raw-fallback=1";
        _token = null;
        Decide(MarkdownRemoteImageAccess.AllowNetwork);
        _images.GetAsync(directRaw, GitHubImageFetchScope.TrustedGitHub, Arg.Any<CancellationToken>())
            .Returns(Image("direct"));

        using GitHubMarkdownImageCandidate candidate = await ResolveAsync(RawUrl);

        AssertResolved(candidate, "direct", "image/png", RawUrl);
    }

    [Fact]
    public async Task CacheOnlyHit_IsServedWithoutAnyNetworkRead()
    {
        Decide(MarkdownRemoteImageAccess.CacheOnly, MarkdownImageUnavailableReason.MeteredConnection);
        _images.TryGetCachedAsync(RawUrl, GitHubImageFetchScope.TrustedGitHub, Arg.Any<CancellationToken>())
            .Returns(Image("cached"));

        using GitHubMarkdownImageCandidate candidate = await ResolveAsync(RelativeSource);

        AssertResolved(candidate, "cached", "image/png", RawUrl);
        await _images.DidNotReceiveWithAnyArgs().GetAsync(default!, default, default);
        Assert.Empty(_client.ReceivedCalls());
    }

    [Fact]
    public async Task CacheOnlyMiss_ReportsThePolicyReason()
    {
        Decide(MarkdownRemoteImageAccess.CacheOnly, MarkdownImageUnavailableReason.Offline);

        using GitHubMarkdownImageCandidate candidate = await ResolveAsync(RelativeSource);

        Assert.Equal(MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Offline), candidate.Resolution);
        Assert.Empty(_client.ReceivedCalls());
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        Decide(MarkdownRemoteImageAccess.CacheOnly);
        _images.TryGetCachedAsync(RawUrl, GitHubImageFetchScope.TrustedGitHub, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<GitHubCachedImage?>(cancellation.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResolveAsync(RelativeSource, cancellation.Token));
    }

    private Task<GitHubMarkdownImageCandidate> ResolveAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        Assert.True(GitHubMarkdownImageUrlResolver.TryResolve(source, Readme, out GitHubMarkdownImageReference reference));
        var stage = new GitHubRepositoryMarkdownImageStage(
            _client,
            rawImageClient: null,
            _policy,
            new GitHubMarkdownImageFetch(_images),
            () => _token ?? throw new InvalidOperationException("GitHub access token is not available."),
            static (_, _) => { });
        return stage.ResolveAsync(
            source,
            reference,
            new MarkdownImageResolveContext(null, DocumentSource: Readme),
            admission: null,
            useLeasedSources: false,
            cancellationToken);
    }

    private void Decide(
        MarkdownRemoteImageAccess access,
        MarkdownImageUnavailableReason reason = MarkdownImageUnavailableReason.None) =>
        _policy.Evaluate(Arg.Any<Uri>(), Arg.Any<bool>())
            .Returns(new MarkdownRemoteImageDecision(access, reason, IsThirdParty: false));

    private void Contents(string? content, string sha = "") =>
        _client.GetRepositoryFileAsync("token", "octo", "repo", "docs/logo.png", "main", Arg.Any<CancellationToken>())
            .Returns(new GitHubMarkdownEncodedContent(content, "base64", sha));

    private static async Task<GitHubCachedImage?> RunFetcherAsync(string sourceUrl, GitHubImageFetcher fetcher)
    {
        GitHubImageDownload? download = await fetcher(null, CancellationToken.None);
        return download?.Bytes is { Length: > 0 } bytes
            ? new GitHubCachedImage($"cache://{sourceUrl}", bytes, download.ContentType, IsFromCache: false)
            : null;
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static GitHubCachedImage Image(string text) =>
        new($"cache://{text}", Encoding.UTF8.GetBytes(text), "image/png", IsFromCache: false);

    private static void AssertResolved(
        GitHubMarkdownImageCandidate candidate,
        string text,
        string? contentType,
        string resolvedUri)
    {
        MarkdownImageAsset asset = Assert.IsType<MarkdownImageAsset>(candidate.Resolution.Asset);
        Assert.Equal(text, Encoding.UTF8.GetString(asset.Bytes));
        Assert.Equal(contentType, asset.ContentType);
        Assert.Equal(new Uri(resolvedUri), asset.ResolvedUri);
    }
}
