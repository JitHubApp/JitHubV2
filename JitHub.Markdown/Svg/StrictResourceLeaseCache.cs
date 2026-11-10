using System;
using System.Collections.Generic;
using JitHub.Markdown.Svg.Internal;

namespace JitHub.Markdown.Svg;

/// <summary>
/// A thread-safe LRU whose reservations, cached resources, and active leases
/// share one strict byte budget. Resources removed while leased remain charged
/// until their final lease is released.
/// </summary>
public sealed partial class StrictResourceLeaseCache<TKey, TResource> : IDisposable
    where TKey : notnull
    where TResource : class, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>>> _entries = [];
    private readonly LinkedList<StrictResourceLeaseEntry<TKey, TResource>> _lru = [];
    private readonly long _maximumBytes;
    private long _residentBytes;
    private long _reservedBytes;
    private bool _disposed;

    public StrictResourceLeaseCache(long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _maximumBytes = maximumBytes;
    }

    public long MaximumBytes => _maximumBytes;

    public long AccountedBytes
    {
        get
        {
            lock (_gate)
            {
                return checked(_residentBytes + _reservedBytes);
            }
        }
    }

    public long ResidentBytes
    {
        get
        {
            lock (_gate)
            {
                return _residentBytes;
            }
        }
    }

    public long ReservedBytes
    {
        get
        {
            lock (_gate)
            {
                return _reservedBytes;
            }
        }
    }

    public int CachedResourceCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryAcquire(TKey key, out StrictResourceLease<TKey, TResource>? lease)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(key, out LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>>? node))
            {
                lease = null;
                return false;
            }

            TouchLocked(node);
            node.Value.LeaseCount++;
            lease = new StrictResourceLease<TKey, TResource>(this, node.Value);
            return true;
        }
    }

    public bool TryReserve(long byteCount, out StrictResourceReservation<TKey, TResource>? reservation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        lock (_gate)
        {
            if (_disposed || byteCount > _maximumBytes)
            {
                reservation = null;
                return false;
            }

            while (checked(_residentBytes + _reservedBytes + byteCount) > _maximumBytes)
            {
                LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>>? candidate = FindOldestUnleasedLocked();
                if (candidate is null)
                {
                    reservation = null;
                    return false;
                }

                RemoveCachedEntryLocked(candidate);
            }

            _reservedBytes = checked(_reservedBytes + byteCount);
            reservation = new StrictResourceReservation<TKey, TResource>(this, byteCount);
            return true;
        }
    }

    public StrictResourceLease<TKey, TResource> StoreAndAcquire(
        TKey key,
        TResource resource,
        long byteCount,
        StrictResourceReservation<TKey, TResource> reservation)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateReservationLocked(reservation, byteCount);

            if (_entries.TryGetValue(key, out LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>>? existing))
            {
                // The reservation continues to cover the duplicate allocation
                // until it has actually been released.
                TryDispose(resource);
                ConsumeReservationLocked(reservation, byteCount);
                TouchLocked(existing);
                existing.Value.LeaseCount++;
                return new StrictResourceLease<TKey, TResource>(this, existing.Value);
            }

            StrictResourceLeaseEntry<TKey, TResource> entry = new(key, resource, byteCount) { LeaseCount = 1 };
            LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>>? node = null;
            try
            {
                node = _lru.AddLast(entry);
                entry.Node = node;
                _entries.Add(key, node);
            }
            catch
            {
                if (node?.List is not null)
                {
                    _lru.Remove(node);
                }

                entry.Node = null;
                // Ownership transfers to the cache at method entry. If the LRU
                // or dictionary publication fails, do not strand the newly
                // uploaded native resource while the caller unwinds and releases
                // its still-valid reservation.
                TryDispose(resource);
                throw;
            }

            ConsumeReservationLocked(reservation, byteCount);
            _residentBytes = checked(_residentBytes + byteCount);
            return new StrictResourceLease<TKey, TResource>(this, entry);
        }
    }

    public void Trim()
    {
        lock (_gate)
        {
            TrimLocked();
        }
    }

    public void RemoveWhere(Predicate<TKey> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_gate)
        {
            LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>>? node = _lru.First;
            while (node is not null)
            {
                LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>>? next = node.Next;
                if (predicate(node.Value.Key))
                {
                    RemoveCachedEntryLocked(node);
                }

                node = next;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            TrimLocked();
        }
    }

    internal void Release(StrictResourceLeaseEntry<TKey, TResource> entry)
    {
        lock (_gate)
        {
            if (entry.LeaseCount <= 0)
            {
                return;
            }

            entry.LeaseCount--;
            if (entry.LeaseCount == 0 && !entry.IsCached)
            {
                DisposeEntryLocked(entry);
            }
        }
    }

    internal void ReleaseReservation(StrictResourceReservation<TKey, TResource> reservation)
    {
        lock (_gate)
        {
            if (reservation.IsDisposed || !ReferenceEquals(reservation.Owner, this))
            {
                return;
            }

            _reservedBytes -= reservation.RemainingBytes;
            reservation.RemainingBytes = 0;
            reservation.IsDisposed = true;
        }
    }

    private void ValidateReservationLocked(StrictResourceReservation<TKey, TResource> reservation, long byteCount)
    {
        if (!ReferenceEquals(reservation.Owner, this) ||
            reservation.IsDisposed ||
            byteCount > reservation.RemainingBytes)
        {
            throw new InvalidOperationException("The resource is not covered by this cache reservation.");
        }
    }

    private void ConsumeReservationLocked(StrictResourceReservation<TKey, TResource> reservation, long byteCount)
    {
        reservation.RemainingBytes -= byteCount;
        _reservedBytes -= byteCount;
    }

    private LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>>? FindOldestUnleasedLocked()
    {
        LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>>? node = _lru.First;
        while (node is not null && node.Value.LeaseCount != 0)
        {
            node = node.Next;
        }

        return node;
    }

    private void TouchLocked(LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>> node)
    {
        _lru.Remove(node);
        _lru.AddLast(node);
    }

    private void TrimLocked()
    {
        LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>>? node = _lru.First;
        while (node is not null)
        {
            LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>>? next = node.Next;
            RemoveCachedEntryLocked(node);
            node = next;
        }
    }

    private void RemoveCachedEntryLocked(LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>> node)
    {
        StrictResourceLeaseEntry<TKey, TResource> entry = node.Value;
        _lru.Remove(node);
        _entries.Remove(entry.Key);
        entry.Node = null;
        entry.IsCached = false;
        if (entry.LeaseCount == 0)
        {
            DisposeEntryLocked(entry);
        }
    }

    private void DisposeEntryLocked(StrictResourceLeaseEntry<TKey, TResource> entry)
    {
        if (entry.IsDisposed)
        {
            return;
        }

        entry.IsDisposed = true;
        TryDispose(entry.Resource);
        _residentBytes -= entry.ByteCount;
    }

    private static void TryDispose(IDisposable resource)
    {
        try
        {
            resource.Dispose();
        }
        catch
        {
            // Cache trimming and device-loss cleanup are best effort. The
            // accounting entry must still retire if a native wrapper faults.
        }
    }
}
