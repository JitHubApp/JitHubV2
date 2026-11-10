using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using static JitHub.WinUI.PerformanceGate.Routes.RouteProbeTimeouts;

namespace JitHub.WinUI.PerformanceGate.Elements;

/// <summary>
/// Finds, selects and edits the UI Automation elements the route probe drives, tolerating transient UI Automation failures.
/// </summary>
internal static class RouteElements
{
    internal static AutomationElement WaitForElement(AutomationElement root, string automationId)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < ElementTimeout)
        {
            try
            {
                AutomationElement? element = FindVisible(root, automationId);
                if (element is not null)
                {
                    return element;
                }
            }
            catch (COMException exception) when (IsTransientUiAutomationFailure(exception))
            {
            }
            catch (Win32Exception exception) when (IsTransientUiAutomationTimeout(exception))
            {
            }

            Thread.Sleep(5);
        }

        throw new TimeoutException($"Automation element '{automationId}' did not become visible within 20 seconds.");
    }

    internal static bool IsTransientUiAutomationFailure(COMException exception) =>
        exception.HResult is unchecked((int)0x80040201) or unchecked((int)0x8000FFFF);

    internal static bool IsTransientUiAutomationTimeout(Win32Exception exception) =>
        exception.NativeErrorCode == 1460 ||
        exception.HResult == unchecked((int)0x800705B4);

    internal static AutomationElement? FindVisible(AutomationElement root, string automationId)
    {
        try
        {
            AutomationElement? element = root.AutomationId == automationId
                ? root
                : root.FindFirstDescendant(condition => condition.ByAutomationId(automationId));
            return element is not null && IsVisible(element) ? element : null;
        }
        catch
        {
            return null;
        }
    }

    internal static AutomationElement? FindVisibleForObservation(AutomationElement root, string automationId)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            AutomationElement? element = FindVisible(root, automationId);
            if (element is not null)
            {
                return element;
            }

            Thread.Sleep(1);
        }

        return null;
    }

    internal static bool IsVisible(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            Rectangle bounds = element.BoundingRectangle;
            return element.Properties.IsOffscreen.ValueOrDefault != true && bounds.Width > 1 && bounds.Height > 1;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsSelected(AutomationElement element)
    {
        try
        {
            return element.Patterns.SelectionItem.IsSupported &&
                element.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault;
        }
        catch
        {
            return false;
        }
    }

    private static string GetElementName(AutomationElement element)
    {
        try
        {
            return element.Name?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static void SelectOrInvoke(AutomationElement element)
    {
        if (element.Patterns.SelectionItem.IsSupported)
        {
            element.Patterns.SelectionItem.Pattern.Select();
            return;
        }

        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
            return;
        }

        element.Click();
    }

    internal static void CommitTextValue(
        AutomationElement root,
        string automationId,
        string value,
        string description)
    {
        string resetValue = $"__jithub_commit_{Guid.NewGuid():N}";
        CommitTextValueOnce(root, automationId, resetValue, description);
        CommitTextValueOnce(root, automationId, value, description);

        // The acknowledgement is published by the WinUI TextChanged handler.
        // Only begin adjacent measured work after the app thread has consumed it.
        Thread.Sleep(16);
    }

    private static void CommitTextValueOnce(
        AutomationElement root,
        string automationId,
        string value,
        string description)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        Exception? lastTransientFailure = null;
        while (timeout.Elapsed < ProductPerformanceInputCommitTimeout)
        {
            try
            {
                AutomationElement? element = FindVisible(root, automationId);
                if (element is not null)
                {
                    TextBox textBox = element.AsTextBox();
                    if (!string.Equals(textBox.Text, value, StringComparison.Ordinal))
                    {
                        textBox.Text = value;
                    }

                    string committedValue =
                        element.Properties.ItemStatus.ValueOrDefault ?? string.Empty;
                    if (string.Equals(committedValue, value, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }
            catch (COMException exception)
            {
                lastTransientFailure = exception;
            }
            catch (InvalidOperationException exception)
            {
                lastTransientFailure = exception;
            }

            Thread.Sleep(8);
        }

        throw new TimeoutException(
            $"The {description} input did not receive a WinUI acknowledgement for '{value}'.",
            lastTransientFailure);
    }
}
