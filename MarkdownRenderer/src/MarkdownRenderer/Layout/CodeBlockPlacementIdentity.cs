using System;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

internal readonly struct CodeBlockPlacementIdentity(
    WeakReference<CodeBlockBox> Source, CodeBlockBox Placement, long Revision, bool WasMeasured)
{
    public WeakReference<CodeBlockBox> Source { get; init; } = Source;
    public CodeBlockBox Placement { get; init; } = Placement;
    public long Revision { get; init; } = Revision;
    public bool WasMeasured { get; init; } = WasMeasured;
}
