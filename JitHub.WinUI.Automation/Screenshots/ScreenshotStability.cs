using System.Drawing;
using FlaUI.Core.AutomationElements;
using JitHub.WinUI.Automation.Native;
using static JitHub.WinUI.Automation.Screenshots.CaptureFiles;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.WindowActivation;

namespace JitHub.WinUI.Automation.Screenshots;

/// <summary>
/// Waits until a screen region stops changing between screenshots.
/// </summary>
internal static class ScreenshotStability
{
    internal static void WaitForScreenshotRegionToStabilize(
        Window window,
        Rectangle screenRegion,
        TimeSpan timeout)
    {
        string firstPath = Path.Combine(Path.GetTempPath(), $"jithub-avatar-normal-{Guid.NewGuid():N}-1.png");
        string secondPath = Path.Combine(Path.GetTempPath(), $"jithub-avatar-normal-{Guid.NewGuid():N}-2.png");
        try
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            do
            {
                CaptureWindow(window, firstPath);
                Thread.Sleep(200);
                CaptureWindow(window, secondPath);
                if (ScreenshotRegionPixelsEqual(
                        firstPath,
                        secondPath,
                        NativeMethods.GetPhysicalWindowBounds(GetNativeWindowHandle(window)),
                        screenRegion))
                    return;

                Thread.Sleep(100);
            }
            while (DateTimeOffset.UtcNow < deadline);

            throw new InvalidOperationException("The comment avatar did not settle into its normal visual state before hover.");
        }
        finally
        {
            TryDeleteFile(firstPath);
            TryDeleteFile(secondPath);
        }
    }

    internal static bool ScreenshotRegionPixelsEqual(
        string firstPath,
        string secondPath,
        Rectangle captureBounds,
        Rectangle screenRegion)
    {
        using var first = new Bitmap(firstPath);
        using var second = new Bitmap(secondPath);
        if (first.Width != second.Width || first.Height != second.Height)
            return false;

        int left = Math.Max(0, screenRegion.Left - captureBounds.Left - 2);
        int top = Math.Max(0, screenRegion.Top - captureBounds.Top - 2);
        int right = Math.Min(first.Width, screenRegion.Right - captureBounds.Left + 2);
        int bottom = Math.Min(first.Height, screenRegion.Bottom - captureBounds.Top + 2);
        if (right <= left || bottom <= top)
            return false;

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb())
                    return false;
            }
        }

        return true;
    }
}
