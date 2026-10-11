using System.Net;
using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownRepositoryImageTransportObserverTests
{
    [Fact]
    public void DisabledObserverReturnsTheExactOriginalFetcher()
    {
        GitHubImageFetcher original = (_, _) => Task.FromResult<GitHubImageDownload?>(null);
        Assert.Same(original, MarkdownRepositoryImageTransportObserver.WrapFetcher(original, null));
    }

    [Fact]
    public async Task ObserverDoesNotOwnTheReturnedPayloadOrSourceReservation()
    {
        using var reservation = new CountingReservation();
        var download = new GitHubImageDownload([1, 2], "image/png", SourceByteLease: reservation);
        var observations = new List<GitHubRawImageFetchOutcome>();
        GitHubImageFetcher observed = MarkdownRepositoryImageTransportObserver.WrapFetcher(
            (_, _) => Task.FromResult<GitHubImageDownload?>(download), observations.Add);
        Assert.Same(download, await observed(null, CancellationToken.None));
        Assert.Equal(new(GitHubRawImageFetchOutcomeKind.Success), Assert.Single(observations));
        Assert.Equal(0, reservation.DisposeCount);
        download.SourceByteLease!.Dispose();
        Assert.Equal(1, reservation.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullAndEmptyPayloadsAreObservedWithoutChangingTheResult(bool empty)
    {
        GitHubImageDownload? result = empty ? new([], "image/png") : null;
        var outcomes = new List<GitHubRawImageFetchOutcome>();
        GitHubImageFetcher observed = MarkdownRepositoryImageTransportObserver.WrapFetcher(
            (_, _) => Task.FromResult(result), outcomes.Add);
        Assert.Same(result, await observed(null, CancellationToken.None));
        Assert.Equal(GitHubRawImageFetchOutcomeKind.NullResult, Assert.Single(outcomes).Kind);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task ApiFailureStatusIsObservedAndTheSameExceptionEscapes(int status)
    {
        var failure = new GitHubApiException((HttpStatusCode)status, "private path/token must not be retained");
        var outcomes = new List<GitHubRawImageFetchOutcome>();
        GitHubImageFetcher observed = MarkdownRepositoryImageTransportObserver.WrapFetcher(
            (_, _) => Task.FromException<GitHubImageDownload?>(failure), outcomes.Add);
        Assert.Same(failure, await Assert.ThrowsAsync<GitHubApiException>(() => observed(null, CancellationToken.None)));
        Assert.Equal(new(GitHubRawImageFetchOutcomeKind.HttpStatus, status), Assert.Single(outcomes));
    }

    [Fact]
    public async Task CancellationAndSourceDeferralRemainDistinctRethrownFailures()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception[] failures = [new OperationCanceledException(cancellation.Token), new MarkdownImageSourceDeferredException(), new OperationCanceledException()];
        GitHubRawImageFetchOutcomeKind[] expected = [GitHubRawImageFetchOutcomeKind.CallerCanceled, GitHubRawImageFetchOutcomeKind.SourceDeferred, GitHubRawImageFetchOutcomeKind.TransportFailure];
        for (int i = 0; i < failures.Length; i++)
        {
            Exception failure = failures[i];
            var outcomes = new List<GitHubRawImageFetchOutcome>();
            GitHubImageFetcher observed = MarkdownRepositoryImageTransportObserver.WrapFetcher(
                (_, _) => Task.FromException<GitHubImageDownload?>(failure), outcomes.Add);
            Exception actual = await Assert.ThrowsAnyAsync<Exception>(() => observed(null, i == 0 ? cancellation.Token : CancellationToken.None));
            Assert.Same(failure, actual);
            Assert.Equal(expected[i], Assert.Single(outcomes).Kind);
        }
    }

    [Fact]
    public async Task ThrowingObserverCannotReplaceSuccessOrTheOriginalFailure()
    {
        var download = new GitHubImageDownload([1], "image/png");
        var failure = new IOException("private details");
        Action<GitHubRawImageFetchOutcome> throwing = _ => throw new InvalidOperationException("diagnostic writer failed");
        GitHubImageFetcher success = MarkdownRepositoryImageTransportObserver.WrapFetcher(
            (_, _) => Task.FromResult<GitHubImageDownload?>(download), throwing);
        GitHubImageFetcher failed = MarkdownRepositoryImageTransportObserver.WrapFetcher(
            (_, _) => Task.FromException<GitHubImageDownload?>(failure), throwing);
        Assert.Same(download, await success(null, CancellationToken.None));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => failed(null, CancellationToken.None)));
    }

    private sealed class CountingReservation : IDisposable
    {
        internal int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
