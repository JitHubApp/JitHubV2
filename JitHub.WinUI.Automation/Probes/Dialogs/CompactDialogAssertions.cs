using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using static JitHub.WinUI.Automation.Probes.Dialogs.CompactDialogElements;

namespace JitHub.WinUI.Automation.Probes.Dialogs;

/// <summary>
/// Checks the compact dialog contract: geometry, scrim, focus trap, light dismiss, Escape and focus restore.
/// </summary>
internal static class CompactDialogAssertions
{
    internal static void AssertDialogVisualContract(
        Window window,
        UIA3Automation automation,
        AutomationElement dialog,
        Rectangle contentBounds,
        string baselinePath,
        string outputDirectory,
        string openedFileName,
        string context)
    {
        _ = automation;
        string openedPath = Capture(window, outputDirectory, openedFileName);
        AssertDialogGeometry(window, contentBounds, context);
        AssertScrim(baselinePath, openedPath, window.BoundingRectangle, contentBounds, context);
        Assert(IsVisible(dialog), $"{context} was not visible after visual capture.");
    }

    internal static void AssertDialogGeometry(Window window, Rectangle contentBounds, string context)
    {
        Rectangle owner = window.BoundingRectangle;
        Assert(contentBounds.Width > 0 && contentBounds.Height > 0, $"{context} exposed empty dialog bounds.");
        Assert(IsInside(contentBounds, owner, 2), $"{context} escaped the owner window: dialog={contentBounds}, owner={owner}.");
        double horizontalDelta = Math.Abs(CenterX(contentBounds) - CenterX(owner));
        double verticalDelta = Math.Abs(CenterY(contentBounds) - CenterY(owner));
        Assert(horizontalDelta <= Math.Max(52, owner.Width * 0.07), $"{context} was not horizontally centered (delta {horizontalDelta:0.0}; owner={owner}; content={contentBounds}).");
        Assert(verticalDelta <= Math.Max(78, owner.Height * 0.14), $"{context} was not vertically centered (delta {verticalDelta:0.0}; owner={owner}; content={contentBounds}).");
        Assert(contentBounds.Width <= owner.Width - 16, $"{context} did not preserve horizontal compact margins.");
        Assert(contentBounds.Height <= owner.Height - 16, $"{context} did not preserve vertical compact margins.");
        Assert(contentBounds.Width <= 700, $"{context} exceeded the standard dialog max-width envelope ({contentBounds.Width}px).");
    }

    internal static void AssertShellOverlay(Window window, AutomationElement overlay, Rectangle contentBounds, string context)
    {
        Rectangle owner = window.BoundingRectangle;
        Rectangle scrim = overlay.BoundingRectangle;
        Assert(IsInside(contentBounds, scrim, 2), $"{context} content escaped its shell scrim.");
        Assert(scrim.Width >= owner.Width - 20, $"{context} shell scrim did not cover the client width (owner={owner}; scrim={scrim}).");
        Assert(scrim.Height >= owner.Height - 48, $"{context} shell scrim did not cover the owner content height (owner={owner}; scrim={scrim}).");
        AutomationElement? background = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchTextBox"));
        Assert(background is null || !background.IsEnabled, $"{context} left the shell search background enabled.");
    }

