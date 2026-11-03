namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Keeps at most two cleared inspection workspaces for reuse by the
/// preflights of one renderer. Each renderer owns its own pool, so no
/// workspace is shared across renderer instances.
/// </summary>
internal sealed class SvgResourceInspectionWorkspacePool
{
    private const int MaximumPooledCollectionCapacity = 1024;

    private SvgResourceInspectionWorkspace? _workspace0;
    private SvgResourceInspectionWorkspace? _workspace1;

    /// <summary>Takes a pooled workspace, or creates one when both slots are empty.</summary>
    internal SvgResourceInspectionWorkspace Rent() =>
        Interlocked.Exchange(ref _workspace0, null) ??
        Interlocked.Exchange(ref _workspace1, null) ??
        new SvgResourceInspectionWorkspace();

    /// <summary>
    /// Clears a workspace and keeps it if its collections stayed small and a
    /// slot is free. The caller must already have detached it from its
    /// context, so a stale borrower can never observe a later one's keys.
    /// </summary>
    internal void Return(SvgResourceInspectionWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.Clear();

        if (workspace.CombinedCollectionCapacity > MaximumPooledCollectionCapacity)
            return;
        if (Interlocked.CompareExchange(ref _workspace0, workspace, null) is null)
            return;
        _ = Interlocked.CompareExchange(ref _workspace1, workspace, null);
    }
}
