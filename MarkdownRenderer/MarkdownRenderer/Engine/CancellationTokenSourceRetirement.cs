using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer;

/// <summary>
/// Requests cancellation without running token callbacks on the caller and
/// retires the source only after both those callbacks and its owning work have
/// finished.
/// </summary>
internal static class CancellationTokenSourceRetirement
{
    private static readonly ConditionalWeakTable<CancellationTokenSource, CancellationState>
        _cancellationStates = new();

    internal static Task RequestCancellation(CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _cancellationStates.GetValue(
            source,
            static _ => new CancellationState()).Request(source);
    }

    /// <summary>
    /// Requests cancellation and returns the complete source-retirement
    /// lifetime. The returned task does not complete until both registered
    /// cancellation callbacks and the supplied owning work have retired.
    /// </summary>
    internal static Task CancelAndDisposeAfter(
        CancellationTokenSource source,
        Task? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        Task cancellationCallbacks = RequestCancellation(source);
        return DisposeAfter(source, cancellationCallbacks, lifetime);
    }

    internal static Task DisposeAfter(
        CancellationTokenSource source,
        Task? cancellationCallbacks,
        Task? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        Task retirement = DisposeAfterCoreAsync(
            source,
            cancellationCallbacks ?? Task.CompletedTask,
            lifetime ?? Task.CompletedTask);
        ObserveFault(retirement);
        return retirement;
    }

    private static async Task DisposeAfterCoreAsync(
        CancellationTokenSource source,
        Task cancellationCallbacks,
        Task lifetime)
    {
        try
        {
            await Task.WhenAll(cancellationCallbacks, lifetime).ConfigureAwait(false);
        }
        catch
        {
            // Both inputs are observed before source disposal. Cancellation
            // registration and worker failures are surfaced by their respective
            // owners; they must not strand the cancellation source itself.
            ObserveCompletedFault(cancellationCallbacks);
            ObserveCompletedFault(lifetime);
        }
        finally
        {
            try { source.Dispose(); }
            catch (ObjectDisposedException) { }
            _cancellationStates.Remove(source);
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

    private static void ObserveCompletedFault(Task task)
    {
        if (task.IsFaulted)
            _ = task.Exception;
    }

    private sealed class CancellationState
    {
        private readonly object _gate = new();
        private Task? _callbacks;

        internal Task Request(CancellationTokenSource source)
        {
            lock (_gate)
            {
                if (_callbacks is not null)
                    return _callbacks;

                try
                {
                    // CancelAsync transitions IsCancellationRequested before
                    // returning, but schedules registrations away from the
                    // initiating thread. Retain its first task so a later
                    // retirement cannot mistake an already-canceling source for
                    // one whose callbacks have completed.
                    _callbacks = source.CancelAsync();
                    ObserveFault(_callbacks);
                }
                catch (ObjectDisposedException)
                {
                    // The owning operation completed while a concurrent
                    // cancellation request was being coordinated.
                    _callbacks = Task.CompletedTask;
                }

                return _callbacks;
            }
        }
    }
}
