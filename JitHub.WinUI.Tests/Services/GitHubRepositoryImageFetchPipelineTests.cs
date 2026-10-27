using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubRepositoryImageFetchPipelineTests
{
    private static readonly Uri RawUri = new(
        "https://raw.githubusercontent.com/owner/repository/main/!/tags/implemented.svg");

    [Fact]
    public async Task ValidRawImageUsesCredentialFreeCdnScope()
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        GitHubCachedImage expected = new(
            "partitioned-cache-key", [1, 2, 3], "image/svg+xml", false);
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GitHubCachedImage?>(expected));
        int reportedFailures = 0;
        var outcomes = new List<GitHubRawImageFetchOutcome>();

        GitHubCachedImage? result = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            images, RawUri, _ => reportedFailures++, CancellationToken.None, outcomes.Add);

        Assert.Same(expected, result);
        Assert.Equal(0, reportedFailures);
        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.Success) },
            outcomes);
        await images.Received(1).GetAsync(
            RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RawImageSuccessOutcomeDoesNotImplyAnHttpRequestWhenSecondOperationUsesCache()
    {
        const string source = "https://raw.githubusercontent.com/owner/repository/main/image.png";
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "JitHubRawImageFetchPipelineTests",
            Guid.NewGuid().ToString("N"));
        var handler = new CountingPngHandler(pngBytes);
        var outcomes = new List<GitHubRawImageFetchOutcome>();

        try
        {
            var store = new GitHubImageCacheStore(cacheRoot, GitHubCachePolicy.Default);
            using HttpClient client = new(handler);
            using var service = new GitHubImageService(store, client);

            GitHubCachedImage? first = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
                service,
                new Uri(source),
                _ => throw new Xunit.Sdk.XunitException("Unexpected raw image failure"),
                CancellationToken.None,
                outcomes.Add);
            GitHubCachedImage? second = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
                service,
                new Uri(source),
                _ => throw new Xunit.Sdk.XunitException("Unexpected raw image failure"),
                CancellationToken.None,
                outcomes.Add);

            Assert.NotNull(first);
            Assert.False(first!.IsFromCache);
            Assert.NotNull(second);
            Assert.True(second!.IsFromCache);
            Assert.Equal(pngBytes, first.Bytes);
            Assert.Equal(pngBytes, second.Bytes);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(
                new[]
                {
                    new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.Success),
                    new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.Success),
                },
                outcomes);
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AdmittedRawImageCarriesTheRequestBudgetToTheReader()
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        IMarkdownImageSourceByteAdmission admission =
            Substitute.For<IMarkdownImageSourceByteAdmission>();
        GitHubCachedImage expected = new("partitioned-cache-key", [1], "image/svg+xml", false);
        var outcomes = new List<GitHubRawImageFetchOutcome>();
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                admission, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GitHubCachedImage?>(expected));

        GitHubCachedImage? actual = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            images, RawUri, _ => throw new Xunit.Sdk.XunitException("Unexpected report"),
            admission, CancellationToken.None, outcomes.Add);

        Assert.Same(expected, actual);
        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.Success) },
            outcomes);
        await images.Received(1).GetAsync(
            RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
            admission, Arg.Any<CancellationToken>());
        await images.DidNotReceiveWithAnyArgs().GetAsync(default!, default, default(CancellationToken));
    }

    [Fact]
    public async Task SpeculativeByteDeferralIsNotReportedAsARawImageFailure()
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        IMarkdownImageSourceByteAdmission admission =
            Substitute.For<IMarkdownImageSourceByteAdmission>();
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                admission, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubCachedImage?>(
                new MarkdownImageSourceDeferredException()));
        int reports = 0;
        var outcomes = new List<GitHubRawImageFetchOutcome>();

        await Assert.ThrowsAsync<MarkdownImageSourceDeferredException>(() =>
            GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
                images, RawUri, _ => reports++, admission, CancellationToken.None, outcomes.Add));

        Assert.Equal(0, reports);
        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.SourceDeferred) },
            outcomes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task PrivateOrMissingRawImagePermitsAuthenticatedFallback(HttpStatusCode status)
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubCachedImage?>(
                new HttpRequestException("Raw object unavailable", null, status)));
        int reportedFailures = 0;
        var outcomes = new List<GitHubRawImageFetchOutcome>();

        GitHubCachedImage? result = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            images, RawUri, _ => reportedFailures++, CancellationToken.None, outcomes.Add);

        Assert.Null(result);
        Assert.Equal(0, reportedFailures);
        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.HttpStatus, (int)status) },
            outcomes);
    }

    [Fact]
    public async Task LfsPointerPermitsAuthenticatedFallback()
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubCachedImage?>(
                new InvalidDataException("The raw response was an LFS pointer.")));
        var outcomes = new List<GitHubRawImageFetchOutcome>();

        GitHubCachedImage? result = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            images, RawUri, _ => throw new Xunit.Sdk.XunitException("Unexpected report"),
            CancellationToken.None, outcomes.Add);

        Assert.Null(result);
        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.InvalidPayload) },
            outcomes);
    }

    [Fact]
    public async Task CancellationDoesNotStartAuthenticatedFallback()
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        var outcomes = new List<GitHubRawImageFetchOutcome>();
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<GitHubCachedImage?>(canceled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
                images, RawUri, _ => { }, canceled.Token, outcomes.Add));

        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.CallerCanceled) },
            outcomes);
    }

    [Fact]
    public async Task NullRawImageResultIsObservedOnce()
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GitHubCachedImage?>(null));
        var outcomes = new List<GitHubRawImageFetchOutcome>();

        GitHubCachedImage? result = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            images, RawUri, _ => throw new Xunit.Sdk.XunitException("Unexpected report"),
            CancellationToken.None, outcomes.Add);

        Assert.Null(result);
        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.NullResult) },
            outcomes);
    }

    [Fact]
    public async Task LeasedSuccessObserverFailureDoesNotChangeOrLeakReturnedLease()
    {
        IMarkdownImageSourceByteAdmission admission =
            Substitute.For<IMarkdownImageSourceByteAdmission>();
        byte[] bytes = [1, 2, 3];
        var reservation = new RecordingDisposable();
        var expected = new GitHubImageSourceLease(
            new GitHubCachedImage("partitioned-cache-key", bytes, "image/png", false),
            reservation);
        var images = new StubLeasedImageService(
            (_, _, _, _) => Task.FromResult<GitHubImageSourceLease?>(expected));

        GitHubImageSourceLease? actual = await GitHubRepositoryImageFetchPipeline.TryGetRawLeasedAsync(
            images, RawUri, _ => throw new Xunit.Sdk.XunitException("Unexpected report"),
            admission, CancellationToken.None,
            _ => throw new InvalidOperationException("diagnostic callback failure"));

        Assert.Same(expected, actual);
        Assert.Same(bytes, actual!.Image.Bytes);
        Assert.Equal(0, reservation.DisposeCount);
        actual.Dispose();
        Assert.Equal(1, reservation.DisposeCount);
        Assert.Equal(1, images.GetLeasedCallCount);
    }

    [Fact]
    public async Task LeasedNullResultAndExpectedHttpStatusAreObservedOnce()
    {
        IMarkdownImageSourceByteAdmission admission =
            Substitute.For<IMarkdownImageSourceByteAdmission>();
        var outcomes = new List<GitHubRawImageFetchOutcome>();
        var images = new StubLeasedImageService(
            (_, _, _, _) => Task.FromResult<GitHubImageSourceLease?>(null));

        GitHubImageSourceLease? missing = await GitHubRepositoryImageFetchPipeline.TryGetRawLeasedAsync(
            images, RawUri, _ => throw new Xunit.Sdk.XunitException("Unexpected report"),
            admission, CancellationToken.None, outcomes.Add);

        Assert.Null(missing);
        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.NullResult) },
            outcomes);

        outcomes.Clear();
        images.GetLeasedHandler = (_, _, _, _) => Task.FromException<GitHubImageSourceLease?>(
            new HttpRequestException("Raw object unavailable", null, HttpStatusCode.NotFound));

        GitHubImageSourceLease? notFound = await GitHubRepositoryImageFetchPipeline.TryGetRawLeasedAsync(
            images, RawUri, _ => throw new Xunit.Sdk.XunitException("Unexpected report"),
            admission, CancellationToken.None, outcomes.Add);

        Assert.Null(notFound);
        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.HttpStatus, 404) },
            outcomes);
        Assert.Equal(2, images.GetLeasedCallCount);
    }

    [Fact]
    public async Task LeasedCancellationAndSourceDeferralAreObservedThenRethrown()
    {
        IMarkdownImageSourceByteAdmission admission =
            Substitute.For<IMarkdownImageSourceByteAdmission>();
        var outcomes = new List<GitHubRawImageFetchOutcome>();
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        var images = new StubLeasedImageService(
            (_, _, _, _) => Task.FromCanceled<GitHubImageSourceLease?>(canceled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitHubRepositoryImageFetchPipeline.TryGetRawLeasedAsync(
                images, RawUri, _ => throw new Xunit.Sdk.XunitException("Unexpected report"),
                admission, canceled.Token, outcomes.Add));

        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.CallerCanceled) },
            outcomes);

        outcomes.Clear();
        images.GetLeasedHandler = (_, _, _, _) => Task.FromException<GitHubImageSourceLease?>(
            new MarkdownImageSourceDeferredException());

        await Assert.ThrowsAsync<MarkdownImageSourceDeferredException>(() =>
            GitHubRepositoryImageFetchPipeline.TryGetRawLeasedAsync(
                images, RawUri, _ => throw new Xunit.Sdk.XunitException("Unexpected report"),
                admission, CancellationToken.None, outcomes.Add));

        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.SourceDeferred) },
            outcomes);
        Assert.Equal(2, images.GetLeasedCallCount);
    }

    [Fact]
    public async Task UnexpectedHttpAndTransportFailuresAreObservedAndStillReported()
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();
        var outcomes = new List<GitHubRawImageFetchOutcome>();
        var httpFailure = new HttpRequestException("server response", null, HttpStatusCode.BadGateway);
        Exception? reported = null;
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubCachedImage?>(httpFailure));

        GitHubCachedImage? httpResult = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            images, RawUri, exception => reported = exception, CancellationToken.None,
            _ => throw new InvalidOperationException("diagnostic callback failure"));

        Assert.Null(httpResult);
        Assert.Same(httpFailure, reported);
        outcomes.Clear();
        reported = null;

        var transportFailure = new HttpRequestException("network unavailable");
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubCachedImage?>(transportFailure));

        GitHubCachedImage? transportResult = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            images, RawUri, exception => reported = exception, CancellationToken.None,
            outcome => outcomes.Add(outcome));

        Assert.Null(transportResult);
        Assert.Same(transportFailure, reported);
        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.TransportFailure) },
            outcomes);

        outcomes.Clear();
        reported = null;
        var requestTimeout = new TaskCanceledException("request timed out");
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubCachedImage?>(requestTimeout));

        GitHubCachedImage? timeoutResult = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            images, RawUri, exception => reported = exception, CancellationToken.None,
            outcomes.Add);

        Assert.Null(timeoutResult);
        Assert.Same(requestTimeout, reported);
        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.TransportFailure) },
            outcomes);

        outcomes.Clear();
        reported = null;
        var otherFailure = new InvalidOperationException("unexpected resolver failure");
        images.GetAsync(RawUri.AbsoluteUri, GitHubImageFetchScope.TrustedGitHub,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubCachedImage?>(otherFailure));

        GitHubCachedImage? otherResult = await GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
            images, RawUri, exception => reported = exception, CancellationToken.None,
            outcomes.Add);

        Assert.Null(otherResult);
        Assert.Same(otherFailure, reported);
        Assert.Equal(
            new[] { new GitHubRawImageFetchOutcome(GitHubRawImageFetchOutcomeKind.OtherFailure) },
            outcomes);
    }

    [Fact]
    public async Task RawFetchCannotBeRedirectedByCallerToThirdPartyHost()
    {
        IGitHubImageService images = Substitute.For<IGitHubImageService>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            GitHubRepositoryImageFetchPipeline.TryGetRawAsync(
                images, new Uri("https://example.org/image.svg"), _ => { },
                CancellationToken.None));
        await images.DidNotReceiveWithAnyArgs().GetAsync(default!, default, default);
    }

    private sealed class RecordingDisposable : IDisposable
    {
        internal int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class CountingPngHandler(byte[] bytes) : HttpMessageHandler
    {
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }

    private sealed class StubLeasedImageService(
        Func<
            string,
            GitHubImageFetchScope,
            IMarkdownImageSourceByteAdmission,
            CancellationToken,
            Task<GitHubImageSourceLease?>> getLeasedHandler) : IGitHubLeasedImageService
    {
        internal Func<
            string,
            GitHubImageFetchScope,
            IMarkdownImageSourceByteAdmission,
            CancellationToken,
            Task<GitHubImageSourceLease?>> GetLeasedHandler { get; set; } = getLeasedHandler;

        internal int GetLeasedCallCount { get; private set; }

        public Task<GitHubImageSourceLease?> GetLeasedAsync(
            string sourceUrl,
            GitHubImageFetchScope scope,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken = default)
        {
            GetLeasedCallCount++;
            return GetLeasedHandler(sourceUrl, scope, admission, cancellationToken);
        }

        public Task<GitHubImageSourceLease?> TryGetCachedLeasedAsync(
            string sourceUrl,
            GitHubImageFetchScope scope,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GitHubImageSourceLease?>(null);

        public Task<GitHubImageSourceLease?> GetOrFetchLeasedAsync(
            string sourceUrl,
            GitHubImageFetcher fetcher,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GitHubImageSourceLease?>(null);
    }
}
