using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using JitHub.WinUI.Automation.Native;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowActivation;

namespace JitHub.WinUI.Automation.Screenshots;

/// <summary>
/// Captures the app window, an element or a verified screen region to PNG, and checks the captured pixels.
/// </summary>
internal static class WindowCapture
{
    internal static void CaptureWindow(Window window, string path)
    {
        AssertJitHubWindow(window);
        (_, IntPtr windowHandle) = ReadCaptureWindowIdentity(window);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        using Bitmap bitmap = NativeMethods.CaptureWindowSurface(windowHandle);
        ValidateCapturePixels(bitmap);
        bitmap.Save(path, ImageFormat.Png);
    }

    internal static void CaptureElement(Window window, AutomationElement element, string path)
    {
        CaptureVerifiedScreenRegion(window, element.BoundingRectangle, path);
    }

    internal static void CaptureWindowWithPopups(Window window, string path)
    {
        CaptureVerifiedScreenRegion(
            window,
            NativeMethods.GetPhysicalWindowBounds(GetNativeWindowHandle(window)),
            path,
            preservePopupForeground: true);
    }

    private static void CaptureVerifiedScreenRegion(
        Window window,
        Rectangle bounds,
        string path,
        bool preservePopupForeground = false)
    {
        AssertJitHubWindow(window);
        (int processId, IntPtr windowHandle) = ReadCaptureWindowIdentity(window);
        // Resizing, opening a picker, or the test runner itself can briefly take foreground
        // ownership. Re-establish the app as the capture target immediately before the
        // native ownership check; that check still refuses the capture if activation fails.
        try
        {
            if (!preservePopupForeground)
            {
                window.SetForeground();
            }
        }
        catch (COMException)
        {
        }
        Thread.Sleep(100);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);

        var captureBounds = new Rectangle(
            bounds.X,
            bounds.Y,
            Math.Max(1, bounds.Width),
            Math.Max(1, bounds.Height));
        IReadOnlyList<IntPtr>? hiddenCaptureOverlays = null;
        InvalidOperationException? lastOwnershipError = null;
        for (int attempt = 0; attempt < 5 && hiddenCaptureOverlays is null; attempt++)
        {
            if (!preservePopupForeground)
            {
                NativeMethods.ActivateForKeyboard(windowHandle);
                TryActivateWindow(window);
            }
            Thread.Sleep(100 + (attempt * 50));
            try
            {
                hiddenCaptureOverlays = NativeMethods.PrepareExclusiveScreenCapture(
                    windowHandle,
                    processId,
                    captureBounds);
            }
            catch (InvalidOperationException ex)
            {
                lastOwnershipError = ex;
            }
        }

        if (hiddenCaptureOverlays is null)
        {
            throw new InvalidOperationException(
                "Could not establish exclusive JitHub foreground ownership for screenshot capture.",
                lastOwnershipError);
        }

        try
        {
            using var bitmap = new Bitmap(captureBounds.Width, captureBounds.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(captureBounds.Location, Point.Empty, captureBounds.Size, CopyPixelOperation.SourceCopy);
            }

            ValidateCapturePixels(bitmap);
            bitmap.Save(path, ImageFormat.Png);
        }
        finally
        {
            NativeMethods.RestoreCaptureOverlays(hiddenCaptureOverlays);
        }
    }

    private static (int ProcessId, IntPtr WindowHandle) ReadCaptureWindowIdentity(Window window)
    {
        COMException? lastProviderError = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                int processId = window.Properties.ProcessId.ValueOrDefault;
                IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
                if (processId > 0 && windowHandle != IntPtr.Zero)
                {
                    return (processId, windowHandle);
                }
            }
            catch (COMException ex)
            {
                lastProviderError = ex;
            }

            Thread.Sleep(75);
        }

        throw new InvalidOperationException(
            "The current JitHub window provider did not stabilize before screenshot capture.",
            lastProviderError);
    }

    private static void ValidateCapturePixels(Bitmap bitmap)
    {
        int stepX = Math.Max(1, bitmap.Width / 24);
        int stepY = Math.Max(1, bitmap.Height / 18);
        int minimumChannel = byte.MaxValue;
        int maximumChannel = byte.MinValue;
        HashSet<int> sampledColors = [];
        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                Color color = bitmap.GetPixel(x, y);
                sampledColors.Add(color.ToArgb());
                minimumChannel = Math.Min(minimumChannel, Math.Min(color.R, Math.Min(color.G, color.B)));
                maximumChannel = Math.Max(maximumChannel, Math.Max(color.R, Math.Max(color.G, color.B)));
            }
        }

        if (sampledColors.Count < 8 || maximumChannel - minimumChannel < 12)
        {
            throw new InvalidOperationException(
                $"Refusing to save an invalid popup capture: colors={sampledColors.Count}, channelRange={maximumChannel - minimumChannel}.");
        }
    }
}
