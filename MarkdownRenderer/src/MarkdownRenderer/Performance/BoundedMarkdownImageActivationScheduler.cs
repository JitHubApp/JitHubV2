using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Starts asynchronous image resolutions away from UI/layout workers. Queue
/// membership is mutable only under <see cref="_gate"/>; the borrowed demand
/// only communicates monotonic priority and an asynchronous promotion signal.
/// </summary>
internal sealed class BoundedMarkdownImageActivationScheduler : IMarkdownImageActivationHost, IDisposable
{
    internal const int DefaultCapacity = 32;
    internal const int DefaultVisibleWaiterCapacity = 512;
    private const int MaxConsecutiveVisibleItemsBeforeOverscan = 8;

    private readonly object _gate = new();
    private readonly LinkedList<MarkdownImageActivation> _visible = new();
    private readonly LinkedList<MarkdownImageActivation> _overscan = new();
    private readonly LinkedList<MarkdownImageActivation> _visibleWaiters = new();
    private readonly int _capacity;
    private readonly int _visibleWaiterCapacity;
    private readonly int _workerCount;
    private readonly Action<Thread> _startWorker;
    private readonly TaskCompletionSource<bool> _stopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _workersRemaining;
    private int _startedWorkerCount;
    private int _queued;
    private int _visibleWaiting;
    private int _visibleItemsSinceOverscan;
    private long _nextArrivalSequence;
    private bool _workersStarted;
    private bool _workerStartupFailureLogged;
    private bool _disposed;

    internal BoundedMarkdownImageActivationScheduler(
        int capacity = DefaultCapacity,
        int visibleWaiterCapacity = DefaultVisibleWaiterCapacity,
        int? workerCount = null,
        Action<Thread>? startWorker = null)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (visibleWaiterCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(visibleWaiterCapacity));

