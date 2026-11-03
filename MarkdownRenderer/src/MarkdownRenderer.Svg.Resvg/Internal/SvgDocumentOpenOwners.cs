namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// What one <see cref="SvgDocumentOpenOperation"/> open still owns. It is a
/// local of the open's async method passed by reference to its synchronous
/// steps, so clearing a field here also clears the async state machine's copy
/// and a retained task cannot pin a source, reservation or worker handle.
/// </summary>
internal struct SvgDocumentOpenOwners
{
    /// <summary>The renderer's actual-work borrow for this open.</summary>
    internal SvgDocumentLifetime.RenderLease? OpenOperation;

    /// <summary>The caller's cancellation linked with renderer shutdown.</summary>
    internal CancellationTokenSource? OpenCancellation;

    /// <summary>The initial snapshot reservation until preflight accepts it.</summary>
    internal SvgProcessSnapshotAdmission.Lease? SnapshotAdmission;

    /// <summary>The accepted preflight until the returned document owns its source.</summary>
    internal SvgPreflightResult? Preflight;

    /// <summary>A worker document that no returned document owns yet.</summary>
    internal WorkerDocumentHandle? UnownedWorkerDocument;

    /// <summary>Whether the returned document owns the preflight source.</summary>
    internal bool SourceTransferredToDocument;
}
