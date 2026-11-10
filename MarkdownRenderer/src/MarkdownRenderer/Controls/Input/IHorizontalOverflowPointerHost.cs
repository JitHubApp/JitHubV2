using MarkdownRenderer.Layout.Internal.Boxes;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// The renderer service a <see cref="HorizontalOverflowPointerDrag"/> calls
/// after it moves a horizontally scrolling block.
/// </summary>
internal interface IHorizontalOverflowPointerHost
{
    /// <summary>
    /// Repaints <paramref name="target"/> at its new offset, moves its hosted
    /// embeds and notifies automation clients of the scroll.
    /// </summary>
    void RefreshHorizontalOverflowVisualState(IHorizontalOverflowBox target, double oldOffset);
}
