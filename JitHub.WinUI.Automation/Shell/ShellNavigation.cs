using FlaUI.Core.AutomationElements;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;

namespace JitHub.WinUI.Automation.Shell;

/// <summary>
/// Finds shell navigation items.
/// </summary>
internal static class ShellNavigation
{
    internal static AutomationElement GetShellNavigationElement(Window window, string automationId)
    {
        AutomationElement? navigation = null;
        AutomationElement? drawerButton = null;
        WaitUntil(
            $"stable shell navigation chrome for {automationId}",
            () =>
            {
                navigation = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                if (IsVisible(navigation) && IsInsideWindowBounds(navigation!, window))
                {
                    return true;
                }

                drawerButton = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRailDrawerButton"));
                return IsVisible(drawerButton) && IsInsideWindowBounds(drawerButton!, window);
            },
            TimeSpan.FromSeconds(8));

        if (IsVisible(navigation) && IsInsideWindowBounds(navigation!, window))
        {
            return navigation!;
        }

        InvokeOrClick(drawerButton!);
        AutomationElement result = WaitForElement(
            automationId,
            () =>
            {
                AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                return IsVisible(candidate) && IsInsideWindowBounds(candidate!, window) ? candidate : null;
            },
            TimeSpan.FromSeconds(8));
        Thread.Sleep(350);
        return window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) ?? result;
    }
}
