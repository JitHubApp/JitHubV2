namespace MarkdownRenderer.Svg.Resvg.Internal.Preflight;

/// <summary>
/// Keeps byte/pixel accounting deduplicated by payload while retaining
/// bounded context-sensitive validation for nested SVGs. A shared payload
/// may be reached at different nesting depths, and each depth can expose a
/// different over-limit descendant path.
/// </summary>
internal sealed class SvgResourceInspectionContext : IDisposable
{
    private readonly SvgResourceInspectionWorkspacePool? _pool;
    private SvgResourceInspectionWorkspace? _workspace;

    /// <summary>
    /// Borrows a workspace from <paramref name="pool"/>, or uses a private
    /// one that is dropped on dispose when no pool is supplied.
    /// </summary>
    internal SvgResourceInspectionContext(SvgResourceInspectionWorkspacePool? pool)
    {
        _pool = pool;
        _workspace = pool?.Rent() ?? new SvgResourceInspectionWorkspace();
    }

    // Per inspection, digest-only and positive raster entries: never retain
    // an XML attribute string or decoded payload. Nested SVG validation is
    // depth-sensitive and deliberately cannot take this shortcut.
    internal bool IsRasterValidated(SvgRasterReferenceKey key) =>
        GetWorkspace().ValidatedRasterReferences.Contains(key);

    internal void RememberRaster(SvgRasterReferenceKey key)
    {
        SvgResourceInspectionWorkspace workspace = GetWorkspace();
        if (workspace.ValidatedRasterReferences.Count < 256)
            workspace.ValidatedRasterReferences.Add(key);
    }

    public bool AddResource(SvgRasterReferenceKey identity) =>
        GetWorkspace().Resources.Add(identity);

    internal char[] GetAttributeScratch(int maximumCapacity)
    {
        SvgResourceInspectionWorkspace workspace = GetWorkspace();
        int capacity = Math.Min(1026, maximumCapacity);
        if (workspace.AttributeScratch is not { } scratch || scratch.Length != capacity)
            workspace.AttributeScratch = scratch = new char[capacity];
        return scratch;
    }

    public bool IsNestedSvgValidatedAtDepth(SvgRasterReferenceKey identity, int embeddedDepth) =>
        GetWorkspace().DeepestValidatedNestedSvgDepth.TryGetValue(identity, out int deepestDepth) &&
        deepestDepth >= embeddedDepth;

    public void MarkNestedSvgValidated(SvgRasterReferenceKey identity, int embeddedDepth)
    {
        Dictionary<SvgRasterReferenceKey, int> validated = GetWorkspace().DeepestValidatedNestedSvgDepth;
        if (!validated.TryGetValue(identity, out int deepestDepth) ||
            embeddedDepth > deepestDepth)
        {
            validated[identity] = embeddedDepth;
        }
    }

    public void Dispose()
    {
        SvgResourceInspectionWorkspace? workspace = Interlocked.Exchange(ref _workspace, null);
        if (workspace is null)
            return;

        // Detach the lease before making the workspace visible to another
        // inspection. A stale or repeated Dispose on this context can then
        // neither clear nor observe a later borrower's keys.
        if (_pool is null)
            workspace.Clear();
        else
            _pool.Return(workspace);
    }

    private SvgResourceInspectionWorkspace GetWorkspace() =>
        Volatile.Read(ref _workspace) ?? throw new ObjectDisposedException(nameof(SvgResourceInspectionContext));
}
