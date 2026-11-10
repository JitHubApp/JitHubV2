using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ScrollActions;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;

namespace JitHub.WinUI.Automation.Workspaces;

/// <summary>
/// Scrolls a work item list and selects rows by their visible signature.
/// </summary>
internal static class WorkItemListSelection
{
    internal static void ExerciseIssueListScrollSelection(Window window, AutomationElement list, string outputDirectory, string prefix)
    {
        string listAutomationId = GetAutomationId(list);
        ScrollElementToBottom(list);
        Thread.Sleep(450);
        AutomationElement target = FindLastVisibleListItem(list)
            ?? throw new InvalidOperationException($"{prefix}: no visible issue item was available after scrolling.");
        string signature = GetListItemSignature(target);
        string targetAutomationId = GetAutomationId(target);
        AssertProbe(!string.IsNullOrWhiteSpace(signature), $"{prefix}: issue item did not expose enough text for scroll-jump validation.");
        var beforeBounds = target.BoundingRectangle;
        CaptureWindow(window, Path.Combine(outputDirectory, $"{prefix}-scroll-click-before.png"));

        ClickListItemSurface(target);
        Thread.Sleep(1500);

        AutomationElement? currentList = FindCurrentVisibleByAutomationId(window, listAutomationId) ?? list;
        AutomationElement? afterTarget = !string.IsNullOrWhiteSpace(targetAutomationId)
            ? FindCurrentVisibleByAutomationId(window, targetAutomationId)
            : null;
        afterTarget = IsVisible(afterTarget)
            ? afterTarget
            : FindVisibleListItemBySignature(currentList, signature);
        CaptureWindow(window, Path.Combine(outputDirectory, $"{prefix}-scroll-click-after.png"));
        if (afterTarget is null)
        {
            string visibleRows = string.Join(
                "; ",
                currentList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .Where(IsVisible)
                    .Select(row => $"{GetAutomationId(row)}:{GetListItemSignature(row)}@{row.BoundingRectangle}"));
            Console.WriteLine($"{prefix}: post-click visible rows: {visibleRows}");
        }
        AssertProbe(afterTarget is not null, $"{prefix}: selected list row disappeared after click.");
        var afterBounds = afterTarget!.BoundingRectangle;
        double movement = Math.Abs(afterBounds.Y - beforeBounds.Y);
        AssertProbe(movement <= 28, $"{prefix}: selected issue row moved {movement:0.0}px after click.");
    }

    private static void ClickListItemSurface(AutomationElement item)
    {
        Rectangle bounds = item.BoundingRectangle;
        AssertProbe(bounds.Width >= 20 && bounds.Height >= 20, $"List item did not expose a usable click surface: {bounds}.");
        Mouse.Click(new System.Drawing.Point(bounds.Left + 8, bounds.Top + 8));
    }

    private static AutomationElement? FindLastVisibleListItem(AutomationElement list)
    {
        return list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .Where(IsVisible)
            .OrderBy(item => item.BoundingRectangle.Y)
            .LastOrDefault();
    }

    private static AutomationElement? FindVisibleListItemBySignature(AutomationElement list, string signature)
    {
        return list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .Where(IsVisible)
            .FirstOrDefault(item => string.Equals(GetListItemSignature(item), signature, StringComparison.Ordinal));
    }

    private static string GetListItemSignature(AutomationElement item)
    {
        string[] names = item.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Select(element => element.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Take(4)
            .ToArray();
        if (names.Length > 0)
        {
            return string.Join("|", names);
        }

        return item.Name ?? string.Empty;
    }
}
