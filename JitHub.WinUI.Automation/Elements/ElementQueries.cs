using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;

namespace JitHub.WinUI.Automation.Elements;

/// <summary>
/// Finds UI Automation elements and reads their names, automation ids and text.
/// </summary>
internal static class ElementQueries
{
    internal static AutomationElement? TryGetAutomationParent(ITreeWalker walker, AutomationElement element)
    {
        try
        {
            return walker.GetParent(element);
        }
        catch (COMException)
        {
            return null;
        }
    }

    internal static string AutomationElementText(AutomationElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            new[] { GetElementName(element) }
                .Concat(element.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(GetElementName))
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal));
    }

    internal static AutomationElement? FindVisibleText(AutomationElement root, string text) =>
        root.FindAllDescendants(cf => cf.ByText(text)).FirstOrDefault(IsVisible);

    internal static AutomationElement? FindCurrentVisibleByAutomationId(AutomationElement root, string automationId) =>
        root.FindAllDescendants(cf => cf.ByAutomationId(automationId)).FirstOrDefault(IsVisible);

    internal static void PrintVisibleAutomationIds(Window window, string context)
    {
        Console.WriteLine($"{context}: visible automation ids");
        foreach (AutomationElement element in window.FindAllDescendants().Take(220))
        {
            try
            {
                string automationId = element.AutomationId;
                string name = element.Name;
                if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!IsVisible(element))
                {
                    continue;
                }

                Console.WriteLine($"  {element.ControlType}: id='{automationId}' name='{name}'");
            }
            catch (Exception)
            {
            }
        }
    }

    internal static bool IsVisible(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            var bounds = element.BoundingRectangle;
            return !element.Properties.IsOffscreen.ValueOrDefault && bounds.Width > 0.5 && bounds.Height > 0.5;
        }
        catch
        {
            return false;
        }
    }

    internal static AutomationElement? FindElementInWindowOrDialog(Window window, UIA3Automation automation, string automationId)
    {
        AutomationElement? inWindow = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (IsVisible(inWindow))
        {
            return inWindow;
        }

        try
        {
            foreach (Window modalWindow in window.ModalWindows)
            {
                AutomationElement? inModal = modalWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                if (IsVisible(inModal))
                {
                    return inModal;
                }
            }
        }
        catch
        {
        }

        try
        {
            AutomationElement[] matches = automation.GetDesktop().FindAllDescendants(cf => cf.ByAutomationId(automationId));
            foreach (AutomationElement match in matches)
            {
                if (IsVisible(match) && IsInsideWindowBounds(match, window))
                {
                    return match;
                }
            }
        }
        catch
        {
        }

        return inWindow;
    }

    internal static string GetElementName(AutomationElement element)
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

    internal static string GetAutomationId(AutomationElement element)
    {
        try
        {
            return element.Properties.AutomationId.ValueOrDefault ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string GetTextBoxText(AutomationElement searchBox)
    {
        try
        {
            return searchBox.AsTextBox().Text ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static AutomationElement AssertNamedAutomationElement(Window window, string automationId, ControlType controlType)
    {
        AutomationElement element = WaitForElement(
            automationId,
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            TimeSpan.FromSeconds(8));
        AssertProbe(IsVisible(element), $"{automationId} was not visible in the live UIA tree.");
        AssertProbe(element.ControlType == controlType, $"{automationId} exposed {element.ControlType} instead of {controlType}.");
        AssertProbe(!string.IsNullOrWhiteSpace(element.Name), $"{automationId} did not expose a meaningful accessible name.");
        return element;
    }
}
