using System.Diagnostics.Tracing;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Privacy-safe worker deadline evidence. Stage 0 is process startup; stages
/// 1-5 match the fixed worker operation codes in the binary protocol. Worker
/// process CPU time is measured during the transaction; -1 means unavailable.
/// Transport phase 0/1/2 identifies request write, pipe flush, or response
/// read; -1 means the timeout happened outside a request transaction.
/// </summary>
[EventSource(Name = "MarkdownRenderer.Svg.Resvg.Worker")]
internal sealed class WorkerTimeoutEvents : EventSource
{
    public static readonly WorkerTimeoutEvents Log = new();

    private WorkerTimeoutEvents()
    {
    }

    [Event(1, Level = EventLevel.Warning)]
    public void Timeout(
        int stage,
        int deadlineMilliseconds,
        int workerProcessCpuMilliseconds,
        int transportPhase,
        int requestWriteMilliseconds,
        int workerExited)
    {
        if (IsEnabled())
            WriteEvent(1, [stage, deadlineMilliseconds, workerProcessCpuMilliseconds,
                transportPhase, requestWriteMilliseconds, workerExited]);
    }
}
