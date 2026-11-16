using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Internal.Input.Deferred;
using MarkdownRenderer.Controls.Internal.Input.Gestures;
using MarkdownRenderer.Controls.Internal.Selection.Adorner;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Controls.Internal.Selection.Adorner;

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
        SelectionAdornerTestSnapshots.PaintWholeTarget(target, PriorFrameColor);

        var block = new SelectionAdornerBlockingBox { BlockIndex = 1 };
        using LayoutSnapshot snapshot = SelectionAdornerTestSnapshots.CreateLazySnapshot(
            block, "busy selection", width: 120, height: 100, viewportHeight: 32);

        SelectionController selection = SelectionAdornerTestSnapshots.CreateSelection();
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
            var operation = new SelectionAdornerRecordingOperation(
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
        var admittedOperation = new SelectionAdornerRecordingOperation(
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
        var block = new SelectionAdornerFixedBox(new Rect(4, 8, 30, 12)) { BlockIndex = 1 };
        block.Measure(80);
        block.Arrange(4, 8, 30);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { block },
            new MarkdownSourceMap("retry"),
            width: 80,
            height: 60);
        SelectionController selection = SelectionAdornerTestSnapshots.CreateSelection();
        var viewport = new Rect(0, 0, 80, 60);
        var retry = new SelectionAdornerRetryState();

        retry.Request();
        retry.Request();
        retry.Request();
        Assert.True(retry.IsPending);
        Assert.True(retry.TryBeginFrame());
        Assert.False(retry.TryBeginFrame());

        var firstAttempt = new SelectionAdornerRecordingOperation(
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
        var nextAttempt = new SelectionAdornerRecordingOperation(
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

        AssertAwaitLifecycleAttemptIsCancelledWithoutRetry(snapshot, selection, viewport, target, retry);
        Assert.True(snapshot.TryBeginPaint());
        snapshot.EndPaint();
    }

    private static void AssertAwaitLifecycleAttemptIsCancelledWithoutRetry(
        LayoutSnapshot snapshot,
        SelectionController selection,
        Rect viewport,
        CanvasRenderTarget target,
        SelectionAdornerRetryState retry)
    {
        retry.Request();
        Assert.True(retry.TryBeginFrame());
        var awaitLifecycle = new SelectionAdornerRecordingOperation(
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
    }

    [Fact]
    public async Task OperationExceptionReleasesPaintLeaseForAnotherThread()
    {
        var block = new SelectionAdornerFixedBox(new Rect(4, 8, 30, 12)) { BlockIndex = 1 };
        block.Measure(80);
        block.Arrange(4, 8, 30);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { block },
            new MarkdownSourceMap("throwing selection operation"),
            width: 80,
            height: 60);
        var expected = new InvalidOperationException("Injected adornment operation failure.");
        var operation = new SelectionAdornerThrowingOperation(expected);

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
        SelectionAdornerTestSnapshots.PaintWholeTarget(target, PriorFrameColor);
        Color[] priorPixels = target.GetPixelColors();

        using (LayoutSnapshot disposed = SelectionAdornerTestSnapshots.CreateReadySnapshot("disposed"))
        {
            disposed.Dispose();
            AssertRefreshRejectedWithoutDrawing(disposed, target, priorPixels);
        }

        using (LayoutSnapshot retired = SelectionAdornerTestSnapshots.CreateReadySnapshot("retired"))
        {
            await retired.Retire().WaitAsync(TimeSpan.FromSeconds(2));
            AssertRefreshRejectedWithoutDrawing(retired, target, priorPixels);
        }

        var failing = new SelectionAdornerFailingMeasureBox { BlockIndex = 1 };
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
        var first = new SelectionAdornerReadinessBox(new Rect(0, 0, 120, 24)) { BlockIndex = 1 };
        var second = new SelectionAdornerReadinessBox(new Rect(0, 36, 120, 24)) { BlockIndex = 2 };
        using LayoutSnapshot snapshot = SelectionAdornerTestSnapshots.CreateLazySnapshot(
            new BlockBox[] { first, second }, "lazy input geometry", width: 120, height: 80, viewportHeight: 32);

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
        AssertBothBandsReadyAndMalformedBandsRejected(snapshot, firstBand, bothBand);

        AssertTaintedGeometryIsNotReplayableInput();
    }

    private static void AssertBothBandsReadyAndMalformedBandsRejected(
        LayoutSnapshot snapshot,
        LazyLayoutBand firstBand,
        LazyLayoutBand bothBand)
    {
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
    }

    private static void AssertTaintedGeometryIsNotReplayableInput()
    {
        // A committed geometry failure is not reclassified as an ordinary
        // unmeasured band eligible for deferred input replay.
        var failing = new SelectionAdornerFailingMeasureBox { BlockIndex = 3 };
        using LayoutSnapshot tainted = SelectionAdornerTestSnapshots.CreateLazySnapshot(
            failing, "tainted input geometry", width: 120, height: 60, viewportHeight: 24);
        Assert.Throws<InvalidOperationException>(() =>
            tainted.EnsureMeasuredBand(new LazyLayoutBand(0, 24), CancellationToken.None));
        Assert.True(tainted.GeometryRecoveryRequired);
        Assert.False(tainted.TryBeginInteraction());
    }

    [Fact]
    public async Task LazyInputReadinessDoesNotWaitForOrInspectGeometryDuringACompetingCommit()
    {
        var block = new SelectionAdornerBlockingBox(new Rect(0, 0, 120, 24)) { BlockIndex = 1 };
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
        var block = new SelectionAdornerBlockingBox(new Rect(0, 0, 120, 24)) { BlockIndex = 1 };
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
        Assert.True(GestureHitTestBandAssertions.TryCreateGestureHitTestBand(
            gesture,
            rasterizationScale,
            includeContact,
            captured,
            out LazyLayoutBand band));

        double slop = global::System.Math.Max(4, 1 / rasterizationScale);
        Assert.Equal(includeContact ? 76.25 : gesture.Y - slop, band.Top);
        Assert.Equal(includeContact ? 124.25 : gesture.Y + slop, band.Bottom);

        GestureHitTestBandAssertions.AssertMalformedContactsFallBackToTheGestureBand(gesture, rasterizationScale, slop);
        GestureHitTestBandAssertions.AssertContactAdmissionBoundaryIsInclusiveAt256Dips();
        GestureHitTestBandAssertions.AssertNonFiniteGesturePointsAreRejected(rasterizationScale, captured);
        GestureHitTestBandAssertions.AssertBandsClampToTheDocumentAndFallBackFromInvalidScales();
    }

    private static void AssertRefreshRejectedWithoutDrawing(
        LayoutSnapshot snapshot,
        CanvasRenderTarget target,
        Color[] priorPixels)
    {
        var operation = new SelectionAdornerPaintWholeTargetOperation(target, SelectionColor);
        Assert.Equal(
            SelectionAdornerRefreshResult.Busy,
            SelectionAdornerRefreshCoordinator.TryRefresh(snapshot, ref operation));
        Assert.Equal(0, operation.CallCount);
        Assert.Equal(priorPixels, target.GetPixelColors());
    }
}
