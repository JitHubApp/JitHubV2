using System.Diagnostics;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// The inputs and mutable progress of one caller that waits for a worker to
/// publish its font generation. <see cref="WorkerTextAdmission"/> creates one
/// only on the slow path, after the caller's worker proved not ready, and its
/// named steps share it so that no step captures locals in a closure. Each
/// step that awaits needs a reference type because an async method cannot
/// take its state by <see langword="ref"/>.
/// </summary>
internal sealed class WorkerFontReadinessWait(
    WorkerOperation operation,
    MarkdownSvgRenderPriority priority,
    long requestGeneration,
    MarkdownSvgRenderDemand? demand,
    CancellationToken cancellationToken,
    CancellationToken waitToken,
    SemaphoreSlim readinessGate,
    Stopwatch elapsed,
    long diagnosticWorkId,
    ulong diagnosticRequestId,
    ulong diagnosticDocumentId,
    long diagnosticSourceBytes,
    long diagnosticTargetPixels)
{
    /// <summary>The operation the caller will dispatch once admitted.</summary>
    internal readonly WorkerOperation Operation = operation;

    /// <summary>The caller's queue priority when no demand ticket overrides it.</summary>
    internal readonly MarkdownSvgRenderPriority Priority = priority;

    /// <summary>The font generation the caller's text must be shaped with.</summary>
    internal readonly long RequestGeneration = requestGeneration;

    /// <summary>The caller's demand ticket, if any.</summary>
    internal readonly MarkdownSvgRenderDemand? Demand = demand;

    /// <summary>The caller's own cancellation.</summary>
    internal readonly CancellationToken CancellationToken = cancellationToken;

    /// <summary>The caller's cancellation linked with pool disposal and the readiness deadline.</summary>
    internal readonly CancellationToken WaitToken = waitToken;

    /// <summary>The gate that serializes HELLO probes against the primary worker.</summary>
    internal readonly SemaphoreSlim ReadinessGate = readinessGate;

    /// <summary>Time since the caller began waiting for readiness.</summary>
    internal readonly Stopwatch Elapsed = elapsed;

    /// <summary>The diagnostic work id for stage tracing.</summary>
    internal readonly long DiagnosticWorkId = diagnosticWorkId;

    /// <summary>The diagnostic request id for stage tracing.</summary>
    internal readonly ulong DiagnosticRequestId = diagnosticRequestId;

    /// <summary>The diagnostic document id for stage tracing.</summary>
    internal readonly ulong DiagnosticDocumentId = diagnosticDocumentId;

    /// <summary>The diagnostic source size for stage tracing.</summary>
    internal readonly long DiagnosticSourceBytes = diagnosticSourceBytes;

    /// <summary>The diagnostic target pixel count for stage tracing.</summary>
    internal readonly long DiagnosticTargetPixels = diagnosticTargetPixels;

    /// <summary>The off-slot admission ticket while the caller holds no worker lease.</summary>
    internal WorkerFontWaitReservation? Reservation;

    /// <summary>Whether this caller currently holds <see cref="ReadinessGate"/>.</summary>
    internal bool GateEntered;

    /// <summary>The worker lease the caller currently holds, if any.</summary>
    internal WorkerLease? CandidateLease;

    /// <summary>Whether <see cref="CandidateLease"/> was handed to the caller.</summary>
    internal bool Transferred;

    /// <summary>The timestamp of the first HELLO probe, or zero before it.</summary>
    internal long InitializationWallStarted;

    /// <summary>The worker's CPU ticks at the first HELLO probe, or -1.</summary>
    internal long WorkerCpuAtStart = -1;

    /// <summary>The worker the last HELLO probe was sent to.</summary>
    internal WindowsWorkerProcess? LastProbeWorker;

    /// <summary>Whether the last HELLO probe was against a worker with no catalog yet.</summary>
    internal bool LastProbeWasInitialCatalog;

    /// <summary>The stage-trace start of the font wait, or zero when not traced.</summary>
    internal long FontWaitStartedAt;

    /// <summary>The last initialization phase the worker reported.</summary>
    internal string InitializationPhase = "font-catalog-startup";
}
