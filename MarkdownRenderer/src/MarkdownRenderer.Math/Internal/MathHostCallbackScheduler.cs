namespace MarkdownRenderer.Math.Internal;

/// <summary>
/// Runs host callbacks on a small set of dedicated background threads. A host
/// callback may block synchronously and ignore cancellation, so it must not
/// run on the shared thread pool, where a saturated callback gate could also
/// starve the timer callback that enforces the processing deadline.
/// </summary>
/// <remarks>
/// Workers start on demand up to the concurrency limit and retire after an
/// idle period, so an engine that stops formatting formulas does not keep
/// threads alive, and a burst of formulas reuses the same threads instead of
/// creating one per formula.
/// </remarks>
internal sealed class MathHostCallbackScheduler : TaskScheduler
{
    private static readonly TimeSpan IdleRetirement = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Queue<Task> _tasks = new();
    private readonly int _concurrency;
    private int _workers;
    private int _idleWorkers;

    internal MathHostCallbackScheduler(int concurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        _concurrency = concurrency;
    }

    public override int MaximumConcurrencyLevel => _concurrency;

    protected override IEnumerable<Task> GetScheduledTasks()
    {
        lock (_gate)
            return _tasks.ToArray();
    }

    protected override void QueueTask(Task task)
    {
        lock (_gate)
        {
            _tasks.Enqueue(task);
            // An idle worker per queued task takes it; otherwise start one
            // more worker while the limit allows. A worker drains the queue
            // before it waits, so a missed wake-up never strands a task.
            if (_idleWorkers >= _tasks.Count)
            {
                Monitor.Pulse(_gate);
                return;
            }

            if (_workers >= _concurrency)
                return;

            _workers++;
        }

        try
        {
            var worker = new Thread(RunWorker)
            {
                IsBackground = true,
                Name = "Markdown math host callback",
            };
            worker.Start();
        }
        catch
        {
            // Fail the queueing only if no running worker has taken the task;
            // a task that a worker already took must run exactly once.
            bool withdrawn;
            lock (_gate)
            {
                _workers--;
                withdrawn = RemoveQueuedLocked(task);
            }

            if (withdrawn)
                throw;
        }
    }

    protected override bool TryDequeue(Task task)
    {
        lock (_gate)
            return RemoveQueuedLocked(task);
    }

    protected override bool TryExecuteTaskInline(
        Task task,
        bool taskWasPreviouslyQueued) => false;

    private void RunWorker()
    {
        while (true)
        {
            Task task;
            lock (_gate)
            {
                while (_tasks.Count == 0)
                {
                    _idleWorkers++;
                    bool signaled = Monitor.Wait(_gate, IdleRetirement);
                    _idleWorkers--;
                    if (!signaled && _tasks.Count == 0)
                    {
                        _workers--;
                        return;
                    }
                }

                task = _tasks.Dequeue();
            }

            _ = TryExecuteTask(task);
        }
    }

    private bool RemoveQueuedLocked(Task task)
    {
        int count = _tasks.Count;
        bool removed = false;
        for (int index = 0; index < count; index++)
        {
            Task queued = _tasks.Dequeue();
            if (!removed && ReferenceEquals(queued, task))
            {
                removed = true;
                continue;
            }

            _tasks.Enqueue(queued);
        }

        return removed;
    }
}
