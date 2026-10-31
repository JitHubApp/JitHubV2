using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>
/// Coordinates estimated transient image-preparation and decode work across all
/// controls and performance sessions in this process. Estimates are scheduling
/// weights, not source-byte ownership or guarantees about native peak memory.
/// </summary>
internal sealed class MarkdownImagePreparationAdmission
{
    private static readonly MarkdownImagePreparationAdmission SharedInstance = new(
        IntPtr.Size == 4 ? 64L * 1024 * 1024 : 128L * 1024 * 1024,
        IntPtr.Size == 4 ? 1 : 2);

    private readonly object _gate = new();
    private readonly Dictionary<object, OwnerQueue> _queues =
        new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<OwnerQueue> _rotation = new();
    private long _activeBytes;
    private int _activeCount;

    internal MarkdownImagePreparationAdmission(long capacityBytes, int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        CapacityBytes = capacityBytes;
        MaxConcurrency = maxConcurrency;
    }

    internal static MarkdownImagePreparationAdmission Shared => SharedInstance;

    internal readonly long CapacityBytes;

    internal readonly int MaxConcurrency;

    internal long ActiveBytes
    {
        get { lock (_gate) return _activeBytes; }
    }

    internal int ActiveCount
    {
        get { lock (_gate) return _activeCount; }
    }

    internal int PendingRequests
    {
        get
        {
            lock (_gate)
            {
                int count = 0;
                foreach (OwnerQueue queue in _queues.Values)
                    count += queue.Waiters.Count;
                return count;
            }
        }
    }

    internal ValueTask<IDisposable> EnterAsync(
        object owner,
        long estimatedBytes,
        bool exclusive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedBytes);
        cancellationToken.ThrowIfCancellationRequested();

        long weight = exclusive || estimatedBytes >= CapacityBytes
            ? CapacityBytes
            : Math.Max(1, estimatedBytes);
        Waiter waiter;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_rotation.Count == 0 && FitsNoLock(weight))
            {
                Lease lease = new(this, weight);
                _activeBytes += weight;
                _activeCount++;
                return ValueTask.FromResult<IDisposable>(lease);
            }

            if (!_queues.TryGetValue(owner, out OwnerQueue? queue))
            {
                queue = new OwnerQueue(owner);
                _queues.Add(owner, queue);
                queue.RotationNode = _rotation.AddLast(queue);
            }

            waiter = new Waiter(queue, weight);
            waiter.Node = queue.Waiters.AddLast(waiter);
            DispatchNoLock();
        }

        return AwaitAsync(waiter, cancellationToken);
    }

    private async ValueTask<IDisposable> AwaitAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        try
        {
            Lease lease = await waiter.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Lease? canceledLease = null;
            lock (_gate)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    waiter.State = WaiterState.Canceled;
                    canceledLease = waiter.GrantedLease;
                    waiter.GrantedLease = null;
                }
                else
                {
                    waiter.State = WaiterState.Delivered;
                    waiter.GrantedLease = null;
                }
            }

            if (canceledLease is not null)
            {
                canceledLease.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return lease;
        }
        catch
        {
            Cancel(waiter)?.Dispose();
            throw;
        }
    }

    private Lease? Cancel(Waiter waiter)
    {
        Lease? lease = null;
        lock (_gate)
        {
            if (waiter.State == WaiterState.Queued)
            {
                OwnerQueue? ownerQueue = waiter.Queue;
                if (ownerQueue is not null)
                {
                    ownerQueue.Waiters.Remove(waiter.Node!);
                    waiter.Node = null;
                    waiter.Queue = null;
                    if (ownerQueue.Waiters.Count == 0)
                        RemoveOwnerNoLock(ownerQueue);
                }
                waiter.State = WaiterState.Canceled;
                waiter.Completion.TrySetCanceled();
                DispatchNoLock();
            }
            else if (waiter.State == WaiterState.Granted)
            {
                waiter.State = WaiterState.Canceled;
                lease = waiter.GrantedLease;
                waiter.GrantedLease = null;
            }
        }
        return lease;
    }

    private bool FitsNoLock(long bytes) =>
        _activeCount < MaxConcurrency && bytes <= CapacityBytes - _activeBytes;

    private void DispatchNoLock()
    {
        while (_rotation.First is { } node)
        {
            OwnerQueue queue = node.Value;
            Waiter waiter = queue.Waiters.First!.Value;
            if (!FitsNoLock(waiter.WeightBytes))
                return;

            Lease lease = new(this, waiter.WeightBytes);
            _rotation.RemoveFirst();
            queue.RotationNode = null;
            queue.Waiters.RemoveFirst();
            waiter.Node = null;
            waiter.Queue = null;
            if (queue.Waiters.Count == 0)
                _queues.Remove(queue.Owner);
            else
            {
                queue.RotationNode = node;
                _rotation.AddLast(node);
            }

            _activeBytes += waiter.WeightBytes;
            _activeCount++;
            waiter.State = WaiterState.Granted;
            waiter.GrantedLease = lease;
            waiter.Completion.TrySetResult(lease);
        }
    }

    private void RemoveOwnerNoLock(OwnerQueue queue)
    {
        _rotation.Remove(queue.RotationNode!);
        queue.RotationNode = null;
        _queues.Remove(queue.Owner);
    }

    private void Release(long bytes)
    {
        lock (_gate)
        {
            if (_activeBytes < bytes || _activeCount < 1)
                throw new InvalidOperationException("An image preparation admission lease was released twice.");
            _activeBytes -= bytes;
            _activeCount--;
            DispatchNoLock();
        }
    }

    private sealed class OwnerQueue
    {
        internal readonly object Owner;
        internal readonly LinkedList<Waiter> Waiters = new();
        internal LinkedListNode<OwnerQueue>? RotationNode;

        internal OwnerQueue(object owner) => Owner = owner;
    }

    private sealed class Waiter
    {
        internal OwnerQueue? Queue;
        internal readonly long WeightBytes;
        internal LinkedListNode<Waiter>? Node;
        internal WaiterState State = WaiterState.Queued;
        internal Lease? GrantedLease;
        internal readonly TaskCompletionSource<Lease> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Waiter(OwnerQueue queue, long weightBytes)
        {
            Queue = queue;
            WeightBytes = weightBytes;
        }
    }

    private enum WaiterState { Queued, Granted, Delivered, Canceled }

    private sealed class Lease(MarkdownImagePreparationAdmission owner, long bytes) : IDisposable
    {
        private MarkdownImagePreparationAdmission? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(bytes);
    }
}
