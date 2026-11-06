using System;
using System.Threading;
using System.Threading.Tasks;
using static MarkdownRenderer.TaskFaultObservation;

namespace MarkdownRenderer;

/// <summary>
/// Requests cancellation without running token callbacks on the caller and
/// retires the source only after both those callbacks and its owning work have
/// finished.
/// </summary>
/// <remarks>
/// Each source carries its own request state; see
/// <see cref="RetirableCancellationTokenSource"/>.
/// </remarks>
internal static class CancellationTokenSourceRetirement
{
    internal static Task RequestCancellation(RetirableCancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.RequestCancellation();
    }

    /// <summary>
    /// Requests cancellation and returns the complete source-retirement
    /// lifetime. The returned task does not complete until both registered
    /// cancellation callbacks and the supplied owning work have retired.
    /// </summary>
    internal static Task CancelAndDisposeAfter(
        RetirableCancellationTokenSource source,
        Task? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        Task cancellationCallbacks = RequestCancellation(source);
        return DisposeAfter(source, cancellationCallbacks, lifetime);
    }

    internal static Task DisposeAfter(
        RetirableCancellationTokenSource source,
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
        RetirableCancellationTokenSource source,
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
        }
    }

    private static void ObserveCompletedFault(Task task)
    {
        if (task.IsFaulted)
            _ = task.Exception;
    }
}
