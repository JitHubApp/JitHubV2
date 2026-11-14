using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;

namespace JitHub.WinUI.Automation.Windows;

/// <summary>
/// Resolves native window handles and brings the app window to the foreground.
/// </summary>
internal static class WindowActivation
{
    internal static IntPtr GetNativeWindowHandle(Window window)
    {
        if (AutomationWindowHandleCache.TryGet(window, out IntPtr cachedHandle))
        {
            return cachedHandle;
        }

        COMException? lastError = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                IntPtr handle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
                if (handle != IntPtr.Zero)
                {
                    AutomationWindowHandleCache.Store(window, handle);
                    return handle;
                }
            }
            catch (COMException ex)
            {
                lastError = ex;
            }

            Thread.Sleep(75);
        }

        throw new InvalidOperationException("Could not reacquire the application window handle after a UI Automation transition.", lastError);
    }

    internal static void TryActivateWindow(Window window)
    {
        try
        {
            window.SetForeground();
        }
        catch (COMException)
        {
        }

        try
        {
            window.FocusNative();
        }
        catch (COMException)
        {
        }
    }
}
