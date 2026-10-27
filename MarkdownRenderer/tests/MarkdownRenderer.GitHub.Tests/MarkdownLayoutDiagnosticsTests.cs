// MarkdownLayoutDiagnostics exists only in the EnableMarkdownLayoutDiagnostics
// lane, like the other diagnostics-only renderer contracts in this project.
#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Diagnostics;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownLayoutDiagnosticsTests
{
    [Fact]
    public void Capture_ReportsNativeLayoutAndBuilderPhaseAggregates()
    {
        var diagnostics = new MarkdownLayoutDiagnostics();
        diagnostics.SetTopLevelBlockCount(20);
        diagnostics.RecordCanvasTextLayoutConstructor(Stopwatch.Frequency / 4);
        diagnostics.RecordCanvasTextLayoutConstructor(Stopwatch.Frequency / 2);
        diagnostics.RecordBlockConstruction(Stopwatch.Frequency / 10);
        diagnostics.RecordMeasureArrange(Stopwatch.Frequency / 5);
        diagnostics.RecordSnapshotConstruction(Stopwatch.Frequency / 20);

        MarkdownLayoutDiagnosticsSnapshot snapshot = diagnostics.Capture();

        Assert.Equal(20, snapshot.TopLevelBlockCount);
        Assert.Equal(2, snapshot.CanvasTextLayoutConstructorCalls);
        Assert.Equal(750, snapshot.CanvasTextLayoutConstructorMilliseconds, precision: 3);
        Assert.Equal(100, snapshot.BlockConstructionMilliseconds, precision: 3);
        Assert.Equal(200, snapshot.MeasureArrangeMilliseconds, precision: 3);
        Assert.Equal(50, snapshot.SnapshotConstructionMilliseconds, precision: 3);

        diagnostics.RecordCanvasTextLayoutConstructor(Stopwatch.Frequency);
        Assert.Equal(snapshot, diagnostics.Capture());
    }

    [Fact]
    public void CaptureDispatcher_ReportsMaximumHeartbeatGapAndSentinelDelay()
    {
        var diagnostics = new MarkdownLayoutDiagnostics();
        long start = Stopwatch.Frequency * 10;
        diagnostics.StartDispatcherHeartbeat(start);
        diagnostics.RecordDispatcherHeartbeat(start + MillisecondsToTicks(16));
        diagnostics.RecordDispatcherHeartbeat(start + MillisecondsToTicks(44));
        diagnostics.RecordDispatcherSentinelEnqueue(
            start + MillisecondsToTicks(50),
            enqueued: true);
        diagnostics.RecordDispatcherSentinelDispatched(start + MillisecondsToTicks(91));

        MarkdownInitialLayoutDispatcherDiagnosticsSnapshot snapshot = diagnostics.CaptureDispatcher();

        Assert.True(snapshot.SentinelEnqueued);
        Assert.True(snapshot.SentinelDispatched);
        Assert.Equal(41, snapshot.SentinelDelayMilliseconds, precision: 3);
        Assert.Equal(2, snapshot.HeartbeatSampleCount);
        Assert.Equal(28, snapshot.HeartbeatMaxGapMilliseconds, precision: 3);
    }

    [Fact]
    public void CaptureDispatcher_FreezesHeartbeatButIncludesLateSentinelDispatch()
    {
        var diagnostics = new MarkdownLayoutDiagnostics();
        long start = Stopwatch.Frequency * 10;
        diagnostics.StartDispatcherHeartbeat(start);
        diagnostics.RecordDispatcherHeartbeat(start + MillisecondsToTicks(16));
        diagnostics.RecordDispatcherSentinelEnqueue(
            start + MillisecondsToTicks(20),
            enqueued: true);
        // StopAndCapture records one final sample so a blocked timer callback
        // cannot hide the gap between its last tick and the first commit.
        diagnostics.RecordDispatcherHeartbeat(start + MillisecondsToTicks(40));

        MarkdownInitialLayoutDispatcherDiagnosticsSnapshot atCommit =
            diagnostics.CaptureDispatcher(freezeHeartbeat: true);
        diagnostics.RecordDispatcherSentinelDispatched(start + MillisecondsToTicks(50));
        diagnostics.RecordDispatcherHeartbeat(start + MillisecondsToTicks(80));
        MarkdownInitialLayoutDispatcherDiagnosticsSnapshot afterSentinel = diagnostics.CaptureDispatcher();

        Assert.True(atCommit.SentinelEnqueued);
        Assert.False(atCommit.SentinelDispatched);
        Assert.Equal(0, atCommit.SentinelDelayMilliseconds);
        Assert.Equal(2, atCommit.HeartbeatSampleCount);
        Assert.Equal(24, atCommit.HeartbeatMaxGapMilliseconds, precision: 3);
        Assert.True(afterSentinel.SentinelDispatched);
        Assert.Equal(30, afterSentinel.SentinelDelayMilliseconds, precision: 3);
        Assert.Equal(atCommit.HeartbeatSampleCount, afterSentinel.HeartbeatSampleCount);
        Assert.Equal(atCommit.HeartbeatMaxGapMilliseconds, afterSentinel.HeartbeatMaxGapMilliseconds);
        Assert.Equal(afterSentinel, diagnostics.CaptureDispatcher());
    }

    [Fact]
    public void CaptureDispatcher_LeavesFailedSentinelExplicit()
    {
        var diagnostics = new MarkdownLayoutDiagnostics();
        long start = Stopwatch.Frequency * 10;
        diagnostics.RecordDispatcherSentinelEnqueue(
            start + MillisecondsToTicks(20),
            enqueued: false);

        MarkdownInitialLayoutDispatcherDiagnosticsSnapshot snapshot =
            diagnostics.CaptureDispatcher(freezeHeartbeat: true);

        Assert.False(snapshot.SentinelEnqueued);
        Assert.False(snapshot.SentinelDispatched);
        Assert.Equal(0, snapshot.SentinelDelayMilliseconds);
    }

    private static long MillisecondsToTicks(long milliseconds) =>
        Stopwatch.Frequency * milliseconds / 1_000;
}
#endif
