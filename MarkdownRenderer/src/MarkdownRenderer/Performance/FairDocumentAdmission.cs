using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Admits bounded work round-robin by document. A lone document can fill all
/// slots, but newly available slots rotate between queued documents.
/// </summary>
internal sealed class FairDocumentAdmission
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<object, OwnerQueue> _queues =
        new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<OwnerQueue> _rotation = new();
    private int _active;

    internal FairDocumentAdmission(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    /// <summary>Slots currently held, sampled under the admission lock.</summary>
    internal int ActiveSlots
    {
        get
        {
            lock (_gate)
                return _active;
        }
    }

    /// <summary>Current queued work, sampled under the admission lock.</summary>
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

    internal ValueTask<IDisposable> EnterAsync(object owner, CancellationToken cancellationToken) =>
        EnterAsync(owner, exclusive: false, cancellationToken);

    internal ValueTask<IDisposable> EnterAsync(
        object owner, bool exclusive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();

        int slots = exclusive ? _capacity : 1;
        Waiter? waiter = null;
        lock (_gate)
        {
            // The caller can be canceled while blocked acquiring _gate after
            // the initial fast check above. Do not grant capacity or enqueue a
            // waiter that is already canceled when this critical section starts.
            cancellationToken.ThrowIfCancellationRequested();

            if (slots <= _capacity - _active && _rotation.Count == 0)
            {
                _active += slots;
                return ValueTask.FromResult<IDisposable>(new Lease(this, slots));
            }

            if (!_queues.TryGetValue(owner, out OwnerQueue? queue))
            {
                queue = new OwnerQueue(owner);
                _queues.Add(owner, queue);
                queue.RotationNode = _rotation.AddLast(queue);
            }

            waiter = new Waiter(queue, slots);
            waiter.Node = queue.Waiters.AddLast(waiter);
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
                    if (waiter.Owner.Waiters.Count == 0)
                        RemoveOwnerNoLock(waiter.Owner);
                    waiter.Completion.TrySetCanceled();
                    DispatchNoLock();
                    break;
                case WaiterState.Granted:
                    // WaitAsync may report cancellation at the same instant a
                    // slot is granted. The caller never receives that lease.
                    waiter.State = WaiterState.Canceled;
                    _active -= waiter.Slots;
                    DispatchNoLock();
                    break;
            }
        }
    }

    private void Release(int slots)
    {
        lock (_gate)
        {
            if (_active < slots)
                throw new InvalidOperationException("A document work slot was released twice.");
            _active -= slots;
            DispatchNoLock();
        }
    }

    private void DispatchNoLock()
    {
        while (_active < _capacity && _rotation.First is { } node)
        {
            OwnerQueue queue = node.Value;
            Waiter waiter = queue.Waiters.First!.Value;
            // Do not bypass an exclusive waiter with new one-slot work. Let
            // existing work drain so a large source cannot starve forever.
            if (waiter.Slots > _capacity - _active)
                return;
            _rotation.RemoveFirst();
            queue.RotationNode = null;
            queue.Waiters.RemoveFirst();
            waiter.Node = null;
            if (queue.Waiters.Count == 0)
                _queues.Remove(queue.Owner);
            else
                queue.RotationNode = _rotation.AddLast(queue);

            waiter.State = WaiterState.Granted;
            _active += waiter.Slots;
            waiter.Completion.TrySetResult(new Lease(this, waiter.Slots));
        }
    }

    private void RemoveOwnerNoLock(OwnerQueue queue)
    {
        _rotation.Remove(queue.RotationNode!);
        queue.RotationNode = null;
        _queues.Remove(queue.Owner);
    }

    private sealed class OwnerQueue(object owner)
    {
        internal object Owner { get; } = owner;
        internal LinkedList<Waiter> Waiters { get; } = new();
        internal LinkedListNode<OwnerQueue>? RotationNode { get; set; }
    }

    private sealed class Waiter(OwnerQueue owner, int slots)
    {
        internal OwnerQueue Owner { get; } = owner;
        internal int Slots { get; } = slots;
        internal LinkedListNode<Waiter>? Node { get; set; }
        internal WaiterState State { get; set; } = WaiterState.Queued;
        internal TaskCompletionSource<Lease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum WaiterState { Queued, Granted, Canceled }

    private sealed class Lease(FairDocumentAdmission owner, int slots) : IDisposable
    {
        private FairDocumentAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(slots);
    }
}
