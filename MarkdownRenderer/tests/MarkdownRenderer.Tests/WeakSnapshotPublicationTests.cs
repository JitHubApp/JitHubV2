using System.Reflection;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class WeakSnapshotPublicationTests
{
    [Fact]
    public void Publish_DeliversSameSnapshotOnceAndAllowsReentrancy()
    {
        var snapshot = new SnapshotGraph();
        WeakSnapshotPublication<SnapshotGraph>? publication = null;
        int deliveries = 0;
        SnapshotGraph? delivered = null;
        publication = new WeakSnapshotPublication<SnapshotGraph>(snapshot, value =>
        {
            Interlocked.Increment(ref deliveries);
            delivered = value;
            publication!.Publish();
        });

        publication.Publish();
        publication.Publish();

        Assert.Same(snapshot, delivered);
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public void RetainedQueuedCallbackDoesNotKeepRetiredGraphAlive()
    {
        (WeakReference<SnapshotGraph> graph, Action callback, DeliveryProbe delivery) =
            CreateQueuedPublication();

        CollectUntilDead(graph);
        Assert.False(IsAlive(graph));

        callback();

        Assert.Equal(1, delivery.Count);
        Assert.True(delivery.ReceivedNull);
    }

    [Fact]
    public void Discard_ReleasesReceiverCaptureAndSnapshotEvenWhenCallbackIsRetained()
    {
        DiscardedPublication publication = CreateCapturedPublication();

        publication.Discard();
        publication.Callback();
        CollectUntilDead(publication.Graph);
        CollectUntilDead(publication.Receiver);

        Assert.False(IsAlive(publication.Graph));
        Assert.False(IsAlive(publication.Receiver));
        Assert.Equal(0, publication.Delivery.Count);
        GC.KeepAlive(publication.Callback);
        GC.KeepAlive(publication.Discard);
    }

    [Fact]
    public void CallbackException_ConsumesPublicationAndClearsCapturedAliases()
    {
        ThrowingPublication publication = CreateThrowingPublication();

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            publication.Callback);
        Assert.Same(publication.Failure, thrown);
        publication.Callback();

        Assert.Equal(1, publication.Observation.Count);
        Assert.True(publication.Observation.ReceivedSameGraph);
        CollectUntilDead(publication.Graph);
        CollectUntilDead(publication.Receiver);
        Assert.False(IsAlive(publication.Graph));
        Assert.False(IsAlive(publication.Receiver));
        GC.KeepAlive(publication.Callback);
    }

    [Fact]
    public async Task ConcurrentPublishAndDiscardDeliverAtMostOnce()
    {
        for (int iteration = 0; iteration < 24; iteration++)
        {
            var snapshot = new SnapshotGraph();
            int deliveries = 0;
            var publication = new WeakSnapshotPublication<SnapshotGraph>(snapshot, delivered =>
            {
                Assert.Same(snapshot, delivered);
                Interlocked.Increment(ref deliveries);
            });
            using var start = new ManualResetEventSlim();

            Task firstPublish = Task.Run(() =>
            {
                start.Wait();
                publication.Publish();
            });
            Task secondPublish = Task.Run(() =>
            {
                start.Wait();
                publication.Publish();
            });
            Task discard = Task.Run(() =>
            {
                start.Wait();
                publication.Discard();
            });

            start.Set();
            await Task.WhenAll(firstPublish, secondPublish, discard)
                .WaitAsync(TimeSpan.FromSeconds(10));
            publication.Publish();

            Assert.InRange(Volatile.Read(ref deliveries), 0, 1);
        }
    }

    [Fact]
    public void AcceptedPublicationOwnerDiscardReleasesCaptureAndLeavesWorkerBorrowUntilDrain()
    {
        PayloadPublicationHarness harness = CreatePayloadPublicationHarness();
        Assert.True(harness.Payload.TryOwnPublication(harness.Publication));

        Action queuedCallback = harness.Callback;
        harness.Payload.DiscardPublication();
        harness.Payload.DiscardPublication();
        AssertPublicationClosedAndDetached(harness.Payload);
        Assert.Null(GetPublicationField(harness.Publication, "_delivery"));

        queuedCallback();
        Assert.Equal(0, harness.Observation.Count);

        CollectUntilDead(harness.Receiver);
        CollectUntilDead(harness.Sentinel);
        Assert.False(IsAlive(harness.Receiver));
        Assert.False(IsAlive(harness.Sentinel));
        Assert.True(IsAlive(harness.Graph));
        Assert.True(IsAlive(harness.Changed));
        Assert.Equal(SnapshotGraph.ExpectedId, harness.Payload.Snapshot.Id);
        Assert.Equal(SnapshotGraph.ExpectedChangedBlockIndex, harness.Payload.Changed[0]);
        Assert.NotNull(GetPayloadField(harness.Payload, "_snapshot"));
        Assert.NotNull(GetPayloadField(harness.Payload, "_changed"));

        harness.Payload.ReleaseAfterWorkDrains();
        AssertPayloadGraphAliasesCleared(harness.Payload);
        CollectUntilDead(harness.Graph);
        CollectUntilDead(harness.Changed);
        Assert.False(IsAlive(harness.Graph));
        Assert.False(IsAlive(harness.Changed));
        GC.KeepAlive(queuedCallback);
        GC.KeepAlive(harness.Publication);
        GC.KeepAlive(harness.Payload);
    }

    [Fact]
    public void ClosedOwnerRejectsLatePublicationWithoutReleasingWorkBorrow()
    {
        PayloadPublicationHarness harness = CreatePayloadPublicationHarness();

        harness.Payload.DiscardPublication();
        AssertPublicationClosedAndDetached(harness.Payload);
        Assert.False(harness.Payload.TryOwnPublication(harness.Publication));
        Assert.Null(GetPublicationField(harness.Publication, "_delivery"));
        harness.Callback();

        Assert.Equal(0, harness.Observation.Count);
        CollectUntilDead(harness.Receiver);
        CollectUntilDead(harness.Sentinel);
        Assert.False(IsAlive(harness.Receiver));
        Assert.False(IsAlive(harness.Sentinel));
        Assert.True(IsAlive(harness.Graph));
        Assert.True(IsAlive(harness.Changed));
        Assert.Equal(SnapshotGraph.ExpectedId, harness.Payload.Snapshot.Id);
        Assert.Equal(SnapshotGraph.ExpectedChangedBlockIndex, harness.Payload.Changed[0]);
        Assert.NotNull(GetPayloadField(harness.Payload, "_snapshot"));
        Assert.NotNull(GetPayloadField(harness.Payload, "_changed"));

        harness.Payload.ReleaseAfterWorkDrains();
        AssertPayloadGraphAliasesCleared(harness.Payload);
        CollectUntilDead(harness.Graph);
        CollectUntilDead(harness.Changed);
        Assert.False(IsAlive(harness.Graph));
        Assert.False(IsAlive(harness.Changed));
        GC.KeepAlive(harness.Callback);
        GC.KeepAlive(harness.Publication);
        GC.KeepAlive(harness.Payload);
    }

    [Fact]
    public async Task ConcurrentRegistrationAndCloseLeavesNoReceiverCapture()
    {
        for (int iteration = 0; iteration < 16; iteration++)
        {
            PayloadPublicationHarness harness = CreatePayloadPublicationHarness();
            using var start = new ManualResetEventSlim();
            ImageRelayoutWorkPayload<SnapshotGraph> payload = harness.Payload;
            WeakSnapshotPublication<SnapshotGraph> publication = harness.Publication;

            Task<bool> registration = Task.Run(() =>
            {
                start.Wait();
                return payload.TryOwnPublication(publication);
            });
            Task close = Task.Run(() =>
            {
                start.Wait();
                payload.DiscardPublication();
            });

            start.Set();
            await Task.WhenAll(registration, close).WaitAsync(TimeSpan.FromSeconds(10));
            _ = await registration;

            AssertPublicationClosedAndDetached(payload);
            Assert.Null(GetPublicationField(publication, "_delivery"));
            harness.Callback();
            Assert.Equal(0, harness.Observation.Count);
            CollectUntilDead(harness.Receiver);
            CollectUntilDead(harness.Sentinel);
            Assert.False(IsAlive(harness.Receiver));
            Assert.False(IsAlive(harness.Sentinel));

            Assert.True(IsAlive(harness.Graph));
            Assert.True(IsAlive(harness.Changed));
            Assert.Equal(SnapshotGraph.ExpectedId, payload.Snapshot.Id);
            Assert.Equal(SnapshotGraph.ExpectedChangedBlockIndex, payload.Changed[0]);
            payload.ReleaseAfterWorkDrains();
            AssertPayloadGraphAliasesCleared(payload);
            CollectUntilDead(harness.Graph);
            CollectUntilDead(harness.Changed);
            Assert.False(IsAlive(harness.Graph));
            Assert.False(IsAlive(harness.Changed));
            GC.KeepAlive(registration);
            GC.KeepAlive(close);
            GC.KeepAlive(harness.Callback);
            GC.KeepAlive(publication);
            GC.KeepAlive(payload);
        }
    }

    [Fact]
    public void ClosingOldOwnerDoesNotDiscardNewOwnersCallback()
    {
        PayloadPublicationHarness old = CreatePayloadPublicationHarness();
        PayloadPublicationHarness current = CreatePayloadPublicationHarness();
        Assert.True(old.Payload.TryOwnPublication(old.Publication));
        Assert.True(current.Payload.TryOwnPublication(current.Publication));

        old.Payload.DiscardPublication();
        AssertPublicationClosedAndDetached(old.Payload);
        Assert.Null(GetPublicationField(old.Publication, "_delivery"));
        old.Callback();
        current.Callback();

        Assert.Equal(0, old.Observation.Count);
        Assert.Equal(1, current.Observation.Count);
        Assert.True(current.Observation.ReceivedSameGraph);

        CollectUntilDead(old.Receiver);
        CollectUntilDead(old.Sentinel);
        Assert.False(IsAlive(old.Receiver));
        Assert.False(IsAlive(old.Sentinel));
        Assert.Equal(SnapshotGraph.ExpectedId, current.Payload.Snapshot.Id);

        current.Payload.DiscardPublication();
        AssertPublicationClosedAndDetached(current.Payload);
        CollectUntilDead(current.Receiver);
        CollectUntilDead(current.Sentinel);
        Assert.False(IsAlive(current.Receiver));
        Assert.False(IsAlive(current.Sentinel));

        old.Payload.ReleaseAfterWorkDrains();
        current.Payload.ReleaseAfterWorkDrains();
        AssertPayloadGraphAliasesCleared(old.Payload);
        AssertPayloadGraphAliasesCleared(current.Payload);
        CollectUntilDead(old.Graph);
        CollectUntilDead(old.Changed);
        CollectUntilDead(current.Graph);
        CollectUntilDead(current.Changed);
        Assert.False(IsAlive(old.Graph));
        Assert.False(IsAlive(old.Changed));
        Assert.False(IsAlive(current.Graph));
        Assert.False(IsAlive(current.Changed));
        GC.KeepAlive(old.Callback);
        GC.KeepAlive(current.Callback);
        GC.KeepAlive(old.Publication);
        GC.KeepAlive(current.Publication);
        GC.KeepAlive(old.Payload);
        GC.KeepAlive(current.Payload);
    }

    [Fact]
    public void SamePublicationRegistrationIsIdempotentAndDistinctSecondPublicationIsDiscarded()
    {
        PayloadPublicationHarness owner = CreatePayloadPublicationHarness();
        PayloadPublicationHarness second = CreatePayloadPublicationHarness();
        Assert.True(owner.Payload.TryOwnPublication(owner.Publication));
        Assert.True(owner.Payload.TryOwnPublication(owner.Publication));

        Assert.False(owner.Payload.TryOwnPublication(second.Publication));
        Assert.Same(owner.Publication, GetPayloadField(owner.Payload, "_publication"));
        Assert.Null(GetPublicationField(second.Publication, "_delivery"));
        second.Callback();
        Assert.Equal(0, second.Observation.Count);
        CollectUntilDead(second.Receiver);
        CollectUntilDead(second.Sentinel);
        Assert.False(IsAlive(second.Receiver));
        Assert.False(IsAlive(second.Sentinel));
        Assert.Equal(SnapshotGraph.ExpectedId, owner.Payload.Snapshot.Id);

        owner.Payload.DiscardPublication();
        AssertPublicationClosedAndDetached(owner.Payload);
        owner.Callback();
        Assert.Equal(0, owner.Observation.Count);
        CollectUntilDead(owner.Receiver);
        CollectUntilDead(owner.Sentinel);
        Assert.False(IsAlive(owner.Receiver));
        Assert.False(IsAlive(owner.Sentinel));

        second.Payload.DiscardPublication();
        AssertPublicationClosedAndDetached(second.Payload);
        owner.Payload.ReleaseAfterWorkDrains();
        second.Payload.ReleaseAfterWorkDrains();
        AssertPayloadGraphAliasesCleared(owner.Payload);
        AssertPayloadGraphAliasesCleared(second.Payload);
        CollectUntilDead(owner.Graph);
        CollectUntilDead(owner.Changed);
        CollectUntilDead(second.Graph);
        CollectUntilDead(second.Changed);
        Assert.False(IsAlive(owner.Graph));
        Assert.False(IsAlive(owner.Changed));
        Assert.False(IsAlive(second.Graph));
        Assert.False(IsAlive(second.Changed));
        GC.KeepAlive(owner.Callback);
        GC.KeepAlive(second.Callback);
        GC.KeepAlive(owner.Publication);
        GC.KeepAlive(second.Publication);
        GC.KeepAlive(owner.Payload);
        GC.KeepAlive(second.Payload);
    }

    [Fact]
    public async Task CancellationSourceRetirementWaitsForGatedWorkAfterPublicationOwnerCloses()
    {
        PayloadPublicationHarness harness = CreatePayloadPublicationHarness();
        Assert.True(harness.Payload.TryOwnPublication(harness.Publication));
        Action queuedCallback = harness.Callback;
        var workEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWork = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workObservation = new WorkObservation();
        using var source = new CancellationTokenSource();
        CancellationToken token = source.Token;
        var cancellationCallbackCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = token.Register(
            () => cancellationCallbackCompleted.TrySetResult());

        Task workerTask = RunModeledCtsWorkerAsync(
            harness.Payload,
            token,
            workEntered,
            releaseWork,
            workObservation);
        Task? retirement = null;
        try
        {
            await workEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            harness.Payload.DiscardPublication();
            AssertPublicationClosedAndDetached(harness.Payload);
            queuedCallback();
            Assert.Equal(0, harness.Observation.Count);

            CollectUntilDead(harness.Receiver);
            CollectUntilDead(harness.Sentinel);
            Assert.False(IsAlive(harness.Receiver));
            Assert.False(IsAlive(harness.Sentinel));
            Assert.True(IsAlive(harness.Graph));
            Assert.True(IsAlive(harness.Changed));
            Assert.NotNull(GetPayloadField(harness.Payload, "_snapshot"));
            Assert.NotNull(GetPayloadField(harness.Payload, "_changed"));

            retirement = CancellationTokenSourceRetirement.CancelAndDisposeAfter(
                source,
                workerTask);
            Assert.True(token.IsCancellationRequested);
            await cancellationCallbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(workerTask.IsCompleted);
            Assert.False(retirement.IsCompleted);
            _ = source.Token;

            releaseWork.TrySetResult();
            await workerTask.WaitAsync(TimeSpan.FromSeconds(10));
            await retirement.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseWork.TrySetResult();
        }

        Assert.Equal(SnapshotGraph.ExpectedId, workObservation.CompletedSnapshotId);
        Assert.Equal(SnapshotGraph.ExpectedChangedBlockIndex, workObservation.CompletedChangedIndex);
        Assert.Equal(SnapshotGraph.ExpectedWidth, workObservation.CompletedWidth);
        Assert.True(workObservation.SnapshotGetterRejectedAfterDrain);
        Assert.True(workObservation.ChangedGetterRejectedAfterDrain);
        AssertPayloadGraphAliasesCleared(harness.Payload);
        Assert.Throws<ObjectDisposedException>(() => _ = source.Token);
        CollectUntilDead(harness.Graph);
        CollectUntilDead(harness.Changed);
        Assert.False(IsAlive(harness.Graph));
        Assert.False(IsAlive(harness.Changed));

        queuedCallback();
        Assert.Equal(0, harness.Observation.Count);
        GC.KeepAlive(workerTask);
        GC.KeepAlive(retirement);
        GC.KeepAlive(queuedCallback);
        GC.KeepAlive(harness.Publication);
        GC.KeepAlive(harness.Payload);
    }

    [Fact]
    public async Task ModeledAsyncWorkerHoldsPayloadThroughWorkThenClearsGraphAndIndexesBeforeWeakHandoff()
    {
        var workEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWork = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workObservation = new WorkObservation();
        var delivery = new DeliveryProbe();
        TaskCompletionSource<Action> callbackQueued = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        (WeakReference<SnapshotGraph> graph,
            WeakReference<int[]> changed,
            Task workerTask) = StartModeledWorker(
            workEntered,
            releaseWork,
            callbackQueued,
            workObservation,
            delivery);

        await workEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(IsAlive(graph));
        Assert.True(IsAlive(changed));
        Assert.Equal(0, workObservation.CompletedSnapshotId);

        releaseWork.TrySetResult();
        await workerTask.WaitAsync(TimeSpan.FromSeconds(10));
        Action heldCallback = await callbackQueued.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SnapshotGraph.ExpectedId, workObservation.CompletedSnapshotId);
        Assert.Equal(SnapshotGraph.ExpectedChangedBlockIndex, workObservation.CompletedChangedIndex);
        Assert.Equal(SnapshotGraph.ExpectedWidth, workObservation.CompletedWidth);
        Assert.True(workObservation.SnapshotGetterRejectedAfterDrain);
        Assert.True(workObservation.ChangedGetterRejectedAfterDrain);
        Assert.True(workObservation.RepeatedReleaseCompleted);
        ImageRelayoutWorkPayload<SnapshotGraph> retainedPayload =
            workObservation.ReleasedPayload ?? throw new InvalidOperationException(
                "The drained payload was not retained for inspection.");
        Assert.Null(GetPayloadField(retainedPayload, "_snapshot"));
        Assert.Null(GetPayloadField(retainedPayload, "_changed"));

        CollectUntilDead(graph);
        CollectUntilDead(changed);
        Assert.False(IsAlive(graph));
        Assert.False(IsAlive(changed));

        heldCallback();
        Assert.Equal(1, delivery.Count);
        Assert.True(delivery.ReceivedNull);
        GC.KeepAlive(workerTask);
        GC.KeepAlive(heldCallback);
        GC.KeepAlive(retainedPayload);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<SnapshotGraph> Graph, Action Callback, DeliveryProbe Delivery)
        CreateQueuedPublication()
    {
        var graph = new SnapshotGraph();
        var weakGraph = new WeakReference<SnapshotGraph>(graph);
        var delivery = new DeliveryProbe();
        var publication = new WeakSnapshotPublication<SnapshotGraph>(graph, delivery.Deliver);
        return (weakGraph, publication.Publish, delivery);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DiscardedPublication CreateCapturedPublication()
    {
        var graph = new SnapshotGraph();
        var receiver = new CapturingReceiver(graph);
        var publication = new WeakSnapshotPublication<SnapshotGraph>(graph, receiver.Deliver);
        return new DiscardedPublication(
            new WeakReference<SnapshotGraph>(graph),
            new WeakReference<CapturingReceiver>(receiver),
            publication.Publish,
            publication.Discard,
            receiver.Observation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ThrowingPublication CreateThrowingPublication()
    {
        var graph = new SnapshotGraph();
        var failure = new InvalidOperationException("expected publication callback failure");
        var observation = new ThrowObservation();
        var receiver = new ThrowingReceiver(graph, failure, observation);
        var publication = new WeakSnapshotPublication<SnapshotGraph>(graph, receiver.Deliver);
        return new ThrowingPublication(
            new WeakReference<SnapshotGraph>(graph),
            new WeakReference<ThrowingReceiver>(receiver),
            publication.Publish,
            failure,
            observation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PayloadPublicationHarness CreatePayloadPublicationHarness()
    {
        var graph = new SnapshotGraph();
        int[] changed = [SnapshotGraph.ExpectedChangedBlockIndex];
        var payload = new ImageRelayoutWorkPayload<SnapshotGraph>(
            graph, changed, SnapshotGraph.ExpectedWidth);
        var sentinel = new PublicationLifetimeSentinel();
        var observation = new PublicationObservation();
        var receiver = new PublicationCaptureReceiver(graph, sentinel, observation);
        var publication = new WeakSnapshotPublication<SnapshotGraph>(graph, receiver.Deliver);
        return new PayloadPublicationHarness(
            payload,
            publication,
            publication.Publish,
            new WeakReference<SnapshotGraph>(graph),
            new WeakReference<int[]>(changed),
            new WeakReference<PublicationCaptureReceiver>(receiver),
            new WeakReference<PublicationLifetimeSentinel>(sentinel),
            observation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (
        WeakReference<SnapshotGraph> Graph,
        WeakReference<int[]> Changed,
        Task WorkerTask) StartModeledWorker(
        TaskCompletionSource workEntered,
        TaskCompletionSource releaseWork,
        TaskCompletionSource<Action> callbackQueued,
        WorkObservation workObservation,
        DeliveryProbe delivery)
    {
        var graph = new SnapshotGraph();
        int[] changed = [SnapshotGraph.ExpectedChangedBlockIndex];
        var weakGraph = new WeakReference<SnapshotGraph>(graph);
        var weakChanged = new WeakReference<int[]>(changed);
        var payload = new ImageRelayoutWorkPayload<SnapshotGraph>(
            graph, changed, SnapshotGraph.ExpectedWidth);
        Task workerTask = RunModeledWorkerAsync(
            payload,
            workEntered,
            releaseWork,
            callbackQueued,
            workObservation,
            delivery);
        return (weakGraph, weakChanged, workerTask);
    }

    // This models the control's worker lifetime boundary through the same
    // strong payload owner used by production: actual work borrows the graph
    // and changed indexes, then the owner is drained before the retained
    // dispatcher callback is allowed to publish. It does not execute
    // DispatcherQueue or qualify the actual WinUI control path.
    private static async Task RunModeledWorkerAsync(
        ImageRelayoutWorkPayload<SnapshotGraph> payload,
        TaskCompletionSource workEntered,
        TaskCompletionSource releaseWork,
        TaskCompletionSource<Action> callbackQueued,
        WorkObservation workObservation,
        DeliveryProbe delivery)
    {
        Func<CancellationToken, bool>? layoutWork = null;
        Task<bool>? layoutTask = null;
        WeakSnapshotPublication<SnapshotGraph>? publication = null;
        try
        {
            layoutWork = _ => RunModeledActualWork(
                payload, workEntered, releaseWork, workObservation);
            layoutTask = Task.Run(() => layoutWork!(CancellationToken.None));
            await layoutTask.ConfigureAwait(false);

            publication = CreateModeledPublication(payload, delivery);
            callbackQueued.TrySetResult(publication.Publish);
        }
        finally
        {
            DrainPayloadAndRecord(payload, workObservation);
            payload = null!;
            layoutWork = null;
            layoutTask = null;
            publication = null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RunModeledActualWork(
        ImageRelayoutWorkPayload<SnapshotGraph> payload,
        TaskCompletionSource workEntered,
        TaskCompletionSource releaseWork,
        WorkObservation workObservation)
    {
        SnapshotGraph strongSnapshotBorrow = payload.Snapshot;
        int[] strongChangedBorrow = payload.Changed;
        float strongWidthBorrow = payload.Width;
        workEntered.TrySetResult();
        releaseWork.Task.GetAwaiter().GetResult();
        workObservation.CompleteFor(
            strongSnapshotBorrow.Id,
            strongChangedBorrow[0],
            strongWidthBorrow);
        return true;
    }

    private static async Task RunModeledCtsWorkerAsync(
        ImageRelayoutWorkPayload<SnapshotGraph> payload,
        CancellationToken cancellationToken,
        TaskCompletionSource workEntered,
        TaskCompletionSource releaseWork,
        WorkObservation workObservation)
    {
        Func<CancellationToken, bool>? layoutWork = null;
        Task<bool>? layoutTask = null;
        try
        {
            layoutWork = _ => RunModeledActualWork(
                payload, workEntered, releaseWork, workObservation);
            layoutTask = Task.Run(() => layoutWork!(cancellationToken), CancellationToken.None);
            await layoutTask.ConfigureAwait(false);
        }
        finally
        {
            DrainPayloadAndRecord(payload, workObservation);
            payload = null!;
            layoutWork = null;
            layoutTask = null;
        }
    }

    private static void DrainPayloadAndRecord(
        ImageRelayoutWorkPayload<SnapshotGraph> payload,
        WorkObservation workObservation)
    {
        payload.ReleaseAfterWorkDrains();
        bool snapshotGetterRejected = false;
        try
        {
            _ = payload.Snapshot;
        }
        catch (InvalidOperationException)
        {
            snapshotGetterRejected = true;
        }

        bool changedGetterRejected = false;
        try
        {
            _ = payload.Changed;
        }
        catch (InvalidOperationException)
        {
            changedGetterRejected = true;
        }

        // A second release must be harmless and leave the owner empty.
        payload.ReleaseAfterWorkDrains();
        workObservation.RecordRelease(
            snapshotGetterRejected,
            changedGetterRejected,
            repeatedReleaseCompleted: true,
            releasedPayload: payload);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakSnapshotPublication<SnapshotGraph> CreateModeledPublication(
        ImageRelayoutWorkPayload<SnapshotGraph> payload,
        DeliveryProbe delivery) =>
        new(payload.Snapshot, delivery.Deliver);

    private static object? GetPayloadField(
        ImageRelayoutWorkPayload<SnapshotGraph> payload,
        string fieldName)
    {
        FieldInfo field = typeof(ImageRelayoutWorkPayload<SnapshotGraph>).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Payload field '{fieldName}' was not found.");
        return field.GetValue(payload);
    }

    private static object? GetPublicationField(
        WeakSnapshotPublication<SnapshotGraph> publication,
        string fieldName)
    {
        FieldInfo field = typeof(WeakSnapshotPublication<SnapshotGraph>).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Publication field '{fieldName}' was not found.");
        return field.GetValue(publication);
    }

    private static void AssertPublicationClosedAndDetached(
        ImageRelayoutWorkPayload<SnapshotGraph> payload)
    {
        FieldInfo closedMarkerField = typeof(ImageRelayoutWorkPayload<SnapshotGraph>).GetField(
            "ClosedPublication",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The closed publication marker was not found.");
        Assert.Same(closedMarkerField.GetValue(null), GetPayloadField(payload, "_publication"));
    }

    private static void AssertPayloadGraphAliasesCleared(
        ImageRelayoutWorkPayload<SnapshotGraph> payload)
    {
        Assert.Null(GetPayloadField(payload, "_snapshot"));
        Assert.Null(GetPayloadField(payload, "_changed"));
    }

    private static void CollectUntilDead<T>(WeakReference<T> reference)
        where T : class
    {
        for (int attempt = 0; attempt < 5 && IsAlive(reference); attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference)
        where T : class => reference.TryGetTarget(out _);

    private sealed class SnapshotGraph
    {
        internal const int ExpectedId = 791;
        internal const int ExpectedChangedBlockIndex = 23;
        internal const float ExpectedWidth = 640f;

        internal int Id => ExpectedId;
    }

    private sealed class DeliveryProbe
    {
        private int _count;
        private int _receivedNull;

        internal int Count => Volatile.Read(ref _count);

        internal bool ReceivedNull => Volatile.Read(ref _receivedNull) != 0;

        internal void Deliver(SnapshotGraph? snapshot)
        {
            Interlocked.Increment(ref _count);
            Volatile.Write(ref _receivedNull, snapshot is null ? 1 : 0);
        }
    }

    private sealed class CapturingReceiver(SnapshotGraph graph)
    {
        internal ThrowObservation Observation { get; } = new();

        internal void Deliver(SnapshotGraph? snapshot)
        {
            Observation.Record(ReferenceEquals(graph, snapshot));
        }
    }

    private sealed class PublicationLifetimeSentinel
    {
    }

    private sealed class PublicationCaptureReceiver(
        SnapshotGraph graph,
        PublicationLifetimeSentinel sentinel,
        PublicationObservation observation)
    {
        internal void Deliver(SnapshotGraph? snapshot)
        {
            observation.Record(ReferenceEquals(graph, snapshot));
            GC.KeepAlive(sentinel);
        }
    }

    private sealed class PublicationObservation
    {
        private int _count;
        private int _receivedSameGraph;

        internal int Count => Volatile.Read(ref _count);

        internal bool ReceivedSameGraph => Volatile.Read(ref _receivedSameGraph) != 0;

        internal void Record(bool receivedSameGraph)
        {
            Interlocked.Increment(ref _count);
            Volatile.Write(ref _receivedSameGraph, receivedSameGraph ? 1 : 0);
        }
    }

    private sealed class ThrowingReceiver(
        SnapshotGraph graph,
        InvalidOperationException failure,
        ThrowObservation observation)
    {
        internal void Deliver(SnapshotGraph? snapshot)
        {
            observation.Record(ReferenceEquals(graph, snapshot));
            throw failure;
        }
    }

    private sealed class ThrowObservation
    {
        private int _count;
        private int _receivedSameGraph;

        internal int Count => Volatile.Read(ref _count);

        internal bool ReceivedSameGraph => Volatile.Read(ref _receivedSameGraph) != 0;

        internal void Record(bool receivedSameGraph)
        {
            Interlocked.Increment(ref _count);
            Volatile.Write(ref _receivedSameGraph, receivedSameGraph ? 1 : 0);
        }
    }

    private sealed class WorkObservation
    {
        private int _completedSnapshotId;
        private int _completedChangedIndex;
        private float _completedWidth;
        private int _snapshotGetterRejectedAfterDrain;
        private int _changedGetterRejectedAfterDrain;
        private int _repeatedReleaseCompleted;
        private ImageRelayoutWorkPayload<SnapshotGraph>? _releasedPayload;

        internal int CompletedSnapshotId => Volatile.Read(ref _completedSnapshotId);

        internal int CompletedChangedIndex => Volatile.Read(ref _completedChangedIndex);

        internal float CompletedWidth => Volatile.Read(ref _completedWidth);

        internal bool SnapshotGetterRejectedAfterDrain =>
            Volatile.Read(ref _snapshotGetterRejectedAfterDrain) != 0;

        internal bool ChangedGetterRejectedAfterDrain =>
            Volatile.Read(ref _changedGetterRejectedAfterDrain) != 0;

        internal bool RepeatedReleaseCompleted => Volatile.Read(ref _repeatedReleaseCompleted) != 0;

        internal ImageRelayoutWorkPayload<SnapshotGraph>? ReleasedPayload =>
            Volatile.Read(ref _releasedPayload);

        internal void CompleteFor(int snapshotId, int changedIndex, float width)
        {
            Volatile.Write(ref _completedSnapshotId, snapshotId);
            Volatile.Write(ref _completedChangedIndex, changedIndex);
            Volatile.Write(ref _completedWidth, width);
        }

        internal void RecordRelease(
            bool snapshotGetterRejected,
            bool changedGetterRejected,
            bool repeatedReleaseCompleted,
            ImageRelayoutWorkPayload<SnapshotGraph> releasedPayload)
        {
            Volatile.Write(
                ref _snapshotGetterRejectedAfterDrain,
                snapshotGetterRejected ? 1 : 0);
            Volatile.Write(
                ref _changedGetterRejectedAfterDrain,
                changedGetterRejected ? 1 : 0);
            Volatile.Write(
                ref _repeatedReleaseCompleted,
                repeatedReleaseCompleted ? 1 : 0);
            Volatile.Write(ref _releasedPayload, releasedPayload);
        }
    }

    private sealed record DiscardedPublication(
        WeakReference<SnapshotGraph> Graph,
        WeakReference<CapturingReceiver> Receiver,
        Action Callback,
        Action Discard,
        ThrowObservation Delivery);

    private sealed record ThrowingPublication(
        WeakReference<SnapshotGraph> Graph,
        WeakReference<ThrowingReceiver> Receiver,
        Action Callback,
        InvalidOperationException Failure,
        ThrowObservation Observation);

    private sealed record PayloadPublicationHarness(
        ImageRelayoutWorkPayload<SnapshotGraph> Payload,
        WeakSnapshotPublication<SnapshotGraph> Publication,
        Action Callback,
        WeakReference<SnapshotGraph> Graph,
        WeakReference<int[]> Changed,
        WeakReference<PublicationCaptureReceiver> Receiver,
        WeakReference<PublicationLifetimeSentinel> Sentinel,
        PublicationObservation Observation);
}
