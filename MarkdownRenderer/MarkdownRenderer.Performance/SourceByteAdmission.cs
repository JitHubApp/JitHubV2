using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Weighted, document-fair admission for source buffers. Speculative work
/// cannot consume the bytes reserved for visible work. A request owns only its
/// actual buffer reservation, never an enclosing resolver call (which may
/// recursively resolve a Git LFS image).
/// </summary>
internal sealed class SourceByteAdmission
{
    private readonly object _gate = new();
    private readonly long _capacity;
    private readonly long _backgroundCapacity;
    private readonly PriorityQueue _visible = new();
    private readonly PriorityQueue _background = new();
    private long _activeBytes;
    private long _activeBackgroundBytes;
    private long _peakActiveBytes;
    private int _pendingRequests;

    internal SourceByteAdmission(long capacity, long reservedVisibleBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (reservedVisibleBytes <= 0 || reservedVisibleBytes >= capacity)
            throw new ArgumentOutOfRangeException(nameof(reservedVisibleBytes));
        _capacity = capacity;
        _backgroundCapacity = capacity - reservedVisibleBytes;
    }

    internal long Capacity => _capacity;
    internal long BackgroundCapacity => _backgroundCapacity;

    internal long ActiveBytes
    {
        get { lock (_gate) return _activeBytes; }
    }

    internal long ActiveBackgroundBytes
    {
        get { lock (_gate) return _activeBackgroundBytes; }
    }

    internal long PeakActiveBytes
    {
        get { lock (_gate) return _peakActiveBytes; }
    }

    internal int PendingRequests
    {
        get { lock (_gate) return _pendingRequests; }
    }

    internal bool CanSpeculate(long bytes) => bytes > 0 && bytes <= _backgroundCapacity;

