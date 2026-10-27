using Windows.Foundation;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Contract implemented by blocks whose content has its own horizontal
/// viewport. The document retains sole ownership of vertical scrolling.
/// </summary>
internal interface IHorizontalOverflowBox
{
    double HorizontalOffset { get; }

    double HorizontalExtent { get; }

    double HorizontalViewport { get; }

    bool CanScrollHorizontally { get; }

    bool IsRightToLeft { get; }

    Rect HorizontalViewportBounds { get; }

    Rect HorizontalScrollTrackBounds { get; }

    Rect HorizontalScrollThumbBounds { get; }

    bool SetHorizontalOffset(double offset);

    bool ScrollHorizontal(double delta);
}
