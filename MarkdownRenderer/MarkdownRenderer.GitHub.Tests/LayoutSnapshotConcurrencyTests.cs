using System.Diagnostics;
using System.Threading;
using Microsoft.Graphics.Canvas;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutSnapshotConcurrencyTests
{
    [Fact]
    public void LazyFocusOrderIncludesUnmeasuredNestedTargetsAndResolvesTheirOwnerBand()
    {
        var root = new StackBox { BlockIndex = 10 };
        root.Add(new EmbedBox(
            Markdig.Markdown.Parse("placeholder")[0],
            StubEmbedFactory.Instance)
        {
            BlockIndex = 42,
        });
        var trailingTarget = new EmbedBox(
            Markdig.Markdown.Parse("trailing")[0],
            StubEmbedFactory.Instance)
        {
            BlockIndex = 84,
        };

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { root, trailingTarget },
            new MarkdownSourceMap(string.Empty),
            width: 320,
            height: 0,
            blockSpacing: 12);
        snapshot.EnableLazyLayout(
            availableWidth: 320,
            viewportTop: 10_000,
            viewportHeight: 100,
            overscan: 0,
            CancellationToken.None);

        IReadOnlyList<FocusableItem> focusables = snapshot.CollectFocusableItems();
        Assert.Collection(
            focusables,
            first => Assert.Equal(42, first.BlockIndex),
            second => Assert.Equal(84, second.BlockIndex));
        Assert.Same(focusables, snapshot.CollectFocusableItems());
        FocusableItem item = focusables[0];
        Assert.Equal(42, item.BlockIndex);
        Assert.True(item.IsBlockEmbed);
        Assert.Equal(0, snapshot.MeasuredTopLevelBlockCount);

        FocusTargetLayoutState state = snapshot.TryGetFocusTargetLayoutState(
            item.BlockIndex,
            out LazyLayoutBand estimatedBand);

        Assert.Equal(FocusTargetLayoutState.RequiresRealization, state);
        Assert.Equal(root.Bounds.Top, estimatedBand.Top);
        Assert.Equal(root.Bounds.Bottom, estimatedBand.Bottom);

        snapshot.EnsureMeasuredBand(estimatedBand, CancellationToken.None);

        Assert.Equal(
            FocusTargetLayoutState.Ready,
            snapshot.TryGetFocusTargetLayoutState(item.BlockIndex, out _));
        Assert.Equal(1, snapshot.MeasuredTopLevelBlockCount);
    }

    [Fact]
    public async Task LazyFocusStateReadDoesNotBlockBehindBackgroundMeasurement()
    {
        var block = new BlockingMeasureBox { BlockIndex = 73 };
        using var snapshot = CreateLazySnapshot(block);
        Task measure = Task.Run(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 100), CancellationToken.None));
        Assert.True(block.MeasureEntered.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            Task<FocusTargetLayoutState> stateRead = Task.Run(() =>
                snapshot.TryGetFocusTargetLayoutState(block.BlockIndex, out _));
            Assert.Same(stateRead, await Task.WhenAny(stateRead, Task.Delay(500)));
            Assert.Equal(FocusTargetLayoutState.Busy, await stateRead);
        }
        finally
        {
            block.ReleaseMeasure.Set();
        }

        await measure.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PointerInteractionRetriesWithoutBlockingBehindBackgroundMeasure()
    {
        var block = new BlockingMeasureBox();
        using var snapshot = CreateLazySnapshot(block);
        Task measure = Task.Run(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 100), CancellationToken.None));
        Assert.True(block.MeasureEntered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            Task<bool> input = Task.Run(() =>
            {
                if (!snapshot.TryBeginInteraction())
                    return false;
                try { return true; }
                finally { snapshot.EndInteraction(); }
            });
            Assert.Same(input, await Task.WhenAny(input, Task.Delay(500)));
            Assert.False(await input);
        }
        finally { block.ReleaseMeasure.Set(); }
        await measure.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            // Retrying sees the committed block, not a false miss from contention.
            Assert.True(snapshot.HitTest(new Point(0, 0), out _));
        }
        finally { snapshot.EndInteraction(); }
    }

    [Fact]
    public void RetiredSnapshotsCannotStartAnotherPointerInteraction()
    {
        var snapshot = new LayoutSnapshot(Array.Empty<BlockBox>(), new MarkdownSourceMap(""), 100, 100);
        snapshot.Dispose();
        Assert.False(snapshot.TryBeginInteraction());
    }

    [Fact]
    public async Task LazyCancellation_ReachesADeepNestedMeasureCheckpoint()
    {
        var leaf = new CancellationAwareLeafBox();
        var root = new NestedMeasureBox(new NestedMeasureBox(leaf));
        using var snapshot = CreateLazySnapshot(root);
        using var cts = new CancellationTokenSource();

        Task<Exception?> work = Task.Run<Exception?>(() => Record.Exception(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 100), cts.Token)));

        Assert.True(leaf.MeasureEntered.Wait(TimeSpan.FromSeconds(2)));
        cts.Cancel();

        Exception? error = await work.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsAssignableFrom<OperationCanceledException>(error);
    }

    [Fact]
    public async Task Retirement_ReturnsWithoutWaitingForContendedLayoutLock()
    {
        var block = new BlockingMeasureBox();
        var snapshot = CreateLazySnapshot(block);
        Task work = Task.Run(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 100), CancellationToken.None));

        Assert.True(block.MeasureEntered.Wait(TimeSpan.FromSeconds(2)));
        Task<(bool Captured, TimeSpan Elapsed, Task Drain)> retirement = Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            bool captured = snapshot.TryCaptureScrollAnchor(0, out _);
            Task drain = snapshot.Retire();
            stopwatch.Stop();
            return (captured, stopwatch.Elapsed, drain);
        });

        Task retirementDrain;
        try
        {
            Task completed = await Task.WhenAny(retirement, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Assert.Same(retirement, completed);
            var result = await retirement;
            Assert.False(result.Captured);
            Assert.True(result.Elapsed < TimeSpan.FromMilliseconds(250),
                $"Retirement waited {result.Elapsed.TotalMilliseconds:F1} ms for the layout lock.");
            Assert.False(result.Drain.IsCompleted);
            Assert.Same(result.Drain, snapshot.Retire());
            retirementDrain = result.Drain;
        }
        finally
        {
            block.ReleaseMeasure.Set();
        }

        await work.WaitAsync(TimeSpan.FromSeconds(2));
        await retirementDrain.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(block.Disposed.IsSet);
    }

    [Fact]
    public async Task QueuedLazyMeasurementDoesNotTouchBlocksAfterRetirementWinsTheLock()
    {
        var block = new DisposalAwareMeasureBox();
        var snapshot = CreateLazySnapshot(block);

        Task retirement = snapshot.Retire();
        await retirement.WaitAsync(TimeSpan.FromSeconds(2));

        LazyLayoutCommit commit = await Task.Run(() =>
            snapshot.EnsureMeasuredBand(
                new LazyLayoutBand(0, 100),
                CancellationToken.None));

        Assert.False(commit.Changed);
        Assert.Equal(0, commit.MeasuredBlocks);
        Assert.Equal(0, block.MeasureCount);
        Assert.True(block.Disposed.IsSet);
    }

    [Fact]
    public void ImageRelayoutRemeasuresOnlyTheChangedTopLevelOwner()
    {
        var first = new CountingMeasureBox(30) { BlockIndex = 10 };
        var changed = new CountingMeasureBox(50) { BlockIndex = 20 };
        var trailing = new CountingMeasureBox(70) { BlockIndex = 30 };
        foreach (BlockBox block in new BlockBox[] { first, changed, trailing })
        {
            float height = block.Measure(200);
            block.Arrange(0, block.BlockIndex, 200);
            Assert.True(height > 0);
        }

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { first, changed, trailing },
            new MarkdownSourceMap(string.Empty),
            width: 200,
            height: 180,
            blockSpacing: 8);

        snapshot.RelayoutChangedBlocks([changed.BlockIndex], 200, CancellationToken.None);

        Assert.Equal(1, first.MeasureCount);
        Assert.Equal(2, changed.MeasureCount);
        Assert.Equal(1, trailing.MeasureCount);
        Assert.Equal(changed.Bounds.Bottom + 8, trailing.Bounds.Top);
    }

    [Fact]
    public void ImageRelayoutFallsBackToFullMeasurementForAnUnknownOwner()
    {
        var first = new CountingMeasureBox(30) { BlockIndex = 10 };
        var second = new CountingMeasureBox(50) { BlockIndex = 20 };
        foreach (BlockBox block in new BlockBox[] { first, second })
        {
            block.Measure(200);
            block.Arrange(0, block.BlockIndex, 200);
        }

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { first, second },
            new MarkdownSourceMap(string.Empty),
            width: 200,
            height: 88,
            blockSpacing: 8);

        snapshot.RelayoutChangedBlocks([999], 200, CancellationToken.None);

        Assert.Equal(2, first.MeasureCount);
        Assert.Equal(2, second.MeasureCount);
        Assert.Equal(first.Bounds.Bottom + 8, second.Bounds.Top);
    }

    [Fact]
    public async Task RetirementDisposesLaterBlocksAfterASingleDisposalFailure()
    {
        var failure = new InvalidOperationException("expected disposal failure");
        var trailing = new TrackingDisposeBox();
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { new ThrowingDisposeBox(failure), trailing },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 100);

        InvalidOperationException observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Same(failure, observed);
        Assert.True(trailing.Disposed.IsSet);
    }

    [Fact]
    public async Task RetirementAggregatesMultipleDisposalFailuresAfterAttemptingEveryBlock()
    {
        var first = new InvalidOperationException("first");
        var second = new NotSupportedException("second");
        var trailing = new TrackingDisposeBox();
        var snapshot = new LayoutSnapshot(
            new BlockBox[]
            {
                new ThrowingDisposeBox(first),
                new ThrowingDisposeBox(second),
                trailing,
            },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 100);

        AggregateException observed = await Assert.ThrowsAsync<AggregateException>(
            () => snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Collection(
            observed.InnerExceptions,
            exception => Assert.Same(first, exception),
            exception => Assert.Same(second, exception));
        Assert.True(trailing.Disposed.IsSet);
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

    private sealed class StubEmbedFactory : IMarkdownEmbedFactory
    {
        internal static StubEmbedFactory Instance { get; } = new();

        public bool CanCreate(Markdig.Syntax.Block block) => true;

        public float MeasureHeight(Markdig.Syntax.Block block, float availableWidth) => 48;

        public FrameworkElement CreateBlock(Markdig.Syntax.Block block) =>
            throw new NotSupportedException("The layout-only regression must not realize XAML.");
    }

    private sealed class NestedMeasureBox : BlockBox
    {
        private readonly BlockBox _child;

        internal NestedMeasureBox(BlockBox child) => _child = child;

        public override float Measure(float availableWidth)
        {
            float height = _child.Measure(availableWidth);
            Bounds = new Rect(0, 0, availableWidth, height);
            return height;
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }

        public override void Dispose() => _child.Dispose();
    }

    private sealed class CancellationAwareLeafBox : BlockBox
    {
        internal ManualResetEventSlim MeasureEntered { get; } = new(initialState: false);

        public override float Measure(float availableWidth)
        {
            MeasureEntered.Set();
            while (true)
            {
                LayoutPassCancellation
                    .GetEffectiveToken(CancellationToken.None)
                    .ThrowIfCancellationRequested();
                Thread.SpinWait(64);
            }
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }

        public override void Dispose() => MeasureEntered.Dispose();
    }

    private sealed class BlockingMeasureBox : BlockBox
    {
        internal ManualResetEventSlim MeasureEntered { get; } = new(initialState: false);
        internal ManualResetEventSlim ReleaseMeasure { get; } = new(initialState: false);
        internal ManualResetEventSlim Disposed { get; } = new(initialState: false);

        public override float Measure(float availableWidth)
        {
            MeasureEntered.Set();
            ReleaseMeasure.Wait();
            Bounds = new Rect(0, 0, availableWidth, 48);
            return 48;
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }

        public override void Dispose() => Disposed.Set();
    }

    private sealed class DisposalAwareMeasureBox : BlockBox
    {
        private int _measureCount;

        internal int MeasureCount => Volatile.Read(ref _measureCount);
        internal ManualResetEventSlim Disposed { get; } = new(initialState: false);

        public override float Measure(float availableWidth)
        {
            if (Disposed.IsSet)
                throw new ObjectDisposedException(nameof(DisposalAwareMeasureBox));

            Interlocked.Increment(ref _measureCount);
            Bounds = new Rect(0, 0, availableWidth, 48);
            return 48;
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }

        public override void Dispose() => Disposed.Set();
    }

    private sealed class CountingMeasureBox(float height) : BlockBox
    {
        internal int MeasureCount { get; private set; }

        public override float Measure(float availableWidth)
        {
            MeasureCount++;
            Bounds = new Rect(0, 0, availableWidth, height);
            return height;
        }

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }
    }

    private sealed class ThrowingDisposeBox : BlockBox
    {
        private readonly Exception _failure;

        internal ThrowingDisposeBox(Exception failure) => _failure = failure;

        public override float Measure(float availableWidth) => 0;

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }

        public override void Dispose() => throw _failure;
    }

    private sealed class TrackingDisposeBox : BlockBox
    {
        internal ManualResetEventSlim Disposed { get; } = new(initialState: false);

        public override float Measure(float availableWidth) => 0;

        public override void Paint(CanvasDrawingSession drawingSession, Rect viewport) { }

        public override void Dispose() => Disposed.Set();
    }
}
