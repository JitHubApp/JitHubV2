namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Records secondary-worker policy decisions and lifecycle phases to the
/// policy trace. It keeps no scheduling state: <see cref="WorkerScheduler"/>
/// calls it only under its state lock and passes the slot and queue values
/// each snapshot reads.
/// </summary>
internal sealed class WorkerPolicyRecorder(
    SvgWorkerPolicyTrace trace,
    WorkerRequestFactory requests,
    WorkerSlot primary)
{
    private readonly SvgWorkerPolicyTrace _trace = trace;
    private readonly WorkerRequestFactory _requests = requests;
    private readonly WorkerSlot _primary = primary;

    /// <summary>
    /// Records one evaluated scheduling decision when tracing is on and there
    /// is visible work or a secondary slot to explain.
    /// </summary>
    /// <param name="secondary">The current secondary slot, if any.</param>
    /// <param name="visibleOpenCount">Queued visible opens.</param>
    /// <param name="visibleRenderCount">Queued visible renders.</param>
    /// <param name="energySaverOn">Whether energy saver was on for this pass.</param>
    /// <param name="energySaverQueryFailed">Whether the energy-saver query failed.</param>
    /// <param name="canUseSecondary">Whether the machine may use a secondary worker.</param>
    /// <param name="shouldStartSecondary">Whether this pass starts a secondary worker.</param>
    /// <param name="snapshot">The recorded snapshot, or default when nothing was recorded.</param>
    /// <returns>The decision's trace sequence, or zero when nothing was recorded.</returns>
    internal long RecordEvaluated(
        WorkerSlot? secondary,
        int visibleOpenCount,
        int visibleRenderCount,
        bool energySaverOn,
        bool energySaverQueryFailed,
        bool canUseSecondary,
        bool shouldStartSecondary,
        out SvgWorkerPolicySnapshot snapshot)
    {
        snapshot = default;
        if (!_trace.IsEnabled ||
            !(visibleOpenCount > 0 || visibleRenderCount > 0 || secondary is not null))
        {
            return 0;
        }

        snapshot = Create(
            (int)SvgWorkerPolicyPhase.Evaluated,
            decisionSequence: 0,
            energySaverOn,
            energySaverQueryFailed,
            canUseSecondary,
            shouldStartSecondary,
            secondary,
            visibleOpenCount,
            visibleRenderCount);
        return _trace.Record(snapshot);
    }

    /// <summary>Records a lifecycle phase of a slot whose start was a traced decision.</summary>
    /// <param name="slot">The secondary slot.</param>
    /// <param name="phase">The phase it reached.</param>
    /// <param name="secondary">The current secondary slot, if any.</param>
    /// <param name="visibleOpenCount">Queued visible opens.</param>
    /// <param name="visibleRenderCount">Queued visible renders.</param>
    internal void RecordPhase(
        WorkerSlot slot,
        SvgWorkerPolicyPhase phase,
        WorkerSlot? secondary,
        int visibleOpenCount,
        int visibleRenderCount)
    {
        if (slot.PolicyDecisionSequence == 0)
            return;

        SvgWorkerPolicySnapshot current = Create(
            (int)phase,
            slot.PolicyDecisionSequence,
            slot.PolicySnapshot.EnergySaverOn != 0,
            slot.PolicySnapshot.EnergySaverQueryFailed != 0,
            slot.PolicySnapshot.CanUseSecondary != 0,
            slot.PolicySnapshot.ShouldStartSecondary != 0,
            secondary,
            visibleOpenCount,
            visibleRenderCount);
        _trace.Record(current);
    }

    private SvgWorkerPolicySnapshot Create(
        int phase,
        long decisionSequence,
        bool energySaverOn,
        bool energySaverQueryFailed,
        bool canUseSecondary,
        bool shouldStartSecondary,
        WorkerSlot? secondary,
        int visibleOpenCount,
        int visibleRenderCount) =>
        WorkerPolicySnapshots.Create(
            phase,
            decisionSequence,
            energySaverOn,
            energySaverQueryFailed,
            canUseSecondary,
            shouldStartSecondary,
            _primary,
            secondary,
            visibleOpenCount,
            visibleRenderCount,
            _requests.FontGeneration);
}
