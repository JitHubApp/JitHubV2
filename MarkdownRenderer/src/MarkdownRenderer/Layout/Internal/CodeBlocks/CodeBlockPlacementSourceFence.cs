
namespace MarkdownRenderer.Layout.Internal.CodeBlocks;

// These strong identities exist only during bounded source capture. The owner
// stores the old code box weakly and never owns its retirement lifetime.
internal readonly struct CodeBlockPlacementSourceFence(
    CodeBlockBox Source, CodeBlockBox Placement, long Revision, bool WasMeasured = true)
{
    public CodeBlockBox Source { get; init; } = Source;
    public CodeBlockBox Placement { get; init; } = Placement;
    public long Revision { get; init; } = Revision;
    public bool WasMeasured { get; init; } = WasMeasured;
}
