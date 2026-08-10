using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class AdaptivePrefetchPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false, AdaptivePrefetchSuppressionReason.Offline)]
    [InlineData(true, true, false, false, AdaptivePrefetchSuppressionReason.MeteredConnection)]
    [InlineData(true, false, true, false, AdaptivePrefetchSuppressionReason.EnergySaver)]
    [InlineData(true, false, false, true, AdaptivePrefetchSuppressionReason.MemoryPressure)]
    public void Evaluate_SuppressesEveryConstrainedEnvironmentAndCountsTheDecision(
        bool isOnline,
        bool isMetered,
        bool isEnergySaverEnabled,
        bool isMemoryPressureHigh,
        AdaptivePrefetchSuppressionReason expectedReason)
    {
        MutableEnvironment environment = new()
        {
            IsNetworkAvailable = isOnline,
            IsMetered = isMetered,
            IsEnergySaverEnabled = isEnergySaverEnabled,
            IsMemoryPressureHigh = isMemoryPressureHigh
        };
        RecordingMetricTelemetry telemetry = new();
        AdaptivePrefetchPolicy policy = new(environment, telemetry);

        AdaptivePrefetchDecision decision = policy.Evaluate(
            "private-account-42",
            AdaptivePrefetchFeature.Issues,
            AdaptivePrefetchStage.Schedule);

        Assert.False(decision.IsAllowed);
        Assert.Equal(expectedReason, decision.SuppressionReason);
        AdaptivePrefetchCounter counter = Assert.Single(policy.GetCounters());
        Assert.Equal(1, counter.Count);
        Assert.False(counter.IsAllowed);
        RecordedMetric metric = Assert.Single(telemetry.Metrics);
        Assert.Equal("prefetch.policy.decision", metric.Name);
        Assert.Equal("suppressed", metric.Properties["result"]);
        Assert.DoesNotContain(
            metric.Properties.Values,
            value => value?.Contains("private-account", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Evaluate_SuppressesLowRateLimitHeadroomUntilResetThenRecovers()
    {
        DateTimeOffset now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        AdaptivePrefetchPolicy policy = new(
            new MutableEnvironment(),
            new RecordingMetricTelemetry(),
            () => now);
        policy.ObserveRateLimit("42", AdaptivePrefetchPolicy.MinimumRateLimitHeadroom, now.AddMinutes(2));

        AdaptivePrefetchDecision suppressed = policy.Evaluate(
            "42",
            AdaptivePrefetchFeature.PullRequests,
            AdaptivePrefetchStage.Execute);
        Assert.Equal(AdaptivePrefetchSuppressionReason.RateLimitHeadroom, suppressed.SuppressionReason);

        now = now.AddMinutes(3);
        AdaptivePrefetchDecision recovered = policy.Evaluate(
            "42",
            AdaptivePrefetchFeature.PullRequests,
            AdaptivePrefetchStage.Execute);
        Assert.True(recovered.IsAllowed);
        Assert.Equal(AdaptivePrefetchSuppressionReason.None, recovered.SuppressionReason);
    }

    [Fact]
    public void ObserveRateLimit_DoesNotLetAnOutOfOrderResponseRestoreHeadroom()
    {
        DateTimeOffset now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        AdaptivePrefetchPolicy policy = new(
            new MutableEnvironment(),
            new RecordingMetricTelemetry(),
            () => now);
        DateTimeOffset reset = now.AddMinutes(2);

        policy.ObserveRateLimit("42", remaining: 25, resetAt: reset);
        policy.ObserveRateLimit("42", remaining: 4500, resetAt: reset);

        AdaptivePrefetchDecision decision = policy.Evaluate(
            "42",
            AdaptivePrefetchFeature.Commits,
            AdaptivePrefetchStage.Execute);
        Assert.Equal(AdaptivePrefetchSuppressionReason.RateLimitHeadroom, decision.SuppressionReason);
    }

    [Fact]
    public void ObserveRateLimit_NewerPrimaryWindowReplacesExhaustedOlderWindow()
    {
        DateTimeOffset now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        AdaptivePrefetchPolicy policy = new(
            new MutableEnvironment(),
            new RecordingMetricTelemetry(),
            () => now);

        policy.ObserveRateLimit("42", remaining: 12, resetAt: now.AddMinutes(2));
        policy.ObserveRateLimit("42", remaining: 4_900, resetAt: now.AddHours(1));

        AdaptivePrefetchDecision decision = policy.Evaluate(
            "42",
            AdaptivePrefetchFeature.Issues,
            AdaptivePrefetchStage.Schedule);
        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void ObserveRateLimit_LateOlderWindowCannotPoisonHealthyNewerWindow()
    {
        DateTimeOffset now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        AdaptivePrefetchPolicy policy = new(
            new MutableEnvironment(),
            new RecordingMetricTelemetry(),
            () => now);

        policy.ObserveRateLimit("42", remaining: 4_900, resetAt: now.AddHours(1));
        policy.ObserveRateLimit("42", remaining: 0, resetAt: now.AddMinutes(2));

        Assert.True(policy.Evaluate(
            "42",
            AdaptivePrefetchFeature.PullRequests,
            AdaptivePrefetchStage.Execute).IsAllowed);
    }

    [Fact]
    public void ObserveRateLimit_SecondaryLimitExpiresWithoutPoisoningHealthyPrimaryWindow()
    {
        DateTimeOffset now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        AdaptivePrefetchPolicy policy = new(
            new MutableEnvironment(),
            new RecordingMetricTelemetry(),
            () => now);
        policy.ObserveRateLimit("42", remaining: 4_900, resetAt: now.AddHours(1));
        policy.ObserveRateLimit(
            "42",
            remaining: 0,
            resetAt: now.AddHours(2),
            retryAfter: TimeSpan.FromSeconds(30));

        Assert.False(policy.Evaluate(
            "42",
            AdaptivePrefetchFeature.Commits,
            AdaptivePrefetchStage.Execute).IsAllowed);

        now = now.AddSeconds(31);
        Assert.True(policy.Evaluate(
            "42",
            AdaptivePrefetchFeature.Commits,
            AdaptivePrefetchStage.Execute).IsAllowed);
    }

    [Theory]
    [InlineData("core", "search", "graphql")]
    [InlineData("search", "core", "graphql")]
    [InlineData("graphql", "core", "search")]
    public void ObserveRateLimit_TracksPrimaryGenerationsPerResourceBucket(
        string exhaustedResource,
        string healthyResourceOne,
        string healthyResourceTwo)
    {
        DateTimeOffset now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        AdaptivePrefetchPolicy policy = new(
            new MutableEnvironment(),
            new RecordingMetricTelemetry(),
            () => now);

        policy.ObserveRateLimit(
            "42",
            remaining: 0,
            resetAt: now.AddMinutes(2),
            resource: exhaustedResource);
        policy.ObserveRateLimit(
            "42",
            remaining: 4_900,
            resetAt: now.AddHours(1),
            resource: healthyResourceOne);
        policy.ObserveRateLimit(
            "42",
            remaining: 4_800,
            resetAt: now.AddHours(1),
            resource: healthyResourceTwo);

        Assert.False(policy.Evaluate(
            "42",
            AdaptivePrefetchFeature.Issues,
            AdaptivePrefetchStage.Schedule).IsAllowed);

        now = now.AddMinutes(3);
        Assert.True(policy.Evaluate(
            "42",
            AdaptivePrefetchFeature.Issues,
            AdaptivePrefetchStage.Schedule).IsAllowed);
    }

    [Fact]
    public void ObserveRateLimit_NewGenerationOnlyReplacesItsOwnResourceBucket()
    {
        DateTimeOffset now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        AdaptivePrefetchPolicy policy = new(
            new MutableEnvironment(),
            new RecordingMetricTelemetry(),
            () => now);
        policy.ObserveRateLimit("42", 0, now.AddMinutes(2), resource: "search");
        policy.ObserveRateLimit("42", 0, now.AddMinutes(2), resource: "core");
        policy.ObserveRateLimit("42", 5_000, now.AddHours(1), resource: "core");

        Assert.False(policy.Evaluate(
            "42",
            AdaptivePrefetchFeature.Commits,
            AdaptivePrefetchStage.Execute).IsAllowed);

        now = now.AddMinutes(3);
        Assert.True(policy.Evaluate(
            "42",
            AdaptivePrefetchFeature.Commits,
            AdaptivePrefetchStage.Execute).IsAllowed);
    }

    [Fact]
    public void Evaluate_TracksAllowedAndSuppressedCountersByFeatureAndStage()
    {
        MutableEnvironment environment = new();
        AdaptivePrefetchPolicy policy = new(environment, new RecordingMetricTelemetry());

        _ = policy.Evaluate("42", AdaptivePrefetchFeature.Commits, AdaptivePrefetchStage.Schedule);
        _ = policy.Evaluate("42", AdaptivePrefetchFeature.Commits, AdaptivePrefetchStage.Schedule);
        environment.IsMetered = true;
        _ = policy.Evaluate("42", AdaptivePrefetchFeature.Commits, AdaptivePrefetchStage.Execute);

        IReadOnlyList<AdaptivePrefetchCounter> counters = policy.GetCounters();
        Assert.Equal(2, counters.Single(counter => counter.IsAllowed).Count);
        AdaptivePrefetchCounter suppressed = counters.Single(counter => !counter.IsAllowed);
        Assert.Equal(AdaptivePrefetchStage.Execute, suppressed.Stage);
        Assert.Equal(AdaptivePrefetchSuppressionReason.MeteredConnection, suppressed.SuppressionReason);
        Assert.Equal(1, suppressed.Count);
    }

    [Fact]
    public async Task IssueSchedule_IsRejectedBeforeCreatingBackgroundWork()
    {
        IGitHubIssueQueryService query = Substitute.For<IGitHubIssueQueryService>();
        RecordingAdmissionPolicy policy = new(allowSchedule: false, allowExecute: true);
        IssueNavigationCache cache = new(
            query,
            new AccountWorkQuiescence(),
            new ApplicationTaskCoordinator(),
            policy);

        using IDisposable scheduled = cache.SchedulePrefetch(
            "token", "42", "owner", "repo", 7, IssuePrefetchReason.Hover, TimeSpan.Zero);
        await Task.Delay(50);

        Assert.Equal([AdaptivePrefetchStage.Schedule], policy.Stages);
        await query.DidNotReceiveWithAnyArgs().GetIssuePrefetchAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task IssueExecution_RechecksPolicyAfterDelay()
    {
        IGitHubIssueQueryService query = Substitute.For<IGitHubIssueQueryService>();
        RecordingAdmissionPolicy policy = new(allowSchedule: true, allowExecute: false);
        IssueNavigationCache cache = new(
            query,
            new AccountWorkQuiescence(),
            new ApplicationTaskCoordinator(),
            policy);

        using IDisposable scheduled = cache.SchedulePrefetch(
            "token", "42", "owner", "repo", 7, IssuePrefetchReason.Dwell, TimeSpan.Zero);
        await policy.ExecutionObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([AdaptivePrefetchStage.Schedule, AdaptivePrefetchStage.Execute], policy.Stages);
        await query.DidNotReceiveWithAnyArgs().GetIssuePrefetchAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task IssueScheduledPrefetch_PriorAccountCancellationReportsCancelledExactlyOnce()
    {
        IGitHubIssueQueryService query = Substitute.For<IGitHubIssueQueryService>();
        ApplicationTaskCoordinator coordinator = new();
        await coordinator.CancelAccountAsync("42");
        IssueNavigationCache cache = new(
            query,
            new AccountWorkQuiescence(),
            coordinator,
            UnrestrictedAdaptivePrefetchPolicy.Instance);
        TaskCompletionSource<IssuePrefetchResult> terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;

        using IDisposable scheduled = cache.SchedulePrefetch(
            "token",
            "42",
            "owner",
            "repo",
            7,
            IssuePrefetchReason.Dwell,
            TimeSpan.FromHours(1),
            (result, _) =>
            {
                Interlocked.Increment(ref callbackCount);
                terminal.TrySetResult(result);
            });

        Assert.Equal(IssuePrefetchResult.Cancelled, await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref callbackCount));
        await query.DidNotReceiveWithAnyArgs().GetIssuePrefetchAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task PullRequestScheduledPrefetch_ShutdownBeforeScheduleReportsCancelledExactlyOnce()
    {
        IGitHubPullRequestQueryService query = Substitute.For<IGitHubPullRequestQueryService>();
        ApplicationTaskCoordinator coordinator = new();
        _ = await coordinator.ShutdownAsync(TimeSpan.FromSeconds(1));
        PullRequestNavigationCache cache = new(
            query,
            new AccountWorkQuiescence(),
            coordinator,
            UnrestrictedAdaptivePrefetchPolicy.Instance);
        TaskCompletionSource<PullRequestPrefetchResult> terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;

        using IDisposable scheduled = cache.SchedulePrefetch(
            "token",
            "42",
            "owner",
            "repo",
            7,
            PullRequestPrefetchReason.Dwell,
            TimeSpan.FromHours(1),
            (result, _) =>
            {
                Interlocked.Increment(ref callbackCount);
                terminal.TrySetResult(result);
            });

        Assert.Equal(PullRequestPrefetchResult.Cancelled, await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref callbackCount));
        await query.DidNotReceiveWithAnyArgs().GetPullRequestPrefetchAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task IssueScheduledPrefetch_DisposeReportsCancelledExactlyOnce()
    {
        IGitHubIssueQueryService query = Substitute.For<IGitHubIssueQueryService>();
        IssueNavigationCache cache = new(query);
        TaskCompletionSource<IssuePrefetchResult> terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;

        IDisposable scheduled = cache.SchedulePrefetch(
            "token",
            "42",
            "owner",
            "repo",
            7,
            IssuePrefetchReason.Hover,
            TimeSpan.FromHours(1),
            (result, _) =>
            {
                Interlocked.Increment(ref callbackCount);
                terminal.TrySetResult(result);
            });
        scheduled.Dispose();

        Assert.Equal(IssuePrefetchResult.Cancelled, await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref callbackCount));
    }

    [Fact]
    public async Task PullRequestExecution_RechecksPolicyAfterDelay()
    {
        IGitHubPullRequestQueryService query = Substitute.For<IGitHubPullRequestQueryService>();
        RecordingAdmissionPolicy policy = new(allowSchedule: true, allowExecute: false);
        PullRequestNavigationCache cache = new(
            query,
            new AccountWorkQuiescence(),
            new ApplicationTaskCoordinator(),
            policy);

        using IDisposable scheduled = cache.SchedulePrefetch(
            "token", "42", "owner", "repo", 7, PullRequestPrefetchReason.Neighbor, TimeSpan.Zero);
        await policy.ExecutionObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([AdaptivePrefetchStage.Schedule, AdaptivePrefetchStage.Execute], policy.Stages);
        await query.DidNotReceiveWithAnyArgs().GetPullRequestPrefetchAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task PullRequestDirectPrefetch_ReturnsUnavailableWhenExecutionPolicyDeniesWork()
    {
        IGitHubPullRequestQueryService query = Substitute.For<IGitHubPullRequestQueryService>();
        RecordingAdmissionPolicy policy = new(allowSchedule: true, allowExecute: false);
        PullRequestNavigationCache cache = new(
            query,
            new AccountWorkQuiescence(),
            new ApplicationTaskCoordinator(),
            policy);

        PullRequestPrefetchResult result = await cache.PrefetchAsync(
            "token", "42", "owner", "repo", 7, PullRequestPrefetchReason.NavigationHandoff);

        Assert.Equal(PullRequestPrefetchResult.Unavailable, result);
        await query.DidNotReceiveWithAnyArgs().GetPullRequestPrefetchAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task IssueDirectPrefetch_ReturnsUnavailableWhenExecutionPolicyDeniesWork()
    {
        IGitHubIssueQueryService query = Substitute.For<IGitHubIssueQueryService>();
        RecordingAdmissionPolicy policy = new(allowSchedule: true, allowExecute: false);
        IssueNavigationCache cache = new(
            query,
            new AccountWorkQuiescence(),
            new ApplicationTaskCoordinator(),
            policy);

        IssuePrefetchResult result = await cache.PrefetchAsync(
            "token", "42", "owner", "repo", 7, IssuePrefetchReason.NavigationHandoff);

        Assert.Equal(IssuePrefetchResult.Unavailable, result);
        await query.DidNotReceiveWithAnyArgs().GetIssuePrefetchAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task PullRequestSchedule_RejectionDoesNotCreateCoordinatedTask()
    {
        IGitHubPullRequestQueryService query = Substitute.For<IGitHubPullRequestQueryService>();
        RecordingAdmissionPolicy policy = new(allowSchedule: false, allowExecute: true);
        RecordingTaskCoordinator coordinator = new();
        PullRequestPrefetchResult? completion = null;
        PullRequestNavigationCache cache = new(
            query,
            new AccountWorkQuiescence(),
            coordinator,
            policy);

        using IDisposable scheduled = cache.SchedulePrefetch(
            "token", "42", "owner", "repo", 7, PullRequestPrefetchReason.Hover, TimeSpan.Zero,
            (result, _) => completion = result);
        await Task.Delay(50);

        Assert.Equal(0, coordinator.RunCount);
        Assert.Equal([AdaptivePrefetchStage.Schedule], policy.Stages);
        Assert.Equal(PullRequestPrefetchResult.Unavailable, completion);
    }

    [Fact]
    public async Task PullRequestPrefetch_PropagatesFailureToItsTelemetryOwner()
    {
        IGitHubPullRequestQueryService query = Substitute.For<IGitHubPullRequestQueryService>();
        query.GetPullRequestPrefetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PullRequestConversationAggregate?>(new InvalidOperationException("offline")));
        PullRequestNavigationCache cache = new(query);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.PrefetchAsync(
            "token", "42", "owner", "repo", 7, PullRequestPrefetchReason.NavigationHandoff));
    }

    [Fact]
    public async Task IssuePrefetch_PropagatesFailureToItsTelemetryOwner()
    {
        IGitHubIssueQueryService query = Substitute.For<IGitHubIssueQueryService>();
        query.GetIssuePrefetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IssuePrefetchAggregate>(new InvalidOperationException("offline")));
        IssueNavigationCache cache = new(query);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.PrefetchAsync(
            "token", "42", "owner", "repo", 7, IssuePrefetchReason.NavigationHandoff));
    }

    [Fact]
    public async Task CommitExecution_RechecksPolicyAfterDelay()
    {
        IGitHubCommitQueryService query = Substitute.For<IGitHubCommitQueryService>();
        RecordingAdmissionPolicy policy = new(allowSchedule: true, allowExecute: false);
        CommitNavigationCache cache = new(query, new AccountWorkQuiescence(), policy);

        using IDisposable scheduled = cache.SchedulePrefetch(
            "token", "42", "owner", "repo", "sha", CommitPrefetchReason.Neighbor, TimeSpan.Zero);
        await policy.ExecutionObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([AdaptivePrefetchStage.Schedule, AdaptivePrefetchStage.Execute], policy.Stages);
        await query.DidNotReceiveWithAnyArgs().GetCommitPrefetchAsync(default!, default!, default!, default!, default!);
    }

    [Fact]
    public async Task CommitSchedule_RejectionDoesNotCreateCoordinatedTask()
    {
        IGitHubCommitQueryService query = Substitute.For<IGitHubCommitQueryService>();
        RecordingAdmissionPolicy policy = new(allowSchedule: false, allowExecute: true);
        RecordingTaskCoordinator coordinator = new();
        CommitNavigationCache cache = new(
            query,
            new AccountWorkQuiescence(),
            coordinator,
            policy);

        using IDisposable scheduled = cache.SchedulePrefetch(
            "token", "42", "owner", "repo", "sha", CommitPrefetchReason.Hover, TimeSpan.Zero);
        await Task.Delay(50);

        Assert.Equal(0, coordinator.RunCount);
        Assert.Equal([AdaptivePrefetchStage.Schedule], policy.Stages);
    }

    [Fact]
    public async Task CommitScheduledPrefetch_IsCancelledAndDrainedByApplicationShutdown()
    {
        IGitHubCommitQueryService query = Substitute.For<IGitHubCommitQueryService>();
        ApplicationTaskCoordinator coordinator = new();
        CommitNavigationCache cache = new(
            query,
            new AccountWorkQuiescence(),
            coordinator,
            UnrestrictedAdaptivePrefetchPolicy.Instance);

        using IDisposable scheduled = cache.SchedulePrefetch(
            "token", "42", "owner", "repo", "sha", CommitPrefetchReason.Dwell, TimeSpan.FromHours(1));
        Assert.Equal(1, coordinator.ActiveTaskCount);

        ApplicationTaskShutdownResult result = await coordinator.ShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Completed);
        Assert.Equal(0, result.PendingTaskCount);
        Assert.Equal(0, coordinator.ActiveTaskCount);
        await query.DidNotReceiveWithAnyArgs().GetCommitPrefetchAsync(default!, default!, default!, default!, default!);
    }

    [Fact]
    public async Task LatestWinsScheduler_HoverStormStartsOnlyFinalPrediction()
    {
        LatestWinsPrefetchScheduler scheduler = new();
        int startedCount = 0;
        int startedItem = -1;
        for (int item = 0; item < 100; item++)
        {
            int captured = item;
            scheduler.Schedule(
                TimeSpan.FromMilliseconds(30),
                () =>
                {
                    Interlocked.Increment(ref startedCount);
                    Volatile.Write(ref startedItem, captured);
                    return new RecordingDisposable();
                });
        }

        await Task.Delay(150);

        Assert.Equal(1, Volatile.Read(ref startedCount));
        Assert.Equal(99, Volatile.Read(ref startedItem));
        scheduler.Cancel();
    }

    [Fact]
    public async Task LatestWinsScheduler_RouteDepartureCancelsPendingAndActivePrediction()
    {
        LatestWinsPrefetchScheduler scheduler = new();
        int pendingStarted = 0;
        scheduler.Schedule(
            TimeSpan.FromMilliseconds(100),
            () =>
            {
                Interlocked.Increment(ref pendingStarted);
                return new RecordingDisposable();
            });
        scheduler.Cancel();
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref pendingStarted));

        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingDisposable active = new();
        scheduler.Schedule(
            TimeSpan.Zero,
            () =>
            {
                started.TrySetResult();
                return active;
            });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        scheduler.Cancel();

        Assert.True(active.IsDisposed);
    }

    [Fact]
    public async Task SaturatedPrefetchLane_DoesNotDelayForegroundRequests()
    {
        GitHubRequestQueue queue = new(
            foregroundReadConcurrency: 1,
            backgroundReadConcurrency: 1,
            mutationConcurrency: 1);
        TaskCompletionSource activeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releasePrefetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> activePrefetch = queue.EnqueueAsync(
            "active-prefetch",
            GitHubRequestPriority.Prefetch,
            async _ =>
            {
                activeStarted.TrySetResult();
                await releasePrefetch.Task;
                return 1;
            });
        await activeStarted.Task;
        Task<int> queuedPrefetch = queue.EnqueueAsync(
            "queued-prefetch",
            GitHubRequestPriority.Prefetch,
            _ => Task.FromResult(2));

        Task<int> foreground = queue.EnqueueAsync(
            "foreground-read",
            GitHubRequestPriority.UserInitiated,
            _ => Task.FromResult(3));

        Assert.Equal(3, await foreground.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(queuedPrefetch.IsCompleted);
        releasePrefetch.TrySetResult();
        int[] prefetchResults = await Task.WhenAll(activePrefetch, queuedPrefetch);
        Assert.Equal(new[] { 1, 2 }, prefetchResults);
    }

    private sealed class MutableEnvironment : IPrefetchEnvironmentState
    {
        public bool IsNetworkAvailable { get; set; } = true;

        public bool IsMetered { get; set; }

        public bool IsEnergySaverEnabled { get; set; }

        public bool IsMemoryPressureHigh { get; set; }
    }

    private sealed class RecordingAdmissionPolicy(bool allowSchedule, bool allowExecute) : IAdaptivePrefetchPolicy
    {
        private readonly object _gate = new();
        private readonly List<AdaptivePrefetchStage> _stages = [];

        public TaskCompletionSource ExecutionObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AdaptivePrefetchStage[] Stages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _stages];
                }
            }
        }

        public AdaptivePrefetchDecision Evaluate(
            string accountPartition,
            AdaptivePrefetchFeature feature,
            AdaptivePrefetchStage stage)
        {
            lock (_gate)
            {
                _stages.Add(stage);
            }

            if (stage == AdaptivePrefetchStage.Execute)
            {
                ExecutionObserved.TrySetResult();
            }

            bool allowed = stage == AdaptivePrefetchStage.Schedule ? allowSchedule : allowExecute;
            return new AdaptivePrefetchDecision(
                allowed,
                allowed ? AdaptivePrefetchSuppressionReason.None : AdaptivePrefetchSuppressionReason.MeteredConnection);
        }

        public void ObserveRateLimit(
            string accountPartition,
            int? remaining,
            DateTimeOffset? resetAt,
            TimeSpan? retryAfter = null,
            string? resource = null)
        {
        }

        public IReadOnlyList<AdaptivePrefetchCounter> GetCounters() => [];
    }

    private sealed class RecordingMetricTelemetry : ITelemetryService
    {
        public List<RecordedMetric> Metrics { get; } = [];

        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
        {
        }

        public void TrackMetric(
            string name,
            double value,
            IReadOnlyDictionary<string, string?>? properties = null) =>
            Metrics.Add(new RecordedMetric(
                name,
                value,
                new Dictionary<string, string?>(properties ?? new Dictionary<string, string?>())));

        public IPerformanceTrace StartTrace(
            string name,
            IReadOnlyDictionary<string, string?>? properties = null) => NoopTrace.Instance;
    }

    private sealed class RecordingTaskCoordinator : IApplicationTaskCoordinator
    {
        public event EventHandler<ApplicationTaskFailure>? TaskFailed
        {
            add { }
            remove { }
        }

        public int ActiveTaskCount => 0;

        public int RunCount { get; private set; }

        public Task RunAsync(
            Func<CancellationToken, Task> operation,
            ApplicationTaskOptions options,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            return operation(cancellationToken);
        }

        public Task CancelAccountAsync(string accountPartition, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void ActivateAccount(string accountPartition)
        {
        }

        public Task<ApplicationTaskShutdownResult> ShutdownAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplicationTaskShutdownResult(true, 0));
    }

    private sealed class RecordingDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    private sealed record RecordedMetric(
        string Name,
        double Value,
        IReadOnlyDictionary<string, string?> Properties);

    private sealed class NoopTrace : IPerformanceTrace
    {
        public static readonly IPerformanceTrace Instance = new NoopTrace();

        public void SetProperty(string key, string? value)
        {
        }

        public void Dispose()
        {
        }
    }
}
