namespace MarkdownRenderer.Svg.Resvg.Internal.Preflight;

/// <summary>
/// The reusable collections behind one <see cref="SvgResourceInspectionContext"/>.
/// It holds digest keys and one small scratch buffer, never source text.
/// </summary>
internal sealed class SvgResourceInspectionWorkspace
{
    internal HashSet<SvgRasterReferenceKey> Resources { get; } = [];
    internal Dictionary<SvgRasterReferenceKey, int> DeepestValidatedNestedSvgDepth { get; } = [];
    internal HashSet<SvgRasterReferenceKey> ValidatedRasterReferences { get; } = [];
    // Retain only the small initial destination, never a grown data-URI
    // or path buffer. Two workspaces retain at most 4,104 char bytes.
    internal char[]? AttributeScratch { get; set; }

    internal long CombinedCollectionCapacity =>
        (long)Resources.EnsureCapacity(0) +
        DeepestValidatedNestedSvgDepth.EnsureCapacity(0) +
        ValidatedRasterReferences.EnsureCapacity(0);

    internal void Clear()
    {
        Resources.Clear();
        DeepestValidatedNestedSvgDepth.Clear();
        ValidatedRasterReferences.Clear();
        if (AttributeScratch is { } scratch)
            scratch.AsSpan().Clear();
    }
}
