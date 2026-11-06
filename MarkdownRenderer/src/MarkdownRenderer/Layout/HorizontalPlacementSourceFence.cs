namespace MarkdownRenderer.Layout;

internal readonly struct HorizontalPlacementSourceFence(IHorizontalOverflowBox Box, double Offset)
{
    public IHorizontalOverflowBox Box { get; init; } = Box;
    public double Offset { get; init; } = Offset;
}