    internal static void AssertFocusTrap(
        UIA3Automation automation,
        AutomationElement focusRoot,
        Rectangle contentBounds,
        string context)
    {
        Rectangle focusEnvelope = contentBounds;
        focusEnvelope.Inflate(36, 36);
        if (!IsFocusWithin(automation, focusRoot, focusEnvelope))
        {
            bool focused = focusRoot.FindAllDescendants()
                .Where(candidate =>
                    IsVisible(candidate) &&
                    candidate.IsEnabled &&
                    candidate.Properties.IsKeyboardFocusable.ValueOrDefault)
                .OrderByDescending(static candidate => IsUsefulDialogFocusTarget(candidate.ControlType))
                .Any(candidate => TryFocusWithin(candidate, focusEnvelope, automation));
            if (!focused)
            {
                Keyboard.Press(VirtualKeyShort.TAB);
            }
        }
        bool receivedFocus = Retry.WhileFalse(
            () => IsFocusWithin(automation, focusRoot, focusEnvelope),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100),
            ignoreException: true).Result;
        if (!receivedFocus)
        {
            AutomationElement focused = automation.FocusedElement();
            string candidates = string.Join(
                " | ",
                focusRoot.FindAllDescendants()
                    .Where(candidate => IsVisible(candidate))
                    .Select(DescribeAutomationElement)
                    .Take(24));
            throw new InvalidOperationException(
                $"{context} did not receive focus. Focus remained on " +
                $"{DescribeAutomationElement(focused)}. " +
                $"Visible dialog descendants: {candidates}");
        }
        for (int index = 0; index < 10; index++)
        {
            Keyboard.Press(VirtualKeyShort.TAB);
            Thread.Sleep(50);
            AutomationElement focused = automation.FocusedElement();
            Assert(
                IsFocusWithin(automation, focusRoot, focusEnvelope),
                $"{context} focus escaped after Tab {index + 1} to " +
                $"'{focused.Properties.AutomationId.ValueOrDefault}'/'{focused.Name}' at {focused.BoundingRectangle}.");
        }
    }

    private static bool TryFocusWithin(
        AutomationElement candidate,
        Rectangle focusEnvelope,
        UIA3Automation automation)
    {
        try
        {
            candidate.FocusNative();
            return Retry.WhileFalse(
                () => IsInside(automation.FocusedElement().BoundingRectangle, focusEnvelope, 2) ||
                    HasKeyboardFocus(candidate),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(50),
                ignoreException: true).Result;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or FlaUI.Core.Exceptions.ElementNotEnabledException)
        {
            return false;
        }
    }

    private static bool IsFocusWithin(
        UIA3Automation automation,
        AutomationElement focusRoot,
        Rectangle focusEnvelope)
    {
        try
        {
            if (IsInside(automation.FocusedElement().BoundingRectangle, focusEnvelope, 2))
            {
                return true;
            }

            return HasKeyboardFocus(focusRoot) ||
                focusRoot.FindAllDescendants().Any(HasKeyboardFocus);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or FlaUI.Core.Exceptions.PropertyNotSupportedException)
        {
            return false;
        }
    }

    private static bool HasKeyboardFocus(AutomationElement element)
    {
        try
        {
            return element.Properties.HasKeyboardFocus.ValueOrDefault;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or FlaUI.Core.Exceptions.PropertyNotSupportedException)
        {
            return false;
        }
    }

    private static bool IsUsefulDialogFocusTarget(FlaUI.Core.Definitions.ControlType controlType) =>
        controlType == FlaUI.Core.Definitions.ControlType.Button ||
        controlType == FlaUI.Core.Definitions.ControlType.Edit ||
        controlType == FlaUI.Core.Definitions.ControlType.ComboBox ||
        controlType == FlaUI.Core.Definitions.ControlType.CheckBox ||
        controlType == FlaUI.Core.Definitions.ControlType.ListItem;

    private static string DescribeAutomationElement(AutomationElement element)
    {
        try
        {
            return $"{element.ControlType}:" +
                $"{element.Properties.AutomationId.ValueOrDefault}/" +
                $"{element.Properties.Name.ValueOrDefault}:" +
                $"enabled={element.Properties.IsEnabled.ValueOrDefault}:" +
                $"focusable={element.Properties.IsKeyboardFocusable.ValueOrDefault}:" +
                $"bounds={element.BoundingRectangle}";
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or FlaUI.Core.Exceptions.PropertyNotSupportedException)
        {
            return $"unavailable:{exception.GetType().Name}";
        }
    }

    internal static void AssertNoLightDismiss(
        Window window,
        UIA3Automation automation,
        string dialogAutomationId,
        Rectangle contentBounds,
        string context)
    {
        Point point = FindScrimPoint(window.BoundingRectangle, contentBounds);
        Mouse.Click(point);
        Thread.Sleep(180);
        Assert(
            IsVisible(Find(window, automation, dialogAutomationId)),
            $"{context} light-dismissed when its modal scrim was clicked.");
    }

    internal static void CloseWithEscape(
        Window window,
        UIA3Automation automation,
        string dialogAutomationId,
        string context)
    {
        window.SetForeground();
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitUntil($"{context} closes with Escape", () => !IsVisible(Find(window, automation, dialogAutomationId)));
    }

    internal static void AssertFocusRestored(UIA3Automation automation, AutomationElement opener, string context)
    {
        string openerId = opener.Properties.AutomationId.ValueOrDefault ?? string.Empty;
        Assert(!string.IsNullOrWhiteSpace(openerId), $"{context} opener did not expose an AutomationId.");
        WaitUntil(
            $"{context} restores opener focus",
            () => string.Equals(
                automation.FocusedElement().Properties.AutomationId.ValueOrDefault,
                openerId,
                StringComparison.Ordinal));
    }

    internal static void AssertSingleVisible(
        Window window,
        UIA3Automation automation,
        string automationId,
        string context)
    {
        int processId = window.Properties.ProcessId.ValueOrDefault;
        int visibleCount = automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByAutomationId(automationId))
            .Where(candidate => candidate.Properties.ProcessId.ValueOrDefault == processId && IsVisible(candidate))
            .Select(candidate => candidate.BoundingRectangle)
            .Distinct()
            .Count();
        Assert(visibleCount == 1, $"{context} produced {visibleCount} visible dialog surfaces.");
    }

    internal static AutomationElement FindDialogButton(
        Window window,
        UIA3Automation automation,
        AutomationElement dialog,
        string name)
    {
        AutomationElement? result = dialog.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
            .FirstOrDefault(candidate => IsVisible(candidate) && string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (result is not null)
        {
            return result;
        }

        Rectangle envelope = GetDialogContentEnvelope(dialog);
        int processId = window.Properties.ProcessId.ValueOrDefault;
        result = automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
            .FirstOrDefault(candidate =>
                candidate.Properties.ProcessId.ValueOrDefault == processId &&
                IsVisible(candidate) &&
                string.Equals(candidate.Name, name, StringComparison.Ordinal) &&
                IsInside(candidate.BoundingRectangle, envelope, 36));
        return result ?? throw new InvalidOperationException($"Could not find '{name}' in dialog '{dialog.Name}'.");
    }

    internal static Rectangle GetDialogContentEnvelope(AutomationElement dialog)
    {
        Rectangle[] bounds = dialog.FindAllDescendants()
            .Where(candidate => IsVisible(candidate) && IsDialogContentControl(candidate))
            .Select(candidate => candidate.BoundingRectangle)
            .Where(static rectangle => rectangle.Width > 0 && rectangle.Height > 0)
            .ToArray();
        Assert(bounds.Length > 0, $"Dialog '{dialog.Name}' exposed no content bounds.");
        return Rectangle.FromLTRB(
            bounds.Min(static rectangle => rectangle.Left) - 24,
            bounds.Min(static rectangle => rectangle.Top) - 24,
            bounds.Max(static rectangle => rectangle.Right) + 24,
            bounds.Max(static rectangle => rectangle.Bottom) + 24);
    }

    private static bool IsDialogContentControl(AutomationElement element)
    {
        FlaUI.Core.Definitions.ControlType type = element.ControlType;
        return type == FlaUI.Core.Definitions.ControlType.Button ||
            type == FlaUI.Core.Definitions.ControlType.Text ||
            type == FlaUI.Core.Definitions.ControlType.CheckBox ||
            type == FlaUI.Core.Definitions.ControlType.Edit ||
            type == FlaUI.Core.Definitions.ControlType.ComboBox ||
            type == FlaUI.Core.Definitions.ControlType.ListItem;
    }

    private static void AssertScrim(
        string baselinePath,
        string openedPath,
        Rectangle windowBounds,
        Rectangle dialogBounds,
        string context)
    {
        using Bitmap baseline = new(baselinePath);
        using Bitmap opened = new(openedPath);
        Assert(baseline.Size == opened.Size, $"{context} changed capture size while opening.");
        int left = dialogBounds.Left - windowBounds.Left;
        int top = dialogBounds.Top - windowBounds.Top;
        int right = dialogBounds.Right - windowBounds.Left;
        int bottom = dialogBounds.Bottom - windowBounds.Top;
        double beforeLuminance = 0;
        double afterLuminance = 0;
        int samples = 0;
        for (int y = 12; y < baseline.Height - 12; y += 12)
        {
            for (int x = 12; x < baseline.Width - 12; x += 12)
            {
                if (x >= left - 12 && x <= right + 12 && y >= top - 12 && y <= bottom + 12)
                {
                    continue;
                }

                Color before = baseline.GetPixel(x, y);
                Color after = opened.GetPixel(x, y);
                beforeLuminance += Luminance(before);
                afterLuminance += Luminance(after);
                samples++;
            }
        }

        Assert(samples > 20, $"{context} left too little background area to verify its scrim.");
        double darkening = (beforeLuminance - afterLuminance) / samples;
        Assert(darkening >= 1.0, $"{context} scrim did not visibly darken the background (average {darkening:0.0}).");
    }

    private static void AssertDisabled(Window window, UIA3Automation automation, string automationId)
    {
        AutomationElement action = WaitFor(automationId, () => Find(window, automation, automationId));
        Assert(!action.IsEnabled, $"Public preview unexpectedly enabled production write trigger '{automationId}'.");
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool IsInside(Rectangle inner, Rectangle outer, int tolerance) =>
        inner.Width > 0 && inner.Height > 0 &&
        inner.Left >= outer.Left - tolerance &&
        inner.Top >= outer.Top - tolerance &&
        inner.Right <= outer.Right + tolerance &&
        inner.Bottom <= outer.Bottom + tolerance;

    private static Point FindScrimPoint(Rectangle owner, Rectangle dialog)
    {
        Point[] candidates =
        [
            new(owner.Left + 8, owner.Top + (owner.Height / 2)),
            new(owner.Right - 8, owner.Top + (owner.Height / 2)),
            new(owner.Left + (owner.Width / 2), owner.Bottom - 8)
        ];
        return candidates.First(candidate => !dialog.Contains(candidate));
    }

    private static double CenterX(Rectangle rectangle) => rectangle.Left + (rectangle.Width / 2d);

    private static double CenterY(Rectangle rectangle) => rectangle.Top + (rectangle.Height / 2d);

    private static double Luminance(Color color) =>
        (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
}
