using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownImageResolutionLeaseTests
{
    [Fact]
    public void RetainedHandleKeepsBorrowedResolutionAliveUntilLastDispose()
    {
        byte[] bytes = [1, 2, 3];
        var asset = new MarkdownImageAsset(bytes, "image/png");
        var reservation = new CountingReservation();
        var lease = new MarkdownImageResolutionLease(MarkdownImageResolution.Resolved(asset), reservation);
        MarkdownImageResolutionLease retained = lease.Retain();

        Assert.Same(bytes, lease.Resolution.Asset!.Bytes);
        Assert.Same(asset, retained.Resolution.Asset);

        lease.Dispose();
        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = lease.Resolution);
        Assert.Throws<ObjectDisposedException>(() => lease.Retain());
        Assert.Same(bytes, retained.Resolution.Asset!.Bytes);
        Assert.Equal(0, reservation.DisposeCount);

        retained.Dispose();
        retained.Dispose();

        Assert.Equal(1, reservation.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => _ = retained.Resolution);
    }

    [Fact]
    public void InternalLeaseCapabilityUsesIndependentRetainedHandles()
    {
        byte[] bytes = [11, 12];
        var reservation = new CountingReservation();
        var carrier = new MarkdownImageResolutionLease(
            MarkdownImageResolution.Resolved(new MarkdownImageAsset(bytes)),
            reservation);
        IMarkdownImageResolutionLease capability = carrier;

        IMarkdownImageResolutionLease retained = capability.Retain();
        capability.Dispose();

        Assert.Same(bytes, retained.Resolution.Asset!.Bytes);
        Assert.Equal(0, reservation.DisposeCount);

        retained.Dispose();

        Assert.Equal(1, reservation.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => _ = retained.Resolution);
    }

    [Fact]
    public void ParallelRetainAndDisposeRacesDoNotResurrectTheOwner()
    {
        byte[] bytes = [4, 5, 6];
        var reservation = new CountingReservation();
        var root = new MarkdownImageResolutionLease(
            MarkdownImageResolution.Resolved(new MarkdownImageAsset(bytes)),
            reservation);
        MarkdownImageResolutionLease keeper = root.Retain();
        var retained = new ConcurrentBag<MarkdownImageResolutionLease>();

        Parallel.Invoke(
            root.Dispose,
            () => Parallel.For(0, 16, _ =>
            {
                for (int attempt = 0; attempt < 64; attempt++)
                {
                    try
                    {
                        retained.Add(root.Retain());
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            }));

        Assert.Throws<ObjectDisposedException>(() => root.Retain());
        Assert.Same(bytes, keeper.Resolution.Asset!.Bytes);
        Assert.Equal(0, reservation.DisposeCount);
        foreach (MarkdownImageResolutionLease handle in retained)
        {
            Assert.Same(bytes, handle.Resolution.Asset!.Bytes);
        }

        Parallel.ForEach(retained, handle => handle.Dispose());
        Assert.Equal(0, reservation.DisposeCount);

        keeper.Dispose();
        Assert.Equal(1, reservation.DisposeCount);
        keeper.Dispose();
        Assert.Equal(1, reservation.DisposeCount);
    }

    [Fact]
    public void ConstructionFailureLeavesReservationWithCaller()
    {
        var reservation = new CountingReservation();
        MarkdownImageResolution resolution = MarkdownImageResolution.Resolved(
            new MarkdownImageAsset([7]));

        Assert.Throws<ArgumentException>(() => new MarkdownImageResolutionLease(resolution, null));
        Assert.Equal(0, reservation.DisposeCount);

        Assert.Throws<ArgumentException>(() => new MarkdownImageResolutionLease(
            MarkdownImageResolution.Resolved(new MarkdownImageAsset(null!)),
            reservation));

        Assert.Equal(0, reservation.DisposeCount);
        reservation.Dispose();
        Assert.Equal(1, reservation.DisposeCount);
    }

    [Fact]
    public void NoAssetResolutionsCanBeLeasedWithoutReservation()
    {
        MarkdownImageResolution[] resolutions =
        [
            MarkdownImageResolution.NotHandled,
            MarkdownImageResolution.Unavailable,
            MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Offline),
        ];

        foreach (MarkdownImageResolution resolution in resolutions)
        {
            using var lease = new MarkdownImageResolutionLease(resolution, null);
            using MarkdownImageResolutionLease retained = lease.Retain();

            Assert.Equal(resolution, lease.Resolution);
            Assert.Equal(resolution, retained.Resolution);
        }
    }

    [Fact]
    public void EmptyAssetCanBeLeasedWithoutReservation()
    {
        MarkdownImageResolution resolution = MarkdownImageResolution.Resolved(
            new MarkdownImageAsset([]));

        using var lease = new MarkdownImageResolutionLease(resolution, null);

        Assert.Empty(lease.Resolution.Asset!.Bytes);
    }

    [Fact]
    public void InternalUnreservedCompatibilityFactoryPreservesLegacyAssetMetadata()
    {
        byte[] bytes = [8, 9, 10];
        var resolvedUri = new Uri("https://images.example.test/avatar.png");
        var asset = new MarkdownImageAsset(bytes, "image/png", resolvedUri, "account/repo/avatar");
        MarkdownImageResolution resolution = MarkdownImageResolution.Resolved(asset);

        using MarkdownImageResolutionLease lease = MarkdownImageResolutionLease.CreateUnreserved(resolution);

        Assert.Equal(resolution, lease.Resolution);
        Assert.Same(bytes, lease.Resolution.Asset!.Bytes);
        Assert.Equal("image/png", lease.Resolution.Asset.ContentType);
        Assert.Equal(resolvedUri, lease.Resolution.Asset.ResolvedUri);
        Assert.Equal("account/repo/avatar", lease.Resolution.Asset.CacheKey);
    }

    [Fact]
    public void InternalUnreservedCompatibilityFactoryClearsBytesAfterFinalDispose()
    {
        (MarkdownImageResolutionLease[] handles, WeakReference<byte[]> bytes) =
            CreateUnreservedHandles();

        foreach (MarkdownImageResolutionLease handle in handles)
        {
            handle.Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(bytes.TryGetTarget(out _));
        foreach (MarkdownImageResolutionLease handle in handles)
        {
            Assert.Throws<ObjectDisposedException>(() => _ = handle.Resolution);
        }

        GC.KeepAlive(handles);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (MarkdownImageResolutionLease[] Handles, WeakReference<byte[]> Bytes)
        CreateUnreservedHandles()
    {
        byte[] sourceBytes = new byte[1024 * 32];
        var bytes = new WeakReference<byte[]>(sourceBytes);
        MarkdownImageResolutionLease lease = MarkdownImageResolutionLease.CreateUnreserved(
            MarkdownImageResolution.Resolved(new MarkdownImageAsset(
                sourceBytes,
                "image/png",
                CacheKey: "legacy-cache-key")));
        MarkdownImageResolutionLease[] handles =
        [
            lease,
            lease.Retain(),
            lease.Retain(),
        ];
        return (handles, bytes);
    }

    [Fact]
    public void FinalDisposeClearsBufferBeforeReturningQuotaAndRemainsDisposedIfQuotaThrows()
    {
        (MarkdownImageResolutionLease[] handles, WeakReference<byte[]> bytes, ThrowingReservation reservation) =
            CreateThrowingLeaseWithLiveWrappers();

        for (int index = 0; index < handles.Length - 1; index++)
        {
            handles[index].Dispose();
        }

        Assert.Equal(0, reservation.DisposeCount);
        Assert.Throws<InvalidOperationException>(() => handles[^1].Dispose());

        Assert.Equal(1, reservation.DisposeCount);
        Assert.True(reservation.BufferWasCollectedBeforeReturn);
        Assert.False(bytes.TryGetTarget(out _));

        foreach (MarkdownImageResolutionLease handle in handles)
        {
            handle.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = handle.Resolution);
        }

        Assert.Equal(1, reservation.DisposeCount);
        GC.KeepAlive(handles);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (
        MarkdownImageResolutionLease[] Handles,
        WeakReference<byte[]> Bytes,
        ThrowingReservation Reservation) CreateThrowingLeaseWithLiveWrappers()
    {
        byte[] sourceBytes = new byte[1024 * 64];
        var bytes = new WeakReference<byte[]>(sourceBytes);
        var reservation = new ThrowingReservation(bytes);

        var lease = new MarkdownImageResolutionLease(
            MarkdownImageResolution.Resolved(new MarkdownImageAsset(sourceBytes)),
            reservation);
        MarkdownImageResolutionLease[] handles =
        [
            lease,
            lease.Retain(),
            lease.Retain(),
        ];
        return (handles, bytes, reservation);
    }

    private class CountingReservation : IDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public virtual void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class ThrowingReservation(WeakReference<byte[]> bytes) : CountingReservation
    {
        public bool BufferWasCollectedBeforeReturn { get; set; }

        public override void Dispose()
        {
            base.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            BufferWasCollectedBeforeReturn = !bytes.TryGetTarget(out _);
            throw new InvalidOperationException("Injected reservation failure.");
        }
    }
}
