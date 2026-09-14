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
            Task<T> callbackTask = Task.Run(
                async () =>
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
                },
                CancellationToken.None);
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
}
