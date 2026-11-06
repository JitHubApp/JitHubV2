using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Owns a bounded, reusable worker lane for opt-in markdown layout work.
/// Primary snapshot construction has a reserved worker on multi-core processes;
/// background band/image reflow cannot occupy that lane. Admitted work is
/// bounded per priority; additional callers await capacity with cancellation
/// and are counted separately because live primary requests cannot be dropped.
/// These waiter counts are telemetry, not hard limits: rejecting a live visible
/// build would surface as a render failure.
/// </summary>
internal sealed class BoundedMarkdownLayoutScheduler : IDisposable
{
    private const int MaxConsecutivePrimaryJobsBeforeBackground = 4;

    private readonly object _gate = new();
    private readonly LinkedList<WorkItem> _primary = new();
    private readonly LinkedList<WorkItem> _background = new();
    private readonly SemaphoreSlim _primaryCapacity;
    private readonly SemaphoreSlim _backgroundCapacity;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<bool> _stopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _workersRemaining;
    private int _primaryJobsSinceBackground;
    private long _primaryAdmissionWaiters;
    private long _backgroundAdmissionWaiters;
    private bool _disposed;

    internal BoundedMarkdownLayoutScheduler(
        int primaryCapacity = 4,
        int backgroundCapacity = 8,
        int? workerCount = null)
    {
        if (primaryCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(primaryCapacity));
        if (backgroundCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(backgroundCapacity));

        int count = workerCount ?? ResolveWorkerCount(Environment.ProcessorCount, IntPtr.Size);
        if (count is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(workerCount));

        _primaryCapacity = new SemaphoreSlim(primaryCapacity, primaryCapacity);
        _backgroundCapacity = new SemaphoreSlim(backgroundCapacity, backgroundCapacity);
        _workersRemaining = count;
        for (int index = 0; index < count; index++)
        {
            // On 64-bit hosts, keep one worker available exclusively for visible
            // snapshot work. The second worker prefers primary work, then helps
            // with background reflow. A constrained 32-bit host uses one worker
            // with bounded primary priority before queued background work.
            bool primaryOnly = count > 1 && index == 0;
            var worker = new Thread(() => RunWorker(primaryOnly))
            {
                IsBackground = true,
                Name = primaryOnly
                    ? "MarkdownRenderer primary layout"
                    : "MarkdownRenderer background layout",
            };
            worker.Start();
        }
    }

    internal Task Completion => _stopped.Task;
    internal long PrimaryAdmissionWaiters => Interlocked.Read(ref _primaryAdmissionWaiters);
    internal long BackgroundAdmissionWaiters => Interlocked.Read(ref _backgroundAdmissionWaiters);

    internal static int ResolveWorkerCount(int processorCount, int pointerSize)
    {
        if (processorCount < 1)
            throw new ArgumentOutOfRangeException(nameof(processorCount));
        if (pointerSize is not 4 and not 8)
            throw new ArgumentOutOfRangeException(nameof(pointerSize));
        return pointerSize == 4 || processorCount == 1 ? 1 : 2;
    }

    internal void ReleaseResources()
    {
        if (!_stopped.Task.IsCompleted)
            throw new InvalidOperationException("The layout scheduler must stop before releasing its resources.");
        _primaryCapacity.Dispose();
        _backgroundCapacity.Dispose();
        _shutdown.Dispose();
    }

    internal Task<T> ScheduleAsync<T>(
        MarkdownLayoutWorkPriority priority,
        Func<CancellationToken, T> work,
        CancellationToken cancellationToken)
    {
        if (work is null)
            return Task.FromException<T>(new ArgumentNullException(nameof(work)));
        if (priority is not MarkdownLayoutWorkPriority.Primary and not MarkdownLayoutWorkPriority.Background)
            return Task.FromException<T>(new ArgumentOutOfRangeException(nameof(priority)));
        SemaphoreSlim capacity = priority == MarkdownLayoutWorkPriority.Primary
            ? _primaryCapacity
            : _backgroundCapacity;

        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        try
        {
            if (capacity.Wait(0))
                return ScheduleWithCapacity(priority, work, cancellationToken, capacity);
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }

        return ScheduleAfterCapacityAsync(priority, work, cancellationToken, capacity);
    }

