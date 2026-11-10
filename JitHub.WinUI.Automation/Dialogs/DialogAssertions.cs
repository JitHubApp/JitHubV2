using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;

namespace JitHub.WinUI.Automation.Dialogs;

/// <summary>
/// Finds dialog buttons and checks dialog focus containment and placement inside the app window.
/// </summary>
internal static class DialogAssertions
{
    internal static void AssertDialogFocusContained(
        UIA3Automation automation,
        AutomationElement dialog,
        string context)
    {
        WaitUntil(
            $"{context} receives focus",
            () => IsInsideElementBounds(automation.FocusedElement(), dialog, tolerance: 2),
            TimeSpan.FromSeconds(5));
        for (int index = 0; index < 8; index++)
        {
            Keyboard.Press(VirtualKeyShort.TAB);
            AutomationElement focused = automation.FocusedElement();
            bool contained = WaitUntilAvailable(
                () => IsInsideElementBounds(automation.FocusedElement(), dialog, tolerance: 2),
                TimeSpan.FromMilliseconds(600));
            AssertProbe(
                contained,
                $"{context} focus escaped after Tab {index + 1} to " +
                $"{focused.ControlType} '{GetAutomationId(focused)}'/'{GetElementName(focused)}' at {focused.BoundingRectangle}; " +
                $"dialog bounds are {dialog.BoundingRectangle}.");
        }
    }

    internal static AutomationElement FindDialogButton(
        AutomationElement dialog,
        UIA3Automation automation,
        string name)
    {
        AutomationElement? button = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(candidate => IsVisible(candidate) && string.Equals(candidate.Name, name, StringComparison.Ordinal));
        button ??= automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(candidate =>
                IsVisible(candidate) &&
                string.Equals(candidate.Name, name, StringComparison.Ordinal) &&
                IsInsideElementBounds(candidate, dialog, tolerance: 2));
        return button ?? throw new InvalidOperationException($"Could not find the '{name}' button in the Settings dialog.");
    }

    internal static Rectangle GetDialogContentEnvelope(AutomationElement dialog)
    {
        Rectangle[] contentBounds = dialog
            .FindAllDescendants()
            .Where(element =>
                IsVisible(element) &&
                (element.ControlType == ControlType.Button ||
                 element.ControlType == ControlType.Text ||
                 element.ControlType == ControlType.CheckBox ||
                 element.ControlType == ControlType.Edit ||
                 element.ControlType == ControlType.ComboBox))
            .Select(element => element.BoundingRectangle)
            .Where(bounds => bounds.Width > 0 && bounds.Height > 0)
            .ToArray();
        AssertProbe(contentBounds.Length > 0, "The Settings dialog did not expose any visible content bounds.");

        int left = contentBounds.Min(bounds => bounds.Left) - 24;
        int top = contentBounds.Min(bounds => bounds.Top) - 24;
        int right = contentBounds.Max(bounds => bounds.Right) + 24;
        int bottom = contentBounds.Max(bounds => bounds.Bottom) + 24;
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    internal static bool IsRectangleInsideWindow(Rectangle bounds, Window window)
    {
        Rectangle windowBounds = window.BoundingRectangle;
        return bounds.Width > 0 &&
            bounds.Height > 0 &&
            bounds.Left >= windowBounds.Left &&
            bounds.Top >= windowBounds.Top &&
            bounds.Right <= windowBounds.Right &&
            bounds.Bottom <= windowBounds.Bottom;
    }

    internal static bool IsRectangleHorizontallyCentered(Rectangle bounds, Window window, double tolerance)
    {
        Rectangle windowBounds = window.BoundingRectangle;
        double elementCenter = bounds.Left + (bounds.Width / 2d);
        double windowCenter = windowBounds.Left + (windowBounds.Width / 2d);
        return Math.Abs(elementCenter - windowCenter) <= tolerance;
    }
}
