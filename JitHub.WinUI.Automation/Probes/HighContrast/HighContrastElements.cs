using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using static JitHub.WinUI.Automation.Probes.HighContrast.HighContrastNativeMethods;
using static JitHub.WinUI.Automation.Probes.HighContrast.HighContrastPixelAssertions;
using static JitHub.WinUI.Automation.Probes.HighContrast.SystemHighContrastSetting;

namespace JitHub.WinUI.Automation.Probes.HighContrast;

/// <summary>
/// Finds, waits for, focuses, invokes and resizes the elements the high contrast probe drives.
/// </summary>
internal static class HighContrastElements
{
    internal static void Invoke(AutomationElement element, string description)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
        }
        else
        {
            element.Click();
        }

        Thread.Sleep(200);
        Assert(ReadHighContrast().IsEnabled, $"High Contrast became inactive while invoking {description}.");
    }

    internal static void Focus(AutomationElement element, string description)
    {
        element.FocusNative();
        WaitUntil(
            $"{description} keyboard focus",
            () => element.Properties.HasKeyboardFocus.ValueOrDefault);
        Thread.Sleep(150);
    }

    internal static void ActivateForKeyboard(Window window, AutomationElement element, string description)
    {
        IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
        Assert(windowHandle != IntPtr.Zero, $"{description} did not expose a native window handle.");
        window.SetForeground();
        _ = SetForegroundWindow(windowHandle);
        WaitUntil(
            $"{description} foreground ownership",
            () => GetForegroundWindow() == windowHandle);
        Focus(element, description);
    }

    internal static Window WaitForWindow(Application app, UIA3Automation automation, string page)
    {
        var retry = Retry.WhileNull(
            () => app.GetMainWindow(automation),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMilliseconds(100));
        return retry.Success && retry.Result is not null
            ? retry.Result
            : throw new InvalidOperationException($"JitHub {page} window did not appear.");
    }

    internal static AutomationElement WaitForVisibleElement(Window window, string automationId) =>
        WaitForElement(
            automationId,
            () =>
            {
                AutomationElement? candidate = window.FindFirstDescendant(
                    cf => cf.ByAutomationId(automationId));
                return IsVisible(candidate) ? candidate : null;
            });

    internal static AutomationElement? FindVisible(Window window, string automationId)
    {
        AutomationElement? candidate = window.FindFirstDescendant(
            cf => cf.ByAutomationId(automationId));
        return IsVisible(candidate) ? candidate : null;
    }

    internal static string SafeAutomationName(AutomationElement element)
    {
        try
        {
            return element.Name;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static AutomationElement WaitForElement(
        string description,
        Func<AutomationElement?> factory)
    {
        var retry = Retry.WhileNull(
            factory,
            TimeSpan.FromSeconds(12),
            TimeSpan.FromMilliseconds(100),
            ignoreException: true);
        return retry.Success && retry.Result is not null
            ? retry.Result
            : throw new InvalidOperationException($"Timed out waiting for {description}.");
    }

    internal static void WaitUntil(string description, Func<bool> condition)
    {
        RetryResult<bool> retry = Retry.WhileFalse(
            condition,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100),
            ignoreException: true);
        Assert(retry.Success, $"Timed out waiting for {description}.");
    }

    internal static void Resize(Window window, Viewport viewport)
    {
        Assert(window.Patterns.Transform.IsSupported, "JitHub window does not support UIA resize.");
        IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
        Assert(windowHandle != IntPtr.Zero, "JitHub did not expose a native window handle for resize.");
        uint dpi = GetDpiForWindow(windowHandle);
        Assert(dpi > 0, "Windows did not report the JitHub window DPI.");
        double scale = dpi / 96d;
        int requestedPhysicalWidth = Math.Max(1, (int)Math.Round(viewport.Width * scale));
        int requestedPhysicalHeight = Math.Max(1, (int)Math.Round(viewport.Height * scale));
        const int outerMargin = 10;
        window.Patterns.Transform.Pattern.Resize(
            requestedPhysicalWidth,
            requestedPhysicalHeight);
        window.Move(outerMargin, outerMargin);
        window.SetForeground();
        Thread.Sleep(500);
        Assert(GetWindowRect(windowHandle, out NativeRect actualBounds), "Could not read JitHub bounds after resize.");
        int actualLogicalWidth = (int)Math.Round((actualBounds.Right - actualBounds.Left) / scale);
        int actualLogicalHeight = (int)Math.Round((actualBounds.Bottom - actualBounds.Top) / scale);
        int minimumUsableHeight = Math.Min(viewport.Height, 760);
        Assert(
            Math.Abs(actualLogicalWidth - viewport.Width) <= 4 &&
            actualLogicalHeight >= minimumUsableHeight &&
            actualLogicalHeight <= viewport.Height + 4,
            $"JitHub logical resize did not preserve the requested responsive width and usable height " +
            $"for {viewport.Width}x{viewport.Height}; actual={actualLogicalWidth}x{actualLogicalHeight}, dpi={dpi}.");
        Console.WriteLine(
            $"High Contrast logical viewport requested={viewport.Width}x{viewport.Height}; " +
            $"actual={actualLogicalWidth}x{actualLogicalHeight}; dpi={dpi}.");
        Assert(ReadHighContrast().IsEnabled, "High Contrast became inactive while resizing JitHub.");
    }

    internal static bool IsVisible(AutomationElement? element) =>
        element is not null &&
        !element.Properties.IsOffscreen.ValueOrDefault &&
        element.BoundingRectangle.Width > 0 &&
        element.BoundingRectangle.Height > 0;

    internal readonly record struct Viewport(int Width, int Height);
}