    internal ValueTask<IDisposable> EnterAsync(
        object documentOwner,
        long bytes,
        bool background,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentOwner);
        if (bytes <= 0 || bytes > (background ? _backgroundCapacity : _capacity))
            throw new ArgumentOutOfRangeException(nameof(bytes));
        cancellationToken.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_gate)
        {
            if (CanGrantNoLock(bytes, background) &&
                _visible.Rotation.Count == 0 &&
                (!background || _background.Rotation.Count == 0))
            {
                GrantBytesNoLock(bytes, background);
                return ValueTask.FromResult<IDisposable>(new Lease(this, bytes, background));
            }

            PriorityQueue priority = background ? _background : _visible;
            if (!priority.Owners.TryGetValue(documentOwner, out OwnerQueue? owner))
            {
                owner = new OwnerQueue(documentOwner);
                priority.Owners.Add(documentOwner, owner);
                owner.RotationNode = priority.Rotation.AddLast(owner);
            }

            waiter = new Waiter(priority, owner, bytes, background);
            waiter.Node = owner.Waiters.AddLast(waiter);
            _pendingRequests++;
            DispatchNoLock();
        }

        return AwaitAsync(waiter, cancellationToken);
    }

    private async ValueTask<IDisposable> AwaitAsync(
        Waiter waiter,
        CancellationToken cancellationToken)
    {
        try
        {
            Lease lease = await waiter.Completion.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                Cancel(waiter);
                cancellationToken.ThrowIfCancellationRequested();
            }
            return lease;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Cancel(waiter);
            throw;
        }
    }

    private void Cancel(Waiter waiter)
    {
        lock (_gate)
        {
            switch (waiter.State)
            {
                case WaiterState.Queued:
                    waiter.Owner.Waiters.Remove(waiter.Node!);
                    waiter.Node = null;
                    waiter.State = WaiterState.Canceled;
                    _pendingRequests--;
                    if (waiter.Owner.Waiters.Count == 0)
                        RemoveOwnerNoLock(waiter.Priority, waiter.Owner);
                    waiter.Completion.TrySetCanceled();
                    DispatchNoLock();
                    break;
                case WaiterState.Granted:
                    // WaitAsync can observe cancellation at the same instant
                    // a grant completes. No lease reached the caller.
                    waiter.State = WaiterState.Canceled;
                    ReleaseBytesNoLock(waiter.Bytes, waiter.Background);
                    DispatchNoLock();
                    break;
            }
        }
    }

    private void Release(long bytes, bool background)
    {
        lock (_gate)
        {
            ReleaseBytesNoLock(bytes, background);
            DispatchNoLock();
        }
    }

    private void ReleaseBytesNoLock(long bytes, bool background)
    {
        if (_activeBytes < bytes || (background && _activeBackgroundBytes < bytes))
            throw new InvalidOperationException("A source-byte reservation was released twice.");
        _activeBytes -= bytes;
        if (background)
            _activeBackgroundBytes -= bytes;
    }

    private bool CanGrantNoLock(long bytes, bool background) =>
        bytes <= _capacity - _activeBytes &&
        (!background || bytes <= _backgroundCapacity - _activeBackgroundBytes);

    private void GrantBytesNoLock(long bytes, bool background)
    {
        _activeBytes += bytes;
        _peakActiveBytes = System.Math.Max(_peakActiveBytes, _activeBytes);
        if (background)
            _activeBackgroundBytes += bytes;
    }

    private void DispatchNoLock()
    {
        while (true)
        {
            if (TryDispatchNoLock(_visible))
                continue;
            // Never admit more speculative work while a visible reservation
            // is waiting for bytes. This avoids priority inversion for a
            // visible image larger than the reserved slice.
            if (_visible.Rotation.Count != 0 || !TryDispatchNoLock(_background))
                return;
        }
    }

    private bool TryDispatchNoLock(PriorityQueue priority)
    {
        int ownersToCheck = priority.Rotation.Count;
        while (ownersToCheck-- > 0 && priority.Rotation.First is { } node)
        {
            OwnerQueue owner = node.Value;
            priority.Rotation.RemoveFirst();
            owner.RotationNode = null;
            Waiter waiter = owner.Waiters.First!.Value;
            if (!CanGrantNoLock(waiter.Bytes, waiter.Background))
            {
                owner.RotationNode = priority.Rotation.AddLast(owner);
                continue;
            }

            owner.Waiters.RemoveFirst();
            waiter.Node = null;
            _pendingRequests--;
            if (owner.Waiters.Count == 0)
                priority.Owners.Remove(owner.Owner);
            else
                owner.RotationNode = priority.Rotation.AddLast(owner);

            waiter.State = WaiterState.Granted;
            GrantBytesNoLock(waiter.Bytes, waiter.Background);
            waiter.Completion.TrySetResult(new Lease(this, waiter.Bytes, waiter.Background));
            return true;
        }
        return false;
    }

    private static void RemoveOwnerNoLock(PriorityQueue priority, OwnerQueue owner)
    {
        priority.Rotation.Remove(owner.RotationNode!);
        owner.RotationNode = null;
        priority.Owners.Remove(owner.Owner);
    }

    private sealed class PriorityQueue
    {
        internal Dictionary<object, OwnerQueue> Owners { get; } =
            new(ReferenceEqualityComparer.Instance);
        internal LinkedList<OwnerQueue> Rotation { get; } = new();
    }

    private sealed class OwnerQueue(object owner)
    {
        internal object Owner { get; } = owner;
        internal LinkedList<Waiter> Waiters { get; } = new();
        internal LinkedListNode<OwnerQueue>? RotationNode { get; set; }
    }

    private sealed class Waiter(PriorityQueue priority, OwnerQueue owner, long bytes, bool background)
    {
        internal PriorityQueue Priority { get; } = priority;
        internal OwnerQueue Owner { get; } = owner;
        internal long Bytes { get; } = bytes;
        internal bool Background { get; } = background;
        internal LinkedListNode<Waiter>? Node { get; set; }
        internal WaiterState State { get; set; } = WaiterState.Queued;
        internal TaskCompletionSource<Lease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum WaiterState { Queued, Granted, Canceled }

    private sealed class Lease(SourceByteAdmission owner, long bytes, bool background) : IDisposable
    {
        private SourceByteAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(bytes, background);
    }
}
