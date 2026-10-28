using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutSnapshotMeasuredReadLeaseTests
{
    [Fact]
    public void MeasuredBandSceneProbeRequiresCallerHeldReadLease()
    {
        var block = new TrackingBlock();
        using var snapshot = CreateSnapshot(block);

        Assert.False(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
            0,
            100,
            out IReadOnlyList<BlockBox> unprotected));
        Assert.Empty(unprotected);

        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.True(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                0,
                100,
                out IReadOnlyList<BlockBox> protectedBlocks));
            Assert.Collection(protectedBlocks, candidate => Assert.Same(block, candidate));
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    [Fact]
    public async Task BusyMeasureRejectsAReadWithoutWaitingAndLaterAdmitsCoherentGeometry()
    {
        var block = new TrackingBlock();
        using var snapshot = CreateSnapshot(block);
        block.HoldMeasure = true;
        Task mutation = Task.Run(() => snapshot.RelayoutMeasuredBlocks(100, CancellationToken.None));
        try
        {
            Assert.True(block.MeasureEntered.Wait(TimeSpan.FromSeconds(5)));
            // The mutation remains blocked until our finally releases it. A
            // blocking reader would deadlock here instead of reporting busy.
            Assert.False(snapshot.TryBeginInteraction());
            Assert.False(snapshot.TryCaptureScrollAnchor(1, out _));
            Assert.False(mutation.IsCompleted);
        }
        finally
        {
            block.ReleaseMeasure.Set();
        }
        await mutation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.Same(block, Assert.Single(snapshot.GetMeasuredTopLevelBlocks()));
            Assert.Equal(24, block.Bounds.Height);
            Assert.True(snapshot.TryCaptureScrollAnchor(1, out SnapshotScrollAnchor anchor));
            Assert.Equal(block.BlockIndex, anchor.BlockIndex);
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    [Fact]
    public async Task ExistingReadLeaseKeepsBorrowedMeasuredBlocksAliveDuringRetirement()
    {
        var block = new TrackingBlock();
        var snapshot = CreateSnapshot(block);
        Assert.True(snapshot.TryBeginInteraction());
        Task retirement;

        try
        {
            Assert.True(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                0,
                100,
                out IReadOnlyList<BlockBox> borrowedBlocks));
            Assert.Collection(borrowedBlocks, candidate => Assert.Same(block, candidate));

            // Retirement must publish its closed state promptly, but native
            // BlockBox destruction waits for this already-started inspection.
            retirement = snapshot.Retire();
            Assert.False(retirement.IsCompleted);
            Assert.False(block.IsDisposed);

            // Reentrant attempts after close cannot acquire a new lease; the
            // original lease remains the sole owner of the borrowed list.
            Assert.False(snapshot.TryBeginInteraction());
            Assert.Same(block, Assert.Single(borrowedBlocks));
            Assert.False(block.IsDisposed);
        }
        finally
        {
            snapshot.EndInteraction();
        }

        await retirement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(block.IsDisposed);
    }

    private static LayoutSnapshot CreateSnapshot(TrackingBlock block)
    {
        block.Measure(100);
        block.Arrange(0, 0, 100);
        return new LayoutSnapshot(
            new BlockBox[] { block },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 24);
    }

    private sealed class TrackingBlock : BlockBox
    {
        private int _disposed;
        internal readonly ManualResetEventSlim MeasureEntered = new();
        internal readonly ManualResetEventSlim ReleaseMeasure = new();
        internal bool HoldMeasure;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public override float Measure(float availableWidth)
        {
            if (HoldMeasure)
            {
                MeasureEntered.Set();
                if (!ReleaseMeasure.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The read-admission test did not release its measure gate.");
            }
            Bounds = new Rect(0, 0, availableWidth, 24);
            return 24;
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }

        public override void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            MeasureEntered.Dispose();
            ReleaseMeasure.Dispose();
        }
    }
}
