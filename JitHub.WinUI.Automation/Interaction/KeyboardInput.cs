using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using JitHub.WinUI.Automation.Native;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;

namespace JitHub.WinUI.Automation.Interaction;

/// <summary>
/// Sends keyboard input to the app window and focuses elements for keyboard activation.
/// </summary>
internal static class KeyboardInput
{
    internal static void PressKeyForWindow(Window window, VirtualKeyShort key)
    {
        IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
        if (NativeMethods.IsForegroundWindow(windowHandle))
        {
            Keyboard.Press(key);
            return;
        }

        NativeMethods.SendKey(windowHandle, key);
    }

    internal static void ApplySelectionFallbackForInvisibleForeignForeground(
        Window window,
        Func<bool> selectionApplied,
        AutomationElement target,
        string description)
    {
        if (WaitUntilAvailable(selectionApplied, TimeSpan.FromSeconds(1)) ||
            !NativeMethods.HasInvisibleForeignForegroundWindow(
                window.Properties.NativeWindowHandle.ValueOrDefault))
        {
            return;
        }

        AssertProbe(
            target.Patterns.SelectionItem.IsSupported,
            $"The {description} did not expose native SelectionItem semantics for the invisible-foreground fallback.");
        target.Patterns.SelectionItem.Pattern.Select();
        Console.WriteLine($"gists-workspace: selected {description} through UIA because an invisible foreign window owned the system foreground queue.");
    }

    internal static void FocusForKeyboardActivation(Window window, AutomationElement element)
    {
        IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
        NativeMethods.ActivateForKeyboard(windowHandle);
        Thread.Sleep(100);
        try
        {
            element.FocusNative();
        }
        catch (COMException)
        {
            element.Focus();
        }

        WaitUntil(
            $"keyboard focus for {GetAutomationId(element)}",
            () => IsElementFocused(element) && NativeMethods.IsForegroundWindow(windowHandle),
            TimeSpan.FromSeconds(3));
        Thread.Sleep(100);
    }
}
