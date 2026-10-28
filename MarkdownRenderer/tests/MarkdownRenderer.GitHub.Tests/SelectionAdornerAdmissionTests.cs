using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Selection;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Exercises the nonblocking selection-adornment seam with a real snapshot
/// lease and a WARP surface. This is not a live WinUI control/lifecycle test.
/// </summary>
public sealed class SelectionAdornerAdmissionTests
{
    private static readonly Color PriorFrameColor = Color.FromArgb(255, 18, 44, 72);
    private static readonly Color SelectionColor = Color.FromArgb(255, 243, 191, 32);

    [Fact]
    public async Task BusySnapshotKeepsCallerOwnedWarpFrameUntilOneAdmittedRetry()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        using var target = new CanvasRenderTarget(device, 120, 100, 96);
        PaintWholeTarget(target, PriorFrameColor);

        var block = new BlockingSelectionBox { BlockIndex = 1 };
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { block },
            new MarkdownSourceMap("busy selection"),
            width: 120,
            height: 100);
        snapshot.EnableLazyLayout(
            availableWidth: 120,
            viewportTop: 10_000,
            viewportHeight: 32,
            overscan: 0,
            CancellationToken.None);

        var selection = new SelectionController();
        selection.SetAnchor(new DocumentPosition(1, 0, 0));
        selection.ExtendTo(new DocumentPosition(1, 0, 1));
        var viewport = new Rect(0, 0, 120, 100);
        var retry = new SelectionAdornerRetryState();
        retry.Request();
        Assert.True(retry.TryBeginFrame());

        Task realization = Task.Run(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 100), CancellationToken.None));
        Assert.True(block.MeasureEntered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            Color[] priorPixels = target.GetPixelColors();
            var operation = new RefreshOperation(
                selection,
                viewport,
                target,
                SelectionColor,
                retryOnce: false,
                succeeds: true);

            Assert.Equal(
                SelectionAdornerRefreshResult.Busy,
                SelectionAdornerRefreshCoordinator.TryRefresh(snapshot, ref operation));
            retry.Retry();

            Assert.Equal(0, operation.CallCount);
            Assert.Equal(0, operation.GeometryCount);
            Assert.Equal(0, operation.CommitCount);
            Assert.True(retry.IsPending);
            Assert.Equal(priorPixels, target.GetPixelColors());
        }
        finally
        {
            block.ReleaseMeasure.Set();
        }

        await realization.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(retry.TryBeginFrame());
        var admittedOperation = new RefreshOperation(
            selection,
            viewport,
            target,
            SelectionColor,
            retryOnce: false,
            succeeds: true);
        Assert.Equal(
            SelectionAdornerRefreshResult.Completed,
            SelectionAdornerRefreshCoordinator.TryRefresh(snapshot, ref admittedOperation));
        retry.Complete();

        Assert.Equal(1, admittedOperation.CallCount);
        Assert.Equal(1, admittedOperation.CommitCount);
        Assert.True(admittedOperation.ObservedReadLease);
        Assert.False(retry.IsPending);
        Assert.Contains(target.GetPixelColors(), pixel => pixel == SelectionColor);

        Assert.True(snapshot.TryBeginPaint());
        snapshot.EndPaint();
    }

    [Fact]
    public void RetryResultsCoalesceToOneFrameAndCancellationRemovesPendingAttempt()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        using var target = new CanvasRenderTarget(device, 80, 60, 96);
        var block = new FixedSelectionBox(new Rect(4, 8, 30, 12)) { BlockIndex = 1 };
        block.Measure(80);
        block.Arrange(4, 8, 30);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { block },
            new MarkdownSourceMap("retry"),
            width: 80,
            height: 60);
        var selection = new SelectionController();
        selection.SetAnchor(new DocumentPosition(1, 0, 0));
        selection.ExtendTo(new DocumentPosition(1, 0, 1));
        var viewport = new Rect(0, 0, 80, 60);
        var retry = new SelectionAdornerRetryState();

        retry.Request();
        retry.Request();
        retry.Request();
        Assert.True(retry.IsPending);
        Assert.True(retry.TryBeginFrame());
        Assert.False(retry.TryBeginFrame());

        var firstAttempt = new RefreshOperation(
            selection,
            viewport,
            target,
            SelectionColor,
            retryOnce: true,
            succeeds: true);
        Assert.Equal(
            SelectionAdornerRefreshResult.Retry,
            SelectionAdornerRefreshCoordinator.TryRefresh(snapshot, ref firstAttempt));
        Assert.Equal(1, firstAttempt.CallCount);
        Assert.Equal(1, firstAttempt.GeometryCount);
        Assert.Equal(0, firstAttempt.CommitCount);
        retry.Retry();

        Assert.True(retry.TryBeginFrame());
        var nextAttempt = new RefreshOperation(
            selection,
            viewport,
            target,
            SelectionColor,
            retryOnce: false,
            succeeds: true);
        Assert.Equal(
            SelectionAdornerRefreshResult.Completed,
            SelectionAdornerRefreshCoordinator.TryRefresh(snapshot, ref nextAttempt));
        retry.Complete();

        Assert.Equal(1, nextAttempt.CallCount);
        Assert.Equal(1, nextAttempt.CommitCount);
        Assert.False(retry.IsPending);
        Assert.False(retry.TryBeginFrame());
        retry.Complete();
        retry.Complete();
        Assert.False(retry.IsPending);
        Assert.False(retry.TryBeginFrame());

        retry.Request();
        Assert.True(retry.TryBeginFrame());
        var awaitLifecycle = new RefreshOperation(
            selection,
            viewport,
            target,
            SelectionColor,
            retryOnce: false,
            succeeds: false);
        Assert.Equal(
            SelectionAdornerRefreshResult.AwaitLifecycle,
            SelectionAdornerRefreshCoordinator.TryRefresh(snapshot, ref awaitLifecycle));
        Assert.Equal(1, awaitLifecycle.CallCount);
        Assert.Equal(1, awaitLifecycle.GeometryCount);
        Assert.Equal(0, awaitLifecycle.CommitCount);
        retry.Cancel();
        retry.Cancel();
        retry.Complete();
        retry.Complete();
        Assert.False(retry.IsPending);
        Assert.False(retry.TryBeginFrame());
        Assert.True(snapshot.TryBeginPaint());
        snapshot.EndPaint();
    }

    [Fact]
    public async Task OperationExceptionReleasesPaintLeaseForAnotherThread()
    {
        var block = new FixedSelectionBox(new Rect(4, 8, 30, 12)) { BlockIndex = 1 };
        block.Measure(80);
        block.Arrange(4, 8, 30);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { block },
            new MarkdownSourceMap("throwing selection operation"),
            width: 80,
            height: 60);
        var expected = new InvalidOperationException("Injected adornment operation failure.");
        var operation = new ThrowingOperation(expected);

        Assert.Same(expected, Record.Exception(() =>
            SelectionAdornerRefreshCoordinator.TryRefresh(snapshot, ref operation)));
        Assert.Equal(1, operation.CallCount);

        // Monitor locks are reentrant on the calling thread, so prove the
        // coordinator's finally released the lease using a different thread.
        bool anotherThreadAcquiredLease = await Task.Run(() =>
        {
            if (!snapshot.TryBeginPaint())
                return false;

            snapshot.EndPaint();
            return true;
        }).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(anotherThreadAcquiredLease);
    }

    [Fact]
    public async Task DisposedRetiredAndGeometryTaintedSnapshotsRejectRefreshWithoutChangingWarpFrame()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        using var target = new CanvasRenderTarget(device, 120, 80, 96);
        PaintWholeTarget(target, PriorFrameColor);
        Color[] priorPixels = target.GetPixelColors();

        using (LayoutSnapshot disposed = CreateReadySnapshot("disposed"))
        {
            disposed.Dispose();
            AssertRefreshRejectedWithoutDrawing(disposed, target, priorPixels);
        }

        using (LayoutSnapshot retired = CreateReadySnapshot("retired"))
        {
            await retired.Retire().WaitAsync(TimeSpan.FromSeconds(2));
            AssertRefreshRejectedWithoutDrawing(retired, target, priorPixels);
        }

        var failing = new FailingMeasureSelectionBox { BlockIndex = 1 };
        using (var tainted = new LayoutSnapshot(
                   new BlockBox[] { failing },
                   new MarkdownSourceMap("tainted"),
                   width: 120,
                   height: 80))
        {
            tainted.EnableLazyLayout(
                availableWidth: 120,
                viewportTop: 10_000,
                viewportHeight: 24,
                overscan: 0,
                CancellationToken.None);

            Assert.Throws<InvalidOperationException>(() =>
                tainted.EnsureMeasuredBand(new LazyLayoutBand(0, 24), CancellationToken.None));
            Assert.True(tainted.GeometryRecoveryRequired);
            AssertRefreshRejectedWithoutDrawing(tainted, target, priorPixels);
        }
    }

    [Fact]
    public void LazyInputReadinessRequiresAnInteractionLeaseAndNeverMeasuresAsAQuery()
    {
        var first = new InputReadinessSelectionBox(new Rect(0, 0, 120, 24)) { BlockIndex = 1 };
        var second = new InputReadinessSelectionBox(new Rect(0, 36, 120, 24)) { BlockIndex = 2 };
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { first, second },
            new MarkdownSourceMap("lazy input geometry"),
            width: 120,
            height: 80);
        snapshot.EnableLazyLayout(
            availableWidth: 120,
            viewportTop: 10_000,
            viewportHeight: 32,
            overscan: 0,
            CancellationToken.None);

        // Keep the requested band inside the first root even after its measured
        // height replaces the conservative estimate; otherwise suffix reflow can
        // legitimately bring the second root into a 0..24-DIP request.
        var firstBand = new LazyLayoutBand(0, 12);
        var bothBand = new LazyLayoutBand(0, 60);

        // A readiness query outside its required read lease fails closed, and
        // asking about geometry never calls Measure or publishes a partial
        // root as hit-testable.
        Assert.False(snapshot.IsInputHitTestBandMeasuredUnderInteraction(firstBand));
        Assert.Equal(0, first.MeasureCount);
        Assert.Equal(0, second.MeasureCount);

        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.False(snapshot.IsInputHitTestBandMeasuredUnderInteraction(firstBand));
            Assert.False(snapshot.IsInputHitTestBandMeasuredUnderInteraction(bothBand));
            Assert.Equal(0, first.MeasureCount);
            Assert.Equal(0, second.MeasureCount);
        }
        finally
        {
            snapshot.EndInteraction();
        }

        snapshot.EnsureMeasuredBand(firstBand, CancellationToken.None);
        Assert.Equal(1, first.MeasureCount);
        Assert.Equal(0, second.MeasureCount);
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.True(snapshot.IsInputHitTestBandMeasuredUnderInteraction(firstBand));
            Assert.False(snapshot.IsInputHitTestBandMeasuredUnderInteraction(bothBand));
        }
        finally
        {
            snapshot.EndInteraction();
        }

        snapshot.EnsureMeasuredBand(bothBand, CancellationToken.None);
        Assert.Equal(1, first.MeasureCount);
        Assert.Equal(1, second.MeasureCount);
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.True(snapshot.IsInputHitTestBandMeasuredUnderInteraction(firstBand));
            Assert.True(snapshot.IsInputHitTestBandMeasuredUnderInteraction(bothBand));
            Assert.False(snapshot.IsInputHitTestBandMeasuredUnderInteraction(new LazyLayoutBand(double.NaN, 20)));
            Assert.False(snapshot.IsInputHitTestBandMeasuredUnderInteraction(new LazyLayoutBand(20, 19)));
        }
        finally
        {
            snapshot.EndInteraction();
        }

        // A committed geometry failure is not reclassified as an ordinary
        // unmeasured band eligible for deferred input replay.
        var failing = new FailingMeasureSelectionBox { BlockIndex = 3 };
        using var tainted = new LayoutSnapshot(
            new BlockBox[] { failing },
            new MarkdownSourceMap("tainted input geometry"),
            width: 120,
            height: 60);
        tainted.EnableLazyLayout(
            availableWidth: 120,
            viewportTop: 10_000,
            viewportHeight: 24,
            overscan: 0,
            CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() =>
            tainted.EnsureMeasuredBand(new LazyLayoutBand(0, 24), CancellationToken.None));
        Assert.True(tainted.GeometryRecoveryRequired);
        Assert.False(tainted.TryBeginInteraction());
    }

    [Fact]
    public async Task LazyInputReadinessDoesNotWaitForOrInspectGeometryDuringACompetingCommit()
    {
        var block = new BlockingSelectionBox(new Rect(0, 0, 120, 24)) { BlockIndex = 1 };
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { block },
            new MarkdownSourceMap("busy lazy input geometry"),
            width: 120,
            height: 80);
        snapshot.EnableLazyLayout(
            availableWidth: 120,
            viewportTop: 10_000,
            viewportHeight: 24,
            overscan: 0,
            CancellationToken.None);

        var requested = new LazyLayoutBand(0, 24);
        Task measurement = Task.Run(() =>
            snapshot.EnsureMeasuredBand(requested, CancellationToken.None));
        Assert.True(block.MeasureEntered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            // The competing layout writer owns the snapshot gate. Input must
            // decline promptly instead of waiting on a geometry query that
            // could stall the event/UI thread.
            Assert.False(snapshot.TryBeginInteraction());
            Assert.False(snapshot.IsInputHitTestBandMeasuredUnderInteraction(requested));
        }
        finally
        {
            block.ReleaseMeasure.Set();
        }

        await measurement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.True(snapshot.IsInputHitTestBandMeasuredUnderInteraction(requested));
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    [Fact]
    public async Task DeferredGestureWaiterDoesNotOwnTheSnapshotNeededByItsRealization()
    {
        var block = new BlockingSelectionBox(new Rect(0, 0, 120, 24)) { BlockIndex = 1 };
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { block },
            new MarkdownSourceMap("a deferred gesture must not block layout"),
            width: 120,
            height: 80);
        snapshot.EnableLazyLayout(
            availableWidth: 120,
            viewportTop: 10_000,
            viewportHeight: 24,
            overscan: 0,
            CancellationToken.None);

        var waiters = new DeferredInputQueue<int>();
        Assert.True(waiters.TryEnqueue(73, pointerId: 0, coalescible: false,
            blocksSnapshotInteraction: false));
        Assert.Equal(0, waiters.BlockingSnapshotInteractionCount);
        Assert.False(waiters.HasBlockingSnapshotInteractions);

        Task measurement = Task.Run(() =>
            snapshot.EnsureMeasuredBand(new LazyLayoutBand(0, 24), CancellationToken.None));
        Assert.True(block.MeasureEntered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            // The input waiter remains queued, but does not make a lazy commit
            // fail its snapshot-admission gate or hold a read lease open.
            Assert.True(waiters.TryPeek(out int pending, out bool blocksSnapshotInteraction));
            Assert.Equal(73, pending);
            Assert.False(blocksSnapshotInteraction);
            Assert.False(snapshot.TryBeginInteraction());
        }
        finally
        {
            block.ReleaseMeasure.Set();
        }

        await measurement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            Assert.True(snapshot.IsInputHitTestBandMeasuredUnderInteraction(new LazyLayoutBand(0, 24)));
        }
        finally
        {
            snapshot.EndInteraction();
        }
        Assert.True(waiters.TryDequeue(out int replayable));
        Assert.Equal(73, replayable);
        Assert.Equal(0, waiters.BlockingSnapshotInteractionCount);
        Assert.False(waiters.TryPeek(out _, out _));
    }

    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.0)]
    [InlineData(false, 0.125)]
    public void GestureAdmissionBandIsFiniteClampedAndIncludesOnlyBoundedContactProbes(
        bool includeContact,
        double rasterizationScale)
    {
        Point gesture = new(24.5, 100.25);
        var captured = new TouchContactGeometry(18.5, 80.25, 12, 40);
        Assert.True(TryCreateGestureHitTestBand(
            gesture,
            rasterizationScale,
            includeContact,
            captured,
            out LazyLayoutBand band));

        double slop = global::System.Math.Max(4, 1 / rasterizationScale);
        Assert.Equal(includeContact ? 76.25 : gesture.Y - slop, band.Top);
        Assert.Equal(includeContact ? 124.25 : gesture.Y + slop, band.Bottom);

        // Bad contacts are ignored rather than allowed to turn one gesture
        // into an unbounded realization request. The original finite gesture
        // remains a valid readiness band.
        TouchContactGeometry[] malformedContacts =
        [
            new TouchContactGeometry(20, 90, 0, 20),
            new TouchContactGeometry(20, 90, -1, 20),
            new TouchContactGeometry(20, 90, double.NaN, 20),
            new TouchContactGeometry(20, 90, 257, 20),
            new TouchContactGeometry(20, 90, 10, 257),
            new TouchContactGeometry(20, 400, 10, 10),
        ];
        foreach (TouchContactGeometry contact in malformedContacts)
        {
            Assert.True(TryCreateGestureHitTestBand(
                gesture,
                rasterizationScale,
                includeContact: true,
                capturedContact: contact,
                out LazyLayoutBand gestureOnly));
            Assert.Equal(gesture.Y - slop, gestureOnly.Top);
            Assert.Equal(gesture.Y + slop, gestureOnly.Bottom);
        }

        // The contact admission boundary is inclusive at 256 DIPs in both
        // dimensions and distance, but one fractional DIP beyond it is
        // ignored. This avoids an accidental document-scale realization band.
        Assert.True(TryCreateGestureHitTestBand(
            new Point(0, 100),
            rasterizationScale: 1,
            includeContact: true,
            capturedContact: new TouchContactGeometry(-128, -28, 256, 256),
            out LazyLayoutBand maximumContact));
        Assert.Equal(0, maximumContact.Top);
        Assert.Equal(232, maximumContact.Bottom);

        Assert.True(TryCreateGestureHitTestBand(
            new Point(0, 100),
            rasterizationScale: 1,
            includeContact: true,
            capturedContact: new TouchContactGeometry(250, 80, 12, 40),
            out LazyLayoutBand boundaryContact));
        Assert.Equal(76, boundaryContact.Top);
        Assert.Equal(124, boundaryContact.Bottom);
        Assert.True(TryCreateGestureHitTestBand(
            new Point(0, 100),
            rasterizationScale: 1,
            includeContact: true,
            capturedContact: new TouchContactGeometry(250.5, 80, 12, 40),
            out LazyLayoutBand beyondBoundary));
        Assert.Equal(96, beyondBoundary.Top);
        Assert.Equal(104, beyondBoundary.Bottom);

        Assert.False(TryCreateGestureHitTestBand(
            new Point(double.NaN, 100),
            rasterizationScale,
            includeContact: true,
            capturedContact: captured,
            out _));
        Assert.False(TryCreateGestureHitTestBand(
            new Point(24, double.PositiveInfinity),
            rasterizationScale,
            includeContact: false,
            capturedContact: default,
            out _));

        Assert.True(TryCreateGestureHitTestBand(
            new Point(24, 2),
            1,
            includeContact: false,
            capturedContact: default,
            out LazyLayoutBand clampedTop));
        Assert.Equal(0, clampedTop.Top);
        Assert.Equal(6, clampedTop.Bottom);

        Assert.True(TryCreateGestureHitTestBand(
            new Point(24, 100),
            rasterizationScale: 0,
            includeContact: false,
            capturedContact: default,
            out LazyLayoutBand invalidScaleFallback));
        Assert.Equal(96, invalidScaleFallback.Top);
        Assert.Equal(104, invalidScaleFallback.Bottom);
        Assert.True(TryCreateGestureHitTestBand(
            new Point(24, 100),
            rasterizationScale: double.NaN,
            includeContact: false,
            capturedContact: default,
            out LazyLayoutBand nonFiniteScaleFallback));
        Assert.Equal(invalidScaleFallback, nonFiniteScaleFallback);
        Assert.True(TryCreateGestureHitTestBand(
            new Point(24, -20),
            rasterizationScale: 1,
            includeContact: false,
            capturedContact: default,
            out LazyLayoutBand entirelyAboveDocument));
        Assert.Equal(new LazyLayoutBand(0, 0), entirelyAboveDocument);
        Assert.True(TryCreateGestureHitTestBand(
            new Point(24, 100),
            rasterizationScale: 0.001,
            includeContact: false,
            capturedContact: default,
            out LazyLayoutBand cappedScaleSlop));
        Assert.Equal(0, cappedScaleSlop.Top);
        Assert.Equal(356, cappedScaleSlop.Bottom);
    }

    private static void PaintWholeTarget(CanvasRenderTarget target, Color color)
    {
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        drawing.Clear(color);
    }

    private static bool TryCreateGestureHitTestBand(
        Point gestureCanvas,
        double rasterizationScale,
        bool includeContact,
        TouchContactGeometry capturedContact,
        out LazyLayoutBand band) => MarkdownRendererControl.TryCreateGestureHitTestBand(
            gestureCanvas,
            rasterizationScale,
            includeContact,
            capturedContact,
            out band);

    private struct RefreshOperation(
        SelectionController selection,
        Rect viewport,
        CanvasRenderTarget target,
        Color color,
        bool retryOnce,
        bool succeeds) : ISelectionAdornerRefreshOperation
    {
        private readonly SelectionController _selection = selection;
        private readonly Rect _viewport = viewport;
        private readonly CanvasRenderTarget _target = target;
        private readonly Color _color = color;
        private bool _retryOnce = retryOnce;
        private readonly bool _succeeds = succeeds;

        internal int CallCount { get; private set; }
        internal int GeometryCount { get; private set; }
        internal int CommitCount { get; private set; }
        internal bool ObservedReadLease { get; private set; }

        public SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot)
        {
            CallCount++;
            ObservedReadLease = snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                _viewport.Top,
                _viewport.Bottom,
                out _);
            Assert.True(ObservedReadLease);

            var staged = new List<Rect>();
            _selection.AppendVisibleHighlightRects(snapshot, _viewport, staged);
            GeometryCount++;
            if (_retryOnce)
            {
                _retryOnce = false;
                return SelectionAdornerRefreshOperationResult.Retry;
            }

            if (!_succeeds)
                return SelectionAdornerRefreshOperationResult.AwaitLifecycle;

            using CanvasDrawingSession drawing = _target.CreateDrawingSession();
            foreach (Rect rectangle in staged)
                drawing.FillRectangle(rectangle, _color);
            CommitCount++;
            return SelectionAdornerRefreshOperationResult.Completed;
        }
    }

    private struct ThrowingOperation(Exception failure) : ISelectionAdornerRefreshOperation
    {
        private readonly Exception _failure = failure;

        internal int CallCount { get; private set; }

        public SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot)
        {
            CallCount++;
            throw _failure;
        }
    }

    private static LayoutSnapshot CreateReadySnapshot(string source)
    {
        var block = new FixedSelectionBox(new Rect(4, 8, 30, 12)) { BlockIndex = 1 };
        block.Measure(120);
        block.Arrange(4, 8, 120);
        return new LayoutSnapshot(
            new BlockBox[] { block },
            new MarkdownSourceMap(source),
            width: 120,
            height: 80);
    }

    private static void AssertRefreshRejectedWithoutDrawing(
        LayoutSnapshot snapshot,
        CanvasRenderTarget target,
        Color[] priorPixels)
    {
        var operation = new PaintWholeTargetOperation(target, SelectionColor);
        Assert.Equal(
            SelectionAdornerRefreshResult.Busy,
            SelectionAdornerRefreshCoordinator.TryRefresh(snapshot, ref operation));
        Assert.Equal(0, operation.CallCount);
        Assert.Equal(priorPixels, target.GetPixelColors());
    }

    private struct PaintWholeTargetOperation(CanvasRenderTarget target, Color color)
        : ISelectionAdornerRefreshOperation
    {
        private readonly CanvasRenderTarget _target = target;
        private readonly Color _color = color;

        internal int CallCount { get; private set; }

        public SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(LayoutSnapshot snapshot)
        {
            CallCount++;
            using CanvasDrawingSession drawing = _target.CreateDrawingSession();
            drawing.Clear(_color);
            return SelectionAdornerRefreshOperationResult.Completed;
        }
    }

    private sealed class BlockingSelectionBox : BlockBox
    {
        internal BlockingSelectionBox() { }

        internal BlockingSelectionBox(Rect initialBounds)
        {
            Bounds = initialBounds;
        }

        internal ManualResetEventSlim MeasureEntered { get; } = new(false);
        internal ManualResetEventSlim ReleaseMeasure { get; } = new(false);

        public override float Measure(float availableWidth)
        {
            MeasureEntered.Set();
            if (!ReleaseMeasure.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Selection-adornment test did not release snapshot measurement.");
            Bounds = new Rect(Bounds.X, Bounds.Y, availableWidth, 24);
            return 24;
        }

        public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
        {
            DocumentRange normalized = range.Normalized();
            if (BlockIndex >= normalized.Start.BlockIndex && BlockIndex <= normalized.End.BlockIndex &&
                !normalized.IsEmpty)
            {
                yield return Bounds;
            }
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }

    private sealed class FixedSelectionBox(Rect bounds) : BlockBox
    {
        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(Bounds.X, Bounds.Y, availableWidth, bounds.Height);
            return (float)Bounds.Height;
        }

        public override void Arrange(float x, float y, float width)
        {
            Bounds = new Rect(x, y, width, bounds.Height);
            IsDirty = false;
        }

        public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
        {
            DocumentRange normalized = range.Normalized();
            if (BlockIndex >= normalized.Start.BlockIndex && BlockIndex <= normalized.End.BlockIndex &&
                !normalized.IsEmpty)
            {
                yield return Bounds;
            }
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }

    private sealed class InputReadinessSelectionBox : BlockBox
    {
        private readonly Rect _estimatedBounds;

        internal InputReadinessSelectionBox(Rect estimatedBounds)
        {
            _estimatedBounds = estimatedBounds;
            Bounds = estimatedBounds;
        }

        internal int MeasureCount { get; private set; }

        public override float Measure(float availableWidth)
        {
            MeasureCount++;
            Bounds = new Rect(Bounds.X, Bounds.Y, availableWidth, _estimatedBounds.Height);
            return (float)Bounds.Height;
        }

        public override void Arrange(float x, float y, float width)
        {
            Bounds = new Rect(x, y, width, _estimatedBounds.Height);
            IsDirty = false;
        }

        public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
        {
            DocumentRange normalized = range.Normalized();
            if (BlockIndex >= normalized.Start.BlockIndex && BlockIndex <= normalized.End.BlockIndex &&
                !normalized.IsEmpty)
            {
                yield return Bounds;
            }
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }

    private sealed class FailingMeasureSelectionBox : BlockBox
    {
        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, 20);
            throw new InvalidOperationException("Injected geometry failure.");
        }

        public override void Arrange(float x, float y, float width)
        {
            Bounds = new Rect(x, y, width, 20);
            IsDirty = false;
        }

        public override IEnumerable<Rect> GetSelectionRects(DocumentRange range)
            => Array.Empty<Rect>();

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }
}
