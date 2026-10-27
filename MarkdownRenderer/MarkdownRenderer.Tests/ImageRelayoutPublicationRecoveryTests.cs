using System.Reflection;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class ImageRelayoutPublicationRecoveryTests
{
    [Fact]
    public void PublicationWaitsForActualWorkDrain()
    {
        var snapshot = new SnapshotGraph(1);
        int[] changed = [17];
        var payload = new ImageRelayoutWorkPayload<SnapshotGraph>(snapshot, changed, 640f);
        var delivery = new DeliveryProbe();
        var publication = new WeakSnapshotPublication<SnapshotGraph>(snapshot, delivery.Deliver);

        Assert.True(payload.TryOwnPublication(publication));
        payload.PublishAfterWorkDrains();

        Assert.Equal(0, delivery.Count);
        Assert.Same(snapshot, payload.Snapshot);
        Assert.Same(changed, payload.Changed);

        payload.ReleaseAfterWorkDrains();
        payload.PublishAfterWorkDrains();

        Assert.Equal(1, delivery.Count);
        Assert.Same(snapshot, delivery.ReceivedSnapshot);
    }

    [Fact]
    public void RejectedDispatchCanRecoverThroughTheSameReadyPublicationMethod()
    {
        var snapshot = new SnapshotGraph(2);
        var payload = new ImageRelayoutWorkPayload<SnapshotGraph>(snapshot, [18], 640f);
        var delivery = new DeliveryProbe();
        var publication = new WeakSnapshotPublication<SnapshotGraph>(snapshot, delivery.Deliver);
        Assert.True(payload.TryOwnPublication(publication));

        // Model a dispatcher rejection after actual worker work has drained.
        payload.ReleaseAfterWorkDrains();
        payload.PublishAfterWorkDrains();

        Assert.Equal(1, delivery.Count);
        Assert.Same(snapshot, delivery.ReceivedSnapshot);
    }

    [Fact]
    public void WithheldAcceptedCallbackCanBeRecoveredAndLaterCallbackIsOneShot()
    {
        var snapshot = new SnapshotGraph(3);
        var payload = new ImageRelayoutWorkPayload<SnapshotGraph>(snapshot, [19], 640f);
        var delivery = new DeliveryProbe();
        var publication = new WeakSnapshotPublication<SnapshotGraph>(snapshot, delivery.Deliver);
        Assert.True(payload.TryOwnPublication(publication));
        Action delayedCallback = payload.PublishAfterWorkDrains;

        payload.ReleaseAfterWorkDrains();
        payload.PublishAfterWorkDrains();
        delayedCallback();
        payload.PublishAfterWorkDrains();

        Assert.Equal(1, delivery.Count);
        Assert.Same(snapshot, delivery.ReceivedSnapshot);
    }

    [Fact]
    public void ReentrantPublicationAndDuplicatesDeliverOnlyOnce()
    {
        var snapshot = new SnapshotGraph(4);
        var payload = new ImageRelayoutWorkPayload<SnapshotGraph>(snapshot, [20], 640f);
        int deliveries = 0;
        WeakSnapshotPublication<SnapshotGraph>? publication = null;
        publication = new WeakSnapshotPublication<SnapshotGraph>(snapshot, _ =>
        {
            Interlocked.Increment(ref deliveries);
            payload.PublishAfterWorkDrains();
            publication!.Publish();
        });
        Assert.True(payload.TryOwnPublication(publication));

        payload.ReleaseAfterWorkDrains();
        payload.PublishAfterWorkDrains();
        payload.PublishAfterWorkDrains();

        Assert.Equal(1, Volatile.Read(ref deliveries));
    }

    [Fact]
    public void ThrowingPublicationConsumesDelivery()
    {
        var snapshot = new SnapshotGraph(5);
        var payload = new ImageRelayoutWorkPayload<SnapshotGraph>(snapshot, [21], 640f);
        int deliveries = 0;
        var failure = new InvalidOperationException("Injected publication failure.");
        var publication = new WeakSnapshotPublication<SnapshotGraph>(snapshot, _ =>
        {
            Interlocked.Increment(ref deliveries);
            throw failure;
        });
        Assert.True(payload.TryOwnPublication(publication));
        payload.ReleaseAfterWorkDrains();

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            payload.PublishAfterWorkDrains);
        Assert.Same(failure, thrown);
        payload.PublishAfterWorkDrains();

        Assert.Equal(1, Volatile.Read(ref deliveries));
    }

    [Fact]
    public void CloseBeforeDrainAndLateRegistrationKeepWorkAliasesUntilDrain()
    {
        var snapshot = new SnapshotGraph(6);
        int[] changed = [22];
        var payload = new ImageRelayoutWorkPayload<SnapshotGraph>(snapshot, changed, 640f);
        var delivery = new DeliveryProbe();
        var publication = new WeakSnapshotPublication<SnapshotGraph>(snapshot, delivery.Deliver);

        payload.DiscardPublication();
        Assert.False(payload.TryOwnPublication(publication));
        payload.PublishAfterWorkDrains();
        Assert.Equal(0, delivery.Count);
        Assert.Same(snapshot, payload.Snapshot);
        Assert.Same(changed, payload.Changed);

        payload.ReleaseAfterWorkDrains();
        payload.PublishAfterWorkDrains();

        Assert.Equal(0, delivery.Count);
        Assert.Null(GetPayloadField(payload, "_snapshot"));
        Assert.Null(GetPayloadField(payload, "_changed"));
    }

    [Fact]
    public void ClosingRegisteredPublicationBeforeDrainDoesNotReleaseWorkerBorrowEarly()
    {
        var snapshot = new SnapshotGraph(7);
        int[] changed = [23];
        var payload = new ImageRelayoutWorkPayload<SnapshotGraph>(snapshot, changed, 640f);
        var delivery = new DeliveryProbe();
        var publication = new WeakSnapshotPublication<SnapshotGraph>(snapshot, delivery.Deliver);
        Assert.True(payload.TryOwnPublication(publication));

        payload.DiscardPublication();
        payload.PublishAfterWorkDrains();
        Assert.Equal(0, delivery.Count);
        Assert.Same(snapshot, payload.Snapshot);
        Assert.Same(changed, payload.Changed);

        payload.ReleaseAfterWorkDrains();
        payload.PublishAfterWorkDrains();

        Assert.Equal(0, delivery.Count);
        Assert.Null(GetPayloadField(payload, "_snapshot"));
        Assert.Null(GetPayloadField(payload, "_changed"));
    }

    [Fact]
    public void ClosingAnOldOwnerCannotAffectANewOwnersPublication()
    {
        var oldSnapshot = new SnapshotGraph(8);
        var newSnapshot = new SnapshotGraph(9);
        var oldPayload = new ImageRelayoutWorkPayload<SnapshotGraph>(oldSnapshot, [24], 640f);
        var newPayload = new ImageRelayoutWorkPayload<SnapshotGraph>(newSnapshot, [25], 640f);
        var oldDelivery = new DeliveryProbe();
        var newDelivery = new DeliveryProbe();
        var oldPublication = new WeakSnapshotPublication<SnapshotGraph>(
            oldSnapshot, oldDelivery.Deliver);
        var newPublication = new WeakSnapshotPublication<SnapshotGraph>(
            newSnapshot, newDelivery.Deliver);
        Assert.True(oldPayload.TryOwnPublication(oldPublication));
        Assert.True(newPayload.TryOwnPublication(newPublication));

        oldPayload.DiscardPublication();
        oldPayload.ReleaseAfterWorkDrains();
        oldPayload.PublishAfterWorkDrains();
        newPayload.PublishAfterWorkDrains();
        Assert.Equal(0, oldDelivery.Count);
        Assert.Equal(0, newDelivery.Count);

        newPayload.ReleaseAfterWorkDrains();
        newPayload.PublishAfterWorkDrains();

        Assert.Equal(0, oldDelivery.Count);
        Assert.Equal(1, newDelivery.Count);
        Assert.Same(newSnapshot, newDelivery.ReceivedSnapshot);
    }

    [Fact]
    public async Task ConcurrentCloseDrainAndPublishDeliverAtMostOnce()
    {
        for (int iteration = 0; iteration < 24; iteration++)
        {
            var snapshot = new SnapshotGraph(iteration + 10);
            var payload = new ImageRelayoutWorkPayload<SnapshotGraph>(snapshot, [26], 640f);
            int deliveries = 0;
            var publication = new WeakSnapshotPublication<SnapshotGraph>(
                snapshot,
                _ => Interlocked.Increment(ref deliveries));
            Assert.True(payload.TryOwnPublication(publication));
            using var start = new ManualResetEventSlim();

            Task close = Task.Run(() =>
            {
                start.Wait();
                payload.DiscardPublication();
            });
            Task drain = Task.Run(() =>
            {
                start.Wait();
                payload.ReleaseAfterWorkDrains();
            });
            Task publish = Task.Run(() =>
            {
                start.Wait();
                payload.PublishAfterWorkDrains();
            });

            start.Set();
            await Task.WhenAll(close, drain, publish).WaitAsync(TimeSpan.FromSeconds(10));
            payload.PublishAfterWorkDrains();

            Assert.InRange(Volatile.Read(ref deliveries), 0, 1);
            Assert.Null(GetPayloadField(payload, "_snapshot"));
            Assert.Null(GetPayloadField(payload, "_changed"));
        }
    }

    [Fact]
    public void RetainedDrainedOwnerDoesNotKeepSnapshotOrChangedIndexesAlive()
    {
        DrainedPayload retained = CreateDrainedPayload();

        Assert.Null(GetPayloadField(retained.Payload, "_snapshot"));
        Assert.Null(GetPayloadField(retained.Payload, "_changed"));
        CollectUntilDead(retained.Snapshot);
        CollectUntilDead(retained.Changed);

        Assert.False(IsAlive(retained.Snapshot));
        Assert.False(IsAlive(retained.Changed));
        retained.Payload.PublishAfterWorkDrains();
        Assert.Equal(1, retained.Delivery.Count);
        Assert.True(retained.Delivery.ReceivedNull);
        GC.KeepAlive(retained.Publication);
        GC.KeepAlive(retained.Payload);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DrainedPayload CreateDrainedPayload()
    {
        var snapshot = new SnapshotGraph(100);
        int[] changed = [27];
        var payload = new ImageRelayoutWorkPayload<SnapshotGraph>(snapshot, changed, 640f);
        var delivery = new DeliveryProbe();
        var publication = new WeakSnapshotPublication<SnapshotGraph>(snapshot, delivery.Deliver);
        if (!payload.TryOwnPublication(publication))
            throw new InvalidOperationException("The test publication was unexpectedly rejected.");
        payload.ReleaseAfterWorkDrains();

        return new DrainedPayload(
            payload,
            publication,
            delivery,
            new WeakReference<SnapshotGraph>(snapshot),
            new WeakReference<int[]>(changed));
    }

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

    private sealed class SnapshotGraph(int id)
    {
        internal int Id { get; } = id;
    }

    private sealed class DeliveryProbe
    {
        private int _count;
        private int _receivedNull;
        private SnapshotGraph? _receivedSnapshot;

        internal int Count => Volatile.Read(ref _count);

        internal bool ReceivedNull => Volatile.Read(ref _receivedNull) != 0;

        internal SnapshotGraph? ReceivedSnapshot => Volatile.Read(ref _receivedSnapshot);

        internal void Deliver(SnapshotGraph? snapshot)
        {
            Interlocked.Increment(ref _count);
            Volatile.Write(ref _receivedNull, snapshot is null ? 1 : 0);
            Volatile.Write(ref _receivedSnapshot, snapshot);
        }
    }

    private sealed record DrainedPayload(
        ImageRelayoutWorkPayload<SnapshotGraph> Payload,
        WeakSnapshotPublication<SnapshotGraph> Publication,
        DeliveryProbe Delivery,
        WeakReference<SnapshotGraph> Snapshot,
        WeakReference<int[]> Changed);
}
