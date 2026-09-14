using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Controls;

/// <summary>Defers owned-resource disposal until all work using it has retired.</summary>
internal static class OwnedResourceRetirement
{
    /// <summary>
    /// Preserves every outstanding lifetime across repeated retirement
    /// boundaries. A later unload, reload, or provider replacement must not
    /// forget work that was already detached from the active task set.
    /// </summary>
    internal static Task CombineLifetimes(Task? previous, Task? current)
    {
        Task first = previous ?? Task.CompletedTask;
        Task second = current ?? Task.CompletedTask;
        if (first.IsCompletedSuccessfully)
            return second;
        if (second.IsCompletedSuccessfully)
            return first;
        if (ReferenceEquals(first, second))
            return first;

        Task combined = Task.WhenAll(first, second);
        ObserveFault(combined);
        return combined;
    }

    internal static Task DisposeAfter(
        IDisposable resource,
        Task? lifetime,
        Action<Exception>? failureObserver = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Task retirement = DisposeAfterCoreAsync(
            resource,
            lifetime ?? Task.CompletedTask,
            failureObserver);
        ObserveFault(retirement);
        return retirement;
    }

    private static async Task DisposeAfterCoreAsync(
        IDisposable resource,
        Task lifetime,
        Action<Exception>? failureObserver)
    {
        try
        {
            await lifetime.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ObserveFailure(failureObserver, exception);
        }

        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            ObserveFailure(failureObserver, exception);
        }
    }

    private static void ObserveFailure(
        Action<Exception>? failureObserver,
        Exception exception)
    {
        try { failureObserver?.Invoke(exception); }
        catch
        {
            // Retirement diagnostics are observational and must not strand or
            // fault a detached resource lifetime.
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
