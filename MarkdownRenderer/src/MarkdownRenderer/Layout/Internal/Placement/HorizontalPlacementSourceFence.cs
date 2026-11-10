using MarkdownRenderer.Layout.Internal.Boxes;

namespace MarkdownRenderer.Layout.Internal.Placement;

internal readonly struct HorizontalPlacementSourceFence(IHorizontalOverflowBox Box, double Offset)
{
    public IHorizontalOverflowBox Box { get; init; } = Box;
    public double Offset { get; init; } = Offset;
}
