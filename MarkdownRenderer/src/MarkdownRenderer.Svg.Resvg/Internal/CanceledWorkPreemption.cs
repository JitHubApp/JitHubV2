namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Terminates a worker that keeps running canceled work while visible work
/// waits, once <see cref="WorkerSchedulingPolicy.CanceledVisibleBlockingGrace"/>
/// has passed. It holds no lock: every <c>*Locked</c> member runs under the
/// scheduler's state lock, and the host takes that lock for each timed step.
/// </summary>
internal sealed class CanceledWorkPreemption
{
    private readonly ICanceledWorkPreemptionHost _host;
    private readonly WorkerProcessFleet _fleet;
    private readonly TimeProvider _timeProvider;

    internal CanceledWorkPreemption(
        ICanceledWorkPreemptionHost host,
        WorkerProcessFleet fleet,
        TimeProvider timeProvider)
    {
        _host = host;
        _fleet = fleet;
        _timeProvider = timeProvider;
    }

    /// <summary>Starts or clears the blocking timer for one slot. Call under the state lock.</summary>
    /// <param name="slot">The slot to evaluate.</param>
    /// <param name="now">The time this evaluation pass sampled.</param>
    /// <param name="visibleQueuedCount">Visible work waiting in the queue.</param>
    internal void EvaluateLocked(WorkerSlot slot, DateTimeOffset now, int visibleQueuedCount)
    {
        PendingWork? active = slot.Active;
        if (active is null || !active.IsCanceled || visibleQueuedCount == 0)
        {
            if (active is not null)
                active.BlockedSince = null;
            slot.KillCanceledWhenStarted = false;
            return;
        }

        active.BlockedSince ??= now;
        if (active.CancellationCheckScheduled)
            return;
        active.CancellationCheckScheduled = true;
        _ = CheckAsync(slot, active);
    }

    /// <summary>The remaining grace before the canceled work may be terminated. Call under the state lock.</summary>
    /// <param name="pending">The canceled active work.</param>
    /// <returns>The delay, never negative.</returns>
    internal TimeSpan GetGraceDelayLocked(PendingWork pending)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset since = pending.BlockedSince ?? now;
        TimeSpan delay = WorkerSchedulingPolicy.CanceledVisibleBlockingGrace - (now - since);
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        return delay;
    }

    /// <summary>
    /// After the grace delay, clears the check, reschedules it, marks a
    /// not-yet-started worker for termination, or detaches the slot's worker.
    /// Call under the state lock.
    /// </summary>
    /// <param name="slot">The slot the check was started for.</param>
    /// <param name="pending">The canceled work the check was started for.</param>
    /// <param name="visibleQueuedCount">Visible work waiting in the queue.</param>
    /// <returns>The detached worker to retire, if any.</returns>
    internal WindowsWorkerProcess? CompleteCheckLocked(WorkerSlot slot, PendingWork pending, int visibleQueuedCount)
    {
        WindowsWorkerProcess? worker = null;
        bool reschedule = false;
        pending.CancellationCheckScheduled = false;
        if (!ReferenceEquals(slot.Active, pending) ||
            pending.BlockedSince is not { } blockedSince ||
            !pending.IsCanceled ||
            visibleQueuedCount == 0)
        {
            pending.BlockedSince = null;
            slot.KillCanceledWhenStarted = false;
            return null;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!WorkerSchedulingPolicy.ShouldTerminateCanceledActive(
                true,
                visibleQueuedCount,
                blockedSince,
                now))
        {
            reschedule = true;
        }
        else if (slot.Worker is null)
        {
            slot.KillCanceledWhenStarted = true;
        }
        else
        {
            worker = slot.Worker;
            slot.Worker = null;
        }

        if (reschedule)
        {
            pending.CancellationCheckScheduled = true;
            _ = CheckAsync(slot, pending);
        }
        return worker;
    }

    private async Task CheckAsync(WorkerSlot slot, PendingWork pending)
    {
        TimeSpan delay = _host.GetCanceledBlockingGraceDelay(pending);
        try
        {
            await Task.Delay(delay, _timeProvider, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        WindowsWorkerProcess? worker = _host.CompleteCanceledBlockingCheck(slot, pending);
        _fleet.Retire(worker);
    }
}
