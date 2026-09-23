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

    internal ValueTask<IDisposable> EnterAsync(object owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();

        Waiter? waiter = null;
        lock (_gate)
        {
            if (_active < _capacity && _rotation.Count == 0)
            {
                _active++;
                return ValueTask.FromResult<IDisposable>(new Lease(this));
            }

            if (!_queues.TryGetValue(owner, out OwnerQueue? queue))
            {
                queue = new OwnerQueue(owner);
                _queues.Add(owner, queue);
                queue.RotationNode = _rotation.AddLast(queue);
            }

            waiter = new Waiter(queue);
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
                    _active--;
                    DispatchNoLock();
                    break;
            }
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_active <= 0)
                throw new InvalidOperationException("A document work slot was released twice.");
            _active--;
            DispatchNoLock();
        }
    }

    private void DispatchNoLock()
    {
        while (_active < _capacity && _rotation.First is { } node)
        {
            OwnerQueue queue = node.Value;
            _rotation.RemoveFirst();
            queue.RotationNode = null;
            Waiter waiter = queue.Waiters.First!.Value;
            queue.Waiters.RemoveFirst();
            waiter.Node = null;
            if (queue.Waiters.Count == 0)
                _queues.Remove(queue.Owner);
            else
                queue.RotationNode = _rotation.AddLast(queue);

            waiter.State = WaiterState.Granted;
            _active++;
            waiter.Completion.TrySetResult(new Lease(this));
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

    private sealed class Waiter(OwnerQueue owner)
    {
        internal OwnerQueue Owner { get; } = owner;
        internal LinkedListNode<Waiter>? Node { get; set; }
        internal WaiterState State { get; set; } = WaiterState.Queued;
        internal TaskCompletionSource<Lease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum WaiterState { Queued, Granted, Canceled }

    private sealed class Lease(FairDocumentAdmission owner) : IDisposable
    {
        private FairDocumentAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
