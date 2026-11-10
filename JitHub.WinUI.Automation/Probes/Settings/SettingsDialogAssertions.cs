using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Dialogs.DialogAssertions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;

namespace JitHub.WinUI.Automation.Probes.Settings;

/// <summary>
/// Checks the Settings confirmation and sign-out dialogs and the disabled state of the Settings actions behind them.
/// </summary>
internal static class SettingsDialogAssertions
{
    // This method stays whole because it runs one confirmation dialog through three ordered open cycles (Escape or
    // Cancel, the Cancel button, then the primary action), checking centering, scrim, disabled actions and opener focus
    // restore, all gated on the single foreground-focus decision made at the start.
    internal static void AssertSettingsConfirmation(
        Window window,
        UIA3Automation automation,
        string outputDirectory,
        string actionId,
        string dialogId,
        string expectedName)
    {
        AutomationElement action = WaitForElement(
            actionId,
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(actionId)),
            TimeSpan.FromSeconds(5));
        RevealForInteraction(action, actionId);
        bool canUseForegroundFocus = !NativeMethods.HasInvisibleForeignForegroundWindow(
            window.Properties.NativeWindowHandle.ValueOrDefault);
        if (canUseForegroundFocus)
        {
            action.Focus();
        }
        string artifactName = dialogId.Replace("SettingsConfirm", "settings-confirm-", StringComparison.Ordinal);
        string baselinePath = Path.Combine(outputDirectory, $"{artifactName}-baseline.png");
        string dialogPath = Path.Combine(outputDirectory, $"{artifactName}-open.png");
        CaptureWindow(window, baselinePath);
        InvokeOrClick(action);
        TryInvokeDisabledSettingsAction(action);
        AutomationElement confirmation = WaitForElement(
            dialogId,
            () => FindElementInWindowOrDialog(window, automation, dialogId),
            TimeSpan.FromSeconds(5));
        AssertProbe(
            string.Equals(confirmation.Name, expectedName, StringComparison.Ordinal),
            $"Settings confirmation {dialogId} exposed the wrong accessible name.");
        Rectangle dialogContentBounds = GetDialogContentEnvelope(confirmation);
        AssertProbe(IsRectangleInsideWindow(dialogContentBounds, window), $"{dialogId} escaped the app window.");
        AssertProbe(IsRectangleHorizontallyCentered(dialogContentBounds, window, 36), $"{dialogId} was not centered in the app window.");
        AssertSingleVisibleSettingsDialog(window, automation, dialogId);
        AssertSettingsActionsDisabled(window);
        if (canUseForegroundFocus)
        {
            AssertDialogFocusContained(automation, confirmation, dialogId);
        }
        CaptureWindowWithPopups(window, dialogPath);
        AssertSettingsDialogScrim(baselinePath, dialogPath, window, dialogContentBounds, dialogId);

