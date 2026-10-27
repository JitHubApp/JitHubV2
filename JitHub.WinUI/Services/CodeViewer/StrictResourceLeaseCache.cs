using System;
using System.Collections.Generic;

namespace JitHub.Services.CodeViewer;

/// <summary>
/// A thread-safe LRU whose reservations, cached resources, and active leases
/// share one strict byte budget. Resources removed while leased remain charged
/// until their final lease is released.
/// </summary>
internal sealed partial class StrictResourceLeaseCache<TKey, TResource> : IDisposable
    where TKey : notnull
    where TResource : class, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = [];
    private readonly long _maximumBytes;
    private long _residentBytes;
    private long _reservedBytes;
    private bool _disposed;

    internal StrictResourceLeaseCache(long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _maximumBytes = maximumBytes;
    }

    internal long MaximumBytes => _maximumBytes;

    internal long AccountedBytes
    {
        get
        {
            lock (_gate)
            {
                return checked(_residentBytes + _reservedBytes);
            }
        }
    }

    internal long ResidentBytes
    {
        get
        {
            lock (_gate)
            {
                return _residentBytes;
            }
        }
    }

    internal long ReservedBytes
    {
        get
        {
            lock (_gate)
            {
                return _reservedBytes;
            }
        }
    }

    internal int CachedResourceCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal bool TryAcquire(TKey key, out Lease? lease)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                lease = null;
                return false;
            }

            TouchLocked(node);
            node.Value.LeaseCount++;
            lease = new Lease(this, node.Value);
            return true;
        }
    }

    internal bool TryReserve(long byteCount, out Reservation? reservation)
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
                LinkedListNode<Entry>? candidate = FindOldestUnleasedLocked();
                if (candidate is null)
                {
                    reservation = null;
                    return false;
                }

                RemoveCachedEntryLocked(candidate);
            }

            _reservedBytes = checked(_reservedBytes + byteCount);
            reservation = new Reservation(this, byteCount);
            return true;
        }
    }

    internal Lease StoreAndAcquire(
        TKey key,
        TResource resource,
        long byteCount,
        Reservation reservation)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateReservationLocked(reservation, byteCount);

            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                // The reservation continues to cover the duplicate allocation
                // until it has actually been released.
                TryDispose(resource);
                ConsumeReservationLocked(reservation, byteCount);
                TouchLocked(existing);
                existing.Value.LeaseCount++;
                return new Lease(this, existing.Value);
            }

            Entry entry = new(key, resource, byteCount) { LeaseCount = 1 };
            LinkedListNode<Entry>? node = null;
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
            return new Lease(this, entry);
        }
    }

    internal void Trim()
    {
        lock (_gate)
        {
            TrimLocked();
        }
    }

    internal void RemoveWhere(Predicate<TKey> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_gate)
        {
            LinkedListNode<Entry>? node = _lru.First;
            while (node is not null)
            {
                LinkedListNode<Entry>? next = node.Next;
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

    private void Release(Entry entry)
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

    private void ReleaseReservation(Reservation reservation)
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

    private void ValidateReservationLocked(Reservation reservation, long byteCount)
    {
        if (!ReferenceEquals(reservation.Owner, this) ||
            reservation.IsDisposed ||
            byteCount > reservation.RemainingBytes)
        {
            throw new InvalidOperationException("The resource is not covered by this cache reservation.");
        }
    }

    private void ConsumeReservationLocked(Reservation reservation, long byteCount)
    {
        reservation.RemainingBytes -= byteCount;
        _reservedBytes -= byteCount;
    }

    private LinkedListNode<Entry>? FindOldestUnleasedLocked()
    {
        LinkedListNode<Entry>? node = _lru.First;
        while (node is not null && node.Value.LeaseCount != 0)
        {
            node = node.Next;
        }

        return node;
    }

    private void TouchLocked(LinkedListNode<Entry> node)
    {
        _lru.Remove(node);
        _lru.AddLast(node);
    }

    private void TrimLocked()
    {
        LinkedListNode<Entry>? node = _lru.First;
        while (node is not null)
        {
            LinkedListNode<Entry>? next = node.Next;
            RemoveCachedEntryLocked(node);
            node = next;
        }
    }

    private void RemoveCachedEntryLocked(LinkedListNode<Entry> node)
    {
        Entry entry = node.Value;
        _lru.Remove(node);
        _entries.Remove(entry.Key);
        entry.Node = null;
        entry.IsCached = false;
        if (entry.LeaseCount == 0)
        {
            DisposeEntryLocked(entry);
        }
    }

    private void DisposeEntryLocked(Entry entry)
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

    internal sealed class Entry(TKey key, TResource resource, long byteCount)
    {
        internal TKey Key { get; } = key;

        internal TResource Resource { get; } = resource;

        internal long ByteCount { get; } = byteCount;

        internal LinkedListNode<Entry>? Node { get; set; }

        internal int LeaseCount { get; set; }

        internal bool IsCached { get; set; } = true;

        internal bool IsDisposed { get; set; }
    }

    internal sealed partial class Lease : IDisposable
    {
        private StrictResourceLeaseCache<TKey, TResource>? _owner;
        private Entry? _entry;

        internal Lease(StrictResourceLeaseCache<TKey, TResource> owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        internal TResource Resource =>
            _entry?.Resource ?? throw new ObjectDisposedException(nameof(Lease));

        public void Dispose()
        {
            StrictResourceLeaseCache<TKey, TResource>? owner = _owner;
            Entry? entry = _entry;
            _owner = null;
            _entry = null;
            if (owner is not null && entry is not null)
            {
                owner.Release(entry);
            }
        }
    }

    internal sealed partial class Reservation : IDisposable
    {
        internal Reservation(StrictResourceLeaseCache<TKey, TResource> owner, long byteCount)
        {
            Owner = owner;
            RemainingBytes = byteCount;
        }

        internal StrictResourceLeaseCache<TKey, TResource> Owner { get; }

        internal long RemainingBytes { get; set; }

        internal bool IsDisposed { get; set; }

        public void Dispose() => Owner.ReleaseReservation(this);
    }
}
