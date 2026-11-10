
namespace MarkdownRenderer.Controls.Internal.Selection.Adorner;

/// <summary>
/// Determines whether a viewport change can affect selection-adornment state.
/// Kept value-only so the scroll callback can skip redundant Rendering
/// subscriptions without inspecting layout or allocating.
/// </summary>
internal static class SelectionAdornerScrollRefreshPolicy
{
    internal static bool ShouldRefreshForScroll(
        bool hasActiveSelection,
        bool hasHoveredLink,
        bool hasFocusedLink,
        bool hasVisibleAdorner,
        bool hasRetainedRectangles,
        bool hasTouchHandles,
        bool hasPendingRefresh)
        => hasActiveSelection ||
           hasHoveredLink ||
           hasFocusedLink ||
           hasVisibleAdorner ||
           hasRetainedRectangles ||
           hasTouchHandles ||
           hasPendingRefresh;
}
