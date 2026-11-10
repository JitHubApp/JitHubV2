using MarkdownRenderer.Layout.Internal.Boxes;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A stack that reports itself as a horizontally scrollable owner, so its descendants
/// index under it in the horizontal-overflow alias map.
/// </summary>
internal sealed class SnapshotIndexOverflowStackBox : StackBox, IHorizontalOverflowBox
{
    public double HorizontalOffset => 0;
    public double HorizontalExtent => 200;
    public double HorizontalViewport => 100;
    public bool CanScrollHorizontally => true;
    public bool IsRightToLeft => false;
    public Windows.Foundation.Rect HorizontalViewportBounds => default;
    public Windows.Foundation.Rect HorizontalScrollTrackBounds => default;
    public Windows.Foundation.Rect HorizontalScrollThumbBounds => default;
    public bool SetHorizontalOffset(double offset) => false;
    public bool ScrollHorizontal(double delta) => false;
}
