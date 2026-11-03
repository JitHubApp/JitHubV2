namespace MarkdownRenderer.Layout;

internal readonly struct LayoutPlacementInput(
    float baseX,
    float baseTop,
    float logicalX,
    float width,
    float height,
    bool measured,
    bool sharesFlowWithNext)
{
    internal readonly float BaseX = baseX;
    internal readonly float BaseTop = baseTop;
    internal readonly float LogicalX = logicalX;
    internal readonly float Width = width;
    internal readonly float Height = height;
    internal readonly bool Measured = measured;
    internal readonly bool SharesFlowWithNext = sharesFlowWithNext;
}
