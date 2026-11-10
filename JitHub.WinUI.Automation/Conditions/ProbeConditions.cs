using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using static JitHub.WinUI.Automation.Elements.ElementQueries;

namespace JitHub.WinUI.Automation.Conditions;

/// <summary>
/// Waits for and asserts the conditions a probe depends on, failing the probe with a described reason.
/// </summary>
internal static class ProbeConditions
{
    internal static AutomationElement WaitForVisibleAutomationElement(
        Window window,
        string automationId,
        TimeSpan? timeout = null) =>
        WaitForElement(
            automationId,
            () =>
            {
                AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                return IsVisible(candidate) ? candidate : null;
            },
            timeout ?? TimeSpan.FromSeconds(10));

    internal static bool WaitUntilAvailable(Func<bool> predicate, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            try
            {
                if (predicate())
                {
                    return true;
                }
            }
            catch
            {
            }

            Thread.Sleep(100);
        }
        while (stopwatch.Elapsed < timeout);

        return false;
    }

    internal static AutomationElement WaitForElement(string name, Func<AutomationElement?> findElement, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        AutomationElement? element;
        do
        {
            try
            {
                element = findElement();
            }
            catch
            {
                element = null;
            }

            if (element is not null)
            {
                return element;
            }

            Thread.Sleep(100);
        }
        while (stopwatch.Elapsed < timeout);

        throw new InvalidOperationException($"Timed out waiting for {name}.");
    }

    internal static void WaitUntil(string name, Func<bool> predicate, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            try
            {
                if (predicate())
                {
                    return;
                }
            }
            catch (COMException) when (stopwatch.Elapsed < timeout)
            {
                // WinUI can briefly invalidate an automation peer while a dialog or flyout closes.
            }

            Thread.Sleep(100);
        }
        while (stopwatch.Elapsed < timeout);

        throw new InvalidOperationException($"Timed out waiting for {name}.");
    }

    internal static void AssertProbe(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
