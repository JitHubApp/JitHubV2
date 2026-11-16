using FlaUI.Core.AutomationElements;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;

namespace JitHub.WinUI.Automation.Interaction;

/// <summary>
/// Invokes, selects, reveals and types into UI Automation elements.
/// </summary>
internal static class ElementActions
{
    internal static void SetTextBoxText(AutomationElement element, string text)
    {
        TextBox textBox = element.AsTextBox();
        textBox.Text = string.Empty;
        if (!string.IsNullOrEmpty(text))
        {
            textBox.Text = text;
        }
    }

    internal static void InvokeOrClick(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            try
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return;
            }
            catch (Exception)
            {
                // Some reparented WinUI controls briefly advertise a stale Invoke provider.
                // Coordinate click remains a valid UIA interaction once the drawer settles.
            }
        }

        if (element.Patterns.Toggle.IsSupported)
        {
            try
            {
                element.Patterns.Toggle.Pattern.Toggle();
                return;
            }
            catch (Exception)
            {
                // Fall through to physical input if a recycled toggle provider is stale.
            }
        }

        element.Click();
    }

    internal static void SelectAutomationItem(AutomationElement element, string description)
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

        if (element.Patterns.Toggle.IsSupported)
        {
            element.Patterns.Toggle.Pattern.Toggle();
            return;
        }

        if (element.Patterns.LegacyIAccessible.IsSupported)
        {
            element.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
            return;
        }

        throw new InvalidOperationException($"{description} omitted a semantic UI Automation activation pattern.");
    }

    internal static void RevealForInteraction(AutomationElement element, string description)
    {
        if (IsVisible(element))
        {
            return;
        }

        if (element.Patterns.ScrollItem.IsSupported)
        {
            element.Patterns.ScrollItem.Pattern.ScrollIntoView();
        }
        else
        {
            element.FocusNative();
        }

        WaitUntil($"{description} to become visible", () => IsVisible(element), TimeSpan.FromSeconds(5));
    }
}
