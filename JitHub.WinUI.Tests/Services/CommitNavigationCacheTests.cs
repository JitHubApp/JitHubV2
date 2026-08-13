using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class CommitNavigationCacheTests
{
    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task ClearPartition_DrainsInFlightWorkRejectsRepopulationAndPreservesOtherAccount()
    {
        AccountWorkQuiescence accountWork = new();
        FakeCommitQueryService queryService = new() { IgnoreCancellation = true };
        CommitNavigationCache cache = new(queryService, accountWork);
        GitHubCommit otherCommit = CreateCommit("shared-sha");
        cache.Store("202", CreateSnapshot(otherCommit));

        Task<CommitPrefetchOutcome> inFlight = cache.PrefetchWithResultAsync(
            "token",
            "101",
            "octo",
            "app",
            "shared-sha",
            CommitPrefetchReason.Hover);
        Task quiesce = accountWork.QuiesceAsync("101");
        Assert.False(quiesce.IsCompleted);

        queryService.Complete(CreateAggregate("shared-sha"));
        await quiesce;
        Assert.Equal(CommitPrefetchOutcome.Canceled, await inFlight);
        await cache.ClearPartitionAsync("101");

        cache.Store("101", CreateSnapshot(CreateCommit("late-sha")));
        Assert.False(cache.TryGet("101", "octo", "app", "shared-sha", out _));
        Assert.False(cache.TryGet("101", "octo", "app", "late-sha", out _));
        Assert.True(cache.TryGet("202", "octo", "app", "shared-sha", out _));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public void TryGet_DoesNotCrossAuthenticatedAccountPartitions()
    {
        CommitNavigationCache cache = new(new FakeCommitQueryService());
        cache.Store("101", CreateSnapshot(CreateCommit("same-sha")));

        Assert.True(cache.TryGet("101", "octo", "app", "same-sha", out _));
        Assert.False(cache.TryGet("202", "octo", "app", "same-sha", out _));
        Assert.False(cache.TryGet("current", "octo", "app", "same-sha", out _));
    }

    [Fact]
    public void TryGet_ReturnsStoredSnapshotCaseInsensitively()
    {
        CommitNavigationCache cache = new(new FakeCommitQueryService());
        GitHubCommit commit = CreateCommit("3F9A1C2");
        CommitNavigationSnapshot snapshot = new(
            "Octo",
            "App",
            commit.Sha,
            commit,
            [],
            null,
            [],
            [],
            DateTimeOffset.UtcNow,
            "unit-test");

        cache.Store("41", snapshot);

        Assert.True(cache.TryGet("41", "octo", "app", "3f9a1c2", out CommitNavigationSnapshot cached));
        Assert.Equal(commit.Sha, cached.Commit.Sha);
    }

    [Fact]
    public void TryGet_IgnoresExpiredSnapshots()
    {
        CommitNavigationCache cache = new(new FakeCommitQueryService());
        GitHubCommit commit = CreateCommit("3f9a1c2");
        cache.Store("41", new CommitNavigationSnapshot(
            "octo",
            "app",
            commit.Sha,
            commit,
            [],
            null,
            [],
            [],
            DateTimeOffset.UtcNow.AddMinutes(-30),
            "unit-test"));

        Assert.False(cache.TryGet("41", "octo", "app", commit.Sha, out _));
    }

    [Fact]
    public async Task PrefetchAsync_DedupesInFlightRequestsAndStoresSnapshot()
    {
        FakeCommitQueryService queryService = new();
        CommitNavigationCache cache = new(queryService);

        Task<CommitPrefetchOutcome> first = cache.PrefetchWithResultAsync("token", "42", "octo", "app", "3f9a1c2", CommitPrefetchReason.Hover);
        Task<CommitPrefetchOutcome> second = cache.PrefetchWithResultAsync("token", "42", "octo", "app", "3f9a1c2", CommitPrefetchReason.Neighbor);

        Assert.Equal(1, queryService.DetailCallCount);
        queryService.Complete(CreateAggregate("3f9a1c2"));
        CommitPrefetchOutcome[] outcomes = await Task.WhenAll(first, second);

        Assert.All(outcomes, outcome => Assert.Equal(CommitPrefetchOutcome.Success, outcome));
        Assert.True(cache.TryGet("42", "octo", "app", "3f9a1c2", out CommitNavigationSnapshot snapshot));
        Assert.Equal("3f9a1c2", snapshot.Commit.Sha);
        Assert.Single(snapshot.Comments);
        Assert.Single(snapshot.CheckRuns);
    }

    [Fact]
    public async Task PrefetchWithResultAsync_ReportsFailureWithoutSurfacingBackgroundException()
    {
        FakeCommitQueryService queryService = new();
        CommitNavigationCache cache = new(queryService);
        Task<CommitPrefetchOutcome> prefetch = cache.PrefetchWithResultAsync(
            "token",
            "42",
            "octo",
            "app",
            "failed-sha",
            CommitPrefetchReason.Dwell);

        queryService.Fail(new HttpRequestException("Injected prefetch failure."));

        Assert.Equal(CommitPrefetchOutcome.Failure, await prefetch);
        Assert.False(cache.TryGet("42", "octo", "app", "failed-sha", out _));
    }

    [Fact]
    public async Task PrefetchWithResultAsync_ReportsCancellationWithoutSurfacingBackgroundException()
    {
        FakeCommitQueryService queryService = new();
        CommitNavigationCache cache = new(queryService);
        using CancellationTokenSource cancellation = new();
        Task<CommitPrefetchOutcome> prefetch = cache.PrefetchWithResultAsync(
            "token",
            "42",
            "octo",
            "app",
            "canceled-sha",
            CommitPrefetchReason.Neighbor,
            cancellation.Token);

        cancellation.Cancel();

        Assert.Equal(CommitPrefetchOutcome.Canceled, await prefetch);
        Assert.False(cache.TryGet("42", "octo", "app", "canceled-sha", out _));
    }

    [Theory]
    [InlineData(CommitPrefetchOutcome.Success, "success")]
    [InlineData(CommitPrefetchOutcome.Failure, "failed")]
    [InlineData(CommitPrefetchOutcome.Canceled, "cancelled")]
    [InlineData(CommitPrefetchOutcome.Suppressed, "suppressed")]
    public async Task TrackedPrefetch_EmitsReachableStartedAndCompletedEvents(
        CommitPrefetchOutcome outcome,
        string expectedResult)
    {
        ICommitNavigationCache navigationCache = NSubstitute.Substitute.For<ICommitNavigationCache>();
        navigationCache
            .PrefetchWithResultAsync(
                "token",
                "42",
                "private-owner",
                "private-repository",
                "private-sha",
                CommitPrefetchReason.Hover,
                Arg.Any<CancellationToken>())
            .Returns(outcome);
        RecordingTelemetryService telemetry = new();

        CommitPrefetchOutcome actualOutcome = await CommitPrefetchTelemetry.RunAsync(
            navigationCache,
            telemetry,
            "token",
            "42",
            "private-owner",
            "private-repository",
            "private-sha",
            CommitPrefetchReason.Hover,
            CancellationToken.None);

        Assert.Equal(outcome, actualOutcome);

        Assert.Collection(
            telemetry.Events,
            started =>
            {
                Assert.Equal("commits.prefetch.started", started.Name);
                Assert.Equal("started", started.Properties["result"]);
                Assert.False(started.Properties.ContainsKey("duration_bucket"));
            },
            completed =>
            {
                Assert.Equal("commits.prefetch.completed", completed.Name);
                Assert.Equal(expectedResult, completed.Properties["result"]);
                Assert.False(string.IsNullOrWhiteSpace(completed.Properties["duration_bucket"]));
            });
        Assert.All(telemetry.Events, telemetryEvent =>
        {
            Assert.Equal("repo", telemetryEvent.Properties["page"]);
            Assert.Equal("hover", telemetryEvent.Properties["source"]);
            Assert.DoesNotContain(
                telemetryEvent.Properties.Values,
                value => value is "token" or "42" or "private-owner" or "private-repository" or "private-sha");
        });
    }

    [Fact]
    public async Task TrackedPrefetch_TelemetryFailureDoesNotAffectBackgroundWork()
    {
        ICommitNavigationCache navigationCache = NSubstitute.Substitute.For<ICommitNavigationCache>();
        navigationCache
            .PrefetchWithResultAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CommitPrefetchReason>(),
                Arg.Any<CancellationToken>())
            .Returns(CommitPrefetchOutcome.Success);
        RecordingTelemetryService telemetry = new(throwOnTrack: true);

        await CommitPrefetchTelemetry.RunAsync(
            navigationCache,
            telemetry,
            "token",
            "42",
            "owner",
            "repository",
            "sha",
            CommitPrefetchReason.Neighbor,
            CancellationToken.None);

        await navigationCache.Received(1).PrefetchWithResultAsync(
            "token",
            "42",
            "owner",
            "repository",
            "sha",
            CommitPrefetchReason.Neighbor,
            Arg.Any<CancellationToken>());
        Assert.Equal(2, telemetry.TrackAttempts);
    }

    private static CommitDetailAggregate CreateAggregate(string sha) =>
        new(
            CreateCommit(sha),
            [new GitHubCommitComment { Id = 1, Body = "Cached comment" }],
            new GitHubCombinedStatus { State = "success" },
            [new GitHubCheckRun { Id = 1, Name = "build" }],
            [new GitHubPullRequest { Id = 1, Number = 42, Title = "Fix" }],
            new CommitSectionState(CacheState.Fresh),
            new CommitSectionState(CacheState.Fresh),
            new CommitSectionState(CacheState.Fresh),
            new CommitSectionState(CacheState.Fresh),
            new CommitSectionState(CacheState.Fresh));

    private static CommitNavigationSnapshot CreateSnapshot(GitHubCommit commit) => new(
        "octo",
        "app",
        commit.Sha,
        commit,
        [],
        null,
        [],
        [],
        DateTimeOffset.UtcNow,
        "security-test");

    private static GitHubCommit CreateCommit(string sha) =>
        new()
        {
            Sha = sha,
            Commit = new GitHubCommitInfo
            {
                Message = "Fix native diff",
                Author = new GitHubCommitSignature { Name = "Octo", Date = DateTimeOffset.UtcNow }
            },
            Files = [new GitHubCommitFile { Filename = "src/app.cs", Patch = "@@ -1 +1 @@\n-old\n+new" }]
        };

    private sealed class FakeCommitQueryService : IGitHubCommitQueryService
    {
        private TaskCompletionSource<CommitDetailAggregate?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DetailCallCount { get; private set; }

        public bool IgnoreCancellation { get; init; }

        public void Complete(CommitDetailAggregate aggregate)
            => _completion.TrySetResult(aggregate);

        public void Fail(Exception exception)
            => _completion.TrySetException(exception);

        public Task<CommitDetailAggregate?> GetCommitDetailAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            string gitRef,
            CancellationToken cancellationToken = default)
        {
            DetailCallCount++;
            return IgnoreCancellation
                ? _completion.Task
                : _completion.Task.WaitAsync(cancellationToken);
        }

        public Task<CachedResult<GitHubBranch[]>> GetBranchesAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubCommit[]>> GetCommitsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            CommitListQueryOptions options,
            int pageSize,
            int pageNumber = 1,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubCommit>> GetCommitAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            string gitRef,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubCommitComment[]>> GetCommitCommentsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            string gitRef,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubCombinedStatus>> GetCombinedStatusAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            string gitRef,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubCheckRun[]>> GetCheckRunsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            string gitRef,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubPullRequest[]>> GetAssociatedPullRequestsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            string gitRef,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CachedResult<GitHubCompareResult>> CompareCommitsAsync(
            string accessToken,
            string userId,
            string owner,
            string repositoryName,
            string @base,
            string head,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingTelemetryService(bool throwOnTrack = false) : ITelemetryService
    {
        public List<(string Name, IReadOnlyDictionary<string, string?> Properties)> Events { get; } = [];

        public int TrackAttempts { get; private set; }

        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
        {
            TrackAttempts++;
            if (throwOnTrack)
            {
                throw new InvalidOperationException("Injected telemetry failure.");
            }

            Events.Add((name, properties ?? new Dictionary<string, string?>()));
        }

        public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null)
        {
        }

        public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
            new NoopPerformanceTrace();
    }

    private sealed class NoopPerformanceTrace : IPerformanceTrace
    {
        public void SetProperty(string key, string? value)
        {
        }

        public void Dispose()
        {
        }
    }
}
