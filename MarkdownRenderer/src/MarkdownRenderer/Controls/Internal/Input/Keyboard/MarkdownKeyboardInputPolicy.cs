using Windows.System;
using MarkdownRenderer.Layout.Internal.Focus;

namespace MarkdownRenderer.Controls.Internal.Input.Keyboard;

/// <summary>
/// Pure keyboard-routing decisions shared by the WinUI event handler and
/// platform-independent contract tests.
/// </summary>
internal static class MarkdownKeyboardInputPolicy
{
    internal static bool ShouldSelectAll(bool controlDown, bool selectionEnabled) =>
        controlDown && selectionEnabled;

    /// <summary>
    /// True when <paramref name="key"/> opens the selection's context menu:
    /// the Application (menu) key or Shift+F10, while a selection is active.
    /// </summary>
    internal static bool ShouldOpenSelectionContextMenu(VirtualKey key, bool shiftDown, bool selectionActive) =>
        selectionActive && (key == VirtualKey.Application || (key == VirtualKey.F10 && shiftDown));

    internal static HorizontalArrowHandlingOrder GetHorizontalArrowHandlingOrder(
        FocusableItemKind? focusedKind) =>
        focusedKind == FocusableItemKind.HorizontalOverflow
            ? HorizontalArrowHandlingOrder.OverflowThenSpatial
            : HorizontalArrowHandlingOrder.SpatialThenOverflow;
}
