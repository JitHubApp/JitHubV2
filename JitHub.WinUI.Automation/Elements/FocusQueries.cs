using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using static JitHub.WinUI.Automation.Elements.ElementQueries;

namespace JitHub.WinUI.Automation.Elements;

/// <summary>
/// Reads and describes which element has keyboard focus.
/// </summary>
internal static class FocusQueries
{
    internal static string GetFocusedAutomationId(UIA3Automation automation)
    {
        try
        {
            return GetAutomationId(automation.FocusedElement());
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static bool IsElementFocused(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.Properties.HasKeyboardFocus.ValueOrDefault;
        }
        catch
        {
            return false;
        }
    }

    internal static string GetFocusedAutomationDescription(UIA3Automation automation)
    {
        try
        {
            AutomationElement focused = automation.FocusedElement();
            return $"id='{GetAutomationId(focused)}', name='{focused.Properties.Name.ValueOrDefault}', " +
                $"type='{focused.ControlType}', bounds='{focused.BoundingRectangle}'";
        }
        catch (Exception exception)
        {
            return $"unavailable ({exception.GetType().Name})";
        }
    }

    internal static bool IsFocusedElementWithin(UIA3Automation automation, AutomationElement ancestor)
    {
        try
        {
            AutomationElement? candidate = automation.FocusedElement();
            ITreeWalker walker = automation.TreeWalkerFactory.GetRawViewWalker();
            for (int depth = 0; candidate is not null && depth < 128; depth++)
            {
                if (candidate.Equals(ancestor))
                {
                    return true;
                }

                candidate = walker.GetParent(candidate);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
