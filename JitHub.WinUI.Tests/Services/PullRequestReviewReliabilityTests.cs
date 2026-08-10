using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class PullRequestReviewReliabilityTests
{
    [Theory]
    [InlineData(PullRequestReviewDecision.Comment, "COMMENT")]
    [InlineData(PullRequestReviewDecision.Approve, "APPROVE")]
    [InlineData(PullRequestReviewDecision.RequestChanges, "REQUEST_CHANGES")]
    public void ReviewDecisionMapsToGitHubEvent(
        PullRequestReviewDecision decision,
        string expectedEvent)
    {
        Assert.Equal(expectedEvent, PullRequestReviewSubmissionPolicy.ToApiEvent(decision));
    }

    [Theory]
    [InlineData(PullRequestReviewDecision.Comment)]
    [InlineData(PullRequestReviewDecision.RequestChanges)]
    public void ReviewCommentAndRequestChangesRequireBody(PullRequestReviewDecision decision)
    {
        Assert.Throws<ArgumentException>(() =>
            PullRequestReviewSubmissionPolicy.Validate(new PullRequestReviewSubmission(decision, "  ")));
    }

    [Fact]
    public void ApprovalAllowsEmptyBody()
    {
        PullRequestReviewSubmissionPolicy.Validate(
            new PullRequestReviewSubmission(PullRequestReviewDecision.Approve, null));
    }

    [Fact]
    public async Task ClientSubmitsTypedReviewToGitHubReviewEndpoint()
    {
        CapturingHandler handler = new();
        using HttpClient httpClient = new(handler);
        GitHubClientService client = new(httpClient);

        GitHubPullRequestReview review = await client.CreatePullRequestReviewAsync(
            "token",
            "octo space",
            "hello-world",
            17,
            new PullRequestReviewSubmission(
                PullRequestReviewDecision.RequestChanges,
                "Please add a test.\r\nThanks."));

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(
            "https://api.github.com/repos/octo%20space/hello-world/pulls/17/reviews",
            handler.RequestUri?.AbsoluteUri);
        using JsonDocument payload = JsonDocument.Parse(handler.Body);
        Assert.Equal("REQUEST_CHANGES", payload.RootElement.GetProperty("event").GetString());
        Assert.Equal("Please add a test.\nThanks.", payload.RootElement.GetProperty("body").GetString());
        Assert.Equal("CHANGES_REQUESTED", review.State);
    }

    [Fact]
    public async Task CapabilityRefreshInvalidatesSpecificTagsAndUsesNetworkOnlyReads()
    {
        RecordingQueryService query = new();
        GitHubPullRequestQueryService service = new(query);

        await service.InvalidatePullRequestAsync("42", "octo", "hello", 17);
        PullRequestCapabilitySnapshot? snapshot = await service.RefreshPullRequestCapabilitiesAsync(
            "token",
            "42",
            "octo",
            "hello",
            17);

        Assert.NotNull(snapshot);
        Assert.Contains("repo:octo/hello", query.InvalidatedTags);
        Assert.Contains("pr:octo/hello#17", query.InvalidatedTags);
        Assert.Equal(3, query.RefreshPaths.Count);
        Assert.Contains("repos/octo/hello", query.RefreshPaths);
        Assert.Contains("repos/octo/hello/pulls/17", query.RefreshPaths);
        Assert.Contains("repos/octo/hello/issues/17", query.RefreshPaths);
        Assert.True(snapshot!.Repository.Permissions?.Push);
        Assert.Equal("blocked", snapshot.PullRequest.MergeableState);
        Assert.True(snapshot.Issue?.Locked);
    }

    [Fact]
    public void ForbiddenCapabilityDenialRecoversOnlyAfterAuthoritativeRefresh()
    {
        PullRequestCapabilityDenialState state = new();

        Assert.True(state.RecordFailure(
            17,
            PullRequestDeniedCapability.Review | PullRequestDeniedCapability.Merge,
            HttpStatusCode.Forbidden));
        Assert.True(state.IsDenied(PullRequestDeniedCapability.Review));
        Assert.True(state.IsDenied(PullRequestDeniedCapability.Merge));
        Assert.False(state.RecordFailure(17, PullRequestDeniedCapability.Comment, HttpStatusCode.BadGateway));

        Assert.True(state.ConfirmSuccessfulRefresh(17));
        Assert.Equal(PullRequestDeniedCapability.None, state.DeniedCapabilities);
    }

    [Fact]
    public async Task DelayedForbiddenForPreviouslySelectedPullRequestDoesNotAffectCurrentPullRequest()
    {
        PullRequestCapabilityDenialState state = new();
        state.TrackPullRequest(17);
        TaskCompletionSource<bool> releaseFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> delayedFailure = RecordForbiddenAfterReleaseAsync(
            state,
            17,
            PullRequestDeniedCapability.Edit,
            releaseFailure.Task);
        state.TrackPullRequest(18);
        releaseFailure.SetResult(true);

        Assert.False(await delayedFailure);
        Assert.Equal(18, state.PullRequestNumber);
        Assert.Equal(PullRequestDeniedCapability.None, state.DeniedCapabilities);
    }

    [Fact]
    public async Task DelayedForbiddenForPreviousPullRequestPreservesCurrentPullRequestDenials()
    {
        PullRequestCapabilityDenialState state = new();
        state.TrackPullRequest(17);
        TaskCompletionSource<bool> releaseFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> delayedFailure = RecordForbiddenAfterReleaseAsync(
            state,
            17,
            PullRequestDeniedCapability.Merge,
            releaseFailure.Task);
        state.TrackPullRequest(18);
        Assert.True(state.RecordFailureForCurrent(
            18,
            PullRequestDeniedCapability.Comment,
            HttpStatusCode.Forbidden));
        releaseFailure.SetResult(true);

        Assert.False(await delayedFailure);
        Assert.Equal(18, state.PullRequestNumber);
        Assert.Equal(PullRequestDeniedCapability.Comment, state.DeniedCapabilities);
    }

    [Fact]
    public void MutationHandlersUseInitiatingPullRequestNumberForCapabilityDenials()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "RepoPullRequestPageViewModel.cs"));

        Assert.Contains("_capabilityDenials.RecordFailureForCurrent(", source, StringComparison.Ordinal);
        Assert.Contains("_capabilityDenials.TrackPullRequest(value.Number);", source, StringComparison.Ordinal);
        foreach (string capability in new[] { "edit", "metadata", "state", "comment", "reaction", "review", "merge" })
        {
            Assert.DoesNotContain(
                $"DisableRejectedCapability(ex, \"{capability}\");",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ActionAndPrefetchTelemetryAreBestEffortAndIdentifierFree()
    {
        RecordingTelemetry telemetry = new(throwOnTrack: true);

        PullRequestTelemetry.TrackAction(telemetry, "review_approve", "success");
        PullRequestTelemetry.TrackPrefetchStarted(telemetry, PullRequestPrefetchReason.Hover);
        PullRequestTelemetry.TrackPrefetchCompleted(
            telemetry,
            PullRequestPrefetchReason.Hover,
            "success",
            TimeSpan.FromMilliseconds(82));

        Assert.Equal(3, telemetry.Attempts);
        Assert.All(telemetry.Events, entry =>
        {
            Assert.DoesNotContain("owner", entry.Properties.Keys);
            Assert.DoesNotContain("repo", entry.Properties.Keys);
            Assert.DoesNotContain("title", entry.Properties.Keys);
            Assert.DoesNotContain("body", entry.Properties.Keys);
        });
        RecordedEvent completed = Assert.Single(
            telemetry.Events,
            entry => entry.Name == "pull_requests.prefetch.completed");
        Assert.Equal("lt_150ms", completed.Properties["duration_bucket"]);
    }

    [Fact]
    public async Task PrefetchObserverReportsFailureWithoutPropagatingWorkOrSinkErrors()
    {
        RecordingTelemetry telemetry = new(throwOnTrack: true);

        await PullRequestTelemetry.ObservePrefetchAsync(
            telemetry,
            PullRequestPrefetchReason.Neighbor,
            () => Task.FromException(new HttpRequestException("offline")));

        Assert.Equal(2, telemetry.Attempts);
        RecordedEvent completed = Assert.Single(
            telemetry.Events,
            entry => entry.Name == "pull_requests.prefetch.completed");
        Assert.Equal("failed", completed.Properties["result"]);
        Assert.Equal("neighbor", completed.Properties["source"]);
    }

    [Fact]
    public async Task PrefetchObserverContainsSynchronousStartFailures()
    {
        RecordingTelemetry telemetry = new(throwOnTrack: true);

        await PullRequestTelemetry.ObservePrefetchAsync(
            telemetry,
            PullRequestPrefetchReason.Hover,
            () => throw new InvalidOperationException("synchronous setup failure"));

        RecordedEvent completed = Assert.Single(
            telemetry.Events,
            entry => entry.Name == "pull_requests.prefetch.completed");
        Assert.Equal("failed", completed.Properties["result"]);
        Assert.Equal("hover", completed.Properties["source"]);
    }

    private static async Task<bool> RecordForbiddenAfterReleaseAsync(
        PullRequestCapabilityDenialState state,
        int pullRequestNumber,
        PullRequestDeniedCapability capability,
        Task release)
    {
        await release;
        return state.RecordFailureForCurrent(
            pullRequestNumber,
            capability,
            HttpStatusCode.Forbidden);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":91,\"state\":\"CHANGES_REQUESTED\",\"body\":\"Please add a test.\",\"html_url\":\"\",\"user\":{\"login\":\"reviewer\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class RecordingQueryService : IGitHubQueryService
    {
        public List<string> RefreshPaths { get; } = [];

        public List<string> InvalidatedTags { get; } = [];

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class => RefreshAsync(query, cancellationToken);

        public Task<CachedResult<T>> RefreshAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class
        {
            RefreshPaths.Add(query.RelativePath);
            object value = typeof(T) == typeof(GitHubRepository)
                ? new GitHubRepository
                {
                    Name = "hello",
                    FullName = "octo/hello",
                    Owner = new GitHubRepositoryOwner { Login = "octo" },
                    Permissions = new GitHubRepositoryPermissions { Pull = true, Push = true },
                    AllowMergeCommit = true
                }
                : typeof(T) == typeof(GitHubPullRequest)
                    ? new GitHubPullRequest
                    {
                        Number = 17,
                        State = "open",
                        Mergeable = false,
                        MergeableState = "blocked",
                        User = new GitHubActor { Login = "author" }
                    }
                    : new GitHubIssue { Number = 17, Locked = true };
            return Task.FromResult(new CachedResult<T>(
                (T)value,
                CacheState.Fresh,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task InvalidateAsync(
            string cacheKey,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(
            IReadOnlyCollection<string> tags,
            CancellationToken cancellationToken = default)
        {
            InvalidatedTags.AddRange(tags);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTelemetry(bool throwOnTrack) : ITelemetryService
    {
        public int Attempts { get; private set; }

        public List<RecordedEvent> Events { get; } = [];

        public void TrackEvent(
            string name,
            IReadOnlyDictionary<string, string?>? properties = null)
        {
            Attempts++;
            Events.Add(new RecordedEvent(
                name,
                properties is null
                    ? new Dictionary<string, string?>()
                    : new Dictionary<string, string?>(properties)));
            if (throwOnTrack)
            {
                throw new InvalidOperationException("Injected telemetry failure.");
            }
        }

        public void TrackMetric(
            string name,
            double value,
            IReadOnlyDictionary<string, string?>? properties = null)
        {
        }

        public IPerformanceTrace StartTrace(
            string name,
            IReadOnlyDictionary<string, string?>? properties = null) => new NoopTrace();
    }

    private sealed record RecordedEvent(
        string Name,
        IReadOnlyDictionary<string, string?> Properties);

    private sealed class NoopTrace : IPerformanceTrace
    {
        public void SetProperty(string key, string? value)
        {
        }

        public void SetResult(string result)
        {
        }

        public void Dispose()
        {
        }
    }
}
