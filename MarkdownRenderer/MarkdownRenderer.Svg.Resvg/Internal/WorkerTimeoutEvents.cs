using System.Diagnostics.Tracing;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Privacy-safe worker deadline evidence. Stage 0 is process startup; stages
/// 1-5 match the fixed worker operation codes in the binary protocol. Worker
/// process CPU time is measured during the transaction; -1 means unavailable.
/// Transport phase 0/1/2 identifies request write, pipe flush, or response
/// read; -1 means the timeout happened outside a request transaction. The
/// Open progress phase is a content-free marker: 0 not started, 1 mapping,
/// 2 hash, 3 XML, 4 security, 5 font gate, 6 tree, 7 document attachment,
/// 8 options initialization, 9 options constructed, 10 resolver configured,
/// 11 theme transformation, and 12 usvg conversion; -1 means the request was
/// not Open. Working set, private commit, and page faults are
/// sampled from the worker only on a hard timeout with an enabled listener.
/// Memory counters are in KiB; -1 means a counter was unavailable. Page faults
/// are cumulative for the worker process, not a request-local delta.
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
        int workerExited,
        int openProgressPhase,
        int workerWorkingSetKiB,
        int workerPrivateCommitKiB,
        int workerPageFaults)
    {
        if (IsEnabled())
            WriteEvent(1, [stage, deadlineMilliseconds, workerProcessCpuMilliseconds,
                transportPhase, requestWriteMilliseconds, workerExited,
                openProgressPhase, workerWorkingSetKiB, workerPrivateCommitKiB,
                workerPageFaults]);
    }
}
