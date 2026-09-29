using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class StrictResourceLeaseCacheTests
{
    [Fact]
    public void ActiveTransitionLease_RemainsChargedUntilReleased()
    {
        using StrictResourceLeaseCache<string, TrackingResource> cache = new(10);
        Assert.True(cache.TryReserve(6, out var firstReservation));
        TrackingResource first = new();
        using StrictResourceLeaseCache<string, TrackingResource>.Lease firstLease =
            cache.StoreAndAcquire("first", first, 6, firstReservation!);
        firstReservation!.Dispose();

        Assert.Equal(6, cache.AccountedBytes);
        Assert.False(cache.TryReserve(5, out _));
        Assert.False(first.IsDisposed);

        firstLease.Dispose();
        Assert.True(cache.TryReserve(5, out var replacementReservation));
        Assert.True(first.IsDisposed);
        Assert.Equal(5, cache.AccountedBytes);

        replacementReservation!.Dispose();
        Assert.Equal(0, cache.AccountedBytes);
    }

    [Fact]
    public void Trim_DropsLookupButKeepsPublishedLeaseInAggregateBudget()
    {
        using StrictResourceLeaseCache<string, TrackingResource> cache = new(10);
        Assert.True(cache.TryReserve(8, out var reservation));
        TrackingResource resource = new();
        using StrictResourceLeaseCache<string, TrackingResource>.Lease lease =
            cache.StoreAndAcquire("published", resource, 8, reservation!);
        reservation!.Dispose();

        cache.Trim();

        Assert.Equal(0, cache.CachedResourceCount);
        Assert.Equal(8, cache.ResidentBytes);
        Assert.False(resource.IsDisposed);
        Assert.False(cache.TryAcquire("published", out _));
        Assert.False(cache.TryReserve(3, out _));

        lease.Dispose();
        Assert.True(resource.IsDisposed);
        Assert.Equal(0, cache.ResidentBytes);
    }

    [Fact]
    public void BatchReservation_AtomicallyBecomesResidentResources()
    {
        using StrictResourceLeaseCache<string, TrackingResource> cache = new(12);
        Assert.True(cache.TryReserve(12, out var reservation));
        using StrictResourceLeaseCache<string, TrackingResource>.Lease first =
            cache.StoreAndAcquire("a", new TrackingResource(), 5, reservation!);

        Assert.Equal(5, cache.ResidentBytes);
        Assert.Equal(7, cache.ReservedBytes);
        Assert.Equal(12, cache.AccountedBytes);

        using StrictResourceLeaseCache<string, TrackingResource>.Lease second =
            cache.StoreAndAcquire("b", new TrackingResource(), 7, reservation!);
        Assert.Equal(12, cache.ResidentBytes);
        Assert.Equal(0, cache.ReservedBytes);
        Assert.Equal(cache.MaximumBytes, cache.AccountedBytes);
    }

    [Fact]
    public void DuplicateStore_DisposesUploadAndLeasesExistingResource()
    {
        using StrictResourceLeaseCache<string, TrackingResource> cache = new(10);
        Assert.True(cache.TryReserve(4, out var firstReservation));
        TrackingResource cached = new();
        using StrictResourceLeaseCache<string, TrackingResource>.Lease first =
            cache.StoreAndAcquire("same", cached, 4, firstReservation!);
        firstReservation!.Dispose();

        Assert.True(cache.TryReserve(4, out var duplicateReservation));
        TrackingResource duplicate = new();
        using StrictResourceLeaseCache<string, TrackingResource>.Lease second =
            cache.StoreAndAcquire("same", duplicate, 4, duplicateReservation!);
        duplicateReservation!.Dispose();

        Assert.True(duplicate.IsDisposed);
        Assert.False(cached.IsDisposed);
        Assert.Same(cached, second.Resource);
        Assert.Equal(4, cache.AccountedBytes);
    }

    [Fact]
    public void RemoveWhere_InvalidatesOnePartitionAndPreservesOtherEntries()
    {
        using StrictResourceLeaseCache<string, TrackingResource> cache = new(10);
        Assert.True(cache.TryReserve(4, out var firstReservation));
        TrackingResource firstResource = new();
        using StrictResourceLeaseCache<string, TrackingResource>.Lease firstLease =
            cache.StoreAndAcquire("device-a/tile", firstResource, 4, firstReservation!);
        firstReservation!.Dispose();
        Assert.True(cache.TryReserve(4, out var secondReservation));
        TrackingResource secondResource = new();
        using StrictResourceLeaseCache<string, TrackingResource>.Lease secondLease =
            cache.StoreAndAcquire("device-b/tile", secondResource, 4, secondReservation!);
        secondReservation!.Dispose();

        cache.RemoveWhere(key => key.StartsWith("device-a/", StringComparison.Ordinal));

        Assert.False(cache.TryAcquire("device-a/tile", out _));
        Assert.True(cache.TryAcquire("device-b/tile", out var retained));
        retained!.Dispose();
        Assert.False(firstResource.IsDisposed);
        Assert.False(secondResource.IsDisposed);

        firstLease.Dispose();
        Assert.True(firstResource.IsDisposed);
        Assert.Equal(4, cache.ResidentBytes);
    }

    [Fact]
    public void StorePublicationFailure_DisposesTransferredResourceAndPreservesReservation()
    {
        using StrictResourceLeaseCache<ThrowingHashKey, TrackingResource> cache = new(10);
        Assert.True(cache.TryReserve(4, out var reservation));
        TrackingResource resource = new();
        var key = new ThrowingHashKey();

        Assert.Throws<InvalidOperationException>(() =>
            cache.StoreAndAcquire(key, resource, 4, reservation!));

        Assert.True(resource.IsDisposed);
        Assert.Equal(4, cache.ReservedBytes);
        Assert.Equal(0, cache.ResidentBytes);
        reservation!.Dispose();
        Assert.Equal(0, cache.AccountedBytes);
    }

    private sealed class ThrowingHashKey
    {
        public override int GetHashCode() =>
            throw new InvalidOperationException("Injected dictionary publication failure.");
    }

    private sealed class TrackingResource : IDisposable
    {
        internal bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
