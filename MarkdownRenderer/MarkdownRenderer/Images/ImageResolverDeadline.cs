using System;
using System.Threading;
using System.Threading.Tasks;

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
        CancellationToken documentCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        documentCancellationToken.ThrowIfCancellationRequested();
        using var resolverCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(documentCancellationToken);
        resolverCancellation.CancelAfter(timeout);

        try
        {
            Task<T> operationTask = operation(resolverCancellation.Token).AsTask();
            ObserveFault(operationTask);
            return await operationTask
                .WaitAsync(resolverCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            !documentCancellationToken.IsCancellationRequested &&
            resolverCancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The image resolver exceeded its deadline.", exception);
        }
    }

    private static void ObserveFault(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
            return;
        }

        if (task.IsCompleted)
            return;

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
