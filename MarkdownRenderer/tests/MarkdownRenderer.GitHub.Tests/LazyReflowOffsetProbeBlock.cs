namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A probe root that arranges itself below the requested Y, so its placement
/// is not in document order.
/// </summary>
internal sealed class LazyReflowOffsetProbeBlock(float height, float verticalOffset) : LazyReflowProbeBlock(height)
{
    public override void Arrange(float x, float y, float width)
    {
        base.Arrange(x, y + verticalOffset, width);
    }
}
