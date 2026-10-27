#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Diagnostics;
using MarkdownRenderer.Diagnostics;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal enum SvgWorkerPolicyPhase
{
    Evaluated = 1,
    StartRequested = 2,
    WorkerLaunchStarted = 3,
    WorkerProcessStarted = 4,
    Ready = 5,
    Failed = 6,
    TraceLimitReached = 7,
}

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

    internal bool IsEnabled => MarkdownPerformanceEventSource.Log.IsSvgWorkerPolicyMeasurementEnabled();

    /// <summary>
    /// Records one numeric scheduling snapshot. For an evaluation event whose
    /// decision sequence is zero, the assigned event sequence becomes that
    /// decision's correlation ID.
    /// </summary>
    internal long Record(MarkdownSvgWorkerPolicySnapshot snapshot)
    {
        MarkdownPerformanceEventSource log = MarkdownPerformanceEventSource.Log;
        if (!log.IsSvgWorkerPolicyMeasurementEnabled())
            return 0;

        int eventCount = Interlocked.Increment(ref _eventCount);
        if (eventCount > _maximumEvents)
        {
            if (Interlocked.CompareExchange(ref _limitEventWritten, 1, 0) == 0)
            {
                long limitSequence = Interlocked.Increment(ref _nextEventSequence);
                log.RecordSvgWorkerPolicy(CreateSignal(
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

        log.RecordSvgWorkerPolicy(CreateSignal(snapshot with
        {
            EventSequence = sequence,
            DecisionSequence = decisionSequence,
        }));
        return sequence;
    }

    private static MarkdownSvgWorkerPolicySnapshot CreateSignal(
        MarkdownSvgWorkerPolicySnapshot snapshot) =>
        snapshot with
        {
            ProcessId = Environment.ProcessId,
            StopwatchTicks = Stopwatch.GetTimestamp(),
            StopwatchFrequency = Stopwatch.Frequency,
        };
}
#endif
