using MarkdownRenderer.Layout.Internal.Boxes;

namespace MarkdownRenderer.Layout.Internal.Focus;

/// <summary>
/// One keyboard focus plan entry; horizontally scrollable owners are filtered
/// at query time.
/// </summary>
internal readonly struct FocusablePlanEntry(
    FocusableItem Item,
    IHorizontalOverflowBox? HorizontalOverflow)
{
    public readonly FocusableItem Item = Item;
    public readonly IHorizontalOverflowBox? HorizontalOverflow = HorizontalOverflow;
}