    private async Task<T> ScheduleAfterCapacityAsync<T>(
        MarkdownLayoutWorkPriority priority,
        Func<CancellationToken, T> work,
        CancellationToken cancellationToken,
        SemaphoreSlim capacity)
    {
        await WaitForCapacityAsync(capacity, priority, cancellationToken).ConfigureAwait(false);
        return await ScheduleWithCapacity(priority, work, cancellationToken, capacity).ConfigureAwait(false);
    }

    private Task<T> ScheduleWithCapacity<T>(
        MarkdownLayoutWorkPriority priority,
        Func<CancellationToken, T> work,
        CancellationToken cancellationToken,
        SemaphoreSlim capacity)
    {
        var item = new WorkItem<T>(this, priority, work, cancellationToken, capacity);
        item.RegisterCancellation();
        CancellationToken rejectedWith = default;
        bool enqueued = false;
        lock (_gate)
        {
            if (_disposed)
            {
                rejectedWith = _shutdown.Token;
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                rejectedWith = cancellationToken;
            }
            else
            {
                LinkedList<WorkItem> queue = priority == MarkdownLayoutWorkPriority.Primary
                    ? _primary
                    : _background;
                item.State = WorkItemState.Queued;
                item.Node = queue.AddLast(item);
                enqueued = true;
                Monitor.PulseAll(_gate);
            }
        }

        if (!enqueued)
        {
            item.UnregisterCancellation();
            item.ReleaseCapacity();
            item.Cancel(rejectedWith.CanBeCanceled ? rejectedWith : _shutdown.Token);
        }

        // Keep the uncontended admission path as the work item's task. Awaiting
        // an async wrapper here would add a thread-pool continuation before a
        // captured UI context can resume.
        return item.Task;
    }

