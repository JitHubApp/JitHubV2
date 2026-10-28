using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls;

internal enum HorizontalArrowHandlingOrder
{
    SpatialThenOverflow,
    OverflowThenSpatial,
}

/// <summary>
/// Pure keyboard-routing decisions shared by the WinUI event handler and
/// platform-independent contract tests.
/// </summary>
internal static class MarkdownKeyboardInputPolicy
{
    internal static bool ShouldSelectAll(bool controlDown, bool selectionEnabled) =>
        controlDown && selectionEnabled;

    internal static HorizontalArrowHandlingOrder GetHorizontalArrowHandlingOrder(
        FocusableItemKind? focusedKind) =>
        focusedKind == FocusableItemKind.HorizontalOverflow
            ? HorizontalArrowHandlingOrder.OverflowThenSpatial
            : HorizontalArrowHandlingOrder.SpatialThenOverflow;
}
