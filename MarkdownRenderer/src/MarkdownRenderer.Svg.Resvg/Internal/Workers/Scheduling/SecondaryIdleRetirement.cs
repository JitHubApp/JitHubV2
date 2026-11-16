using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

/// <summary>
/// Retires the secondary worker after it has been idle for
/// <see cref="WorkerSchedulingPolicy.SecondaryIdleTimeout"/>. It holds no
/// lock: <see cref="ScheduleLocked"/> and <see cref="CancelLocked"/> run under
/// the scheduler's state lock, and when the timer fires the host re-checks
/// and detaches the slot under that same lock.
/// </summary>
internal sealed class SecondaryIdleRetirement
{
    private readonly ISecondaryIdleRetirementHost _host;
    private readonly WorkerProcessFleet _fleet;
    private readonly TimeProvider _timeProvider;

    internal SecondaryIdleRetirement(
        ISecondaryIdleRetirementHost host,
        WorkerProcessFleet fleet,
        TimeProvider timeProvider)
    {
        _host = host;
        _fleet = fleet;
        _timeProvider = timeProvider;
    }

    /// <summary>Starts the idle timer for a slot that has none. Call under the state lock.</summary>
    /// <param name="slot">The idle secondary slot.</param>
    internal void ScheduleLocked(WorkerSlot slot)
    {
        if (slot.IdleRetirement is not null)
            return;
        slot.IdleSince = _timeProvider.GetUtcNow();
        var cancellation = new CancellationTokenSource();
        slot.IdleRetirement = cancellation;
        _ = RetireAfterIdleAsync(slot, cancellation);
    }

    /// <summary>Cancels the slot's idle timer, if any. Call under the state lock.</summary>
    /// <param name="slot">The slot that is busy again or leaving the pool.</param>
    internal static void CancelLocked(WorkerSlot slot)
    {
        CancellationTokenSource? cancellation = slot.IdleRetirement;
        slot.IdleRetirement = null;
        if (cancellation is null)
            return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task RetireAfterIdleAsync(
        WorkerSlot slot,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(
                WorkerSchedulingPolicy.SecondaryIdleTimeout,
                _timeProvider,
                cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_host.TryDetachIdleSecondary(slot, cancellation, out WindowsWorkerProcess? worker))
            return;
        cancellation.Dispose();
        _fleet.Retire(worker);
    }
}
