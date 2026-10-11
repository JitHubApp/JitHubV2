using System;
using System.Threading;
using System.Threading.Tasks;
using static MarkdownRenderer.TaskFaultObservation;

namespace MarkdownRenderer.Images;

/// <summary>
/// Applies the renderer deadline to both the resolver's cancellation token and
/// the caller's wait. A resolver that observes cancellation can stop its work;
/// one that does not cannot keep the renderer waiting indefinitely.
/// </summary>
internal static class ImageResolverDeadline
{
    internal static async ValueTask<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        TimeSpan timeout,
        CancellationToken documentCancellationToken,
        Action<T>? abandonedResultDisposer = null,
        Action<Task>? abandonedCompletionObserver = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        documentCancellationToken.ThrowIfCancellationRequested();
        using var resolverCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(documentCancellationToken);
        resolverCancellation.CancelAfter(timeout);

        Task<T>? operationTask = null;
        try
        {
            operationTask = operation(resolverCancellation.Token).AsTask();
            ObserveFault(operationTask);
            T result = await operationTask
                .WaitAsync(resolverCancellation.Token)
                .ConfigureAwait(false);
            // A resolver may complete in an earlier cancellation callback,
            // before the linked token/waiter observes document cancellation.
            // Do not publish that now-abandoned result; the catch path still
            // owns exactly-once disposal of the successful operation task.
            documentCancellationToken.ThrowIfCancellationRequested();
            resolverCancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException exception)
        {
            if (operationTask is not null)
            {
                Task abandonedCompletion = DisposeSuccessfulAbandonedResult(
                    operationTask,
                    abandonedResultDisposer);
                ObserveFault(abandonedCompletion);
                ObserveAbandonedCompletion(abandonedCompletion, abandonedCompletionObserver);
            }

            if (!documentCancellationToken.IsCancellationRequested &&
                resolverCancellation.IsCancellationRequested)
            {
                throw new TimeoutException("The image resolver exceeded its deadline.", exception);
            }

            throw;
        }
    }

    private static Task DisposeSuccessfulAbandonedResult<T>(
        Task<T> operationTask,
        Action<T>? disposer)
    {
        if (disposer is null)
            return operationTask.IsCompleted ? Task.CompletedTask : operationTask;

        if (operationTask.IsCompleted)
        {
            try
            {
                DisposeSuccessfulResult(operationTask, disposer);
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                // Surface cleanup failure to an owner that tracks detached work,
                // while RunAsync still preserves its original cancellation/timeout.
                return Task.FromException(exception);
            }
        }

        // Keep the actual resolver task alive until it settles. A late successful
        // result is abandoned by this call and must release any source-byte lease
        // it owns; faulted results remain observed without exposing source data.
        return operationTask.ContinueWith(
            static (completed, state) =>
            {
                var resultDisposer = (Action<T>)state!;
                DisposeSuccessfulResult(completed, resultDisposer);
            },
            disposer,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveAbandonedCompletion(
        Task completion,
        Action<Task>? observer)
    {
        if (observer is null)
            return;

        try
        {
            observer(completion);
        }
        catch (Exception)
        {
            // The observation hook must not replace the resolver's cancellation
            // or timeout, and it must not expose source or result data.
            ObserveFault(completion);
        }
    }

    private static void DisposeSuccessfulResult<T>(Task<T> operationTask, Action<T> disposer)
    {
        if (operationTask.Status == TaskStatus.RanToCompletion)
        {
            // Let the cleanup-completion task fault so its owner can retire the
            // detached operation with the correct failure status. The exception
            // stays source-free: it is never formatted or logged here.
            disposer(operationTask.Result);
        }
        else if (operationTask.IsFaulted)
        {
            _ = operationTask.Exception;
        }
    }

}
