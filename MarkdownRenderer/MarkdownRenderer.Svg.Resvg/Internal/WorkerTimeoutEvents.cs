using System.Diagnostics.Tracing;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Privacy-safe worker deadline evidence. Stage 0 is process startup; stages
/// 1-5 match the fixed worker operation codes in the binary protocol. Worker
/// process CPU time is measured during the transaction; -1 means unavailable.
/// Transport phase 0/1/2 identifies request write, pipe flush, or response
/// read; -1 means the timeout happened outside a request transaction. The
/// Open progress is the last successful, content-free checkpoint: 0 not
/// started, 1 mapping opened, 2 hash verified, 3 XML parsed, 4 security
/// inspected, 5 font gate passed, 6 cached tree acquired, 7 document attached,
/// 8 options constructed, 9 resolver configured, 10 theme source resolved,
/// 11 source ready immediately before usvg conversion, and 12 usvg tree built;
/// -1 means the request was not Open. Elapsed wall time is measured from the
/// start of the evidence window (the Open transaction or full font-init
/// window), matching the worker CPU delta. Worker input and executable hashes
/// are uppercase SHA-256 hex and are empty when unavailable. Working set,
/// private commit, and page faults are sampled only on a hard timeout with an
/// enabled listener. Memory counters are in KiB; -1 means unavailable. Page
/// faults are cumulative for the worker process, not request-local.
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
        int workerPageFaults,
        int elapsedWallMilliseconds,
        string workerInputSha256,
        string workerExecutableSha256)
    {
        if (IsEnabled())
            WriteEvent(1, [stage, deadlineMilliseconds, workerProcessCpuMilliseconds,
                transportPhase, requestWriteMilliseconds, workerExited,
                openProgressPhase, workerWorkingSetKiB, workerPrivateCommitKiB,
                workerPageFaults, elapsedWallMilliseconds, workerInputSha256,
                workerExecutableSha256]);
    }
}
