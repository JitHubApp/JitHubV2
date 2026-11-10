using System.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;

/// <summary>
/// The diagnostic and timeout-evidence state of one
/// <see cref="WindowsWorkerProcess.ExchangeAsync"/> transaction. It is a local
/// of the exchange passed by reference to its synchronous steps, so tracing a
/// request adds no allocation to the exchange path.
/// </summary>
internal struct WorkerExchangeTrace
{
    /// <summary>Creates the trace and resolves the identifiers every stage record reports.</summary>
    /// <param name="request">The request as the caller supplied it.</param>
    /// <param name="workId">The diagnostic work id, or zero when not traced.</param>
    /// <param name="documentId">The diagnostic document id, or zero to report the request's.</param>
    /// <param name="sourceBytes">The diagnostic source size, or negative to report the request's.</param>
    /// <param name="targetPixels">The diagnostic target pixels, or negative to report the request's.</param>
    internal WorkerExchangeTrace(
        WorkerRequest request,
        long workId,
        ulong documentId,
        long sourceBytes,
        long targetPixels)
    {
        WorkId = workId;
        DocumentId = documentId != 0 ? documentId : request.DocumentId;
        SourceBytes = sourceBytes >= 0 ? sourceBytes : request.SourceLength;
        TargetPixels = targetPixels >= 0
            ? targetPixels
            : (long)request.TargetWidth * request.TargetHeight;
    }

    /// <summary>The diagnostic work id, or zero when not traced.</summary>
    internal readonly long WorkId;

    /// <summary>The document id every stage record reports.</summary>
    internal readonly ulong DocumentId;

    /// <summary>The source size the queue-wait and transaction records report.</summary>
    internal readonly long SourceBytes;

    /// <summary>The target pixels the queue-wait and transaction records report.</summary>
    internal readonly long TargetPixels;

    /// <summary>The stage the whole transaction is recorded as, or zero for none.</summary>
    internal int EffectiveStageId;

    /// <summary>The transaction's trace start, or zero when it is not recorded.</summary>
    internal long TransactionStartedAt;

    /// <summary>The worker's CPU ticks when the transaction began, or -1.</summary>
    internal long WorkerCpuStartedAt;

    /// <summary>Whether a timeout-evidence listener is installed.</summary>
    internal bool CaptureTimeoutEvidence;

    /// <summary>The deadline and CPU window a timeout reports against.</summary>
    internal WorkerTimeoutEvidenceScope EvidenceWindow;

    /// <summary>The timestamp before the request write, or zero without evidence capture.</summary>
    internal long RequestStarted;

    /// <summary>0 while writing, 1 while flushing, 2 while reading the response.</summary>
    internal int TransportPhase;

    /// <summary>How long the request write and flush took, or -1.</summary>
    internal int RequestWriteMilliseconds = -1;

    /// <summary>Captures the request write time when timeout evidence is being collected.</summary>
    internal void MarkRequestWritten()
    {
        if (CaptureTimeoutEvidence)
        {
            RequestWriteMilliseconds = (int)Math.Min(
                Stopwatch.GetElapsedTime(RequestStarted).TotalMilliseconds,
                int.MaxValue);
        }
    }

    /// <summary>Records one pipe stage of this transaction.</summary>
    /// <param name="stage">The stage that ended.</param>
    /// <param name="workerInstanceId">The worker that served it.</param>
    /// <param name="requestId">The request id.</param>
    /// <param name="startedAt">The stage's trace start.</param>
    internal readonly void RecordPipeStage(
        SvgWorkerStageId stage,
        long workerInstanceId,
        ulong requestId,
        long startedAt) =>
        SvgWorkerStageTrace.Record(
            WorkId,
            stage,
            workerInstanceId,
            requestId,
            DocumentId,
            startedAt);
}
