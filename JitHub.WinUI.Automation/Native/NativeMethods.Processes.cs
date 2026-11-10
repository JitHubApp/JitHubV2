using System.Runtime.InteropServices;

namespace JitHub.WinUI.Automation.Native;

internal static partial class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr processHandle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    internal static bool TryRequestGracefulClose(int processId, out IntPtr windowHandle)
    {
        windowHandle = IntPtr.Zero;
        if (processId <= 0)
        {
            return false;
        }

        IntPtr selectedWindow = IntPtr.Zero;
        long selectedArea = -1;
        bool enumerationCompleted = EnumWindows(
            (candidate, parameter) =>
            {
                _ = parameter;
                _ = GetWindowThreadProcessId(candidate, out uint candidateProcessId);
                if (candidateProcessId != (uint)processId ||
                    !IsWindowVisible(candidate) ||
                    GetWindow(candidate, GwOwner) != IntPtr.Zero)
                {
                    return true;
                }

                long area = 0;
                if (GetWindowRect(candidate, out NativeRect bounds))
                {
                    long width = Math.Max(0L, (long)bounds.Right - bounds.Left);
                    long height = Math.Max(0L, (long)bounds.Bottom - bounds.Top);
                    area = width * height;
                }

                if (selectedWindow == IntPtr.Zero || area > selectedArea)
                {
                    selectedWindow = candidate;
                    selectedArea = area;
                }

                return true;
            },
            IntPtr.Zero);

        if (!enumerationCompleted || selectedWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(selectedWindow, out uint selectedProcessId);
        if (selectedProcessId != (uint)processId || !IsWindowVisible(selectedWindow))
        {
            return false;
        }

        windowHandle = selectedWindow;
        return PostMessage(selectedWindow, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    internal static IntPtr OpenProcessExitHandle(int processId)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, inheritHandle: false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not retain a native process handle for JitHub process {processId}.");
        }

        return handle;
    }

    internal static uint GetProcessExitCode(IntPtr processHandle)
    {
        if (!GetExitCodeProcess(processHandle, out uint exitCode))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read the native JitHub process exit code.");
        }

        return exitCode;
    }

    internal static void CloseProcessExitHandle(IntPtr processHandle)
    {
        if (processHandle != IntPtr.Zero)
            _ = CloseHandle(processHandle);
    }
}