        _capacity = capacity;
        _visibleWaiterCapacity = visibleWaiterCapacity;
        _workerCount = workerCount ?? ResolveWorkerCount(Environment.ProcessorCount, IntPtr.Size);
        _startWorker = startWorker ?? (static worker => worker.Start());
        if (_workerCount is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(workerCount));
    }

    internal Task Completion => _stopped.Task;
    internal int QueuedCount { get { lock (_gate) return _queued; } }
    internal int VisibleWaitingCount { get { lock (_gate) return _visibleWaiting; } }
    internal int WorkerCount { get { lock (_gate) return _startedWorkerCount; } }

    internal static int ResolveWorkerCount(int processorCount, int pointerSize)
    {
        if (processorCount < 1)
            throw new ArgumentOutOfRangeException(nameof(processorCount));
        if (pointerSize is not 4 and not 8)
            throw new ArgumentOutOfRangeException(nameof(pointerSize));
        return pointerSize == 4 || processorCount == 1 ? 1 : 2;
    }

    /// <summary>
    /// Queues without waiting for capacity. Every node/delegate is staged
    /// before queue mutation; promotion registration is acquired before any
    /// incumbent can be evicted. onNotStarted is always invoked outside gate.
    /// </summary>
    internal bool TrySchedule(
        IMarkdownImageActivationDemand demand,
        Action startAsyncWork,
        Action onNotStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(startAsyncWork);
        ArgumentNullException.ThrowIfNull(onNotStarted);
        if (cancellationToken.IsCancellationRequested)
            return false;

        // These can allocate. Prepare them before touching queue counts or an
        // overscan victim so failure leaves existing accepted work untouched.
        var activation = new MarkdownImageActivation(this, demand, startAsyncWork, onNotStarted, cancellationToken);
        activation.PrepareNode();
        activation.PreparePromotionCallback();
        try
        {
            activation.RegisterCancellation();
        }
        catch (Exception exception)
        {
            LogSchedulingFailure(exception);
            activation.Complete(started: false);
            return false;
        }

        MarkdownImageActivation? evicted = null;
        bool admitted = false;
        IDisposable? uncommittedRegistration = null;
        try
        {
            lock (_gate)
            {
                admitted = TryAdmitNoLock(
                    activation,
                    demand,
                    cancellationToken,
                    ref uncommittedRegistration,
                    ref evicted);
            }
        }
        catch (Exception exception)
        {
            // All potentially throwing preparation happens before publication;
            // the node is preallocated, and a failed subscription cannot evict
            // an incumbent or leave queue accounting partially committed.
            LogSchedulingFailure(exception);
        }

        // A subscription not transferred to an accepted activation is retired
        // outside _gate; claimed callbacks validate exact membership.
        MarkdownImageActivation.DisposeRegistrationSafely(uncommittedRegistration);
        evicted?.Complete(started: false);
        if (!admitted)
        {
            activation.Complete(started: false);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Admission under <see cref="_gate"/>: decides whether the activation can
    /// queue or wait, starts workers, and registers an overscan promotion
    /// before any incumbent is chosen as a victim. A registration that is not
    /// transferred to the activation is left in
    /// <paramref name="uncommittedRegistration"/> for disposal outside the gate.
    /// </summary>
    private bool TryAdmitNoLock(
        MarkdownImageActivation activation,
        IMarkdownImageActivationDemand demand,
        CancellationToken cancellationToken,
        ref IDisposable? uncommittedRegistration,
        ref MarkdownImageActivation? evicted)
    {
        if (_disposed || cancellationToken.IsCancellationRequested)
            return false;

        // Notifications are asynchronous, so the demand bit may
        // have changed before its callback gets this gate.
        PromoteAlreadyVisibleOverscanNoLock();

        bool initiallyVisible = demand.IsVisible;
        bool mightQueue = _queued < _capacity ||
            (initiallyVisible && _overscan.First is not null);
        bool mightWait = !mightQueue && initiallyVisible &&
            _visibleWaiting < _visibleWaiterCapacity;

        if (!(mightQueue || mightWait) ||
            !TryGetNextArrivalSequenceNoLock(out _) || !EnsureWorkersStarted())
        {
            return false;
        }

        // An initially overscan queued activation needs a
        // subscription. The callback may be claimed before
        // registration returns, but it runs asynchronously and
        // blocks on _gate until this transaction publishes.
        // The contract forbids an inline callback.
        if (mightQueue && !initiallyVisible)
        {
            uncommittedRegistration = demand.RegisterVisiblePromotion(
                activation.PromotionCallback!) ??
                throw new InvalidOperationException(
                    "Image demand returned a null promotion registration.");
        }

        // Reconcile incumbent promotions only after every
        // fallible registration has returned. Selecting an
        // overscan victim before this point could evict an item
        // whose monotonic demand became visible during Register.
        if (_disposed || cancellationToken.IsCancellationRequested)
            return false;
        return TryPublishNoLock(activation, demand, cancellationToken, ref uncommittedRegistration, ref evicted);
    }

    /// <summary>
    /// The nonallocating publication transaction: queue (possibly evicting the
    /// oldest overscan item for a visible arrival) or join the visible waiters.
    /// </summary>
    private bool TryPublishNoLock(
        MarkdownImageActivation activation,
        IMarkdownImageActivationDemand demand,
        CancellationToken cancellationToken,
        ref IDisposable? uncommittedRegistration,
        ref MarkdownImageActivation? evicted)
    {
        PromoteAlreadyVisibleOverscanNoLock();
        bool visible = demand.IsVisible;
        bool hasQueueSlot = _queued < _capacity;
        MarkdownImageActivation? victim = !hasQueueSlot && visible
            ? _overscan.First?.Value
            : null;
        bool queueAdmission = hasQueueSlot || victim is not null;
        bool waiterAdmission = !queueAdmission && visible &&
            _visibleWaiting < _visibleWaiterCapacity;

        // This is the final cancellation check after all
        // fallible registration and immediately before the
        // nonallocating queue publication transaction.
        if (!(queueAdmission || waiterAdmission) ||
            cancellationToken.IsCancellationRequested ||
            !TryGetNextArrivalSequenceNoLock(out long arrivalSequence))
        {
            return false;
        }

        activation.ArrivalSequence = arrivalSequence;
        activation.SetPromotionRegistration(uncommittedRegistration);
        uncommittedRegistration = null;

        if (queueAdmission)
        {
            LinkedList<MarkdownImageActivation> destination = visible ? _visible : _overscan;
            activation.State = MarkdownImageActivationState.Queued;
            InsertByArrivalNoLock(destination, activation.Node!);
            _nextArrivalSequence = arrivalSequence;
            _queued++;

            if (victim is not null)
            {
                RemoveQueuedNoLock(victim);
                evicted = victim;
            }
            Monitor.PulseAll(_gate);
        }
        else
        {
            activation.State = MarkdownImageActivationState.Waiting;
            InsertByArrivalNoLock(_visibleWaiters, activation.Node!);
            _nextArrivalSequence = arrivalSequence;
            _visibleWaiting++;
        }

        return true;
    }

    private bool TryGetNextArrivalSequenceNoLock(out long sequence)
    {
        if (_nextArrivalSequence == long.MaxValue)
        {
            sequence = 0;
            return false;
        }
        sequence = _nextArrivalSequence + 1;
        return true;
    }

    private void PromoteAlreadyVisibleOverscanNoLock()
    {
        bool moved = false;
        LinkedListNode<MarkdownImageActivation>? node = _overscan.First;
        while (node is not null)
        {
            LinkedListNode<MarkdownImageActivation>? next = node.Next;
            if (node.Value.Demand.IsVisible)
            {
                MoveNodeByArrivalNoLock(_overscan, _visible, node);
                moved = true;
            }
            node = next;
        }
        if (moved)
            Monitor.PulseAll(_gate);
    }

    void IMarkdownImageActivationHost.PromoteQueuedActivation(MarkdownImageActivation activation)
    {
        lock (_gate)
        {
            if (_disposed ||
                activation.State != MarkdownImageActivationState.Queued ||
                activation.Node is not { } node ||
                !ReferenceEquals(node.List, _overscan) ||
                !activation.Demand.IsVisible)
            {
                return;
            }
            MoveNodeByArrivalNoLock(_overscan, _visible, node);
            Monitor.PulseAll(_gate);
        }
    }

    private static void MoveNodeByArrivalNoLock(
        LinkedList<MarkdownImageActivation> source,
        LinkedList<MarkdownImageActivation> destination,
        LinkedListNode<MarkdownImageActivation> node)
    {
        source.Remove(node);
        InsertByArrivalNoLock(destination, node);
    }

    private static void InsertByArrivalNoLock(
        LinkedList<MarkdownImageActivation> destination,
        LinkedListNode<MarkdownImageActivation> node)
    {
        LinkedListNode<MarkdownImageActivation>? cursor = destination.Last;
        while (cursor is not null && cursor.Value.ArrivalSequence > node.Value.ArrivalSequence)
            cursor = cursor.Previous;
        if (cursor is null)
            destination.AddFirst(node);
        else
            destination.AddAfter(cursor, node);
    }

    private bool EnsureWorkersStarted()
    {
        if (_workersStarted)
            return true;
        if (_disposed)
            return false;

        int started = 0;
        try
        {
            for (; started < _workerCount; started++)
            {
                bool visibleOnly = _workerCount > 1 && started == 1;
                var worker = new Thread(() => RunWorker(visibleOnly))
                {
                    IsBackground = true,
                    Name = visibleOnly
                        ? "MarkdownRenderer visible image activation"
                        : "MarkdownRenderer image activation",
                };
                _workersRemaining++;
                try
                {
                    _startWorker(worker);
                    _startedWorkerCount++;
                }
                catch
                {
                    _workersRemaining--;
                    throw;
                }
            }
            _workersStarted = true;
            return true;
        }
        catch (Exception exception)
        {
            if (!_workerStartupFailureLogged)
            {
                _workerStartupFailureLogged = true;
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownPerformanceSession] Image activation worker startup failed: {exception.Message}");
            }
            if (started > 0)
            {
                _workersStarted = true;
                return true;
            }
            return false;
        }
    }

    private void RunWorker(bool visibleOnly)
    {
        try
        {
            while (true)
            {
                MarkdownImageActivation? activation = null;
                lock (_gate)
                {
                    while (!_disposed)
                    {
                        PromoteAlreadyVisibleOverscanNoLock();
                        activation = TakeNextNoLock(visibleOnly);
                        if (activation is not null)
                            break;
                        Monitor.Wait(_gate);
                    }

                    if (_disposed)
                        return;

                    activation!.Node = null;
                    activation.State = MarkdownImageActivationState.Running;
                    _queued--;
                    PromoteVisibleWaitersNoLock();
                }

                activation.DisposePromotionRegistration();
                activation.Run();
            }
        }
        finally
        {
            bool last;
            lock (_gate)
                last = --_workersRemaining == 0;
            if (last)
                _stopped.TrySetResult(true);
        }
    }

    private MarkdownImageActivation? TakeNextNoLock(bool visibleOnly)
    {
        if (!visibleOnly && _overscan.First is { } overscan &&
            (_visible.First is null ||
             _visibleItemsSinceOverscan >= MaxConsecutiveVisibleItemsBeforeOverscan))
        {
            _overscan.RemoveFirst();
            _visibleItemsSinceOverscan = 0;
            return overscan.Value;
        }

        if (_visible.First is { } visible)
        {
            _visible.RemoveFirst();
            if (!visibleOnly)
                _visibleItemsSinceOverscan++;
            return visible.Value;
        }

        if (!visibleOnly && _overscan.First is { } remainingOverscan)
        {
            _overscan.RemoveFirst();
            _visibleItemsSinceOverscan = 0;
            return remainingOverscan.Value;
        }
        return null;
    }

    private void PromoteVisibleWaitersNoLock()
    {
        while (_queued < _capacity && _visibleWaiters.First is { } first)
        {
            LinkedListNode<MarkdownImageActivation> node = first;
            MarkdownImageActivation activation = node.Value;
            _visibleWaiters.Remove(node);
            InsertByArrivalNoLock(_visible, node);
            activation.State = MarkdownImageActivationState.Queued;
            _visibleWaiting--;
            _queued++;
        }
    }

    private void RemoveQueuedNoLock(MarkdownImageActivation activation)
    {
        LinkedListNode<MarkdownImageActivation>? node = activation.Node;
        if (node is null)
            return;
        LinkedList<MarkdownImageActivation>? list = node.List;
        if (list is null)
            return;
        list.Remove(node);
        activation.Node = null;
        activation.State = MarkdownImageActivationState.Completed;
        if (ReferenceEquals(list, _visibleWaiters))
            _visibleWaiting--;
        else
            _queued--;
    }

    void IMarkdownImageActivationHost.CancelQueued(MarkdownImageActivation activation)
    {
        bool canceled = false;
        lock (_gate)
        {
            if (activation.State is MarkdownImageActivationState.Queued or MarkdownImageActivationState.Waiting &&
                activation.Node is { } node)
            {
                RemoveQueuedNoLock(activation);
                PromoteVisibleWaitersNoLock();
                canceled = true;
                Monitor.PulseAll(_gate);
            }
        }
        if (canceled)
            activation.Complete(started: false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            MarkQueueCompletedNoLock(_visible);
            MarkQueueCompletedNoLock(_overscan);
            MarkQueueCompletedNoLock(_visibleWaiters);
            _queued = 0;
            _visibleWaiting = 0;
            if (!_workersStarted)
                _stopped.TrySetResult(true);
            Monitor.PulseAll(_gate);
        }

        // Disposal owns all remaining queue nodes after _disposed is published:
        // workers/cancellation/promotion paths now no-op under the gate. Walk
        // and clear the existing lists outside the gate without allocating a
        // temporary collection after queue state has changed.
        CompleteAndClearQueue(_visible);
        CompleteAndClearQueue(_overscan);
        CompleteAndClearQueue(_visibleWaiters);
    }

    private static void MarkQueueCompletedNoLock(LinkedList<MarkdownImageActivation> queue)
    {
        for (LinkedListNode<MarkdownImageActivation>? node = queue.First; node is not null; node = node.Next)
        {
            MarkdownImageActivation activation = node.Value;
            activation.Node = null;
            activation.State = MarkdownImageActivationState.Completed;
        }
    }

    private static void CompleteAndClearQueue(LinkedList<MarkdownImageActivation> queue)
    {
        while (queue.First is { } first)
        {
            MarkdownImageActivation activation = first.Value;
            queue.RemoveFirst();
            activation.Complete(started: false);
        }
    }

    private static void LogSchedulingFailure(Exception exception) =>
        MarkdownDiagnostics.WriteLine(
            $"[MarkdownPerformanceSession] Image activation scheduling failed: {exception.Message}");
}
