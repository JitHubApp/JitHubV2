using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class LayoutGeometryRecoveryTests
{
    // Bounds a wait so a broken handshake fails instead of hanging the run.
    // It is not a deadline under test, so it stays generous enough for a
    // loaded parallel testhost.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    public enum LivePass
    {
        LazyBand,
        EagerRelayout,
        ChangedBlockRelayout,
    }

    public enum FailurePoint
    {
        NestedMeasure,
        NestedMeasureCancellation,
        NestedArrange,
        CommitArrange,
    }

    [Theory]
    [InlineData(LivePass.LazyBand, FailurePoint.NestedMeasure)]
    [InlineData(LivePass.LazyBand, FailurePoint.NestedMeasureCancellation)]
    [InlineData(LivePass.LazyBand, FailurePoint.NestedArrange)]
    [InlineData(LivePass.LazyBand, FailurePoint.CommitArrange)]
    [InlineData(LivePass.EagerRelayout, FailurePoint.NestedMeasure)]
    [InlineData(LivePass.EagerRelayout, FailurePoint.NestedMeasureCancellation)]
    [InlineData(LivePass.EagerRelayout, FailurePoint.NestedArrange)]
    [InlineData(LivePass.EagerRelayout, FailurePoint.CommitArrange)]
    [InlineData(LivePass.ChangedBlockRelayout, FailurePoint.NestedMeasure)]
    [InlineData(LivePass.ChangedBlockRelayout, FailurePoint.NestedMeasureCancellation)]
    [InlineData(LivePass.ChangedBlockRelayout, FailurePoint.NestedArrange)]
    [InlineData(LivePass.ChangedBlockRelayout, FailurePoint.CommitArrange)]
    public void FailedLivePassLatchesRecoveryAndKeepsCommittedMetadataAndIndex(
        LivePass livePass,
        FailurePoint failurePoint)
    {
        Fixture fixture = CreateFixture();
        using var snapshot = CreateSnapshot(fixture);
        if (livePass == LivePass.LazyBand)
        {
            snapshot.EnableLazyLayout(
                availableWidth: 100,
                viewportTop: 100_000,
                viewportHeight: 1,
                overscan: 0,
                CancellationToken.None);
        }

        SnapshotState committedBeforeFailure = CaptureSnapshotState(snapshot);
        Assert.False(snapshot.GeometryRecoveryRequired);
        Assert.False(snapshot.TryClaimGeometryRecoveryRequest());

        Exception failure = failurePoint == FailurePoint.NestedMeasureCancellation
            ? new OperationCanceledException("Injected after nested geometry mutation.")
            : new InvalidOperationException($"Injected {failurePoint} failure after nested mutation.");
        fixture.Leaf.NextHeight = 61;
        switch (failurePoint)
        {
            case FailurePoint.NestedMeasure:
            case FailurePoint.NestedMeasureCancellation:
                fixture.Leaf.FailNextMeasure = failure;
                break;
            case FailurePoint.NestedArrange:
                fixture.Leaf.FailNextArrange = failure;
                break;
            case FailurePoint.CommitArrange:
                fixture.Root.FailNextArrange = failure;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failurePoint));
        }

        Exception? observed = Record.Exception(() => RunLivePass(snapshot, fixture, livePass));

        Assert.Same(failure, observed);
        Assert.True(snapshot.GeometryRecoveryRequired);
        Assert.Equal(committedBeforeFailure.Size, snapshot.Size);
        Assert.Equal(committedBeforeFailure.Revision, snapshot.LayoutRevision);
        Assert.Equal(committedBeforeFailure.MeasuredCount, snapshot.MeasuredTopLevelBlockCount);
        AssertViewportIndexUnchanged(snapshot, committedBeforeFailure);
        AssertReadLeasesDenied(snapshot);
        Assert.False(snapshot.IsBandMeasured(new LazyLayoutBand(0, 1)));

        // The lock-free request latch is consumable once, even when several
        // scheduling paths race to notice the same failed snapshot.
        Assert.True(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.False(snapshot.TryClaimGeometryRecoveryRequest());

        Fixture replacementFixture = CreateFixture();
        using var replacement = CreateSnapshot(replacementFixture);
        Assert.False(replacement.GeometryRecoveryRequired);
        Assert.True(replacement.TryBeginPaint());
        replacement.EndPaint();
        Assert.True(replacement.TryBeginInteraction());
        replacement.EndInteraction();
        Assert.True(snapshot.GeometryRecoveryRequired);
    }

    [Fact]
    public void LatchedRecoveryRejectsFurtherRelayoutWithoutNativeOrIndexWork()
    {
        Fixture fixture = CreateFixture();
        using var snapshot = CreateSnapshot(fixture);
        snapshot.EnableLazyLayout(
            availableWidth: 100,
            viewportTop: 100_000,
            viewportHeight: 1,
            overscan: 0,
            CancellationToken.None);

        var injected = new InvalidOperationException("The first live measure mutates then fails.");
        fixture.Leaf.NextHeight = 61;
        fixture.Leaf.FailNextMeasure = injected;
        Assert.Same(injected, Record.Exception(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 100), CancellationToken.None)));
        Assert.True(snapshot.GeometryRecoveryRequired);

        // Capture after the original failure and its rollback. The contract
        // under test is that every later incremental path is a read-only
        // refusal: it neither retries a broken native tree nor publishes a new
        // index/revision/size from that tainted snapshot.
        SnapshotState failed = CaptureSnapshotState(snapshot);
        int leafMeasures = fixture.Leaf.MeasureCount;
        int leafArranges = fixture.Leaf.ArrangeCount;
        int rootMeasures = fixture.Root.MeasureCount;
        int rootArranges = fixture.Root.ArrangeCount;

        LazyLayoutCommit lazyRetry = snapshot.EnsureMeasuredBand(
            new LazyLayoutBand(0, double.MaxValue), CancellationToken.None);
        Assert.False(lazyRetry.Changed);
        snapshot.RelayoutMeasuredBlocks(100, CancellationToken.None);
        snapshot.RelayoutChangedBlocks(
            new[] { fixture.Root.BlockIndex },
            100,
            CancellationToken.None);

        Assert.Equal(leafMeasures, fixture.Leaf.MeasureCount);
        Assert.Equal(leafArranges, fixture.Leaf.ArrangeCount);
        Assert.Equal(rootMeasures, fixture.Root.MeasureCount);
        Assert.Equal(rootArranges, fixture.Root.ArrangeCount);
        Assert.Equal(failed.Size, snapshot.Size);
        Assert.Equal(failed.Revision, snapshot.LayoutRevision);
        Assert.Equal(failed.MeasuredCount, snapshot.MeasuredTopLevelBlockCount);
        AssertViewportIndexUnchanged(snapshot, failed);
        AssertReadLeasesDenied(snapshot);

        int winners = 0;
        Parallel.For(0, 32, _ =>
        {
            if (snapshot.TryClaimGeometryRecoveryRequest())
                Interlocked.Increment(ref winners);
        });
        Assert.Equal(1, winners);
    }

    [Fact]
    public void PreCanceledLazyPassDoesNotLatchGeometryRecovery()
    {
        Fixture fixture = CreateFixture();
        using var snapshot = CreateSnapshot(fixture);
        snapshot.EnableLazyLayout(
            availableWidth: 100,
            viewportTop: 100_000,
            viewportHeight: 1,
            overscan: 0,
            CancellationToken.None);
        int measureCount = fixture.Leaf.MeasureCount;
        SnapshotState before = CaptureSnapshotState(snapshot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 1), cancellation.Token));

        Assert.False(snapshot.GeometryRecoveryRequired);
        Assert.False(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.Equal(measureCount, fixture.Leaf.MeasureCount);
        Assert.Equal(before.Size, snapshot.Size);
        Assert.Equal(before.Revision, snapshot.LayoutRevision);
        Assert.Equal(before.MeasuredCount, snapshot.MeasuredTopLevelBlockCount);
        Assert.True(snapshot.TryBeginPaint());
        snapshot.EndPaint();
        Assert.True(snapshot.TryBeginInteraction());
        snapshot.EndInteraction();
    }

    [Fact]
    public void PrivateImagePlacementPreflightFailureDoesNotLatchGeometryRecovery()
    {
        Fixture fixture = CreateFixture();
        using var snapshot = CreateSnapshot(fixture);
        int measureCount = fixture.Leaf.MeasureCount;
        SnapshotState before = CaptureSnapshotState(snapshot);

        PreparedImageRelayoutResult result = snapshot.TryRelayoutCompletedImageRoots(
            new[] { fixture.Root.BlockIndex },
            availableWidth: 100,
            CancellationToken.None,
            out _);

        Assert.Equal(PreparedImageRelayoutResult.Unavailable, result);
        Assert.False(snapshot.GeometryRecoveryRequired);
        Assert.False(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.Equal(measureCount, fixture.Leaf.MeasureCount);
        Assert.Equal(before.Size, snapshot.Size);
        Assert.Equal(before.Revision, snapshot.LayoutRevision);
        Assert.Equal(before.MeasuredCount, snapshot.MeasuredTopLevelBlockCount);
        Assert.True(snapshot.TryBeginPaint());
        snapshot.EndPaint();
        Assert.True(snapshot.TryBeginInteraction());
        snapshot.EndInteraction();
    }

    [Fact]
    public void CancellationWithNestedDeviceLossLatchesPrimaryHResultAndRecoveryClass()
    {
        var deviceFailure = new COMException(
            "Nested DirectWrite/device failure.",
            MarkdownRenderer.Diagnostics.GraphicsDeviceErrors.DxgiErrorDeviceRemoved);
        var primary = new OperationCanceledException(
            "The live measure was canceled after native device loss.",
            new AggregateException(deviceFailure));

        AssertLiveFailurePreservesPrimaryRecoveryScalars(primary);
    }

    [Fact]
    public void AggregateWithNestedDeviceLossLatchesAggregateHResultAndRecoveryClass()
    {
        var deviceFailure = new COMException(
            "Nested render-target failure.",
            MarkdownRenderer.Diagnostics.GraphicsDeviceErrors.D2DErrorRecreateTarget);
        var primary = new AggregateException(
            "Measure and native cleanup failed.",
            new InvalidOperationException("Primary measure failure."),
            new AggregateException(deviceFailure));

        AssertLiveFailurePreservesPrimaryRecoveryScalars(primary);
    }

    [Fact]
    public async Task RollbackDeviceLossUpgradesRecoveryClassWithoutRetainingExceptionObjects()
    {
        var stableRoot = new FaultingArrangeStackBox { BlockIndex = 0 };
        stableRoot.Add(new MutatingLeafBox { BlockIndex = 2, NextHeight = 24 });
        stableRoot.Measure(100);
        stableRoot.Arrange(0, 0, 100);

        var failingLeaf = new MutatingLeafBox { BlockIndex = 3, NextHeight = 61 };
        var failingRoot = new FaultingArrangeStackBox { BlockIndex = 1 };
        failingRoot.Add(failingLeaf);
        failingRoot.Measure(100);
        failingRoot.Arrange(0, (float)stableRoot.Bounds.Bottom + 8, 100);

        var trailing = new FixedHeightBox(30) { BlockIndex = 4 };
        trailing.Measure(100);
        trailing.Arrange(0, (float)failingRoot.Bounds.Bottom + 8, 100);

        var snapshot = new LayoutSnapshot(
            new BlockBox[] { stableRoot, failingRoot, trailing },
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: (float)trailing.Bounds.Bottom,
            blockSpacing: 8);
        snapshot.EnableLazyLayout(
            availableWidth: 100,
            viewportTop: 0,
            viewportHeight: 1,
            overscan: 0,
            CancellationToken.None);
        Assert.True(snapshot.IsBandMeasured(new LazyLayoutBand(0, 1)));

        var primary = new InvalidOperationException("The later root measure failed.");
        var rollbackDeviceFailure = new COMException(
            "Reflow of the committed predecessor failed during rollback.",
            MarkdownRenderer.Diagnostics.GraphicsDeviceErrors.DxgiErrorDeviceReset);
        failingLeaf.FailNextMeasure = primary;
        stableRoot.FailNextArrange = rollbackDeviceFailure;
        var failingBand = new LazyLayoutBand(failingRoot.Bounds.Top, failingRoot.Bounds.Bottom);

        Exception? failure = Record.Exception(() =>
            snapshot.EnsureMeasuredBand(failingBand, CancellationToken.None));

        var aggregate = Assert.IsType<AggregateException>(failure);
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Same(rollbackDeviceFailure, aggregate.InnerExceptions[1]);
        Assert.True(snapshot.GeometryRecoveryRequired);
        Assert.True(snapshot.GeometryRecoveryIsDeviceLost);
        Assert.Equal(primary.HResult, snapshot.GeometryRecoveryFailureHResult);

        // Recovery state is deliberately scalar-only. The exception graph is
        // surfaced to this caller, never retained by the published snapshot.
        AssertRecoveryStateUsesOnlyScalarFields();
        await snapshot.Retire().WaitAsync(HangGuard);
    }

    [Fact]
    public async Task HealthyBusySnapshotIsRetryableAndDoesNotClaimRecovery()
    {
        Fixture fixture = CreateFixture();
        using var snapshot = CreateSnapshot(fixture);
        snapshot.EnableLazyLayout(
            availableWidth: 100,
            viewportTop: 100_000,
            viewportHeight: 1,
            overscan: 0,
            CancellationToken.None);
        using var measureEntered = new ManualResetEventSlim();
        using var releaseMeasure = new ManualResetEventSlim();
        fixture.Leaf.MeasureEntered = measureEntered;
        fixture.Leaf.ReleaseMeasure = releaseMeasure;
        // A dedicated thread, so the busy measure starts even when the thread
        // pool is saturated by the parallel testhost.
        Task realization = Task.Factory.StartNew(
            () => snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 1), CancellationToken.None),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.True(measureEntered.Wait(HangGuard));
            Assert.False(snapshot.TryBeginPaint());
            Assert.False(snapshot.TryBeginInteraction());
            Assert.False(snapshot.GeometryRecoveryRequired);
            Assert.False(snapshot.TryClaimGeometryRecoveryRequest());
            Assert.False(snapshot.IsBandMeasured(new LazyLayoutBand(0, 1)));
        }
        finally
        {
            releaseMeasure.Set();
        }
        await realization.WaitAsync(HangGuard);

        Assert.False(snapshot.GeometryRecoveryRequired);
        Assert.False(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.True(snapshot.IsBandMeasured(new LazyLayoutBand(0, 1)));
        Assert.True(snapshot.MeasuredTopLevelBlockCount < snapshot.Blocks.Count,
            "The assertion must cover a healthy snapshot with estimated lazy roots remaining.");
        Assert.True(snapshot.TryBeginPaint());
        snapshot.EndPaint();
        Assert.True(snapshot.TryBeginInteraction());
        snapshot.EndInteraction();
    }

    [Fact]
    public async Task RetiredSnapshotIsNotConfusedWithRecoverableGeometryFailure()
    {
        var snapshot = new LayoutSnapshot(
            Array.Empty<BlockBox>(),
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 100);

        await snapshot.Retire().WaitAsync(HangGuard);

        Assert.False(snapshot.GeometryRecoveryRequired);
        Assert.False(snapshot.TryClaimGeometryRecoveryRequest());
        Assert.False(snapshot.TryBeginPaint());
        Assert.False(snapshot.TryBeginInteraction());
    }

    private static void RunLivePass(LayoutSnapshot snapshot, Fixture fixture, LivePass pass)
    {
        switch (pass)
        {
            case LivePass.LazyBand:
                snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 1), CancellationToken.None);
                break;
            case LivePass.EagerRelayout:
                snapshot.RelayoutMeasuredBlocks(100, CancellationToken.None);
                break;
            case LivePass.ChangedBlockRelayout:
                snapshot.RelayoutChangedBlocks(
                    new[] { fixture.Root.BlockIndex },
                    100,
                    CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pass));
        }
    }

    private static void AssertLiveFailurePreservesPrimaryRecoveryScalars(Exception failure)
    {
        Fixture fixture = CreateFixture();
        using var snapshot = CreateSnapshot(fixture);
        fixture.Leaf.FailNextMeasure = failure;

        Exception? observed = Record.Exception(() =>
            snapshot.RelayoutMeasuredBlocks(100, CancellationToken.None));

        Assert.Same(failure, observed);
        Assert.True(snapshot.GeometryRecoveryRequired);
        Assert.True(snapshot.GeometryRecoveryIsDeviceLost);
        Assert.Equal(failure.HResult, snapshot.GeometryRecoveryFailureHResult);
        AssertRecoveryStateUsesOnlyScalarFields();
    }

    private static void AssertRecoveryStateUsesOnlyScalarFields()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo? required = typeof(LayoutSnapshot).GetField("_geometryRecoveryRequired", flags);
        FieldInfo? claimed = typeof(LayoutSnapshot).GetField("_geometryRecoveryRequestClaimed", flags);
        FieldInfo? deviceLost = typeof(LayoutSnapshot).GetField("_geometryRecoveryIsDeviceLost", flags);
        FieldInfo? hresult = typeof(LayoutSnapshot).GetField("_geometryRecoveryFailureHResult", flags);

        Assert.NotNull(required);
        Assert.NotNull(claimed);
        Assert.NotNull(deviceLost);
        Assert.NotNull(hresult);
        Assert.Equal(typeof(int), required!.FieldType);
        Assert.Equal(typeof(int), claimed!.FieldType);
        Assert.Equal(typeof(int), deviceLost!.FieldType);
        Assert.Equal(typeof(int), hresult!.FieldType);
    }

    private static Fixture CreateFixture()
    {
        var leaf = new MutatingLeafBox { BlockIndex = 1, NextHeight = 24 };
        var root = new FaultingArrangeStackBox { BlockIndex = 0 };
        root.Add(leaf);
        root.Measure(100);
        root.Arrange(0, 0, 100);

        var trailing = new FixedHeightBox(30) { BlockIndex = 2 };
        trailing.Measure(100);
        trailing.Arrange(0, (float)root.Bounds.Bottom + 8, 100);
        return new Fixture(root, leaf, trailing);
    }

    private static LayoutSnapshot CreateSnapshot(Fixture fixture) => new(
        new BlockBox[] { fixture.Root, fixture.Trailing },
        new MarkdownSourceMap(string.Empty),
        width: 100,
        height: (float)fixture.Trailing.Bounds.Bottom,
        blockSpacing: 8);

    private static SnapshotState CaptureSnapshotState(LayoutSnapshot snapshot)
    {
        LayoutPlacementGeneration index = GetViewportIndex(snapshot);
        int[] ordinals = Enumerable.Range(0, index.Count)
            .Select(index.GetBlockOrdinal)
            .ToArray();
        (double Top, double Bottom)[] bounds = Enumerable.Range(0, index.Count)
            .Select(index.GetIndexedBounds)
            .ToArray();
        ViewportRange firstBand = index.Find(0, 1);
        ViewportRange fullBand = index.Find(0, double.MaxValue);
        return new SnapshotState(
            snapshot.Size,
            snapshot.LayoutRevision,
            snapshot.MeasuredTopLevelBlockCount,
            index,
            index.IsDocumentOrder,
            ordinals,
            bounds,
            firstBand.Start,
            firstBand.End,
            fullBand.Start,
            fullBand.End);
    }

    private static void AssertViewportIndexUnchanged(LayoutSnapshot snapshot, SnapshotState before)
    {
        LayoutPlacementGeneration current = GetViewportIndex(snapshot);
        Assert.Same(before.Index, current);
        Assert.Equal(before.IsDocumentOrder, current.IsDocumentOrder);
        Assert.Equal(before.Ordinals, Enumerable.Range(0, current.Count).Select(current.GetBlockOrdinal));
        Assert.Equal(before.Bounds, Enumerable.Range(0, current.Count).Select(current.GetIndexedBounds));
        ViewportRange firstBand = current.Find(0, 1);
        ViewportRange fullBand = current.Find(0, double.MaxValue);
        Assert.Equal(before.FirstBandStart, firstBand.Start);
        Assert.Equal(before.FirstBandEnd, firstBand.End);
        Assert.Equal(before.FullBandStart, fullBand.Start);
        Assert.Equal(before.FullBandEnd, fullBand.End);
    }

    private static LayoutPlacementGeneration GetViewportIndex(LayoutSnapshot snapshot)
        => snapshot.PublishedPlacementGeneration;

    private static void AssertReadLeasesDenied(LayoutSnapshot snapshot)
    {
        bool paint = snapshot.TryBeginPaint();
        if (paint)
            snapshot.EndPaint();
        Assert.False(paint);

        bool interaction = snapshot.TryBeginInteraction();
        if (interaction)
            snapshot.EndInteraction();
        Assert.False(interaction);
    }

    private sealed record Fixture(
        FaultingArrangeStackBox Root,
        MutatingLeafBox Leaf,
        FixedHeightBox Trailing);

    private sealed record SnapshotState(
        Size Size,
        long Revision,
        int MeasuredCount,
        LayoutPlacementGeneration Index,
        bool IsDocumentOrder,
        int[] Ordinals,
        (double Top, double Bottom)[] Bounds,
        int FirstBandStart,
        int FirstBandEnd,
        int FullBandStart,
        int FullBandEnd);

    private sealed class FaultingArrangeStackBox : StackBox
    {
        internal Exception? FailNextArrange { get; set; }
        internal int MeasureCount { get; private set; }
        internal int ArrangeCount { get; private set; }

        public override float Measure(float availableWidth)
        {
            MeasureCount++;
            return base.Measure(availableWidth);
        }

        public override void Arrange(float x, float y, float width)
        {
            ArrangeCount++;
            base.Arrange(x, y, width);
            Exception? failure = FailNextArrange;
            FailNextArrange = null;
            if (failure is not null)
                throw failure;
        }
    }

    private sealed class MutatingLeafBox : BlockBox
    {
        internal float NextHeight { get; set; }
        internal Exception? FailNextMeasure { get; set; }
        internal Exception? FailNextArrange { get; set; }
        internal int MeasureCount { get; private set; }
        internal int ArrangeCount { get; private set; }
        internal ManualResetEventSlim? MeasureEntered { get; set; }
        internal ManualResetEventSlim? ReleaseMeasure { get; set; }

        public override float Measure(float availableWidth)
        {
            MeasureCount++;
            Bounds = new Rect(0, 0, availableWidth, NextHeight);
            MeasureEntered?.Set();
            if (ReleaseMeasure is { } release && !release.Wait(HangGuard))
                throw new TimeoutException("The test did not release nested measurement.");
            Exception? failure = FailNextMeasure;
            FailNextMeasure = null;
            if (failure is not null)
                throw failure;
            return NextHeight;
        }

        public override void Arrange(float x, float y, float width)
        {
            ArrangeCount++;
            Bounds = new Rect(x, y, width, Bounds.Height);
            IsDirty = false;
            Exception? failure = FailNextArrange;
            FailNextArrange = null;
            if (failure is not null)
                throw failure;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }

    private sealed class FixedHeightBox(float height) : BlockBox
    {
        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, height);
            return height;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }
}
