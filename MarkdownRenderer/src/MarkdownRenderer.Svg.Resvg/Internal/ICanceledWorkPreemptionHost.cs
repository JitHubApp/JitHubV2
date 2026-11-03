namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// The scheduler's state lock around each timed step of
/// <see cref="CanceledWorkPreemption"/>. Each member takes the lock and calls
/// the matching <c>*Locked</c> member with the scheduler's queue state.
/// </summary>
internal interface ICanceledWorkPreemptionHost
{
    /// <summary>Reads the remaining grace under the state lock.</summary>
    /// <param name="pending">The canceled active work.</param>
    /// <returns>The delay before the check completes.</returns>
    TimeSpan GetCanceledBlockingGraceDelay(PendingWork pending);

    /// <summary>Completes a timed check under the state lock.</summary>
    /// <param name="slot">The slot the check was started for.</param>
    /// <param name="pending">The canceled work the check was started for.</param>
    /// <returns>The detached worker to retire outside the lock, if any.</returns>
    WindowsWorkerProcess? CompleteCanceledBlockingCheck(WorkerSlot slot, PendingWork pending);
}
