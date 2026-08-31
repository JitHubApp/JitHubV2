using System.Collections.Generic;
using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class PullRequestTelemetryTests
{
    [Fact]
    public async Task Prefetch_EmitsCanonicalCompletionAndDurationTaxonomy()
    {
        RecordingTelemetryService telemetry = new();

        await PullRequestTelemetry.ObservePrefetchAsync(
            telemetry,
            PullRequestPrefetchReason.Hover,
            static () => Task.CompletedTask);

        Assert.Collection(
            telemetry.Events,
            started =>
            {
                Assert.Equal("pull_requests.prefetch.started", started.Name);
                Assert.Equal("hover", started.Properties["source"]);
            },
            completed =>
            {
                Assert.Equal("pull_requests.prefetch.completed", completed.Name);
                Assert.Equal("success", completed.Properties["result"]);
                Assert.Equal("lt_50ms", completed.Properties["duration_bucket"]);
            });
    }

    [Fact]
    public async Task CancelledPrefetch_UsesOneCanonicalSpelling()
    {
        RecordingTelemetryService telemetry = new();

        await PullRequestTelemetry.ObservePrefetchAsync(
            telemetry,
            PullRequestPrefetchReason.Dwell,
            static () => Task.FromCanceled(new CancellationToken(canceled: true)));

        Assert.Equal("cancelled", telemetry.Events[1].Properties["result"]);
    }

    [Fact]
    public async Task FailedPrefetch_CompletesOnceAfterStartedWithTruthfulResult()
    {
        RecordingTelemetryService telemetry = new();

        await PullRequestTelemetry.ObservePrefetchAsync(
            telemetry,
            PullRequestPrefetchReason.NavigationHandoff,
            static () => Task.FromException(new InvalidOperationException("offline")));

        Assert.Collection(
            telemetry.Events,
            started =>
            {
                Assert.Equal("pull_requests.prefetch.started", started.Name);
                Assert.Equal("navigation_handoff", started.Properties["source"]);
            },
            completed =>
            {
                Assert.Equal("pull_requests.prefetch.completed", completed.Name);
                Assert.Equal("navigation_handoff", completed.Properties["source"]);
                Assert.Equal("failed", completed.Properties["result"]);
            });
    }

    [Fact]
    public async Task SuppressedDirectPrefetch_CompletesAsUnavailable()
    {
        RecordingTelemetryService telemetry = new();

        await PullRequestTelemetry.ObservePrefetchAsync(
            telemetry,
            PullRequestPrefetchReason.NavigationHandoff,
            static () => Task.FromResult(PullRequestPrefetchResult.Unavailable));

        Assert.Collection(
            telemetry.Events,
            started => Assert.Equal("pull_requests.prefetch.started", started.Name),
            completed => Assert.Equal(TelemetryTaxonomy.Results.Unavailable, completed.Properties["result"]));
    }

    [Theory]
    [InlineData(PullRequestPrefetchReason.NavigationHandoff, "navigation_handoff")]
    [InlineData(PullRequestPrefetchReason.Dwell, "dwell")]
    [InlineData(PullRequestPrefetchReason.Hover, "hover")]
    [InlineData(PullRequestPrefetchReason.Neighbor, "neighbor")]
    public void PrefetchReasonRoundTripsThroughSanitizer(
        PullRequestPrefetchReason reason,
        string expected)
    {
        RecordingTelemetryService telemetry = new();

        PullRequestTelemetry.TrackPrefetchStarted(telemetry, reason);

        RecordedTelemetryEvent item = Assert.Single(telemetry.Events);
        IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(item.Properties);
        Assert.Equal(expected, sanitized["source"]);
    }

    [Theory]
    [InlineData(PullRequestPrefetchResult.Success, "success")]
    [InlineData(PullRequestPrefetchResult.Cancelled, "cancelled")]
    [InlineData(PullRequestPrefetchResult.Unavailable, "unavailable")]
    [InlineData(PullRequestPrefetchResult.Failed, "failed")]
    public void ScheduledPrefetchCompletion_MapsCacheOutcomeToCanonicalTelemetry(
        PullRequestPrefetchResult result,
        string expected)
    {
        RecordingTelemetryService telemetry = new();

        PullRequestTelemetry.TrackPrefetchCompleted(
            telemetry,
            PullRequestPrefetchReason.Neighbor,
            result,
            TimeSpan.FromMilliseconds(4));

        RecordedTelemetryEvent completed = Assert.Single(telemetry.Events);
        Assert.Equal("pull_requests.prefetch.completed", completed.Name);
        Assert.Equal(expected, completed.Properties["result"]);
        Assert.Equal("neighbor", completed.Properties["source"]);
    }

    [Fact]
    public void MyPageScheduledPrefetch_PreservesItsTelemetryOwner()
    {
        RecordingTelemetryService telemetry = new();

        PullRequestTelemetry.TrackPrefetchStarted(telemetry, PullRequestPrefetchReason.Dwell, "my");
        PullRequestTelemetry.TrackPrefetchCompleted(
            telemetry,
            PullRequestPrefetchReason.Dwell,
            PullRequestPrefetchResult.Failed,
            TimeSpan.Zero,
            "my");

        Assert.Collection(
            telemetry.Events,
            started => Assert.Equal("my", started.Properties["page"]),
            completed =>
            {
                Assert.Equal("my", completed.Properties["page"]);
                Assert.Equal(TelemetryTaxonomy.Results.Failed, completed.Properties["result"]);
            });
    }

    [Fact]
    public void CachedNavigation_EmitsOpenedBeforeCachedListCompletion()
    {
        RecordingTelemetryService telemetry = new();

        PullRequestTelemetry.TrackOpened(
            telemetry,
            "repo",
            TelemetryTaxonomy.Sources.Navigation);
        PullRequestTelemetry.TrackListLoaded(
            telemetry,
            "repo",
            TelemetryTaxonomy.Results.Success,
            TimeSpan.FromMilliseconds(12),
            CacheState.Fresh,
            count: 3);

        Assert.Collection(
            telemetry.Events,
            opened =>
            {
                Assert.Equal("pull_requests.opened", opened.Name);
                Assert.Equal(TelemetryTaxonomy.Sources.Navigation, opened.Properties["source"]);
            },
            loaded =>
            {
                Assert.Equal("pull_requests.list.loaded", loaded.Name);
                Assert.Equal(TelemetryTaxonomy.Results.Success, loaded.Properties["result"]);
                Assert.Equal("fresh", loaded.Properties["cache_state"]);
            });
    }

    [Fact]
    public void SupersededListRead_CompletesAsCancelledNotSuccess()
    {
        RecordingTelemetryService telemetry = new();

        PullRequestTelemetry.TrackListLoaded(
            telemetry,
            "repo",
            TelemetryTaxonomy.Results.Cancelled,
            TimeSpan.FromMilliseconds(4));

        RecordedTelemetryEvent loaded = Assert.Single(telemetry.Events);
        Assert.Equal("pull_requests.list.loaded", loaded.Name);
        Assert.Equal(TelemetryTaxonomy.Results.Cancelled, loaded.Properties["result"]);
        Assert.NotEqual(TelemetryTaxonomy.Results.Success, loaded.Properties["result"]);
    }

    [Fact]
    public void ThrowingTelemetryCannotAffectPullRequestAction()
    {
        ThrowingTelemetryService telemetry = new();

        Exception? exception = Record.Exception(() =>
            PullRequestTelemetry.TrackAction(telemetry, "merge", "success"));

        Assert.Null(exception);
        Assert.Equal(1, telemetry.Attempts);
    }

    private sealed class ThrowingTelemetryService : ITelemetryService
    {
        public int Attempts { get; private set; }

        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
        {
            Attempts++;
            throw new InvalidOperationException("telemetry unavailable");
        }

        public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null) =>
            throw new InvalidOperationException("telemetry unavailable");

        public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
            throw new InvalidOperationException("telemetry unavailable");
    }
}
