using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;

namespace JitHub.WinUI.Automation.Shell;

/// <summary>
/// Finds and focuses the shell search box.
/// </summary>
internal static class ShellSearch
{
    internal static AutomationElement? FindShellSearchTextBox(Window window)
    {
        AutomationElement? byId = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchTextBox"));
        if (IsVisible(byId))
        {
            return byId;
        }

        return window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
            .Where(IsVisible)
            .OrderByDescending(element => element.BoundingRectangle.Width)
            .FirstOrDefault();
    }

    internal static bool IsSearchActuallyFocused(UIA3Automation automation, AutomationElement searchBox)
    {
        if (IsElementFocused(searchBox))
        {
            return true;
        }

        try
        {
            AutomationElement focused = automation.FocusedElement();
            return string.Equals(GetAutomationId(focused), "ShellSearchTextBox", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static void MoveMouseToEmptyTitleBar(Window window, AutomationElement searchBox)
    {
        var windowBounds = window.BoundingRectangle;
        var searchBounds = searchBox.BoundingRectangle;
        double logicalX = Math.Min(windowBounds.X + windowBounds.Width - 340, searchBounds.X + searchBounds.Width + 160);
        logicalX = Math.Max(logicalX, searchBounds.X + searchBounds.Width + 80);
        logicalX = Math.Min(logicalX, windowBounds.X + windowBounds.Width - 260);
        double logicalY = windowBounds.Y + 30;

        Mouse.MoveTo(new System.Drawing.Point(
            (int)Math.Round(logicalX),
            (int)Math.Round(logicalY)));
    }

    internal static void PressCtrlK()
    {
        using (Keyboard.Pressing(VirtualKeyShort.LCONTROL))
        {
            Thread.Sleep(100);
            Keyboard.Press(VirtualKeyShort.KEY_K);
            Thread.Sleep(100);
        }
    }
}
