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

    [Fact]
    public void DeferredGestureReadinessAndQueueDispatchWiringPreserveNonblockingFifoOwnership()
    {
        string pointer = File.ReadAllText(FindControlSource("MarkdownRendererControl.PointerInput.cs"));
        string control = File.ReadAllText(FindControlSource("MarkdownRendererControl.cs"));

        string interactionGate = MethodBody(pointer, "private bool HasSnapshotPointerInteraction");
        Assert.Contains("HasBlockingSnapshotInteractions", interactionGate, StringComparison.Ordinal);
        Assert.DoesNotContain("_deferredPointerInput?.Count", interactionGate, StringComparison.Ordinal);

        string dispatch = MethodBody(pointer, "private bool DispatchRecognizedGesture(");
        Assert.Contains("TryGetGestureAdmissionBand", dispatch, StringComparison.Ordinal);
        Assert.Contains("EnqueueRecognizedGesture", dispatch, StringComparison.Ordinal);
        int acquireGestureLease = dispatch.IndexOf("snapshot.TryBeginInteraction()", StringComparison.Ordinal);
        int gestureReadiness = dispatch.IndexOf("TryGetGestureAdmissionBand", acquireGestureLease, StringComparison.Ordinal);
        int releaseGestureLease = dispatch.IndexOf("snapshot.EndInteraction();", gestureReadiness, StringComparison.Ordinal);
        int requestGeometry = dispatch.IndexOf("RequestDeferredGestureBandRealization", releaseGestureLease, StringComparison.Ordinal);
        Assert.True(acquireGestureLease >= 0 && gestureReadiness > acquireGestureLease);
        Assert.True(releaseGestureLease > gestureReadiness && requestGeometry > releaseGestureLease);
        int dispatchWaitForPublication = dispatch.IndexOf("if (input.WaitForLayoutPublication)", releaseGestureLease, StringComparison.Ordinal);
        int dispatchPublicationCheck = dispatch.IndexOf("IsDeferredGestureLayoutPublished(snapshot)", dispatchWaitForPublication, StringComparison.Ordinal);
        int dispatchPublicationReturn = dispatch.IndexOf("return true;", dispatchPublicationCheck, StringComparison.Ordinal);
        Assert.True(dispatchWaitForPublication > releaseGestureLease);
        Assert.True(dispatchPublicationCheck > dispatchWaitForPublication && dispatchPublicationReturn > dispatchPublicationCheck);
        Assert.DoesNotContain("RequestDeferredGestureBandRealization", dispatch[dispatchWaitForPublication..dispatchPublicationReturn], StringComparison.Ordinal);
        Assert.True(requestGeometry > dispatchPublicationReturn);
        Assert.DoesNotContain("EnsureMeasuredBand", dispatch, StringComparison.Ordinal);
        Assert.DoesNotContain(".Measure(", dispatch, StringComparison.Ordinal);

        string admission = MethodBody(pointer, "private bool TryGetGestureAdmissionBand(");
        int publicationGuard = admission.IndexOf(
            "_pendingLayoutCommit is not null || _appliedLazyLayoutRevision < snapshot.LayoutRevision",
            StringComparison.Ordinal);
        int reentrantPublicationGuard = admission.IndexOf("_publishingLayoutCommit ||", StringComparison.Ordinal);
        int markPublicationWait = admission.IndexOf("input.WaitForLayoutPublication = true;", publicationGuard, StringComparison.Ordinal);
        int deferBeforeMaskRead = admission.IndexOf("TryGetGestureHitTestBand(ref input", publicationGuard, StringComparison.Ordinal);
        Assert.True(reentrantPublicationGuard >= 0 && publicationGuard > reentrantPublicationGuard);
        Assert.True(markPublicationWait > publicationGuard);
        Assert.True(deferBeforeMaskRead > markPublicationWait);
        Assert.Contains("input.WaitForLayoutPublication = false;", admission, StringComparison.Ordinal);
        Assert.Contains("ready = false;", admission[markPublicationWait..deferBeforeMaskRead], StringComparison.Ordinal);

        int rawBand = admission.IndexOf("TryGetGestureHitTestBand(ref input, out LazyLayoutBand gestureBand)", StringComparison.Ordinal);
        int rawReadiness = admission.IndexOf("IsInputHitTestBandMeasuredUnderInteraction(gestureBand)", rawBand, StringComparison.Ordinal);
        int directPointFastPath = admission.IndexOf("IsSelectableTouchPoint(input.GesturePosition, snapshot, out _)", rawReadiness, StringComparison.Ordinal);
        int contactBand = admission.IndexOf("TryCreateGestureHitTestBand(", directPointFastPath, StringComparison.Ordinal);
        int contactReadiness = admission.IndexOf("IsInputHitTestBandMeasuredUnderInteraction(contactBand)", contactBand, StringComparison.Ordinal);
        Assert.True(rawBand >= 0 && rawReadiness > rawBand && directPointFastPath > rawReadiness);
        Assert.True(contactBand > directPointFastPath && contactReadiness > contactBand);
        Assert.Contains("if (!IncludesContactProbes(input)", admission, StringComparison.Ordinal);

        string bandFactory = MethodBody(pointer, "internal static bool TryCreateGestureHitTestBand(");
        Assert.Contains("Math.Min(256, Math.Max(4, 1 / rasterizationScale))", bandFactory, StringComparison.Ordinal);
        Assert.Contains("IsBoundedGestureContact", bandFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("Measure(", bandFactory, StringComparison.Ordinal);

        string enqueue = MethodBody(pointer, "private bool EnqueueRecognizedGesture(");
        Assert.Contains("TryEnqueue", enqueue, StringComparison.Ordinal);
        Assert.Contains("blocksSnapshotInteraction: false", enqueue, StringComparison.Ordinal);
        Assert.Contains("IsDeferredGeometryWait = true", enqueue, StringComparison.Ordinal);

        string realize = MethodBody(pointer, "private void RequestDeferredGestureBandRealization(");
        Assert.Contains("QueueLazyLayoutForBand(", realize, StringComparison.Ordinal);
        Assert.Contains("IsDeferredGestureBandCovered", realize, StringComparison.Ordinal);
        Assert.Contains("DropDeferredInputQueuePreservingSelection();", realize, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureMeasuredBand", realize, StringComparison.Ordinal);

        string drain = MethodBody(pointer, "private void OnPointerRetryTimer(");
        int peekBeforeLease = drain.IndexOf("_deferredPointerInput?.TryPeek(out _, out _)", StringComparison.Ordinal);
        int lease = drain.IndexOf("if (!snapshot.TryBeginInteraction())", peekBeforeLease, StringComparison.Ordinal);
        int peek = drain.IndexOf("while (_deferredPointerInput?.TryPeek", lease, StringComparison.Ordinal);
        int headReadiness = drain.IndexOf("TryGetGestureAdmissionBand", lease, StringComparison.Ordinal);
        int preserveHead = drain.IndexOf("TryReplaceHead(", headReadiness, StringComparison.Ordinal);
        int waitForGeometry = drain.IndexOf("waitingForGeometry = true", preserveHead, StringComparison.Ordinal);
        int dequeue = drain.IndexOf("_deferredPointerInput.TryDequeue(out _);", waitForGeometry, StringComparison.Ordinal);
        int endLease = drain.IndexOf("snapshot.EndInteraction();", waitForGeometry, StringComparison.Ordinal);
        int realization = drain.IndexOf("RequestDeferredGestureBandRealization", endLease, StringComparison.Ordinal);
        Assert.True(peekBeforeLease >= 0 && lease > peekBeforeLease && peek > lease && headReadiness > peek);
        Assert.True(preserveHead > headReadiness && waitForGeometry > preserveHead);
        Assert.True(dequeue > waitForGeometry && endLease > waitForGeometry && realization > endLease);
        Assert.Contains("blocksSnapshotInteraction: false", drain[preserveHead..waitForGeometry], StringComparison.Ordinal);

        string reproject = MethodBody(pointer, "private bool TryReprojectDeferredGesture(ref PointerInput input)");
        Assert.Contains("TryMapViewportToCanvas", reproject, StringComparison.Ordinal);
        Assert.Contains("TryMapViewportContactToCanvas", reproject, StringComparison.Ordinal);
        Assert.Contains("InvalidDeferredProjection = true", reproject, StringComparison.Ordinal);

        string completion = MethodBody(pointer, "private void OnDeferredGestureLayoutCompletion(");
        Assert.Contains("if (succeeded)", completion, StringComparison.Ordinal);
        Assert.Contains("DropDeferredInputQueuePreservingSelection();", completion, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_snapshot, snapshot)", completion, StringComparison.Ordinal);
        int publishedBeforeWake = completion.IndexOf("IsDeferredGestureLayoutPublished(snapshot)", StringComparison.Ordinal);
        int stopUntilPublication = completion.IndexOf("_pointerRetryTimer?.Stop();", publishedBeforeWake, StringComparison.Ordinal);
        int resumeAfterPublication = completion.IndexOf("_pointerRetryTimer.Start();", stopUntilPublication, StringComparison.Ordinal);
        Assert.True(publishedBeforeWake >= 0 && stopUntilPublication > publishedBeforeWake);
        Assert.True(resumeAfterPublication > stopUntilPublication);

        string publicationCheck = MethodBody(pointer, "private bool IsDeferredGestureLayoutPublished(LayoutSnapshot snapshot)");
        Assert.Contains("ReferenceEquals(snapshot, _snapshot)", publicationCheck, StringComparison.Ordinal);
        Assert.Contains("!_publishingLayoutCommit", publicationCheck, StringComparison.Ordinal);
        Assert.Contains("_pendingLayoutCommit is null", publicationCheck, StringComparison.Ordinal);
        Assert.Contains("_appliedLazyLayoutRevision >= snapshot.LayoutRevision", publicationCheck, StringComparison.Ordinal);

        int waitBranch = drain.IndexOf("if (input.WaitForLayoutPublication)", headReadiness, StringComparison.Ordinal);
        int waitHeadUpdate = drain.IndexOf("_deferredPointerInput.TryReplaceHead(", waitBranch, StringComparison.Ordinal);
        int waitMark = drain.IndexOf("waitingForLayoutPublication = true;", waitHeadUpdate, StringComparison.Ordinal);
        int geometryBranch = drain.IndexOf("if (hasBand && !bandReady)", waitMark, StringComparison.Ordinal);
        Assert.True(waitBranch > headReadiness && waitHeadUpdate > waitBranch);
        Assert.True(waitMark > waitHeadUpdate && geometryBranch > waitMark);
        Assert.Contains("blocksSnapshotInteraction: false", drain[waitHeadUpdate..geometryBranch], StringComparison.Ordinal);
        Assert.DoesNotContain("TryDequeue", drain[waitBranch..geometryBranch], StringComparison.Ordinal);

        int publicationDrain = drain.IndexOf("if (waitingForLayoutPublication)", geometryBranch, StringComparison.Ordinal);
        int stopPublicationTimer = drain.IndexOf("sender.Stop();", publicationDrain, StringComparison.Ordinal);
        int verifyPublishedBeforeTimerStart = drain.IndexOf("IsDeferredGestureLayoutPublished(snapshot)", stopPublicationTimer, StringComparison.Ordinal);
        int startOnlyWhenPublished = drain.IndexOf("sender.Start();", verifyPublishedBeforeTimerStart, StringComparison.Ordinal);
        int nextGeometryHandling = drain.IndexOf("if (waitingForGeometry)", startOnlyWhenPublished, StringComparison.Ordinal);
        Assert.True(publicationDrain > geometryBranch && stopPublicationTimer > publicationDrain);
        Assert.True(verifyPublishedBeforeTimerStart > stopPublicationTimer && startOnlyWhenPublished > verifyPublishedBeforeTimerStart);
        Assert.True(nextGeometryHandling > startOnlyWhenPublished);
        Assert.DoesNotContain("RequestDeferredGestureBandRealization", drain[publicationDrain..nextGeometryHandling], StringComparison.Ordinal);

        string applyCommit = MethodBody(control, "private void ApplyLazyLayoutCommit(");
        int commitScrollResume = applyCommit.IndexOf("TryResumePendingScrollToBlock(snapshot", StringComparison.Ordinal);
        int commitPublishedFence = applyCommit.IndexOf(
            "_pendingLayoutCommit is null &&",
            commitScrollResume,
            StringComparison.Ordinal);
        int commitReleaseWaiter = applyCommit.IndexOf("OnDeferredGestureLayoutCompletion(snapshot, succeeded: true);", commitPublishedFence, StringComparison.Ordinal);
        Assert.True(commitScrollResume >= 0 && commitPublishedFence > commitScrollResume);
        Assert.True(commitReleaseWaiter > commitPublishedFence);

        string enqueueIdentity = MethodBody(pointer, "private bool EnqueueRecognizedGesture(");
        Assert.Contains("!ReferenceEquals(snapshot, _deferredPointerSnapshot)", enqueueIdentity, StringComparison.Ordinal);
        Assert.Contains("ResetDeferredPointerInput();", enqueueIdentity, StringComparison.Ordinal);

        // Recognized gestures are allowed to await the layout they need, but a
        // real captured press/release remains a snapshot-interaction owner.
        string physicalDispatch = MethodBody(pointer, "private void DispatchPointerInput(");
        string inputOwnerClassification = ExtractAfter(physicalDispatch, "bool hasSnapshotPointerOwner =");
        Assert.Contains("input.CapturedBeforeDeferral", inputOwnerClassification, StringComparison.Ordinal);
        Assert.Contains("_pointerSession.PointerId == input.Pointer.PointerId", inputOwnerClassification, StringComparison.Ordinal);
        Assert.Contains("_horizontalOverflowPointerId == input.Pointer.PointerId", inputOwnerClassification, StringComparison.Ordinal);
        Assert.Contains("_selectionHandlePointerId == input.Pointer.PointerId", inputOwnerClassification, StringComparison.Ordinal);
        string blockingClassification = ExtractAfter(physicalDispatch, "bool blocksSnapshotInteraction =");
        Assert.Contains("PointerInputKind.Move or PointerInputKind.Release or PointerInputKind.Cancel or PointerInputKind.Exit",
            blockingClassification,
            StringComparison.Ordinal);
        Assert.Matches(@"\|\|\s*hasSnapshotPointerOwner\b", blockingClassification);
        Assert.Contains("blocksSnapshotInteraction: blocksSnapshotInteraction", physicalDispatch, StringComparison.Ordinal);

        int newCapturedOwner = physicalDispatch.IndexOf("bool startsNewCapturedOwnership =", StringComparison.Ordinal);
        int pressOrHandleStart = physicalDispatch.IndexOf("kind == PointerInputKind.HandlePress", newCapturedOwner, StringComparison.Ordinal);
        int inContactPress = physicalDispatch.IndexOf("kind == PointerInputKind.Press && input.Point.IsInContact", pressOrHandleStart, StringComparison.Ordinal);
        int touchPenEligibility = physicalDispatch.IndexOf("PointerDeviceType.Touch or PointerDeviceType.Pen", newCapturedOwner, StringComparison.Ordinal);
        int dropLogicalWaiter = physicalDispatch.IndexOf("if (startsNewCapturedOwnership &&", touchPenEligibility, StringComparison.Ordinal);
        int requireOnlyNonblockingWaiters = physicalDispatch.IndexOf("HasBlockingSnapshotInteractions: false", dropLogicalWaiter, StringComparison.Ordinal);
        int preserveSelectionOnCancel = physicalDispatch.IndexOf("DropDeferredInputQueuePreservingSelection();", requireOnlyNonblockingWaiters, StringComparison.Ordinal);
        int retryFreshPress = physicalDispatch.IndexOf("if (snapshot.TryBeginInteraction())", preserveSelectionOnCancel, StringComparison.Ordinal);
        Assert.True(newCapturedOwner >= 0 && pressOrHandleStart > newCapturedOwner);
        Assert.True(inContactPress > pressOrHandleStart && touchPenEligibility > inContactPress);
        Assert.True(dropLogicalWaiter > touchPenEligibility && requireOnlyNonblockingWaiters > dropLogicalWaiter);
        Assert.True(preserveSelectionOnCancel > requireOnlyNonblockingWaiters && retryFreshPress > preserveSelectionOnCancel);
        Assert.DoesNotContain("CancelDeferredGesture();", physicalDispatch[dropLogicalWaiter..retryFreshPress], StringComparison.Ordinal);
        Assert.Contains("if (HasSnapshotPointerInteraction)", control, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlFrameLifecycleRetainsReentrantRequestsAndStopsAwaitLifecycleRetries()
    {
        string control = File.ReadAllText(FindControlSource("MarkdownRendererControl.cs"));
        string attempt = MethodBody(control, "private void TrySelectionAdornerRefreshAttempt()");

        int reentrantGuard = attempt.IndexOf("if (_selectionAdornerRefreshInProgress)", StringComparison.Ordinal);
        int retainRequest = attempt.IndexOf("_selectionAdornerRetryState.Request();", reentrantGuard, StringComparison.Ordinal);
        int incrementVersion = attempt.IndexOf(
            "_selectionAdornerRequestVersion = unchecked(_selectionAdornerRequestVersion + 1);",
            retainRequest,
            StringComparison.Ordinal);
        int reschedule = attempt.IndexOf("ScheduleSelectionAdornerRetry();", incrementVersion, StringComparison.Ordinal);
        int reentrantReturn = attempt.IndexOf("return;", reschedule, StringComparison.Ordinal);
        int consumeFrame = attempt.IndexOf("_selectionAdornerRetryState.TryBeginFrame()", reentrantReturn, StringComparison.Ordinal);
        int markInProgress = attempt.IndexOf("_selectionAdornerRefreshInProgress = true;", consumeFrame, StringComparison.Ordinal);
        int coordinatorCall = attempt.IndexOf("SelectionAdornerRefreshCoordinator.TryRefresh", markInProgress, StringComparison.Ordinal);
        int resetInFinally = attempt.IndexOf("_selectionAdornerRefreshInProgress = false;", coordinatorCall, StringComparison.Ordinal);

        Assert.True(reentrantGuard >= 0 && retainRequest > reentrantGuard && incrementVersion > retainRequest);
        Assert.True(reschedule > incrementVersion && reentrantReturn > reschedule && consumeFrame > reentrantReturn);
        Assert.True(markInProgress > consumeFrame && coordinatorCall > markInProgress && resetInFinally > coordinatorCall);
        Assert.Contains("finally", attempt[coordinatorCall..resetInFinally], StringComparison.Ordinal);

        int awaitLifecycle = attempt.IndexOf(
            "if (result == SelectionAdornerRefreshResult.AwaitLifecycle)",
            coordinatorCall,
            StringComparison.Ordinal);
        int keepGeometryDirty = attempt.IndexOf("_selectionAdornerGeometryDirty = true;", awaitLifecycle, StringComparison.Ordinal);
        int keepVisualDirty = attempt.IndexOf("_selectionAdornerVisualDirty = true;", keepGeometryDirty, StringComparison.Ordinal);
        int versionChanged = attempt.IndexOf("if (requestVersion != _selectionAdornerRequestVersion)", keepVisualDirty, StringComparison.Ordinal);
        int retryForNewRequest = attempt.IndexOf("ScheduleSelectionAdornerRetry();", versionChanged, StringComparison.Ordinal);
        int cancelWithoutNewRequest = attempt.IndexOf("CancelSelectionAdornerRetry();", retryForNewRequest, StringComparison.Ordinal);

        Assert.True(awaitLifecycle > resetInFinally && keepGeometryDirty > awaitLifecycle && keepVisualDirty > keepGeometryDirty);
        Assert.True(versionChanged > keepVisualDirty && retryForNewRequest > versionChanged && cancelWithoutNewRequest > retryForNewRequest);

        string schedule = MethodBody(control, "private void ScheduleSelectionAdornerRetry()");
        int coalescingGuard = schedule.IndexOf("if (_selectionAdornerRetryFrameSubscribed)", StringComparison.Ordinal);
        int subscribe = schedule.IndexOf("CompositionTarget.Rendering += _selectionAdornerRetryFrameHandler;", coalescingGuard, StringComparison.Ordinal);
        Assert.True(coalescingGuard >= 0 && subscribe > coalescingGuard);

        string unsubscribe = MethodBody(control, "private void UnsubscribeSelectionAdornerRetryFrame()");
        int unsubscribeGuard = unsubscribe.IndexOf("if (!_selectionAdornerRetryFrameSubscribed)", StringComparison.Ordinal);
        int detach = unsubscribe.IndexOf("CompositionTarget.Rendering -= handler;", unsubscribeGuard, StringComparison.Ordinal);
        int clearSubscription = unsubscribe.IndexOf("_selectionAdornerRetryFrameSubscribed = false;", detach, StringComparison.Ordinal);
        Assert.True(unsubscribeGuard >= 0 && detach > unsubscribeGuard && clearSubscription > detach);
    }

    [Fact]
    public void ControlTracksTheCapturedPhysicalHandleAcrossLogicalEndpointCrossing()
    {
        // This source contract protects control wiring only. The test suite
        // does not synthesize a live WinUI pointer-capture/crossing gesture.
        string control = File.ReadAllText(FindControlSource("MarkdownRendererControl.cs"));
        Assert.Contains("private Button? _selectionHandleCaptureTarget;", control, StringComparison.Ordinal);

        string pressed = MethodBody(control, "private void ProcessSelectionHandlePointerPressed(ref PointerInput e)");
        int duplicateCaptureGuard = pressed.IndexOf("_selectionHandleCaptureTarget is not null", StringComparison.Ordinal);
        int capture = pressed.IndexOf("CapturePointerForInput(handle, e)", StringComparison.Ordinal);
        int confirmPointerIdentity = pressed.IndexOf("_touchSelection.ContactPointerId != e.Pointer.PointerId", capture, StringComparison.Ordinal);
        int publishPhysicalTarget = pressed.IndexOf("_selectionHandleCaptureTarget = handle;", capture, StringComparison.Ordinal);
        Assert.True(duplicateCaptureGuard >= 0 && capture > duplicateCaptureGuard && confirmPointerIdentity > capture);
        Assert.True(publishPhysicalTarget > confirmPointerIdentity);

        string isCaptured = MethodBody(control, "private bool IsSelectionHandleCaptured(SelectionHandleEndpoint endpoint)");
        Assert.Contains("endpointButton is not null", isCaptured, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(endpointButton, _selectionHandleCaptureTarget)", isCaptured, StringComparison.Ordinal);
        Assert.Contains("IsSelectionHandlePointerDragActive()", isCaptured, StringComparison.Ordinal);

        string pointerDrag = MethodBody(control, "private bool IsSelectionHandlePointerDragActive()");
        Assert.Contains("_selectionHandleCaptureTarget is not null", pointerDrag, StringComparison.Ordinal);
        Assert.Contains("_activeSelectionHandle != SelectionHandleEndpoint.None", pointerDrag, StringComparison.Ordinal);
        Assert.Contains("_selectionHandlePointerId != 0", pointerDrag, StringComparison.Ordinal);
        Assert.Contains("_touchSelection.ContactPointerId == _selectionHandlePointerId", pointerDrag, StringComparison.Ordinal);
        Assert.Contains("TouchSelectionState.DraggingStartHandle", pointerDrag, StringComparison.Ordinal);
        Assert.Contains("TouchSelectionState.DraggingEndHandle", pointerDrag, StringComparison.Ordinal);

        string crossing = MethodBody(control, "private void AdjustSelectionHandleAtTextOffset(");
        Assert.Contains("if (pointerDrag)", crossing, StringComparison.Ordinal);
        Assert.Contains("_activeSelectionHandle = adjustment.IsStartEndpoint", crossing, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectionHandleCaptureTarget =", crossing, StringComparison.Ordinal);

        string update = MethodBody(control, "private void UpdateSelectionHandlesUnderPaint(LayoutSnapshot snapshot, Rect viewport)");
        Assert.Contains("bool startCaptured = IsSelectionHandleCaptured(SelectionHandleEndpoint.Start);", update, StringComparison.Ordinal);
        Assert.Contains("bool endCaptured = IsSelectionHandleCaptured(SelectionHandleEndpoint.End);", update, StringComparison.Ordinal);
        Assert.Contains("SelectionHandleEndpoint freeLogicalEndpoint = pointerDragActive", update, StringComparison.Ordinal);
        Assert.Contains("? SelectionHandleEndpoint.End", update, StringComparison.Ordinal);
        Assert.Contains(": SelectionHandleEndpoint.Start", update, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(
            update,
            "pointerDragActive ? freeLogicalEndpoint : SelectionHandleEndpoint."));

        int startCapturedBranch = update.IndexOf("if (startCaptured)", StringComparison.Ordinal);
        int startPlacement = update.IndexOf("else if (startAvailable)", startCapturedBranch, StringComparison.Ordinal);
        int endCapturedBranch = update.IndexOf("if (endCaptured)", startPlacement, StringComparison.Ordinal);
        int endPlacement = update.IndexOf("else if (endAvailable)", endCapturedBranch, StringComparison.Ordinal);
        Assert.True(startCapturedBranch >= 0 && startPlacement > startCapturedBranch);
        Assert.True(endCapturedBranch > startPlacement && endPlacement > endCapturedBranch);
        Assert.Contains("_selectionStartHandleEndpointAvailable = true;", update[startCapturedBranch..startPlacement], StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceSelectionHandle", update[startCapturedBranch..startPlacement], StringComparison.Ordinal);
        Assert.Contains("_selectionEndHandleEndpointAvailable = true;", update[endCapturedBranch..endPlacement], StringComparison.Ordinal);
        Assert.DoesNotContain("PlaceSelectionHandle", update[endCapturedBranch..endPlacement], StringComparison.Ordinal);
    }

    [Fact]
    public void ControlReleaseAndCancellationClearPhysicalCaptureBeforeRestoringLogicalHandleRoles()
    {
        // These assertions cover source wiring, not actual routed-event or
        // device-level pointer capture behavior.
        string control = File.ReadAllText(FindControlSource("MarkdownRendererControl.cs"));
        string released = MethodBody(control, "private void ProcessSelectionHandlePointerReleased(ref PointerInput e)");
        int finalMove = released.IndexOf("ApplyPendingSelectionHandleMove(sustained: false);", StringComparison.Ordinal);
        int clearTarget = released.IndexOf("_selectionHandleCaptureTarget = null;", finalMove, StringComparison.Ordinal);
        int completeDrag = released.IndexOf("_touchSelection.CompleteHandleDrag(e.Pointer.PointerId);", clearTarget, StringComparison.Ordinal);
        int clearLogicalRole = released.IndexOf("_activeSelectionHandle = SelectionHandleEndpoint.None;", completeDrag, StringComparison.Ordinal);
        int clearPointer = released.IndexOf("_selectionHandlePointerId = 0;", clearLogicalRole, StringComparison.Ordinal);
        int collapse = released.IndexOf("CollapseSelectionHandlesForPendingRefresh();", clearPointer, StringComparison.Ordinal);
        int releaseTarget = released.IndexOf("element.ReleasePointerCapture(e.Pointer);", clearPointer, StringComparison.Ordinal);
        int refreshBothRoles = released.IndexOf("UpdateSelectionHandles();", clearPointer, StringComparison.Ordinal);
        Assert.True(finalMove >= 0 && clearTarget > finalMove && completeDrag > clearTarget);
        Assert.True(clearLogicalRole > completeDrag && clearPointer > clearLogicalRole && collapse > clearPointer);
        Assert.True(releaseTarget > collapse && refreshBothRoles > releaseTarget);

        string createHandle = MethodBody(control, "private Button CreateSelectionHandle(SelectionHandleEndpoint endpoint)");
        Assert.Contains("Tag = endpoint,", createHandle, StringComparison.Ordinal);

        string endpointLookup = MethodBody(control, "private bool TryGetSelectionHandleEndpointPoint(");
        Assert.Contains("DocumentRange normalized = _selection.Range.Normalized();", endpointLookup, StringComparison.Ordinal);
        Assert.Contains("bool rangeStart = endpoint == SelectionHandleEndpoint.Start;", endpointLookup, StringComparison.Ordinal);
        Assert.Contains("DocumentPosition position = rangeStart ? normalized.Start : normalized.End;", endpointLookup, StringComparison.Ordinal);

        string canceled = MethodBody(control, "private void ProcessSelectionHandlePointerCanceled(ref PointerInput e)");
        int cancelReset = canceled.IndexOf("ResetSelectionHandleDrag();", StringComparison.Ordinal);
        int cancelSession = canceled.IndexOf("_touchSelection.Cancel(e.Pointer.PointerId);", cancelReset, StringComparison.Ordinal);
        int cancelRefresh = canceled.IndexOf("UpdateSelectionHandles();", cancelSession, StringComparison.Ordinal);
        Assert.True(cancelReset >= 0 && cancelSession > cancelReset && cancelRefresh > cancelSession);

        string reset = MethodBody(control, "private void ResetSelectionHandleDrag()");
        int nullTarget = reset.IndexOf("_selectionHandleCaptureTarget = null;", StringComparison.Ordinal);
        int noneRole = reset.IndexOf("_activeSelectionHandle = SelectionHandleEndpoint.None;", nullTarget, StringComparison.Ordinal);
        int clearPointerId = reset.IndexOf("_selectionHandlePointerId = 0;", noneRole, StringComparison.Ordinal);
        int collapseForCaptured = reset.IndexOf("CollapseSelectionHandlesForPendingRefresh();", clearPointerId, StringComparison.Ordinal);
        int releaseCapture = reset.IndexOf("ReleasePointerCaptures()", collapseForCaptured, StringComparison.Ordinal);
        Assert.True(nullTarget >= 0 && noneRole > nullTarget && clearPointerId > noneRole);
        Assert.True(collapseForCaptured > clearPointerId && releaseCapture > collapseForCaptured);
        Assert.Contains("if (hadCapturedButton)", reset, StringComparison.Ordinal);
        Assert.Contains("RequestSelectionAdornerRefresh();", reset, StringComparison.Ordinal);

        string update = MethodBody(control, "private void UpdateSelectionHandlesUnderPaint(LayoutSnapshot snapshot, Rect viewport)");
        int pointerDragActive = update.IndexOf("bool pointerDragActive = IsSelectionHandlePointerDragActive();", StringComparison.Ordinal);
        int normalStart = update.IndexOf("pointerDragActive ? freeLogicalEndpoint : SelectionHandleEndpoint.Start", pointerDragActive, StringComparison.Ordinal);
        int normalEnd = update.IndexOf("pointerDragActive ? freeLogicalEndpoint : SelectionHandleEndpoint.End", normalStart, StringComparison.Ordinal);
        Assert.True(pointerDragActive >= 0 && normalStart > pointerDragActive && normalEnd > normalStart);
    }

    private static void PaintWholeTarget(CanvasRenderTarget target, Color color)
    {
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        drawing.Clear(color);
    }

    private static string FindControlSource(string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string direct = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer",
                "Controls",
                fileName);
            if (File.Exists(direct))
                return direct;

            string nested = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "Controls",
                fileName);
            if (File.Exists(nested))
                return nested;
        }

        throw new FileNotFoundException($"Could not locate {fileName} from the test base directory.");
    }

#pragma warning disable MR1001 // Exercise the shared control's private hit-test helper without widening its consumer API.
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
#pragma warning restore MR1001

    private static string MethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find method '{signature}'.");
        int expressionArrow = source.IndexOf("=>", member + signature.Length, StringComparison.Ordinal);
        int openingBrace = source.IndexOf('{', member + signature.Length);
        if (expressionArrow >= 0 && (openingBrace < 0 || expressionArrow < openingBrace))
        {
            int semicolon = source.IndexOf(';', expressionArrow);
            Assert.True(semicolon > expressionArrow, $"Could not find the expression terminator for method '{signature}'.");
            return source[expressionArrow..(semicolon + 1)];
        }

        Assert.True(openingBrace >= 0, $"Could not find the body for method '{signature}'.");

        int depth = 0;
        for (int index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[openingBrace..(index + 1)];
        }

        throw new InvalidOperationException($"The source method body for '{signature}' is not balanced.");
    }

    private static string ExtractAfter(string source, string start)
    {
        int index = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Could not find source fragment '{start}'.");
        return source[index..];
    }

    private static int CountOccurrences(string value, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

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
