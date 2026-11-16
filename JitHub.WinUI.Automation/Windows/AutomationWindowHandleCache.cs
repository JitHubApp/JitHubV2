using FlaUI.Core.AutomationElements;

namespace JitHub.WinUI.Automation.Windows;

internal static class AutomationWindowHandleCache
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, HandleHolder> Handles = new();

    internal static bool TryGet(Window window, out IntPtr handle)
    {
        if (Handles.TryGetValue(window, out HandleHolder? holder) && holder.Value != IntPtr.Zero)
        {
            handle = holder.Value;
            return true;
        }

        handle = IntPtr.Zero;
        return false;
    }

    internal static void Store(Window window, IntPtr handle)
    {
        Handles.Remove(window);
        Handles.Add(window, new HandleHolder(handle));
    }

    private sealed record HandleHolder(IntPtr Value);
}
