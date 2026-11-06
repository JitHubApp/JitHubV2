using Windows.System;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Reads the keyboard modifiers held during pointer and keyboard input.
/// </summary>
internal static class InputModifierState
{
    internal static MarkdownInputModifiers GetCurrentInputModifiers()
    {
        MarkdownInputModifiers result = MarkdownInputModifiers.None;
        if (IsVirtualKeyDown(VirtualKey.Control)) result |= MarkdownInputModifiers.Control;
        if (IsVirtualKeyDown(VirtualKey.Shift)) result |= MarkdownInputModifiers.Shift;
        if (IsVirtualKeyDown(VirtualKey.Menu)) result |= MarkdownInputModifiers.Alt;
        if (IsVirtualKeyDown(VirtualKey.LeftWindows) || IsVirtualKeyDown(VirtualKey.RightWindows))
            result |= MarkdownInputModifiers.Windows;
        return result;
    }

    internal static MarkdownInputModifiers MapInputModifiers(VirtualKeyModifiers modifiers)
    {
        MarkdownInputModifiers result = MarkdownInputModifiers.None;
        if ((modifiers & VirtualKeyModifiers.Control) != 0) result |= MarkdownInputModifiers.Control;
        if ((modifiers & VirtualKeyModifiers.Shift) != 0) result |= MarkdownInputModifiers.Shift;
        if ((modifiers & VirtualKeyModifiers.Menu) != 0) result |= MarkdownInputModifiers.Alt;
        if ((modifiers & VirtualKeyModifiers.Windows) != 0) result |= MarkdownInputModifiers.Windows;
        return result;
    }

    internal static bool IsVirtualKeyDown(VirtualKey key)
        => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) &
            Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
}
