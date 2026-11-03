using System.Diagnostics;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Emits opt-in scheduling snapshots with a strict per-pool event bound.
/// </summary>
internal sealed class SvgWorkerPolicyTrace(int maximumEvents = 256)
{
    private readonly int _maximumEvents = maximumEvents > 0
        ? maximumEvents
        : throw new ArgumentOutOfRangeException(nameof(maximumEvents));
    private long _nextEventSequence;
    private int _eventCount;
    private int _limitEventWritten;

    internal bool IsEnabled => ResvgEventSource.Log.IsWorkerPolicyMeasurementEnabled();

    /// <summary>
    /// Records one numeric scheduling snapshot. For an evaluation event whose
    /// decision sequence is zero, the assigned event sequence becomes that
    /// decision's correlation ID.
    /// </summary>
    internal long Record(SvgWorkerPolicySnapshot snapshot)
    {
        ResvgEventSource log = ResvgEventSource.Log;
        if (!log.IsWorkerPolicyMeasurementEnabled())
            return 0;

        int eventCount = Interlocked.Increment(ref _eventCount);
        if (eventCount > _maximumEvents)
        {
            if (Interlocked.CompareExchange(ref _limitEventWritten, 1, 0) == 0)
            {
                long limitSequence = Interlocked.Increment(ref _nextEventSequence);
                log.RecordWorkerPolicy(CreateSignal(
                    snapshot with
                    {
                        Phase = (int)SvgWorkerPolicyPhase.TraceLimitReached,
                        EventSequence = limitSequence,
                        DecisionSequence = snapshot.DecisionSequence,
                    }));
            }
            return 0;
        }

        long sequence = Interlocked.Increment(ref _nextEventSequence);
        long decisionSequence = snapshot.DecisionSequence;
        if (snapshot.Phase == (int)SvgWorkerPolicyPhase.Evaluated && decisionSequence == 0)
            decisionSequence = sequence;

        log.RecordWorkerPolicy(CreateSignal(snapshot with
        {
            EventSequence = sequence,
            DecisionSequence = decisionSequence,
        }));
        return sequence;
    }

    private static SvgWorkerPolicySnapshot CreateSignal(
        SvgWorkerPolicySnapshot snapshot) =>
        snapshot with
        {
            ProcessId = Environment.ProcessId,
            StopwatchTicks = Stopwatch.GetTimestamp(),
            StopwatchFrequency = Stopwatch.Frequency,
        };
}
