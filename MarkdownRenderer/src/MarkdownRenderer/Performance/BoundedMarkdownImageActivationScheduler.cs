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
internal sealed class BoundedMarkdownImageActivationScheduler : IDisposable
{
    internal const int DefaultCapacity = 32;
    internal const int DefaultVisibleWaiterCapacity = 512;
    private const int MaxConsecutiveVisibleItemsBeforeOverscan = 8;

    private readonly object _gate = new();
    private readonly LinkedList<Activation> _visible = new();
    private readonly LinkedList<Activation> _overscan = new();
    private readonly LinkedList<Activation> _visibleWaiters = new();
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
        var activation = new Activation(this, demand, startAsyncWork, onNotStarted, cancellationToken);
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

        Activation? evicted = null;
        bool admitted = false;
        IDisposable? uncommittedRegistration = null;
        try
        {
            lock (_gate)
            {
                if (!_disposed && !cancellationToken.IsCancellationRequested)
                {
                    // Notifications are asynchronous, so the demand bit may
                    // have changed before its callback gets this gate.
                    PromoteAlreadyVisibleOverscanNoLock();

                    bool initiallyVisible = demand.IsVisible;
                    bool mightQueue = _queued < _capacity ||
                        (initiallyVisible && _overscan.First is not null);
                    bool mightWait = !mightQueue && initiallyVisible &&
                        _visibleWaiting < _visibleWaiterCapacity;

                    if ((mightQueue || mightWait) &&
                        TryGetNextArrivalSequenceNoLock(out _) && EnsureWorkersStarted())
                    {
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
                        if (!_disposed && !cancellationToken.IsCancellationRequested)
                        {
                            PromoteAlreadyVisibleOverscanNoLock();
                            bool visible = demand.IsVisible;
                            bool hasQueueSlot = _queued < _capacity;
                            Activation? victim = !hasQueueSlot && visible
                                ? _overscan.First?.Value
                                : null;
                            bool queueAdmission = hasQueueSlot || victim is not null;
                            bool waiterAdmission = !queueAdmission && visible &&
                                _visibleWaiting < _visibleWaiterCapacity;

                            // This is the final cancellation check after all
                            // fallible registration and immediately before the
                            // nonallocating queue publication transaction.
                            if ((queueAdmission || waiterAdmission) &&
                                !cancellationToken.IsCancellationRequested &&
                                TryGetNextArrivalSequenceNoLock(out long arrivalSequence))
                            {
                                activation.ArrivalSequence = arrivalSequence;
                                activation.SetPromotionRegistration(uncommittedRegistration);
                                uncommittedRegistration = null;

                                if (queueAdmission)
                                {
                                    LinkedList<Activation> destination = visible ? _visible : _overscan;
                                    activation.State = ActivationState.Queued;
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
                                    activation.State = ActivationState.Waiting;
                                    InsertByArrivalNoLock(_visibleWaiters, activation.Node!);
                                    _nextArrivalSequence = arrivalSequence;
                                    _visibleWaiting++;
                                }

                                admitted = true;
                            }
                        }
                    }
                }
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
        DisposeRegistrationSafely(uncommittedRegistration);
        evicted?.Complete(started: false);
        if (!admitted)
        {
            activation.Complete(started: false);
            return false;
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
        LinkedListNode<Activation>? node = _overscan.First;
        while (node is not null)
        {
            LinkedListNode<Activation>? next = node.Next;
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

    private void PromoteQueuedActivation(Activation activation)
    {
        lock (_gate)
        {
            if (_disposed ||
                activation.State != ActivationState.Queued ||
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
        LinkedList<Activation> source,
        LinkedList<Activation> destination,
        LinkedListNode<Activation> node)
    {
        source.Remove(node);
        InsertByArrivalNoLock(destination, node);
    }

    private static void InsertByArrivalNoLock(
        LinkedList<Activation> destination,
        LinkedListNode<Activation> node)
    {
        LinkedListNode<Activation>? cursor = destination.Last;
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
                Activation? activation = null;
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
                    activation.State = ActivationState.Running;
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

    private Activation? TakeNextNoLock(bool visibleOnly)
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
            LinkedListNode<Activation> node = first;
            Activation activation = node.Value;
            _visibleWaiters.Remove(node);
            InsertByArrivalNoLock(_visible, node);
            activation.State = ActivationState.Queued;
            _visibleWaiting--;
            _queued++;
        }
    }

    private void RemoveQueuedNoLock(Activation activation)
    {
        LinkedListNode<Activation>? node = activation.Node;
        if (node is null)
            return;
        LinkedList<Activation>? list = node.List;
        if (list is null)
            return;
        list.Remove(node);
        activation.Node = null;
        activation.State = ActivationState.Completed;
        if (ReferenceEquals(list, _visibleWaiters))
            _visibleWaiting--;
        else
            _queued--;
    }

    private void CancelQueued(Activation activation)
    {
        bool canceled = false;
        lock (_gate)
        {
            if (activation.State is ActivationState.Queued or ActivationState.Waiting &&
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

    private static void MarkQueueCompletedNoLock(LinkedList<Activation> queue)
    {
        for (LinkedListNode<Activation>? node = queue.First; node is not null; node = node.Next)
        {
            Activation activation = node.Value;
            activation.Node = null;
            activation.State = ActivationState.Completed;
        }
    }

    private static void CompleteAndClearQueue(LinkedList<Activation> queue)
    {
        while (queue.First is { } first)
        {
            Activation activation = first.Value;
            queue.RemoveFirst();
            activation.Complete(started: false);
        }
    }

    private static void DisposeRegistrationSafely(IDisposable? registration)
    {
        if (registration is null)
            return;
        try { registration.Dispose(); }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownPerformanceSession] Image activation promotion cleanup failed: {exception.Message}");
        }
    }

    private static void LogSchedulingFailure(Exception exception) =>
        MarkdownDiagnostics.WriteLine(
            $"[MarkdownPerformanceSession] Image activation scheduling failed: {exception.Message}");

    private enum ActivationState : byte { Created, Queued, Waiting, Running, Completed }

    private sealed class Activation
    {
        private readonly BoundedMarkdownImageActivationScheduler _owner;
        private Action? _startAsyncWork;
        private Action? _onNotStarted;
        private CancellationTokenRegistration _cancellationRegistration;
        private IDisposable? _promotionRegistration;
        private int _completed;

        internal Activation(
            BoundedMarkdownImageActivationScheduler owner,
            IMarkdownImageActivationDemand demand,
            Action startAsyncWork,
            Action onNotStarted,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            Demand = demand;
            _startAsyncWork = startAsyncWork;
            _onNotStarted = onNotStarted;
            CancellationToken = cancellationToken;
        }

        internal IMarkdownImageActivationDemand Demand { get; }
        internal Action? PromotionCallback { get; private set; }
        internal LinkedListNode<Activation>? Node { get; set; }
        internal long ArrivalSequence { get; set; }
        internal ActivationState State { get; set; }
        internal CancellationToken CancellationToken { get; }

        internal void PrepareNode() => Node = new LinkedListNode<Activation>(this);
        internal void PreparePromotionCallback() => PromotionCallback = OnDemandPromoted;
        private void OnDemandPromoted() => _owner.PromoteQueuedActivation(this);
        internal void SetPromotionRegistration(IDisposable? registration) => _promotionRegistration = registration;
        internal void RegisterCancellation()
        {
            if (CancellationToken.CanBeCanceled)
            {
                _cancellationRegistration = CancellationToken.Register(
                    static state =>
                    {
                        var activation = (Activation)state!;
                        activation._owner.CancelQueued(activation);
                    },
                    this);
            }
        }

        internal void DisposePromotionRegistration() =>
            DisposeRegistrationSafely(Interlocked.Exchange(ref _promotionRegistration, null));

        internal void Run()
        {
            bool started = !CancellationToken.IsCancellationRequested;
            try
            {
                if (started)
                    Volatile.Read(ref _startAsyncWork)?.Invoke();
            }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownPerformanceSession] Image activation start failed: {exception.Message}");
            }
            finally { Complete(started); }
        }

        internal void Complete(bool started)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            DisposePromotionRegistration();
            Interlocked.Exchange(ref _startAsyncWork, null);
            Action? retiredNotStarted = Interlocked.Exchange(ref _onNotStarted, null);
            try { _cancellationRegistration.Unregister(); }
            catch (Exception exception)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownPerformanceSession] Image activation cancellation cleanup failed: {exception.Message}");
            }
            if (!started)
            {
                try { retiredNotStarted?.Invoke(); }
                catch (Exception exception)
                {
                    MarkdownDiagnostics.WriteLine(
                        $"[MarkdownPerformanceSession] Image activation retirement failed: {exception.Message}");
                }
            }
        }
    }
}
