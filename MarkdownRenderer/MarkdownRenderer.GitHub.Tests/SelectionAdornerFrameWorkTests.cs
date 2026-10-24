using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SelectionAdornerFrameWorkTests
{
    [Fact]
    public void AdornerPaintWorkContributesToTheExistingLogicalFrameTotals()
    {
        const long logicalFrameId = 73;
        using var listener = new PerformanceEventListener();
        listener.PrepareFrameCapture(logicalFrameId, frameCount: 1);

        // Canvas invalidation and selection-adornment painting are separate
        // callbacks, but both belong in the same frame's paint totals.
        listener.PaintWork(logicalFrameId, elapsedStopwatchTicks: 11, allocatedBytes: 17);
        listener.PaintWork(logicalFrameId, elapsedStopwatchTicks: 19, allocatedBytes: 23);
        listener.PaintWork(logicalFrameId: 0, elapsedStopwatchTicks: 101, allocatedBytes: 103);
        listener.PaintWork(logicalFrameId: 0, elapsedStopwatchTicks: 107, allocatedBytes: 109);

        FrameWork frame = listener.SnapshotFrames(logicalFrameId, logicalFrameId)[logicalFrameId];
        Assert.Equal(2, frame.PaintCallbacks);
        Assert.Equal(30, frame.UiThreadWorkElapsedTicks);
        Assert.Equal(40, frame.AllocatedBytes);
        Assert.Equal(40, frame.PaintAllocatedBytes);
        Assert.Equal(30 * 1000.0 / System.Diagnostics.Stopwatch.Frequency, frame.PaintElapsedMilliseconds, precision: 10);
    }

    [Fact]
    public void PaintWorkAggregationKeepsEachLogicalFrameSeparate()
    {
        const long priorOpenIntervalId = 73;
        const long nextOpenIntervalId = 74;
        using var listener = new PerformanceEventListener();
        listener.PrepareFrameCapture(priorOpenIntervalId, frameCount: 2);

        // The callback wrapper supplies the ID of its open interval. Verify
        // the listener does not move work between the two reported frames.
        listener.PaintWork(priorOpenIntervalId, elapsedStopwatchTicks: 11, allocatedBytes: 17);
        listener.PaintWork(nextOpenIntervalId, elapsedStopwatchTicks: 19, allocatedBytes: 23);
        listener.PaintWork(logicalFrameId: 0, elapsedStopwatchTicks: 101, allocatedBytes: 103);

        IReadOnlyDictionary<long, FrameWork> frames = listener.SnapshotFrames(
            priorOpenIntervalId,
            nextOpenIntervalId);
        FrameWork prior = frames[priorOpenIntervalId];
        Assert.Equal(1, prior.PaintCallbacks);
        Assert.Equal(11, prior.UiThreadWorkElapsedTicks);
        Assert.Equal(17, prior.PaintAllocatedBytes);

        FrameWork next = frames[nextOpenIntervalId];
        Assert.Equal(1, next.PaintCallbacks);
        Assert.Equal(19, next.UiThreadWorkElapsedTicks);
        Assert.Equal(23, next.PaintAllocatedBytes);
    }
}
