namespace MarkdownRenderer.Math.Internal;

/// <summary>
/// Bounds synchronous or cancellation-ignoring host callbacks for the math
/// extension or processor that owns this gate.
/// </summary>
/// <remarks>
/// A callback that ignores cancellation keeps its admission slot until it
/// returns, so at most <see cref="MaximumConcurrentCallbacks"/> stuck callbacks
/// can hold threads for one owner. A saturated gate makes later formulas wait
/// for admission within their own processing deadline instead of starting
/// another callback.
/// </remarks>
internal sealed class MathHostCallbackGate
{
    internal const int MaximumConcurrentCallbacks = 4;

    private readonly SemaphoreSlim _admission = new(
        MaximumConcurrentCallbacks,
        MaximumConcurrentCallbacks);
    private readonly MathHostCallbackScheduler _scheduler = new(MaximumConcurrentCallbacks);

    internal async ValueTask<Task<T>> StartAsync<T>(
        Func<CancellationToken, ValueTask<T>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        await _admission.WaitAsync(cancellationToken).ConfigureAwait(false);

        bool leaseTransferred = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A host callback may block synchronously and ignore cancellation,
            // so its entry point runs on the dedicated scheduler rather than on
            // the shared thread pool.
            Task<T> callbackTask = Task.Factory.StartNew(
                    () => InvokeAsync(callback, cancellationToken),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    _scheduler)
                .Unwrap();
            leaseTransferred = true;
            ObserveFault(callbackTask);
            return callbackTask;
        }
        finally
        {
            if (!leaseTransferred)
                _admission.Release();
        }
    }

    private async Task<T> InvokeAsync<T>(
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
            _admission.Release();
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
