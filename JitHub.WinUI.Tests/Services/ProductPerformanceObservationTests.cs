using System.Diagnostics;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ProductPerformanceObservationTests
{
    private static readonly object ReadinessGate = new();

    [Fact]
    public void ContentTransition_DoesNotPassWhenInvokeReturnsButDestinationIdentityDoesNotChange()
    {
        long started = Stopwatch.GetTimestamp();
        ProductPerformanceContentTransitionTracker tracker = new(
            started,
            previousIdentity: "issue:41",
            requiresIdentityChange: true,
            requiredStableFrames: 3);

        for (int frame = 1; frame <= 8; frame++)
        {
            tracker.Observe(Observation("issue:41", frame), AtMilliseconds(started, frame * 5));
        }

        Assert.NotNull(tracker.FirstDataContent);
        Assert.False(tracker.IsSettled);
        Assert.Null(tracker.SettledDataContent);
    }

    [Fact]
    public void ContentTransition_MeasuresFirstMeaningfulAndSettledContentIndependently()
    {
        long started = Stopwatch.GetTimestamp();
        ProductPerformanceContentTransitionTracker tracker = new(started, requiredStableFrames: 3);

        tracker.Observe(Observation("detail:42", 1), AtMilliseconds(started, 10));
        tracker.Observe(Observation("detail:42", 2), AtMilliseconds(started, 18));
        tracker.Observe(Observation("detail:42", 3), AtMilliseconds(started, 26));

        Assert.True(tracker.IsSettled);
        Assert.Equal(TimeSpan.FromMilliseconds(10), tracker.FirstDataContent);
        Assert.Equal(TimeSpan.FromMilliseconds(26), tracker.SettledDataContent);
        Assert.True(tracker.SettledDataContent > tracker.FirstDataContent);
    }

    [Fact]
    public void ContentTransition_RecordsCachedRowsWhileBackgroundRefreshIsStillBusy()
    {
        long started = Stopwatch.GetTimestamp();
        ProductPerformanceContentTransitionTracker tracker = new(started, requiredStableFrames: 3);

        tracker.Observe(Observation("cached:42", 1, busy: true), AtMilliseconds(started, 6));
        tracker.Observe(Observation("cached:42", 2, busy: true), AtMilliseconds(started, 12));
        tracker.Observe(Observation("cached:42", 3), AtMilliseconds(started, 20));
        tracker.Observe(Observation("cached:42", 4), AtMilliseconds(started, 28));
        tracker.Observe(Observation("cached:42", 5), AtMilliseconds(started, 36));

        Assert.Equal(TimeSpan.FromMilliseconds(6), tracker.FirstDataContent);
        Assert.Equal(TimeSpan.FromMilliseconds(36), tracker.SettledDataContent);
        Assert.Equal(0, tracker.BlankingFrameCount);
    }

    [Fact]
    public void ContentTransition_CountsRenderedBlankFramesAndIgnoresDuplicateSamplesWithinOneFrame()
    {
        long started = Stopwatch.GetTimestamp();
        ProductPerformanceContentTransitionTracker tracker = new(started, requiredStableFrames: 3);

        tracker.Observe(Observation("detail:42", 1), AtMilliseconds(started, 5));
        tracker.Observe(Observation(string.Empty, 2, count: 0, visible: false), AtMilliseconds(started, 10));
        tracker.Observe(Observation(string.Empty, 2, count: 0, visible: false), AtMilliseconds(started, 11));
        tracker.Observe(Observation("detail:42", 3), AtMilliseconds(started, 15));
        tracker.Observe(Observation("detail:42", 4), AtMilliseconds(started, 20));
        tracker.Observe(Observation("detail:42", 5), AtMilliseconds(started, 25));

        Assert.Equal(1, tracker.BlankingFrameCount);
        Assert.True(tracker.IsSettled);
    }

    [Fact]
    public void ScrollTransition_RequiresBothOffsetAndRenderedFrameChange()
    {
        long started = Stopwatch.GetTimestamp();
        ProductPerformanceScrollTransitionTracker tracker = new(started, initialOffset: 10, initialFrame: 100);

        tracker.Observe(20, new ProductPerformanceHeartbeat(100, 4), AtMilliseconds(started, 5));
        Assert.False(tracker.IsCompleted);
        tracker.Observe(10, new ProductPerformanceHeartbeat(101, 5), AtMilliseconds(started, 8));
        Assert.False(tracker.IsCompleted);
        tracker.Observe(20, new ProductPerformanceHeartbeat(101, 5), AtMilliseconds(started, 12));

        Assert.True(tracker.IsCompleted);
        Assert.Equal(TimeSpan.FromMilliseconds(12), tracker.Completed);
    }

    [Fact]
    public void ScrollTransition_UsesAppSideRenderedTimestampInsteadOfObserverLatency()
    {
        long started = Stopwatch.GetTimestamp();
        ProductPerformanceScrollTransitionTracker tracker = new(started, initialOffset: 10, initialFrame: 100);

        tracker.ObserveRenderedTimestamp(AtMilliseconds(started, 14));
        Assert.False(tracker.IsCompleted);
        tracker.Observe(20, new ProductPerformanceHeartbeat(101, 5), AtMilliseconds(started, 80));

        Assert.Equal(TimeSpan.FromMilliseconds(14), tracker.Completed);
    }

    [Fact]
    public void ScrollTransition_UsesAppSideInputReceiptForCrossProcessAutomation()
    {
        long observerStarted = Stopwatch.GetTimestamp();
        long appStarted = AtMilliseconds(observerStarted, 80);
        ProductPerformanceScrollTransitionTracker tracker = new(observerStarted, initialOffset: 10, initialFrame: 100);

        tracker.ObserveRenderedInterval(appStarted, AtMilliseconds(appStarted, 15));
        Assert.False(tracker.IsCompleted);
        tracker.Observe(20, new ProductPerformanceHeartbeat(101, 5), AtMilliseconds(observerStarted, 120));

        Assert.Equal(TimeSpan.FromMilliseconds(15), tracker.Completed);
    }

    [Fact]
    public void ScrollStatus_RoundTripsCompositionTimestamps()
    {
        ProductPerformanceScrollStatus expected = new(7, 1234, 5678);

        Assert.True(ProductPerformanceScrollStatus.TryParse(expected.Format(), out ProductPerformanceScrollStatus actual));
        Assert.Equal(expected, actual);
        Assert.False(ProductPerformanceScrollStatus.TryParse("scroll;sequence=7;started_ticks=0;rendered_ticks=8", out _));
    }

    [Fact]
    public void Heartbeat_RequiresBothCompositionAndDispatcherCounters()
    {
        Assert.True(ProductPerformanceHeartbeat.TryParse(
            "frame=12;dispatcher=9;interactive_ticks=1234",
            out ProductPerformanceHeartbeat heartbeat));
        Assert.Equal(12, heartbeat.Frame);
        Assert.Equal(9, heartbeat.Dispatcher);
        Assert.Equal(1234, heartbeat.InteractiveTimestamp);
        Assert.False(ProductPerformanceHeartbeat.TryParse("frame=12", out _));
        Assert.False(ProductPerformanceHeartbeat.TryParse("dispatcher=9", out _));
    }

    [Fact]
    public void ReadyStatus_PreservesRouteAndCompleteIdentity()
    {
        Assert.True(ProductPerformanceReadyStatus.TryParse(
            "ready;route=repo_code;identity=repo=JitHubApp/JitHubV2;count=4",
            out ProductPerformanceReadyStatus status));
        Assert.Equal("repo_code", status.Route);
        Assert.Equal("repo=JitHubApp/JitHubV2;count=4", status.Identity);
        Assert.False(ProductPerformanceReadyStatus.TryParse("ready;identity=count=4", out _));
        Assert.False(ProductPerformanceReadyStatus.TryParse("pending", out _));
    }

    [Fact]
    public void ReadyStatus_PreservesCompositionTimestampsWithoutConsumingIdentityMetadata()
    {
        string formatted = ProductPerformanceReadiness.FormatStatus(
            "repo_pull_requests",
            "count=42;selected=17",
            startedTimestamp: 1000,
            firstRenderedTimestamp: 1234,
            settledTimestamp: 5678);

        Assert.True(ProductPerformanceReadyStatus.TryParse(formatted, out ProductPerformanceReadyStatus status));
        Assert.Equal("repo_pull_requests", status.Route);
        Assert.Equal("count=42;selected=17", status.Identity);
        Assert.Equal(1000, status.StartedTimestamp);
        Assert.Equal(1234, status.FirstRenderedTimestamp);
        Assert.Equal(5678, status.SettledTimestamp);
    }

    [Fact]
    public void ContentTransition_UsesAppSideCompositionTimestampsInsteadOfObserverLatency()
    {
        long started = Stopwatch.GetTimestamp();
        long first = AtMilliseconds(started, 12);
        long settled = AtMilliseconds(started, 38);
        ProductPerformanceContentTransitionTracker tracker = new(started, requiredStableFrames: 3);

        tracker.Observe(
            new ProductPerformanceContentObservation(
                "detail:42",
                1,
                true,
                false,
                new ProductPerformanceHeartbeat(20, 20),
                MeasurementStartedTimestamp: started,
                FirstRenderedTimestamp: first,
                SettledTimestamp: settled),
            AtMilliseconds(started, 400));

        Assert.Equal(TimeSpan.FromMilliseconds(12), tracker.FirstDataContent);
        Assert.Equal(TimeSpan.FromMilliseconds(38), tracker.SettledDataContent);
    }

    [Fact]
    public void TraversalCommitEndsGenerationAndRejectsLaterStages()
    {
        lock (ReadinessGate)
        {
            string? previous = Environment.GetEnvironmentVariable("JITHUB_PERFORMANCE_FIXTURE");
            List<ProductPerformanceTraversalStage> stages = [];
            EventHandler<ProductPerformanceTraversalStage> handler = (_, stage) => stages.Add(stage);
            ProductPerformanceReadiness.TraversalStageRecorded += handler;
            try
            {
                Environment.SetEnvironmentVariable("JITHUB_PERFORMANCE_FIXTURE", "1");
                ProductPerformanceReadiness.BeginTraversal("stars", "repo:42", "repo_code");
                ProductPerformanceReadiness.RecordTraversalStage("before.commit");
                ProductPerformanceReadiness.CommitTraversal("repo_code", "repo:42");
                ProductPerformanceReadiness.RecordTraversalStage("after.commit");

                ProductPerformanceTraversalStage stage = Assert.Single(
                    stages,
                    candidate => candidate.Stage is "before.commit" or "after.commit");
                Assert.Equal("before.commit", stage.Stage);
                Assert.True(stage.Generation > 0);
            }
            finally
            {
                ProductPerformanceReadiness.CancelTraversal();
                ProductPerformanceReadiness.TraversalStageRecorded -= handler;
                Environment.SetEnvironmentVariable("JITHUB_PERFORMANCE_FIXTURE", previous);
            }
        }
    }

    private static ProductPerformanceContentObservation Observation(
        string identity,
        long frame,
        int count = 1,
        bool visible = true,
        bool busy = false) =>
        new(identity, count, visible, busy, new ProductPerformanceHeartbeat(frame, frame));

    private static long AtMilliseconds(long started, int milliseconds) =>
        started + (long)(Stopwatch.Frequency * (milliseconds / 1000d));
}
