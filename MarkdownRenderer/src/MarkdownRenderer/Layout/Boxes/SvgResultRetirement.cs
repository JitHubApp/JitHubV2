using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Internal.Concurrency;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Awaits SVG provider results so that a result the caller never receives,
/// because cancellation won the wait, is still disposed when its task
/// finishes.
/// </summary>
internal static class SvgResultRetirement
{
    /// <summary>
    /// Awaits <paramref name="operation"/> under <paramref name="cancellationToken"/>;
    /// an abandoned result is disposed when its task completes.
    /// </summary>
    internal static async Task<T> AwaitAsync<T>(
        ValueTask<T> operation,
        CancellationToken cancellationToken)
        where T : IDisposable
    {
        Task<T> task = operation.AsTask();
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // WaitAsync may report cancellation while the provider task becomes
            // successful immediately afterward. Always attach cleanup, even if
            // the task is already complete; ExecuteSynchronously closes that
            // race and disposes every result that this caller did not receive.
            TaskFaultObservation.ObserveFault(RetireUnclaimed(task));
            throw;
        }
    }

    /// <summary>Disposes the result of <paramref name="task"/> once it completes, and observes its fault.</summary>
    internal static Task RetireUnclaimed<T>(Task<T> task)
        where T : IDisposable
        => RetireUnclaimed(task, nativeUse: null);

    /// <summary>
    /// Disposes the result of <paramref name="task"/> once it completes, then
    /// ends <paramref name="nativeUse"/>, which keeps the provider's document
    /// alive while the abandoned task may still read it.
    /// </summary>
    internal static Task RetireUnclaimed<T>(Task<T> task, IDisposable? nativeUse)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.ContinueWith(
            static (completed, state) =>
            {
                var lease = (IDisposable?)state;
                try
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                    {
                        // Keep native cleanup failures on this barrier task so the
                        // owning ImageBox and LayoutSnapshot can fail retirement.
                        completed.Result.Dispose();
                    }
                    else if (completed.IsFaulted)
                    {
                        // Cancellation can win WaitAsync while a provider later
                        // faults. Observe that abandoned failure as well.
                        _ = completed.Exception;
                    }
                }
                finally
                {
                    lease?.Dispose();
                }
            },
            nativeUse,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
