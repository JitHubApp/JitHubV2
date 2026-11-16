using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;

namespace JitHub.WinUI.Automation.Probes.Repositories;

/// <summary>
/// Captures and compares repository library screenshots of the probe-owned window only.
/// </summary>
internal static class RepositoryLibraryScreenshots
{
    internal static ScreenshotTarget CreateScreenshotTarget(Window window)
    {
        COMException? lastError = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
                int processId = window.Properties.ProcessId.ValueOrDefault;
                if (windowHandle != IntPtr.Zero && processId > 0)
                {
                    _ = GetWindowThreadProcessId(windowHandle, out uint actualProcessId);
                    if (actualProcessId == (uint)processId)
                    {
                        return new ScreenshotTarget(windowHandle, processId);
                    }
                }
            }
            catch (COMException exception)
            {
                lastError = exception;
            }

            Thread.Sleep(75);
        }

        throw new InvalidOperationException("Could not resolve the verified native JitHub window for screenshots.", lastError);
    }

    internal static void CaptureScreenshot(ScreenshotTarget target, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        AssertNoForeignJitHubProcesses(target);
        NativeRect nativeBounds = default;
        bool hasBounds = false;
        for (int attempt = 0; attempt < 5 && !hasBounds; attempt++)
        {
            if (IsWindow(target.WindowHandle))
            {
                _ = GetWindowThreadProcessId(target.WindowHandle, out uint actualProcessId);
                if (actualProcessId != (uint)target.ProcessId)
                {
                    throw new InvalidOperationException(
                        $"Native window ownership changed before screenshot '{path}': expected {target.ProcessId}, actual {actualProcessId}.");
                }

                _ = SetForegroundWindow(target.WindowHandle);
                hasBounds = GetWindowRect(target.WindowHandle, out nativeBounds);
            }

            if (!hasBounds)
            {
                Thread.Sleep(100);
            }
        }

        if (!hasBounds)
        {
            throw new InvalidOperationException($"Could not read current native bounds for screenshot '{path}'.");
        }

        Thread.Sleep(100);

        Rectangle bounds = Rectangle.FromLTRB(
            nativeBounds.Left,
            nativeBounds.Top,
            nativeBounds.Right,
            nativeBounds.Bottom);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException($"Native screenshot bounds are empty for '{path}'.");
        }

        using Bitmap bitmap = new(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        AssertNoForeignJitHubProcesses(target);

        bitmap.Save(path, ImageFormat.Png);
    }

    private static void AssertNoForeignJitHubProcesses(ScreenshotTarget target)
    {
        int[] foreignProcessIds = Process.GetProcessesByName("JitHub.WinUI")
            .Where(process => process.Id != target.ProcessId && !process.HasExited)
            .Select(static process => process.Id)
            .OrderBy(static processId => processId)
            .ToArray();
        if (foreignProcessIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Foreign JitHub windows were active during screenshot capture: {string.Join(", ", foreignProcessIds)}.");
        }
    }

    internal static bool ScreenshotRegionDiffers(
        string beforePath,
        string afterPath,
        ScreenshotTarget target,
        Rectangle screenRegion)
    {
        if (!GetWindowRect(target.WindowHandle, out NativeRect nativeBounds))
        {
            throw new InvalidOperationException("Could not resolve the native window bounds for hover comparison.");
        }

        Rectangle region = new(
            screenRegion.X - nativeBounds.Left,
            screenRegion.Y - nativeBounds.Top,
            screenRegion.Width,
            screenRegion.Height);
        using Bitmap before = new(beforePath);
        using Bitmap after = new(afterPath);
        region.Intersect(new Rectangle(0, 0, before.Width, before.Height));
        if (region.Width <= 0 || region.Height <= 0 || before.Size != after.Size)
        {
            return false;
        }

        int changedPixels = 0;
        int requiredChanges = Math.Max(12, region.Width * region.Height / 500);
        for (int y = region.Top; y < region.Bottom; y += 2)
        {
            for (int x = region.Left; x < region.Right; x += 2)
            {
                if (before.GetPixel(x, y) != after.GetPixel(x, y) && ++changedPixels >= requiredChanges)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    internal readonly record struct ScreenshotTarget(IntPtr WindowHandle, int ProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
