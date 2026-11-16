using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images.Svg;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

/// <summary>
/// Awaits or abandons a queued worker admission without leaking its lease
/// or its cancellation registration.
/// </summary>
internal static class WorkerAdmissionTasks
{
    internal static async Task<TLease> AwaitAdmissionAsync<TLease>(
        Task<TLease> completion,
        CancellationTokenRegistration cancellationRegistration)
    {
        try
        {
            // Successful admission transfers registration ownership to the
            // active lease, which needs it to preempt canceled worker work.
            return await completion.ConfigureAwait(false);
        }
        catch
        {
            // Drained/canceled queue entries never obtain a lease. Detach
            // their callback from a possibly long-lived caller token outside
            // the state lock so disposal can wait for an in-flight callback.
            cancellationRegistration.Dispose();
            throw;
        }
    }

    internal static async Task CancelAndDrainAcquireAsync<TLease>(
        Task<TLease> acquire,
        Action cancelAcquire)
        where TLease : class, IDisposable
    {
        try
        {
            ArgumentNullException.ThrowIfNull(acquire);
            ArgumentNullException.ThrowIfNull(cancelAcquire);
            cancelAcquire();
            try
            {
                using TLease abandoned = await acquire.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or
                ObjectDisposedException or
                MarkdownSvgException)
            {
                // The acquire's original outcome owns its failure semantics.
                // Observe only cancellation/late-grant cleanup here.
            }
        }
        finally
        {
            // A retained completed cleanup Task must not retain the original
            // admission Task's lease/slot or its cancellation callback owner.
            acquire = null!;
            cancelAcquire = null!;
        }
    }
}