    private async Task WaitForCapacityAsync(
        SemaphoreSlim capacity,
        MarkdownLayoutWorkPriority priority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (capacity.Wait(0))
            return;

        IncrementAdmissionWaiters(priority);
        try
        {
            using CancellationTokenSource admission = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _shutdown.Token);
            await capacity.WaitAsync(admission.Token).ConfigureAwait(false);
        }
        finally
        {
            DecrementAdmissionWaiters(priority);
        }
    }

    private void IncrementAdmissionWaiters(MarkdownLayoutWorkPriority priority)
    {
        if (priority == MarkdownLayoutWorkPriority.Primary)
            Interlocked.Increment(ref _primaryAdmissionWaiters);
        else
            Interlocked.Increment(ref _backgroundAdmissionWaiters);
    }

    private void DecrementAdmissionWaiters(MarkdownLayoutWorkPriority priority)
    {
        if (priority == MarkdownLayoutWorkPriority.Primary)
            Interlocked.Decrement(ref _primaryAdmissionWaiters);
        else
            Interlocked.Decrement(ref _backgroundAdmissionWaiters);
    }

    private void CancelQueued(WorkItem item)
    {
        bool canceled = false;
        lock (_gate)
        {
            if (item.State == WorkItemState.Queued && item.Node is { } node)
            {
                (item.Priority == MarkdownLayoutWorkPriority.Primary ? _primary : _background)
                    .Remove(node);
                item.Node = null;
                item.State = WorkItemState.Completed;
                canceled = true;
                Monitor.PulseAll(_gate);
            }
        }

        if (!canceled)
            return;

        item.Cancel(item.CancellationToken);
        item.ReleaseCapacity();
        item.UnregisterCancellation();
    }

    private void RunWorker(bool primaryOnly)
    {
        try
        {
            while (true)
            {
                WorkItem? item;
                lock (_gate)
                {
                    while ((item = TakeNext(primaryOnly)) is null && !_disposed)
                        Monitor.Wait(_gate);

                    if (item is null)
                        return;

                    item.Node = null;
                    item.State = WorkItemState.Running;
                }

                item.Execute();
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _workersRemaining) == 0)
                _stopped.TrySetResult(true);
        }
    }

    private WorkItem? TakeNext(bool primaryOnly)
    {
        if (!primaryOnly && _background.First is { } background &&
            (_primary.First is null ||
             _primaryJobsSinceBackground >= MaxConsecutivePrimaryJobsBeforeBackground))
        {
            _background.RemoveFirst();
            _primaryJobsSinceBackground = 0;
            return background.Value;
        }

        if (_primary.First is { } primary)
        {
            _primary.RemoveFirst();
            if (!primaryOnly && _primaryJobsSinceBackground < MaxConsecutivePrimaryJobsBeforeBackground)
                _primaryJobsSinceBackground++;
            return primary.Value;
        }

        if (!primaryOnly && _background.First is { } remainingBackground)
        {
            _background.RemoveFirst();
            _primaryJobsSinceBackground = 0;
            return remainingBackground.Value;
        }

        return null;
    }

    public void Dispose()
    {
        List<WorkItem> abandoned = [];
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            MoveQueuedTo(abandoned, _primary);
            MoveQueuedTo(abandoned, _background);
            Monitor.PulseAll(_gate);
        }

        _shutdown.Cancel();
        foreach (WorkItem item in abandoned)
        {
            item.UnregisterCancellation();
            item.Cancel(_shutdown.Token);
            item.ReleaseCapacity();
        }
    }

    private static void MoveQueuedTo(List<WorkItem> destination, LinkedList<WorkItem> queue)
    {
        while (queue.First is { } first)
        {
            queue.RemoveFirst();
            WorkItem item = first.Value;
            item.Node = null;
            item.State = WorkItemState.Completed;
            destination.Add(item);
        }
    }

    private enum WorkItemState : byte
    {
        Created,
        Queued,
        Running,
        Completed,
    }

    private abstract class WorkItem(
        BoundedMarkdownLayoutScheduler owner,
        MarkdownLayoutWorkPriority priority,
        CancellationToken cancellationToken,
        SemaphoreSlim capacity)
    {
        private int _capacityReleased;

        internal MarkdownLayoutWorkPriority Priority { get; } = priority;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal LinkedListNode<WorkItem>? Node { get; set; }
        internal WorkItemState State { get; set; }

        internal void RegisterCancellation()
        {
            if (CancellationToken.CanBeCanceled)
                Register(CancellationToken.Register(
                    static state =>
                    {
                        var item = (WorkItem)state!;
                        item.CancelQueued();
                    }, this));
        }

        internal abstract void Execute();
        internal abstract void Cancel(CancellationToken token);
        protected abstract void Register(CancellationTokenRegistration registration);

        private void CancelQueued() => owner.CancelQueued(this);

        internal void UnregisterCancellation() => Unregister();
        protected abstract void Unregister();

        internal void ReleaseCapacity()
        {
            if (Interlocked.Exchange(ref _capacityReleased, 1) == 0)
                capacity.Release();
        }
    }

    private sealed class WorkItem<T>(
        BoundedMarkdownLayoutScheduler owner,
        MarkdownLayoutWorkPriority priority,
        Func<CancellationToken, T> work,
        CancellationToken cancellationToken,
        SemaphoreSlim capacity)
        : WorkItem(owner, priority, cancellationToken, capacity)
    {
        private readonly TaskCompletionSource<T> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;

        internal Task<T> Task => _completion.Task;

        protected override void Register(CancellationTokenRegistration registration) =>
            _registration = registration;

        protected override void Unregister() => _registration.Unregister();

        internal override void Cancel(CancellationToken token) =>
            _completion.TrySetCanceled(token);

        internal override void Execute()
        {
            try
            {
                CancellationToken.ThrowIfCancellationRequested();
                _completion.TrySetResult(work(CancellationToken));
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(CancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                Unregister();
                ReleaseCapacity();
            }
        }
    }
}
