using System.Drawing;
using System.Runtime.InteropServices;

namespace JitHub.WinUI.Automation.Native;

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeHighContrast
    {
        internal uint Size;
        internal uint Flags;
        internal IntPtr DefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref NativeHighContrast highContrast,
        uint updateFlags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoForRect(
        uint action,
        uint parameter,
        out NativeRect value,
        uint updateFlags);

    internal static bool IsHighContrastEnabled()
    {
        NativeHighContrast highContrast = new()
        {
            Size = (uint)Marshal.SizeOf<NativeHighContrast>()
        };
        return SystemParametersInfo(SpiGetHighContrast, highContrast.Size, ref highContrast, 0) &&
            (highContrast.Flags & HcfHighContrastOn) != 0;
    }

    internal static Rectangle GetWorkArea()
    {
        return SystemParametersInfoForRect(SpiGetWorkArea, 0, out NativeRect workArea, 0)
            ? Rectangle.FromLTRB(workArea.Left, workArea.Top, workArea.Right, workArea.Bottom)
            : Rectangle.Empty;
    }

    internal static uint GetWindowDpi(IntPtr windowHandle)
    {
        uint dpi = windowHandle == IntPtr.Zero ? 0 : GetDpiForWindow(windowHandle);
        if (dpi == 0)
        {
            throw new InvalidOperationException("Could not read the JitHub window DPI for website capture.");
        }

        return dpi;
    }

    internal static void EnablePerMonitorV2DpiAwareness()
    {
        IntPtr perMonitorV2 = new(-4);
        if (!SetProcessDpiAwarenessContext(perMonitorV2))
        {
            int error = Marshal.GetLastWin32Error();
            const int errorAccessDenied = 5;
            if (error != errorAccessDenied)
            {
                throw new System.ComponentModel.Win32Exception(
                    error,
                    "The automation harness could not enable per-monitor-v2 DPI awareness.");
            }
        }

        int awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        const int perMonitorAware = 2;
        if (awareness != perMonitorAware)
        {
            throw new InvalidOperationException(
                $"The automation harness requires per-monitor DPI awareness; active awareness={awareness}.");
        }
    }
}
