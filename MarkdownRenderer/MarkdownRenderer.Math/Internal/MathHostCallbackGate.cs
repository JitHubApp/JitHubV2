using System.Collections.Concurrent;

namespace MarkdownRenderer.Math.Internal;

/// <summary>
/// Bounds synchronous or cancellation-ignoring host callbacks across all math
/// processors and engine extensions in the process.
/// </summary>
internal static class MathHostCallbackGate
{
    private const int MaximumConcurrentWorkers = 4;
    private static readonly SemaphoreSlim Admission = new(
        MaximumConcurrentWorkers,
        MaximumConcurrentWorkers);
    private static readonly TaskScheduler CallbackScheduler =
        new DedicatedTaskScheduler(MaximumConcurrentWorkers);

    internal static async ValueTask<Task<T>> StartAsync<T>(
        Func<CancellationToken, ValueTask<T>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        await Admission.WaitAsync(cancellationToken).ConfigureAwait(false);

        bool leaseTransferred = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A host callback may block synchronously and ignore cancellation.
            // Keep that entry point off the shared thread pool: saturating the
            // callback gate must not also starve the timer callback that enforces
            // MaximumProcessingTime. The fixed scheduler avoids creating a new
            // operating-system thread for every localized formula.
            Task<T> callbackTask = Task.Factory.StartNew(
                    () => InvokeAsync(callback, cancellationToken),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    CallbackScheduler)
                .Unwrap();
            leaseTransferred = true;
            ObserveFault(callbackTask);
            return callbackTask;
        }
        finally
        {
            if (!leaseTransferred)
                Admission.Release();
        }
    }

    private static async Task<T> InvokeAsync<T>(
        Func<CancellationToken, ValueTask<T>> callback,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await callback(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Admission.Release();
        }
    }

    private static void ObserveFault(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private sealed class DedicatedTaskScheduler : TaskScheduler
    {
        private readonly BlockingCollection<Task> _tasks = new();

        internal DedicatedTaskScheduler(int concurrency)
        {
            MaximumConcurrencyLevel = concurrency;
            for (int index = 0; index < concurrency; index++)
            {
                var worker = new Thread(ConsumeTasks)
                {
                    IsBackground = true,
                    Name = $"Markdown math host callback {index + 1}",
                };
                worker.Start();
            }
        }

        public override int MaximumConcurrencyLevel { get; }

        protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();

        protected override void QueueTask(Task task) => _tasks.Add(task);

        protected override bool TryExecuteTaskInline(
            Task task,
            bool taskWasPreviouslyQueued) => false;

        private void ConsumeTasks()
        {
            foreach (Task task in _tasks.GetConsumingEnumerable())
                _ = TryExecuteTask(task);
        }
    }
}
