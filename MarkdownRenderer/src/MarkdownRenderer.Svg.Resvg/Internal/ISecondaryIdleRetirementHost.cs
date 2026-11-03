namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>The locked scheduler step that <see cref="SecondaryIdleRetirement"/> runs when its timer fires.</summary>
internal interface ISecondaryIdleRetirementHost
{
    /// <summary>
    /// Under the state lock, detaches the secondary slot and its worker if the
    /// slot is still the pool's idle secondary, still owns this timer, and has
    /// been idle long enough.
    /// </summary>
    /// <param name="slot">The secondary slot the timer was started for.</param>
    /// <param name="cancellation">The timer's cancellation source.</param>
    /// <param name="worker">The detached worker to retire, if any.</param>
    /// <returns>False when the slot was reused, replaced, or is not idle long enough.</returns>
    bool TryDetachIdleSecondary(
        WorkerSlot slot,
        CancellationTokenSource cancellation,
        out WindowsWorkerProcess? worker);
}