        if (canUseForegroundFocus)
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
        }
        else
        {
            InvokeOrClick(FindDialogButton(confirmation, automation, "Cancel"));
        }
        WaitUntil(
            $"{dialogId} closes on Escape",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, dialogId)),
            TimeSpan.FromSeconds(5));
        if (canUseForegroundFocus)
        {
            WaitUntil(
                $"{dialogId} restores opener focus",
                () => IsElementFocused(FindElementInWindowOrDialog(window, automation, actionId)),
                TimeSpan.FromSeconds(5));
        }

        InvokeOrClick(action);
        confirmation = WaitForElement(
            $"{dialogId} cancel cycle",
            () => FindElementInWindowOrDialog(window, automation, dialogId),
            TimeSpan.FromSeconds(5));
        AutomationElement cancel = FindDialogButton(confirmation, automation, "Cancel");
        InvokeOrClick(cancel);
        WaitUntil(
            $"{dialogId} cancel button closes",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, dialogId)),
            TimeSpan.FromSeconds(5));
        if (canUseForegroundFocus)
        {
            WaitUntil(
                $"{dialogId} cancel restores opener focus",
                () => IsElementFocused(FindElementInWindowOrDialog(window, automation, actionId)),
                TimeSpan.FromSeconds(5));
        }

        InvokeOrClick(action);
        confirmation = WaitForElement(
            $"{dialogId} primary cycle",
            () => FindElementInWindowOrDialog(window, automation, dialogId),
            TimeSpan.FromSeconds(5));
        AutomationElement primary = confirmation.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .Where(IsVisible)
            .First(button => !string.Equals(button.Name, "Cancel", StringComparison.Ordinal));
        InvokeOrClick(primary);
        WaitUntil(
            $"{dialogId} primary action completes",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, dialogId)) &&
                FindElementInWindowOrDialog(window, automation, actionId) is { IsEnabled: true },
            TimeSpan.FromSeconds(15));
        if (canUseForegroundFocus)
        {
            WaitUntil(
                $"{dialogId} primary restores opener focus",
                () => IsElementFocused(FindElementInWindowOrDialog(window, automation, actionId)),
                TimeSpan.FromSeconds(5));
        }
    }

    private static string[] GetSettingsSerializedActionIds() =>
    [
        "SettingsSignOutButton",
        "SettingsClearQueryCacheButton",
        "SettingsClearStarLibraryButton",
        "SettingsClearImageCacheButton",
        "SettingsClearRepoFileCacheButton",
        "SettingsClearAllCacheButton",
        "SettingsExportDiagnosticsButton",
        "SettingsClearDiagnosticsButton"
    ];

    internal static void TryInvokeDisabledSettingsAction(AutomationElement action)
    {
        WaitUntil("Settings action enters busy state", () => !action.IsEnabled, TimeSpan.FromSeconds(3));
        try
        {
            if (action.Patterns.Invoke.IsSupported)
            {
                action.Patterns.Invoke.Pattern.Invoke();
            }
            else
            {
                action.Click();
            }
        }
        catch (Exception)
        {
            // A disabled provider may reject the second activation. Either result is valid;
            // the dialog-count assertion proves that no concurrent ShowAsync was attempted.
        }
    }

    internal static void AssertSettingsActionsDisabled(Window window)
    {
        foreach (string automationId in GetSettingsSerializedActionIds())
        {
            AutomationElement? action = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (action is not null)
            {
                AssertProbe(!action.IsEnabled, $"{automationId} remained enabled while a Settings operation was active.");
            }
        }
    }

    private static void AssertSingleVisibleSettingsDialog(
        Window window,
        UIA3Automation automation,
        string dialogId)
    {
        AutomationElement[] visiblePeers = automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByAutomationId(dialogId))
            .Where(element => IsVisible(element) && IsInsideWindowBounds(element, window))
            .ToArray();
        int visibleSurfaceCount = visiblePeers
            .Select(element => element.BoundingRectangle)
            .Distinct()
            .Count();
        AssertProbe(
            visibleSurfaceCount == 1,
            $"Rapid Settings activation produced {visibleSurfaceCount} distinct visible {dialogId} dialog surfaces " +
            $"across {visiblePeers.Length} UIA peers.");
    }

    private static void AssertSettingsDialogScrim(
        string baselinePath,
        string dialogPath,
        Window window,
        Rectangle dialogBounds,
        string context)
    {
        using var baseline = new Bitmap(baselinePath);
        using var opened = new Bitmap(dialogPath);
        Rectangle windowBounds = window.BoundingRectangle;
        double scaleX = windowBounds.Width > 0 ? (double)baseline.Width / windowBounds.Width : 1;
        double scaleY = windowBounds.Height > 0 ? (double)baseline.Height / windowBounds.Height : 1;
        int left = Math.Clamp(
            (int)Math.Round((dialogBounds.Left - windowBounds.Left) * scaleX),
            0,
            baseline.Width);
        int top = Math.Clamp(
            (int)Math.Round((dialogBounds.Top - windowBounds.Top) * scaleY),
            0,
            baseline.Height);
        int right = Math.Clamp(
            (int)Math.Round((dialogBounds.Right - windowBounds.Left) * scaleX),
            0,
            baseline.Width);
        int bottom = Math.Clamp(
            (int)Math.Round((dialogBounds.Bottom - windowBounds.Top) * scaleY),
            0,
            baseline.Height);
        double baselineLuminance = 0;
        double openedLuminance = 0;
        int samples = 0;

        for (int y = 72; y < Math.Min(baseline.Height, opened.Height) - 16; y += 12)
        {
            for (int x = 16; x < Math.Min(baseline.Width, opened.Width) - 16; x += 12)
            {
                if (x >= left - 12 && x <= right + 12 && y >= top - 12 && y <= bottom + 12)
                {
                    continue;
                }

                Color before = baseline.GetPixel(x, y);
                Color after = opened.GetPixel(x, y);
                baselineLuminance += (0.2126 * before.R) + (0.7152 * before.G) + (0.0722 * before.B);
                openedLuminance += (0.2126 * after.R) + (0.7152 * after.G) + (0.0722 * after.B);
                samples++;
            }
        }

        AssertProbe(samples > 20, $"{context} did not leave enough background pixels to verify its scrim.");
        double darkening = (baselineLuminance - openedLuminance) / samples;
        AssertProbe(darkening >= 1.0, $"{context} did not render a visible modal scrim (average darkening {darkening:0.0}).");
    }

    internal static void AssertSettingsSignOutConfirmation(
        Window window,
        UIA3Automation automation,
        string outputDirectory)
    {
        AutomationElement action = WaitForElement(
            "Settings sign-out button",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSignOutButton")),
            TimeSpan.FromSeconds(5));
        RevealForInteraction(action, "Settings sign-out button");
        bool canUseForegroundFocus = !NativeMethods.HasInvisibleForeignForegroundWindow(
            window.Properties.NativeWindowHandle.ValueOrDefault);
        if (canUseForegroundFocus)
        {
            action.Focus();
        }
        InvokeOrClick(action);
        TryInvokeDisabledSettingsAction(action);
        AutomationElement dialog = WaitForElement(
            "Settings sign-out confirmation",
            () => FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog"),
            TimeSpan.FromSeconds(5));
        AssertProbe(string.Equals(dialog.Name, "Sign out of JitHub", StringComparison.Ordinal), "Sign-out dialog exposed the wrong accessible name.");
        Rectangle dialogContentBounds = GetDialogContentEnvelope(dialog);
        AssertProbe(IsRectangleInsideWindow(dialogContentBounds, window), "Sign-out dialog escaped the app window.");
        AssertProbe(IsRectangleHorizontallyCentered(dialogContentBounds, window, 36), "Sign-out dialog was not centered in the app window.");
        AssertSingleVisibleSettingsDialog(window, automation, "SignOutConfirmationDialog");
        AssertSettingsActionsDisabled(window);
        if (canUseForegroundFocus)
        {
            AssertDialogFocusContained(automation, dialog, "Settings sign-out confirmation");
        }
        CaptureWindowWithPopups(window, Path.Combine(outputDirectory, "settings-sign-out-confirmation-open.png"));
        AutomationElement cancel = FindDialogButton(dialog, automation, "Cancel");
        InvokeOrClick(cancel);
        WaitUntil(
            "Settings sign-out confirmation closes",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog")),
            TimeSpan.FromSeconds(5));
        if (canUseForegroundFocus)
        {
            WaitUntil("Settings sign-out restores focus", () => IsElementFocused(action), TimeSpan.FromSeconds(5));
        }

        InvokeOrClick(action);
        _ = WaitForElement(
            "Settings sign-out confirmation Escape cycle",
            () => FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog"),
            TimeSpan.FromSeconds(5));
        if (canUseForegroundFocus)
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
        }
        else
        {
            AutomationElement escapeDialog = WaitForElement(
                "Settings sign-out confirmation fallback",
                () => FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog"),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(FindDialogButton(escapeDialog, automation, "Cancel"));
        }
        WaitUntil(
            "Settings sign-out Escape closes",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog")),
            TimeSpan.FromSeconds(5));
        if (canUseForegroundFocus)
        {
            WaitUntil("Settings sign-out Escape restores focus", () => IsElementFocused(action), TimeSpan.FromSeconds(5));
        }
    }
}
