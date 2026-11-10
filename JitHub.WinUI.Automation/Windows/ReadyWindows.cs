using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Applications;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Windows.WindowActivation;

namespace JitHub.WinUI.Automation.Windows;

/// <summary>
/// Waits for the expected JitHub window of the launched process and checks that a window belongs to it.
/// </summary>
internal static class ReadyWindows
{
    internal static Window GetReadyWindow(Application app, UIA3Automation automation, string context)
    {
        int expectedProcessId = TryGetApplicationProcessId(app);
        Application windowProvider = app;
        if (AutomationApplicationPathRegistry.TryGet(
                app,
                out AutomationApplicationRegistration? registration) &&
            registration is not null)
        {
            expectedProcessId = registration.ProcessId;
        }

        if (expectedProcessId <= 0)
        {
            throw new InvalidOperationException($"Unable to resolve the JitHub process for {context}.");
        }

        var windowRetry = Retry.WhileNull(
            () => FindExpectedJitHubWindow(windowProvider, automation, expectedProcessId),
            timeout: TimeSpan.FromSeconds(20),
            interval: TimeSpan.FromMilliseconds(250),
            ignoreException: true);
        if (!windowRetry.Success || windowRetry.Result is null)
        {
            throw new InvalidOperationException(
                $"Unable to find the verified JitHub main window for {context} (expected process {expectedProcessId}).");
        }

        Window window = windowRetry.Result;
        if (window.Patterns.Transform.IsSupported)
        {
            window.Patterns.Transform.Pattern.Resize(1600, 1000);
        }

        window.Move(80, 80);
        TryActivateWindow(window);
        Thread.Sleep(1200);
        var currentWindowRetry = Retry.WhileNull(
            () =>
            {
                Window? candidate = FindExpectedJitHubWindow(windowProvider, automation, expectedProcessId);
                return candidate is not null && IsVisible(candidate) ? candidate : null;
            },
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(150),
            ignoreException: true);
        if (!currentWindowRetry.Success || currentWindowRetry.Result is null)
        {
            throw new InvalidOperationException(
                $"The current visible JitHub window provider was unavailable for {context} (expected process {expectedProcessId}).");
        }

        Window currentWindow = currentWindowRetry.Result;
        AssertJitHubWindow(currentWindow, expectedProcessId, context);
        return currentWindow;
    }

    private static Window? FindExpectedJitHubWindow(
        Application windowProvider,
        UIA3Automation automation,
        int expectedProcessId)
    {
        try
        {
            Window? candidate = windowProvider.GetMainWindow(automation);
            if (IsExpectedJitHubWindow(candidate, expectedProcessId))
            {
                return candidate;
            }
        }
        catch (COMException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            using Process process = Process.GetProcessById(expectedProcessId);
            process.Refresh();
            IntPtr mainWindowHandle = process.MainWindowHandle;
            if (mainWindowHandle == IntPtr.Zero)
            {
                return null;
            }

            Window candidate = automation.FromHandle(mainWindowHandle).AsWindow();
            return IsExpectedJitHubWindow(candidate, expectedProcessId) ? candidate : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    internal static Window GetReadyWindowByHandle(
        UIA3Automation automation,
        IntPtr windowHandle,
        int expectedProcessId,
        string context)
    {
        var retry = Retry.WhileNull(
            () =>
            {
                try
                {
                    Window candidate = automation.FromHandle(windowHandle).AsWindow();
                    return IsExpectedJitHubWindow(candidate, expectedProcessId) ? candidate : null;
                }
                catch
                {
                    return null;
                }
            },
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(200));
        if (!retry.Success || retry.Result is null)
        {
            throw new InvalidOperationException($"Could not reacquire the original JitHub window during {context}.");
        }

        AssertJitHubWindow(retry.Result, expectedProcessId, context);
        return retry.Result;
    }

    private static bool IsExpectedJitHubWindow(Window? window, int expectedProcessId)
    {
        if (window is null)
        {
            return false;
        }

        try
        {
            return window.Properties.ProcessId.ValueOrDefault == expectedProcessId
                && string.Equals(window.Title, "JitHub", StringComparison.Ordinal)
                && window.FindFirstDescendant(cf => cf.ByAutomationId("JitHubMainWindowRoot")) is not null;
        }
        catch (COMException)
        {
            return false;
        }
    }

    internal static void AssertJitHubWindow(Window window, int? expectedProcessId = null, string? context = null)
    {
        int actualProcessId;
        bool hasRoot;
        try
        {
            actualProcessId = window.Properties.ProcessId.ValueOrDefault;
            hasRoot = window.FindFirstDescendant(cf => cf.ByAutomationId("JitHubMainWindowRoot")) is not null;
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException($"The JitHub window became unavailable before {context ?? "capture"}.", exception);
        }

        if (!string.Equals(window.Title, "JitHub", StringComparison.Ordinal)
            || !hasRoot
            || (expectedProcessId.HasValue && actualProcessId != expectedProcessId.Value))
        {
            throw new InvalidOperationException(
                $"Refusing to automate or capture an unverified window during {context ?? "capture"}. " +
                $"Title='{window.Title}', process={actualProcessId}, expectedProcess={expectedProcessId?.ToString() ?? "n/a"}, hasJitHubRoot={hasRoot}.");
        }
    }
}
