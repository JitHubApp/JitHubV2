using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using System.Runtime.CompilerServices;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerFontGenerationTests
{
    [Fact]
    public void WorkerFontGenerationIsMonotonicAndBoundedByWireType()
    {
        var generation = new WorkerFontCatalogGeneration();

        Assert.Equal(-1L, generation.ReadyGeneration);
        Assert.False(generation.IsReadyFor(0));
        generation.MarkReady(0);
        generation.MarkReady(4);
        generation.MarkReady(2);

        Assert.Equal(4L, generation.ReadyGeneration);
        Assert.True(generation.IsReadyFor(4));
        Assert.True(WorkerFontCatalogGeneration.IsReadyFor(uint.MaxValue, uint.MaxValue));
        Assert.False(WorkerFontCatalogGeneration.IsReadyFor(-1, 0));
        Assert.Throws<WorkerProtocolException>(() => generation.MarkReady((long)uint.MaxValue + 1));
        Assert.Throws<WorkerProtocolException>(() => WorkerFontCatalogGeneration.ValidateRequest(-1));
        Assert.Throws<WorkerProtocolException>(() => WorkerFontCatalogGeneration.ValidateRequest((long)uint.MaxValue + 1));
    }

    [Theory]
    [InlineData((int)WorkerStatus.Ok, 1, true)]
    [InlineData((int)WorkerStatus.Ok, 2, true)]
    [InlineData((int)WorkerStatus.FontCatalogPending, 0, true)]
    [InlineData((int)WorkerStatus.FontCatalogFailure, 0, true)]
    [InlineData((int)WorkerStatus.FontCatalogPending, 1, false)]
    [InlineData((int)WorkerStatus.FontCatalogFailure, 1, false)]
    public void ProbeResponseMustMatchRequestedGenerationSemantics(
        int statusValue,
        long readyGeneration,
        bool valid)
    {
        WorkerStatus status = (WorkerStatus)statusValue;
        WorkerResponse response = Response(status, readyGeneration);

        if (valid)
            WorkerFontCatalogProtocolPolicy.ValidateProbeResponse(1, response);
        else
            Assert.Throws<WorkerProtocolException>(
                () => WorkerFontCatalogProtocolPolicy.ValidateProbeResponse(1, response));
    }

    [Fact]
    public void InitialGenerationProbeAcceptsOnlyZeroReadyGeneration()
    {
        WorkerFontCatalogProtocolPolicy.ValidateProbeResponse(
            0,
            Response(WorkerStatus.FontCatalogPending, 0));
        WorkerFontCatalogProtocolPolicy.ValidateProbeResponse(
            0,
            Response(WorkerStatus.FontCatalogFailure, 0));
        WorkerFontCatalogProtocolPolicy.ValidateProbeResponse(0, Response(WorkerStatus.Ok, 0));

        Assert.Throws<WorkerProtocolException>(() => WorkerFontCatalogProtocolPolicy.ValidateProbeResponse(
            0,
            Response(WorkerStatus.FontCatalogPending, 1)));
        Assert.Throws<WorkerProtocolException>(() => WorkerFontCatalogProtocolPolicy.ValidateProbeResponse(
            0,
            Response(WorkerStatus.FontCatalogFailure, 1)));
    }

    [Theory]
    [InlineData((int)WorkerStatus.Unsupported, 0, 0)]
    [InlineData((int)WorkerStatus.Ok, 0, 0)]
    [InlineData((int)WorkerStatus.Ok, 2, 0)]
    [InlineData((int)WorkerStatus.FontCatalogPending, 1, 0)]
    [InlineData((int)WorkerStatus.FontCatalogFailure, 1, 0)]
    [InlineData((int)WorkerStatus.FontCatalogPending, 0, 1)]
    public void MalformedCurrentGenerationProbeInvalidatesWorkerAndMapsToTransientFailure(
        int statusValue,
        long readyGeneration,
        int outputLength)
    {
        WorkerResponse response = Response((WorkerStatus)statusValue, readyGeneration) with
        {
            OutputLength = outputLength,
        };
        WorkerProtocolException malformed = Assert.Throws<WorkerProtocolException>(
            () => WorkerPool.ValidateFontCatalogProbeResponse(1, response));
        bool invalidated = false;

        WorkerInitializationException exception = WorkerPool.InvalidateMalformedFontCatalogProbe(
            malformed,
            ensureRequestCurrent: static () => { },
            invalidateWorker: () => invalidated = true);

        Assert.True(invalidated);
        Assert.IsType<WorkerProtocolException>(exception.InnerException);
        Assert.Contains("font-catalog probe response", exception.Message);
    }

    [Theory]
    [InlineData((int)WorkerStatus.Ok, 1)]
    [InlineData((int)WorkerStatus.FontCatalogPending, 0)]
    [InlineData((int)WorkerStatus.FontCatalogFailure, 0)]
    public void WellFormedCurrentGenerationProbePassesValidation(
        int statusValue,
        long readyGeneration)
    {
        WorkerPool.ValidateFontCatalogProbeResponse(
            requestGeneration: 1,
            response: Response((WorkerStatus)statusValue, readyGeneration));
    }

    [Fact]
    public void CancellationDuringMalformedProbeHandlingDoesNotInvalidateWorker()
    {
        bool invalidated = false;
        var cancellation = new OperationCanceledException("font generation rotated");

        OperationCanceledException observed = Assert.Throws<OperationCanceledException>(
            () => WorkerPool.InvalidateMalformedFontCatalogProbe(
                new WorkerProtocolException("malformed response"),
                ensureRequestCurrent: () => throw cancellation,
                invalidateWorker: () => invalidated = true));

        Assert.Same(cancellation, observed);
        Assert.False(invalidated);
    }

    [Fact]
    public void DetachedWaitAdmissionAllowsParkedContinuationsWithoutOpeningNewCapacity()
    {
        const int capacity = 32;

        Assert.False(WorkerFontWaitAdmissionPolicy.CanAdmitNew(0, capacity, capacity));
        Assert.True(WorkerFontWaitAdmissionPolicy.CanReenter(0, capacity));
        Assert.False(WorkerFontWaitAdmissionPolicy.CanReenter(capacity, capacity));
        Assert.True(WorkerFontWaitAdmissionPolicy.CanReserveFromActive(capacity, 0, capacity, 1));
        Assert.False(WorkerFontWaitAdmissionPolicy.CanReserveFromActive(capacity + 1, 0, capacity, 1));
        Assert.True(WorkerFontWaitAdmissionPolicy.CanReserveFromActive(0, 33, capacity, 2));
        Assert.False(WorkerFontWaitAdmissionPolicy.CanAdmitNew(0, 33, capacity));
        Assert.True(WorkerFontWaitAdmissionPolicy.CanReenter(0, capacity));
        Assert.False(WorkerFontWaitAdmissionPolicy.CanReserveFromActive(
            0,
            0,
            capacity,
            1,
            leaseIsActive: false));
        Assert.False(WorkerFontWaitAdmissionPolicy.CanReserveFromActive(
            0,
            0,
            capacity,
            1,
            alreadyReserved: true));
    }

    [Fact]
    public void DetachedVisibleTextRenderDemandParticipatesInSecondaryWarmThreshold()
    {
        int visibleTextDemand = WorkerFontWaitAdmissionPolicy.CountVisibleDemand(
            queuedCount: 0,
            detachedCount: 2);

        Assert.Equal(2, visibleTextDemand);
        Assert.True(WorkerSchedulingPolicy.ShouldWarmSecondaryFontCatalog(
            primaryReadyGeneration: 0,
            secondaryReadyGeneration: -1,
            requestedGeneration: 0,
            queuedVisibleTextRenders: visibleTextDemand));
        Assert.True(WorkerSchedulingPolicy.ShouldStartSecondary(
            canUseSecondary: true,
            primaryBusy: true,
            secondaryExists: false,
            queuedVisibleUncachedRenders: visibleTextDemand));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorkerFontWaitAdmissionPolicy.CountVisibleDemand(0, -1));
    }

    [Fact]
    public void ReservationReleasesItsAdmissionExactlyOnceOrTransfersItExactlyOnce()
    {
        int releases = 0;
        using (var reservation = new WorkerFontWaitReservation(
                   () => releases++,
                   countsAsVisibleRender: true,
                   countsAsVisibleTextRender: true))
        {
            Assert.True(reservation.IsActive);
            Assert.True(reservation.CountsAsVisibleRender);
            Assert.True(reservation.CountsAsVisibleTextRender);
            Assert.True(reservation.TryConsume());
            Assert.False(reservation.IsActive);
            Assert.False(reservation.TryConsume());
        }
        Assert.Equal(0, releases);

        var canceled = new WorkerFontWaitReservation(() => releases++);
        canceled.Dispose();
        canceled.Dispose();
        Assert.False(canceled.IsActive);
        Assert.Equal(1, releases);

        int racedReleases = 0;
        var raced = new WorkerFontWaitReservation(() => Interlocked.Increment(ref racedReleases));
        Parallel.Invoke(raced.Dispose, () => _ = raced.TryConsume());
        raced.Dispose();
        Assert.InRange(racedReleases, 0, 1);
        Assert.False(raced.IsActive);
    }

    [Fact]
    public void TerminalReservationDropsItsOwnerCallback()
    {
        (WorkerFontWaitReservation reservation, WeakReference owner) = CreateReservationWithOwner();
        Assert.True(reservation.TryConsume());

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(owner.IsAlive);
        reservation.Dispose();
    }

    [Fact]
    public async Task CanceledSecondaryAcquireDrainsAndDisposesALateLease()
    {
        var grant = new TaskCompletionSource<ProbeLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancelCalls = 0;
        Task drain = WorkerPool.CancelAndDrainAcquireAsync(grant.Task, () => cancelCalls++);

        Assert.Equal(1, cancelCalls);
        Assert.False(drain.IsCompleted);
        var lateLease = new ProbeLease();
        grant.TrySetResult(lateLease);
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, lateLease.DisposeCount);
    }

    [Fact]
    public async Task CanceledSecondaryAcquireDrainObservesCancellationAndDisposalFaults()
    {
        var canceledToken = new CancellationToken(canceled: true);
        Task<ProbeLease> canceled = Task.FromCanceled<ProbeLease>(canceledToken);
        await WorkerPool.CancelAndDrainAcquireAsync(canceled, static () => { });

        Task<ProbeLease> disposed = Task.FromException<ProbeLease>(new ObjectDisposedException("pool"));
        await WorkerPool.CancelAndDrainAcquireAsync(disposed, static () => { });

        Assert.True(canceled.IsCanceled);
        Assert.True(disposed.IsFaulted);
        _ = disposed.Exception;
    }

    [Fact]
    public async Task SecondaryRefreshKeepsGeometryAvailableButWaitsForMatchingTextGeneration()
    {
        var readiness = new SecondaryWorkerReadiness();
        Assert.True(readiness.TryBeginWarmup(workerAlreadyReady: true, fontCatalogReadyGeneration: 0));
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishRefresh = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task warmup = readiness.WarmAsync(
            _ => Task.CompletedTask,
            () => true,
            _ =>
            {
                refreshEntered.TrySetResult();
                return publishRefresh.Task;
            },
            CancellationToken.None);

        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(readiness.CanDispatch(requiresFontCatalog: false));
        Assert.False(readiness.CanDispatch(requiresFontCatalog: true, requestedGeneration: 1));

        publishRefresh.TrySetResult(1);
        await warmup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(readiness.CanDispatch(requiresFontCatalog: false));
        Assert.True(readiness.CanDispatch(requiresFontCatalog: true, requestedGeneration: 1));
    }

    [Fact]
    public async Task SecondaryCatalogTimeoutKeepsLiveWorkerGeometryEligible()
    {
        var readiness = new SecondaryWorkerReadiness();
        Assert.True(readiness.TryBeginWarmup(workerAlreadyReady: true, fontCatalogReadyGeneration: 0));

        await readiness.WarmAsync(
            _ => Task.CompletedTask,
            () => true,
            _ => Task.FromResult(-1L),
            CancellationToken.None);

        Assert.True(readiness.IsWorkerReady);
        Assert.False(readiness.HasFailed);
        Assert.True(readiness.CanDispatch(requiresFontCatalog: false));
        Assert.False(readiness.CanDispatch(requiresFontCatalog: true, requestedGeneration: 1));
    }

    [Fact]
    public void OlderSecondaryPublicationCannotEraseANewerFailureMarker()
    {
        var readiness = new SecondaryWorkerReadiness();
        Assert.True(readiness.TryBeginWarmup(workerAlreadyReady: true, fontCatalogReadyGeneration: 2));
        readiness.MarkFontCatalogFailed(4);

        readiness.MarkFontCatalogReady(3);
        Assert.Equal(4L, readiness.FontCatalogFailedGeneration);
        readiness.MarkFontCatalogFailed(3);
        Assert.Equal(4L, readiness.FontCatalogFailedGeneration);

        readiness.MarkFontCatalogReady(4);
        Assert.Equal(-1L, readiness.FontCatalogFailedGeneration);
    }

    [Fact]
    public void SecondaryFontProbeDoesNotConsumePrimaryQueueWork()
    {
        var queue = new WorkerSchedulingQueue<object>();
        object probe = new();
        queue.Enqueue(
            probe,
            MarkdownSvgRenderPriority.Visible,
            isRender: true,
            requiresFontCatalog: true,
            isSecondaryFontProbe: true);

        Assert.False(queue.TryDequeuePrimary(out _));
        Assert.False(queue.TryDequeueSecondaryFontProbe(secondaryWorkerInstanceId: 42, out _));
        Assert.True(queue.TryDequeueSecondaryFontProbe(secondaryWorkerInstanceId: 0, out object? actual));
        Assert.Same(probe, actual);
    }

    [Theory]
    [InlineData((int)WorkerStatus.Ok)]
    [InlineData((int)WorkerStatus.FontCatalogPending)]
    [InlineData((int)WorkerStatus.FontCatalogFailure)]
    public void FontRotationBetweenProbeAndOpenCancelsStaleResponseWithoutQuarantine(int statusValue)
    {
        WorkerStatus status = (WorkerStatus)statusValue;
        WorkerResponse response = Response(status, 0);

        Assert.Throws<OperationCanceledException>(() => WorkerPool.ValidateTextResponse(
            response,
            requestGeneration: 0,
            currentGeneration: 1,
            operation: "document open"));
    }

    [Fact]
    public void CurrentGenerationPendingOpenResponseRequestsRetryWithoutContentFailure()
    {
        Assert.Throws<OperationCanceledException>(() => WorkerPool.ValidateTextResponse(
            Response(WorkerStatus.FontCatalogPending, 0),
            requestGeneration: 0,
            currentGeneration: 0,
            operation: "document open"));
    }

    [Fact]
    public void CurrentGenerationFontFailureIsTransientAndNotAContentResponse()
    {
        Assert.Throws<WorkerInitializationException>(() => WorkerPool.ValidateTextResponse(
            Response(WorkerStatus.FontCatalogFailure, 0),
            requestGeneration: 0,
            currentGeneration: 0,
            operation: "document open"));
    }

    [Fact]
    public void SuccessfulTextResponseMustUseTheRequestedCatalogGeneration()
    {
        WorkerPool.ValidateTextResponse(
            Response(WorkerStatus.Ok, 1),
            requestGeneration: 1,
            currentGeneration: 1,
            operation: "render");

        Assert.Throws<WorkerProtocolException>(() => WorkerPool.ValidateTextResponse(
            Response(WorkerStatus.Ok, 0),
            requestGeneration: 1,
            currentGeneration: 1,
            operation: "render"));
    }

    private static WorkerResponse Response(WorkerStatus status, long readyGeneration) =>
        new(
            status,
            IntrinsicWidth: 0,
            IntrinsicHeight: 0,
            AspectRatio: 0,
            OutputLength: 0,
            Stride: 0,
            Width: 0,
            Height: 0,
            PixelFormat: MarkdownSvgPixelFormat.Rgba8Premultiplied,
            HasText: false,
            UsesCurrentColor: false,
            UsesColorScheme: false,
            Detail: string.Empty,
            ReadyFontGeneration: readyGeneration);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WorkerFontWaitReservation Reservation, WeakReference Owner) CreateReservationWithOwner()
    {
        var owner = new object();
        var weakOwner = new WeakReference(owner);
        var reservation = new WorkerFontWaitReservation(() => GC.KeepAlive(owner));
        return (reservation, weakOwner);
    }

    private sealed class ProbeLease : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
