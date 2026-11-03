using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// The admitted worker and borrowed inputs of one render in
/// <see cref="WorkerDocumentRenderer"/>. It is passed by <see langword="in"/>
/// reference to the render's synchronous steps. The render clears its copy
/// when it finishes so a pooled state machine does not retain the source.
/// </summary>
internal readonly struct WorkerRenderInputs(
    WorkerLease lease,
    WindowsWorkerProcess worker,
    WorkerDocumentHandle document,
    SvgPreflightResult preflight,
    MarkdownSvgOpenRequest openRequest,
    MarkdownSvgRenderRequest renderRequest,
    SharedMemoryLease outputMemory,
    long requestGeneration,
    long diagnosticWorkId,
    long targetPixels,
    CancellationToken cancellationToken)
{
    /// <summary>The admitted worker lease.</summary>
    internal readonly WorkerLease Lease = lease;

    /// <summary>The admitted worker.</summary>
    internal readonly WindowsWorkerProcess Worker = worker;

    /// <summary>The document being rendered.</summary>
    internal readonly WorkerDocumentHandle Document = document;

    /// <summary>The document's preflight, which owns its source.</summary>
    internal readonly SvgPreflightResult Preflight = preflight;

    /// <summary>The request the document was opened with.</summary>
    internal readonly MarkdownSvgOpenRequest OpenRequest = openRequest;

    /// <summary>The render request.</summary>
    internal readonly MarkdownSvgRenderRequest RenderRequest = renderRequest;

    /// <summary>The output section the worker rasterizes into.</summary>
    internal readonly SharedMemoryLease OutputMemory = outputMemory;

    /// <summary>The font generation admitted for this render.</summary>
    internal readonly long RequestGeneration = requestGeneration;

    /// <summary>The diagnostic work id for stage tracing.</summary>
    internal readonly long DiagnosticWorkId = diagnosticWorkId;

    /// <summary>The requested target pixel count for stage tracing.</summary>
    internal readonly long TargetPixels = targetPixels;

    /// <summary>The caller's cancellation.</summary>
    internal readonly CancellationToken CancellationToken = cancellationToken;
}
