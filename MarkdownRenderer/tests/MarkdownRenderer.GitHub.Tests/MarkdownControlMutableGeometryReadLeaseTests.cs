using System.IO;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Regressions for renderer-control readers that used to inspect mutable layout
/// geometry without pinning the exact snapshot generation that owned it.
/// </summary>
public sealed class MarkdownControlMutableGeometryReadLeaseTests
{
    [Fact]
    public void LazyBlockScrollTargetUsesOwnerEstimateUntilNestedTargetIsMeasured()
    {
        var root = new StackBox { BlockIndex = 1, InterChildSpacing = 5 };
        var first = new FixedBox(10, 20);
        var targetBlock = new FixedBox(11, 35);
        root.Add(first);
        root.Add(targetBlock);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { root },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 0);
        snapshot.EnableLazyLayout(
            availableWidth: 100,
            viewportTop: 10_000,
            viewportHeight: 100,
            overscan: 0,
            CancellationToken.None);

        SnapshotBlockScrollTargetState estimatedState = snapshot.TryCaptureBlockScrollTarget(
            targetBlock.BlockIndex,
            out SnapshotBlockScrollTarget estimated);

        Assert.Equal(SnapshotBlockScrollTargetState.RequiresRealization, estimatedState);
        Assert.False(estimated.OwnerMeasured);
        Assert.Equal(targetBlock.BlockIndex, estimated.BlockIndex);
        Assert.Equal(estimated.OwnerTop, estimated.TargetTop);
        Assert.Equal(root.Bounds.Top, estimated.OwnerTop);
        Assert.Equal(root.Bounds.Bottom, estimated.OwnerBottom);

        snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(estimated.OwnerTop, System.Math.Max(estimated.OwnerTop + 1, estimated.OwnerBottom)),
            CancellationToken.None);

        SnapshotBlockScrollTargetState measuredState = snapshot.TryCaptureBlockScrollTarget(
            targetBlock.BlockIndex,
            out SnapshotBlockScrollTarget measured);

        Assert.Equal(SnapshotBlockScrollTargetState.Ready, measuredState);
        Assert.True(measured.OwnerMeasured);
        Assert.Equal(targetBlock.Bounds.Top, measured.TargetTop);
        Assert.Equal(snapshot.LayoutRevision, measured.LayoutRevision);
        Assert.Equal(first.Bounds.Bottom + root.InterChildSpacing, measured.TargetTop);
    }

    [Fact]
    public async Task BlockScrollGeometryReadReturnsBusyInsteadOfWaitingForMeasurement()
    {
        var block = new GatedMeasureBox(73, 48);
        using var snapshot = CreateLazySnapshot(block);
        Task measurement = Task.Run(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 100), CancellationToken.None));
        Assert.True(block.MeasureEntered.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            Task<SnapshotBlockScrollTargetState> read = Task.Run(() =>
                snapshot.TryCaptureBlockScrollTarget(block.BlockIndex, out _));
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(500)));
            Assert.Equal(SnapshotBlockScrollTargetState.Busy, await read);
            Assert.False(measurement.IsCompleted);
        }
        finally
        {
            block.ReleaseMeasure.Set();
        }

        await measurement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
            SnapshotBlockScrollTargetState.Ready,
            snapshot.TryCaptureBlockScrollTarget(block.BlockIndex, out _));
    }

    [Fact]
    public void ScrollAnchorReplacementUsesOnlyTheCapturedBlockIdentity()
    {
        var first = new FixedBox(10, 20);
        var second = new FixedBox(20, 30);
        first.Measure(100);
        first.Arrange(0, 100, 100);
        second.Measure(100);
        second.Arrange(0, 200, 100);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { first, second },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 230);

        Assert.True(snapshot.TryCaptureScrollAnchorTarget(20, 5, out double exactOffset));
        Assert.Equal(195, exactOffset);

        // A replacement that removed the original anchor must preserve the
        // established height-ratio fallback, not snap to an unrelated block.
        Assert.False(snapshot.TryCaptureScrollAnchorTarget(15, 5, out double missingOffset));
        Assert.Equal(0, missingOffset);
    }

    private static LayoutSnapshot CreateLazySnapshot(BlockBox block)
    {
        var snapshot = new LayoutSnapshot(
            new[] { block },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 0);
        snapshot.EnableLazyLayout(
            availableWidth: 100,
            viewportTop: 10_000,
            viewportHeight: 100,
            overscan: 0,
            CancellationToken.None);
        return snapshot;
    }

    private sealed class FixedBox : BlockBox
    {
        private readonly float _height;

        public FixedBox(int blockIndex, double height)
        {
            BlockIndex = blockIndex;
            _height = checked((float)height);
        }

        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, _height);
            return _height;
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }
    }

    private sealed class GatedMeasureBox : BlockBox
    {
        private readonly float _height;

        internal ManualResetEventSlim MeasureEntered { get; } = new(initialState: false);
        internal ManualResetEventSlim ReleaseMeasure { get; } = new(initialState: false);

        public GatedMeasureBox(int blockIndex, double height)
        {
            BlockIndex = blockIndex;
            _height = checked((float)height);
        }

        public override float Measure(float availableWidth)
        {
            MeasureEntered.Set();
            ReleaseMeasure.Wait();
            Bounds = new Rect(0, 0, availableWidth, _height);
            return _height;
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }

        public override void Dispose()
        {
            ReleaseMeasure.Set();
            MeasureEntered.Dispose();
            ReleaseMeasure.Dispose();
        }
    }
}
