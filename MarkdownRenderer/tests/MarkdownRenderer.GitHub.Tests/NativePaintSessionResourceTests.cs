using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class NativePaintSessionResourceTests
{
    [Fact]
    public void NestedLayersCloseInOrderAndReuseTheirPrivateReservations()
    {
        using var target = CreateTarget();
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposal = new DisposalProbe();
        NativePaintSessionResources.RegisterTestOwner(drawing, retirement, disposal.Dispose);

        NativePaintSessionResources.LayerLease outer = NativePaintSessionResources.CreateLayer(
            drawing, 1f, new Rect(0, 0, 96, 96));
        NativePaintSessionResources.LayerLease inner = NativePaintSessionResources.CreateLayer(
            drawing, 0.82f, new Rect(4, 4, 80, 80));

        inner.Close();
        outer.Close();

        Assert.Equal(2, disposal.LayerAttempts);
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.QuarantinedResourceCount);
        Assert.False(retirement.DrainOnce());
    }

    [Fact]
    public void FailedInnerPopQuarantinesOpenAncestorsAndAggregatesPaintAndCleanupFailures()
    {
        using var target = CreateTarget();
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposal = new DisposalProbe();
        NativePaintSessionResources.RegisterTestOwner(drawing, retirement, disposal.Dispose);

        NativePaintSessionResources.LayerLease outer = NativePaintSessionResources.CreateLayer(
            drawing, 1f, new Rect(0, 0, 96, 96));
        NativePaintSessionResources.LayerLease inner = NativePaintSessionResources.CreateLayer(
            drawing, 0.82f, new Rect(4, 4, 80, 80));
        CanvasActiveLayer outerNative = Assert.IsType<CanvasActiveLayer>(outer.Layer);
        CanvasActiveLayer innerNative = Assert.IsType<CanvasActiveLayer>(inner.Layer);
        disposal.FailBeforeClosingNextLayer = true;
        var primary = new FormatException("paint body failure");

        try
        {
            AggregateException failure = Assert.Throws<AggregateException>(
                () => inner.ClosePreservingPrimary(primary));

            Assert.Contains(primary, failure.Flatten().InnerExceptions);
            Assert.Contains(failure.Flatten().InnerExceptions, exception =>
                exception is InvalidOperationException && exception.Message.Contains("injected layer close failure", StringComparison.Ordinal));
            Assert.Equal(2, retirement.QuarantinedResourceCount);
            Assert.Equal(0, retirement.PendingRecordCount);
            Assert.Equal(1, disposal.FailedLayerAttempts);
            Assert.Equal(1, disposal.LayerAttempts);

            // The helper intentionally never closes an ancestor after a failed
            // PopLayer. Closing its now-stale token must not attempt another pop.
            outer.Close();
            Assert.False(retirement.DrainOnce());
            Assert.Equal(1, disposal.LayerAttempts);
            Assert.Equal(2, retirement.QuarantinedResourceCount);
        }
        finally
        {
            // This known pre-pop failure leaves the native stack intact.
            // Unwind it in LIFO order while the drawing session is alive.
            try
            {
                innerNative.Dispose();
            }
            finally
            {
                outerNative.Dispose();
            }
        }
    }

    [Fact]
    public void FailedInnerPopQuarantinesItsClipGeometryWithoutRetryOrUnwind()
    {
        using var target = CreateTarget();
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposal = new DisposalProbe();
        NativePaintSessionResources.RegisterTestOwner(drawing, retirement, disposal.Dispose);

        NativePaintSessionResources.LayerLease outer = NativePaintSessionResources.CreateLayer(
            drawing, 1f, new Rect(0, 0, 96, 96));
        NativePaintSessionResources.AuxiliaryLease geometryOwner = NativePaintSessionResources.ReserveAuxiliary(drawing);
        CanvasGeometry geometry = CanvasGeometry.CreateRoundedRectangle(target, new Rect(0, 0, 80, 80), 6, 6);
        geometryOwner.Attach(geometry);
        NativePaintSessionResources.LayerLease inner = NativePaintSessionResources.CreateLayer(
            drawing, 0.82f, geometry, geometryOwner);
        CanvasActiveLayer outerNative = Assert.IsType<CanvasActiveLayer>(outer.Layer);
        CanvasActiveLayer innerNative = Assert.IsType<CanvasActiveLayer>(inner.Layer);
        disposal.FailBeforeClosingNextLayer = true;

        try
        {
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(inner.Close);

            Assert.Contains("injected layer close failure", failure.Message, StringComparison.Ordinal);
            Assert.Equal(3, retirement.QuarantinedResourceCount);
            Assert.Equal(0, retirement.PendingRecordCount);
            Assert.Equal(1, disposal.LayerAttempts);
            Assert.Equal(0, disposal.GeometryAttempts);

            // Neither stale token may retry quarantined native objects, and the
            // retirement manager must leave the unsafe live stack untouched.
            inner.Close();
            geometryOwner.Close();
            outer.Close();
            Assert.False(retirement.DrainOnce());
            Assert.Equal(3, retirement.QuarantinedResourceCount);
            Assert.Equal(1, disposal.LayerAttempts);
            Assert.Equal(0, disposal.GeometryAttempts);
        }
        finally
        {
            // This known pre-pop failure leaves the native stack intact.
            // Unwind it in LIFO/dependency order with the session still alive.
            try
            {
                innerNative.Dispose();
            }
            finally
            {
                try
                {
                    geometry.Dispose();
                }
                finally
                {
                    outerNative.Dispose();
                }
            }
        }
    }

    [Fact]
    public void ExternalActiveLayerFailureQuarantinesHelperLayersWithoutUnsafeUnwind()
    {
        using var target = CreateTarget();
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposal = new DisposalProbe();
        NativePaintSessionResources.RegisterTestOwner(drawing, retirement, disposal.Dispose);

        NativePaintSessionResources.LayerLease outer = NativePaintSessionResources.CreateLayer(
            drawing, 1f, new Rect(0, 0, 96, 96));
        NativePaintSessionResources.LayerLease inner = NativePaintSessionResources.CreateLayer(
            drawing, 0.82f, new Rect(4, 4, 80, 80));
        CanvasActiveLayer outerNative = Assert.IsType<CanvasActiveLayer>(outer.Layer);
        CanvasActiveLayer innerNative = Assert.IsType<CanvasActiveLayer>(inner.Layer);

        NativePaintSessionResources.PoisonSession(drawing);

        Assert.Equal(2, retirement.QuarantinedResourceCount);
        Assert.Equal(0, disposal.LayerAttempts);
        inner.Close();
        outer.Close();
        Assert.Throws<InvalidOperationException>(() =>
            NativePaintSessionResources.CreateLayer(drawing, 1f, new Rect(0, 0, 64, 64)));

        // Simulate the owner unwinding the actual session after the drawing
        // stack reports its failure; quarantine must never retry these layers.
        innerNative.Dispose();
        outerNative.Dispose();
        Assert.False(retirement.DrainOnce());
        Assert.Equal(2, retirement.QuarantinedResourceCount);
    }

    [Fact]
    public void ReportedLayerCloseFailureAfterNativePopIsQuarantinedAndNeverRetried()
    {
        using var target = CreateTarget();
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposal = new DisposalProbe { FailAfterClosingNextLayer = true };
        NativePaintSessionResources.RegisterTestOwner(drawing, retirement, disposal.Dispose);

        NativePaintSessionResources.LayerLease layer = NativePaintSessionResources.CreateLayer(
            drawing, 1f, new Rect(0, 0, 64, 64));
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(layer.Close);

        Assert.Contains("injected layer close failure", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, retirement.QuarantinedResourceCount);
        Assert.Equal(0, retirement.PendingResourceCount);
        Assert.False(retirement.DrainOnce());
        Assert.Equal(1, disposal.LayerAttempts);
        Assert.Equal(1, disposal.FailedLayerAttempts);
    }

    [Fact]
    public void GeometryIsRetriedOnlyAfterItsDependentLayerHasClosed()
    {
        using var target = CreateTarget();
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposal = new DisposalProbe { FailNextGeometry = true };
        NativePaintSessionResources.RegisterTestOwner(drawing, retirement, disposal.Dispose);

        NativePaintSessionResources.AuxiliaryLease geometryOwner = NativePaintSessionResources.ReserveAuxiliary(drawing);
        CanvasGeometry geometry = CanvasGeometry.CreateRoundedRectangle(target, new Rect(0, 0, 64, 64), 6, 6);
        geometryOwner.Attach(geometry);
        NativePaintSessionResources.LayerLease layer = NativePaintSessionResources.CreateLayer(
            drawing, 1f, geometry, geometryOwner);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(layer.Close);

        Assert.Contains("injected geometry disposal failure", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, disposal.LayerAttempts);
        Assert.Equal(1, disposal.GeometryAttempts);
        Assert.Equal(1, retirement.PendingRecordCount);
        Assert.Equal(1, retirement.PendingResourceCount);
        Assert.Equal(0, retirement.QuarantinedResourceCount);
        Assert.True(retirement.DrainOnce());
        Assert.Equal(2, disposal.GeometryAttempts);
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.PendingResourceCount);
    }

    [Fact]
    public void ReusedLayerPoolRejectsStaleLeaseWithoutClosingCurrentLayer()
    {
        using var target = CreateTarget();
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        NativePaintSessionResources.RegisterTestOwner(drawing, retirement);

        NativePaintSessionResources.LayerLease oldLease = NativePaintSessionResources.CreateLayer(
            drawing, 1f, new Rect(0, 0, 64, 64));
        oldLease.Close();
        NativePaintSessionResources.LayerLease currentLease = NativePaintSessionResources.CreateLayer(
            drawing, 1f, new Rect(0, 0, 64, 64));

        Assert.Same(oldLease.Node, currentLease.Node);
        Assert.NotEqual(oldLease.Generation, currentLease.Generation);
        oldLease.Close();
        Assert.False(currentLease.IsClosed);

        currentLease.Close();
        Assert.True(currentLease.IsClosed);
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.QuarantinedResourceCount);
    }

    [Fact]
    public void ReusedAuxiliaryPoolRejectsStaleTokenWithoutDisposingCurrentGeometry()
    {
        using var target = CreateTarget();
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposal = new DisposalProbe();
        NativePaintSessionResources.RegisterTestOwner(drawing, retirement, disposal.Dispose);

        NativePaintSessionResources.AuxiliaryLease oldLease = NativePaintSessionResources.ReserveAuxiliary(drawing);
        oldLease.Attach(CanvasGeometry.CreateRoundedRectangle(target, new Rect(0, 0, 32, 32), 3, 3));
        oldLease.Close();
        Assert.Equal(1, disposal.GeometryAttempts);

        NativePaintSessionResources.AuxiliaryLease currentLease = NativePaintSessionResources.ReserveAuxiliary(drawing);
        Assert.Same(oldLease.Node, currentLease.Node);
        Assert.NotEqual(oldLease.Generation, currentLease.Generation);

        var staleResource = new DisposableSentinel();
        Assert.Throws<InvalidOperationException>(() => oldLease.Attach(staleResource));
        Assert.False(staleResource.Disposed);
        staleResource.Dispose();

        currentLease.Attach(CanvasGeometry.CreateRoundedRectangle(target, new Rect(0, 0, 48, 48), 3, 3));

        oldLease.Close();
        Assert.Equal(1, disposal.GeometryAttempts);
        currentLease.Close();
        Assert.Equal(2, disposal.GeometryAttempts);
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.QuarantinedResourceCount);
    }

    [Fact]
    public void RejectedCrossSessionClipLayerReleasesItsAttachedGeometry()
    {
        using var target = CreateTarget();
        using var otherTarget = CreateTarget();
        using CanvasDrawingSession ownerSession = target.CreateDrawingSession();
        using CanvasDrawingSession otherSession = otherTarget.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposal = new DisposalProbe();
        NativePaintSessionResources.RegisterTestOwner(ownerSession, retirement, disposal.Dispose);

        NativePaintSessionResources.AuxiliaryLease owner = NativePaintSessionResources.ReserveAuxiliary(ownerSession);
        CanvasGeometry geometry = CanvasGeometry.CreateRoundedRectangle(target, new Rect(0, 0, 64, 64), 4, 4);
        owner.Attach(geometry);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            NativePaintSessionResources.CreateLayer(otherSession, 1f, geometry, owner));

        Assert.Contains("does not belong", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, disposal.GeometryAttempts);
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.QuarantinedResourceCount);
    }

    [Fact]
    public void WarmSequentialLayersStayWithinSteadyStateAllocationBudgetInOneDrawingSession()
    {
        using var target = CreateTarget();
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        NativePaintSessionResources.RegisterTestOwner(drawing, retirement);
        const int iterations = 512;
        const int sampleCount = 5;
        Rect clip = new(0, 0, 64, 64);

        // The pool is deliberately drawing-session-local. Warm the CWT, node
        // pools, projected Win2D wrappers and both paint paths in this session.
        MeasureDirectLayers(drawing, clip, 128);
        MeasureManagedLayers(drawing, clip, 128);

        NativePaintSessionResources.LayerLease initial = NativePaintSessionResources.CreateLayer(drawing, 1f, clip);
        NativePaintSessionResources.LayerNode pooledNode = Assert.IsType<NativePaintSessionResources.LayerNode>(initial.Node);
        NativeResourceRetirementRecord pooledRecord = pooledNode.Retirement;
        initial.Close();
        for (int index = 0; index < 16; index++)
        {
            NativePaintSessionResources.LayerLease lease = NativePaintSessionResources.CreateLayer(drawing, 1f, clip);
            Assert.Same(pooledNode, lease.Node);
            Assert.Same(pooledRecord, lease.Node!.Retirement);
            lease.Close();
        }

        var excessBytes = new long[sampleCount];
        for (int sample = 0; sample < sampleCount; sample++)
        {
            long directBytes;
            long managedBytes;
            if ((sample & 1) == 0)
            {
                directBytes = MeasureDirectLayers(drawing, clip, iterations);
                managedBytes = MeasureManagedLayers(drawing, clip, iterations, pooledNode, pooledRecord);
            }
            else
            {
                managedBytes = MeasureManagedLayers(drawing, clip, iterations, pooledNode, pooledRecord);
                directBytes = MeasureDirectLayers(drawing, clip, iterations);
            }
            excessBytes[sample] = managedBytes - directBytes;
        }
        Array.Sort(excessBytes);
        long medianExcessBytes = excessBytes[sampleCount / 2];

        // Keep the original 64 B/op steady-state budget. Longer batches dilute
        // one-off Win2D/JIT initialization, alternating order reduces systematic
        // warm-order bias, and the median only rejects isolated noisy samples:
        // a persistent per-operation cost still fails the gate.
        Assert.True(medianExcessBytes <= iterations * 64L,
            $"Median managed-minus-direct allocation was {medianExcessBytes} bytes over {iterations} layers; samples: {string.Join(", ", excessBytes)}.");
        Assert.Equal(0, retirement.PendingRecordCount);
        Assert.Equal(0, retirement.QuarantinedResourceCount);
    }

    [Fact]
    public void FreshDrawingSessionsAddBoundedPerLayerOwnerAllocation()
    {
        using var target = CreateTarget();
        const int iterations = 16;
        Rect clip = new(0, 0, 64, 64);

        // Initialize the process-wide retirement clock and CWT buckets before
        // comparing per-session ownership cost; this is not a global cache hit
        // claim because every iteration creates a fresh drawing session.
        MeasureManagedLayersAcrossFreshSessions(target, clip, 16);
        MeasureDirectLayersAcrossFreshSessions(target, clip, 8);
        long directBytes = MeasureDirectLayersAcrossFreshSessions(target, clip, iterations);
        long managedBytes = MeasureManagedLayersAcrossFreshSessions(target, clip, iterations);

        Assert.True(managedBytes - directBytes <= iterations * 1024L,
            $"Fresh-session direct={directBytes} bytes; managed={managedBytes} bytes for {iterations} layers.");
    }

    [Fact]
    public void PoisonDuringLayerFactoryKeepsThePreReservedSlotUntilNativeResultIsOwned()
    {
        using var target = CreateTarget();
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        var retirement = new NativeResourceRetirement(createTimer: false);
        var disposal = new DisposalProbe();
        NativePaintSessionResources.RegisterTestOwner(
            drawing,
            retirement,
            disposal.Dispose,
            afterLayerReservation: () => NativePaintSessionResources.PoisonSession(drawing));

        CanvasActiveLayer? quarantinedLayer = null;
        try
        {
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
                NativePaintSessionResources.CreateLayer(drawing, 1f, new Rect(0, 0, 64, 64)));

            Assert.Contains("poisoned", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, retirement.QuarantinedResourceCount);
            Assert.Equal(0, retirement.PendingRecordCount);
            object quarantineRecord = GetPrivateField(retirement, "_quarantine")!;
            quarantinedLayer = Assert.IsType<CanvasActiveLayer>(GetPrivateField(quarantineRecord, "_singleResource"));
            Assert.Equal(0, disposal.LayerAttempts);
            Assert.False(retirement.DrainOnce());
            Assert.Equal(0, disposal.LayerAttempts);
            Assert.Throws<InvalidOperationException>(() =>
                NativePaintSessionResources.CreateLayer(drawing, 1f, new Rect(0, 0, 64, 64)));
        }
        finally
        {
            // The factory completed and its layer is intentionally quarantined
            // as session-bound, nonretryable custody. Clean up only this known
            // result directly, before the live drawing session is disposed.
            quarantinedLayer ??= TryGetQuarantinedLayer(retirement);
            quarantinedLayer?.Dispose();
        }
    }

    private static CanvasRenderTarget CreateTarget() => new(CanvasDevice.GetSharedDevice(), 128, 128, 96);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long MeasureDirectLayers(CanvasDrawingSession drawing, Rect clip, int count)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < count; index++)
            drawing.CreateLayer(1f, clip).Dispose();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long MeasureManagedLayers(
        CanvasDrawingSession drawing,
        Rect clip,
        int count,
        NativePaintSessionResources.LayerNode? expectedNode = null,
        NativeResourceRetirementRecord? expectedRecord = null)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < count; index++)
        {
            NativePaintSessionResources.LayerLease lease = NativePaintSessionResources.CreateLayer(drawing, 1f, clip);
            bool expectedOwnerReused = expectedNode is null ||
                (ReferenceEquals(lease.Node, expectedNode) && ReferenceEquals(lease.Node!.Retirement, expectedRecord));
            lease.Close();
            if (!expectedOwnerReused)
                throw new InvalidOperationException("The measured layer loop did not reuse its reserved native owner node and record.");
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long MeasureDirectLayersAcrossFreshSessions(CanvasRenderTarget target, Rect clip, int count)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < count; index++)
        {
            using CanvasDrawingSession drawing = target.CreateDrawingSession();
            drawing.CreateLayer(1f, clip).Dispose();
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long MeasureManagedLayersAcrossFreshSessions(CanvasRenderTarget target, Rect clip, int count)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < count; index++)
        {
            using CanvasDrawingSession drawing = target.CreateDrawingSession();
            NativePaintSessionResources.CreateLayer(drawing, 1f, clip).Close();
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static object? GetPrivateField(object owner, string name) =>
        owner.GetType().GetField(
            name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(owner);

    private static CanvasActiveLayer? TryGetQuarantinedLayer(NativeResourceRetirement retirement)
    {
        object? record = GetPrivateField(retirement, "_quarantine");
        return record is null ? null : GetPrivateField(record, "_singleResource") as CanvasActiveLayer;
    }

    private sealed class DisposalProbe
    {
        internal int LayerAttempts;
        internal int FailedLayerAttempts;
        internal int GeometryAttempts;
        internal bool FailBeforeClosingNextLayer;
        internal bool FailAfterClosingNextLayer;
        internal bool FailNextGeometry;

        internal void Dispose(IDisposable resource)
        {
            if (resource is CanvasActiveLayer)
            {
                LayerAttempts++;
                if (FailBeforeClosingNextLayer)
                {
                    FailBeforeClosingNextLayer = false;
                    FailedLayerAttempts++;
                    throw new InvalidOperationException("injected layer close failure");
                }
                resource.Dispose();
                if (FailAfterClosingNextLayer)
                {
                    FailAfterClosingNextLayer = false;
                    FailedLayerAttempts++;
                    throw new InvalidOperationException("injected layer close failure");
                }
                return;
            }

            if (resource is CanvasGeometry)
            {
                GeometryAttempts++;
                if (FailNextGeometry)
                {
                    FailNextGeometry = false;
                    throw new InvalidOperationException("injected geometry disposal failure");
                }
            }

            resource.Dispose();
        }
    }

    private sealed class DisposableSentinel : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
