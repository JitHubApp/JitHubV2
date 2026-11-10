using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using static JitHub.WinUI.Automation.Probes.HighContrast.HighContrastElements;
using static JitHub.WinUI.Automation.Probes.HighContrast.HighContrastLiveProbe;
using static JitHub.WinUI.Automation.Probes.HighContrast.HighContrastPixelAssertions;
using static JitHub.WinUI.Automation.Probes.HighContrast.SystemHighContrastSetting;

namespace JitHub.WinUI.Automation.Probes.HighContrast;

/// <summary>
/// Validates each page and dialog under high contrast at the wide and compact viewports and captures its focused and selected states.
/// </summary>
internal static class HighContrastSurfaceValidation
{
    internal static void ValidateDestructiveDialog(
        Window window,
        string outputDirectory,
        SystemColorPalette palette,
        Viewport viewport,
        string viewportName)
    {
        Resize(window, viewport);
        if (viewport.Width <= Compact.Width)
        {
            AutomationElement picker = WaitForVisibleElement(window, "SettingsCompactSectionPicker");
            picker.AsComboBox().Select("Data & Cache");
            WaitForVisibleElement(window, "SettingsClearQueryCacheButton");
        }
        else
        {
            Invoke(WaitForVisibleElement(window, "SettingsSection_data-cache"), "Settings Data & Cache section");
        }
        AutomationElement clearButton = WaitForElement(
            "Settings clear-all-cache action",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsClearAllCacheButton")));
        Assert(
            clearButton.Patterns.ScrollItem.IsSupported,
            "Settings clear-all-cache action does not expose the native ScrollItem pattern.");
        clearButton.Patterns.ScrollItem.Pattern.ScrollIntoView();
        clearButton = WaitForVisibleElement(window, "SettingsClearAllCacheButton");
        Invoke(clearButton, "Settings clear-all-cache action");
        AutomationElement dialog = WaitForVisibleElement(window, "SettingsConfirmClearAllCache");
        AutomationElement destructiveButton = WaitForElement(
            "Settings destructive dialog primary button",
            () => dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(element => IsVisible(element) &&
                    SafeAutomationName(element).Contains("Clear", StringComparison.OrdinalIgnoreCase)));
        CaptureDialogState(
            window,
            dialog,
            destructiveButton,
            palette,
            Path.Combine(outputDirectory, $"high-contrast-live-destructive-dialog-{viewportName}.png"),
            "Settings destructive confirmation dialog");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitUntil(
            "Settings destructive dialog dismissal",
            () => FindVisible(window, "SettingsConfirmClearAllCache") is null);
    }

    internal static void ValidateSettingsWide(
        Window window,
        string outputDirectory,
        SystemColorPalette palette)
    {
        Resize(window, Wide);
        AutomationElement appearance = WaitForVisibleElement(window, "SettingsSection_appearance");
        AutomationElement general = WaitForVisibleElement(window, "SettingsSection_general");
        AssertSelected(appearance, "Settings Appearance section at wide width");
        CaptureKeyboardSelectionState(
            window,
            appearance,
            general,
            VirtualKeyShort.DOWN,
            VirtualKeyShort.UP,
            palette,
            Path.Combine(outputDirectory, "high-contrast-live-settings-1366x900.png"),
            "Settings wide keyboard section selection");
    }

    internal static void ValidateSettingsCompact(
        Window window,
        string outputDirectory,
        SystemColorPalette palette)
    {
        Resize(window, Compact);
        AutomationElement picker = WaitForVisibleElement(window, "SettingsCompactSectionPicker");
        AutomationElement neutralFocus = WaitForVisibleElement(window, "SettingsThemeSystem");
        string selectedLabel = picker.AsComboBox().SelectedItem?.Properties.Name.ValueOrDefault ?? string.Empty;
        Assert(
            selectedLabel.Contains("Appearance", StringComparison.OrdinalIgnoreCase),
            $"Settings compact picker did not expose its selected Appearance section: '{selectedLabel}'.");
        CaptureFocusedState(
            window,
            neutralFocus,
            picker,
            palette,
            Path.Combine(outputDirectory, "high-contrast-live-settings-760x650.png"),
            "Settings compact section picker");
    }

    internal static void ValidateMarkdownHostViewport(
        Window window,
        Viewport viewport,
        string hostId,
        SystemColorPalette palette,
        string screenshotPath,
        string description)
    {
        Resize(window, viewport);
        AutomationElement host = WaitForElement(
            description,
            () =>
            {
                // The host renders the repository's real README; wait until its
                // TextPattern exposes rendered text before selecting it.
                AutomationElement? candidate = FindVisible(window, hostId);
                string text = candidate?.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(-1) ?? string.Empty;
                return string.IsNullOrWhiteSpace(text) ? null : candidate;
            });
        Assert(host.Patterns.Text.IsSupported, $"{description} does not expose TextPattern.");
        Assert(host.Properties.IsKeyboardFocusable.ValueOrDefault, $"{description} is not keyboard focusable.");
        Focus(host, description);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        WaitUntil(
            $"{description} keyboard text selection",
            () => host.Patterns.Text.Pattern.GetSelection().Any(range =>
                !string.IsNullOrWhiteSpace(range.GetText(-1))));
        using CapturedFrame frame = CaptureWindow(window);
        frame.Save(screenshotPath);
        Assert(window.BoundingRectangle.Contains(host.BoundingRectangle),
            $"{description} escaped or was clipped by the app window.");
        AssertSystemColorTreatment(frame, host.BoundingRectangle, palette, description);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        Assert(ReadHighContrast().IsEnabled, $"High Contrast became inactive while selecting {description}.");
    }

    internal static void ValidateGistEditorDialog(
        Window window,
        string outputDirectory,
        SystemColorPalette palette,
        Viewport viewport,
        string viewportName)
    {
        Resize(window, viewport);
        if (viewport.Width <= Compact.Width)
        {
            Invoke(WaitForVisibleElement(window, "GistsLeadingPaneButton"), "Open Gists list");
        }

        Invoke(WaitForVisibleElement(window, "GistsNew"), "New gist action");
        AutomationElement dialog = WaitForVisibleElement(window, "GistEditorDialog");
        AutomationElement editor = WaitForVisibleElement(window, "GistEditorDescription");
        CaptureDialogState(
            window,
            dialog,
            editor,
            palette,
            Path.Combine(outputDirectory, $"high-contrast-live-editor-dialog-{viewportName}.png"),
            "Gist editor dialog");
        AutomationElement cancelButton = WaitForElement(
            "Gist editor Cancel button",
            () => dialog.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")) is { } candidate &&
                IsVisible(candidate)
                    ? candidate
                    : null);
        Invoke(cancelButton, "Gist editor Cancel action");
        WaitUntil(
            "Gist editor dialog dismissal",
            () => FindVisible(window, "GistEditorDialog") is null);
    }

    internal static void ValidateDashboardCustomShellDialog(
        Window window,
        string outputDirectory,
        SystemColorPalette palette,
        Viewport viewport,
        string viewportName)
    {
        Resize(window, viewport);
        Invoke(WaitForVisibleElement(window, "DashboardCustomizeButton"), "Dashboard customize action");
        AutomationElement dialog = WaitForVisibleElement(window, "DashboardCustomizeDialog");
        AutomationElement saveButton = WaitForVisibleElement(window, "DashboardCustomizeSaveButton");
        CaptureDialogState(
            window,
            dialog,
            saveButton,
            palette,
            Path.Combine(outputDirectory, $"high-contrast-live-custom-shell-dialog-{viewportName}.png"),
            "Dashboard custom-shell dialog");
        Invoke(WaitForVisibleElement(window, "DashboardCustomizeCancelButton"), "Dashboard customize cancel action");
        WaitUntil(
            "Dashboard custom-shell dialog dismissal",
            () => FindVisible(window, "DashboardCustomizeDialog") is null);
    }

    private static void CaptureDialogState(
        Window window,
        AutomationElement dialog,
        AutomationElement focusTarget,
        SystemColorPalette palette,
        string screenshotPath,
        string description)
    {
        Rectangle windowBounds = window.BoundingRectangle;
        Rectangle dialogBounds = dialog.BoundingRectangle;
        Assert(
            windowBounds.Contains(dialogBounds),
            $"{description} escaped or was clipped by the app window: window={windowBounds}, dialog={dialogBounds}.");
        Focus(focusTarget, $"{description} focus target");
        using CapturedFrame frame = CaptureWindow(window);
        frame.Save(screenshotPath);
        AssertSystemColorTreatment(
            frame,
            dialogBounds,
            focusTarget.BoundingRectangle,
            palette,
            description);
    }

    internal static void ValidateProfileViewport(
        Window window,
        Viewport viewport,
        string neutralFocusId,
        SystemColorPalette palette,
        string screenshotPath,
        string description)
    {
        Resize(window, viewport);
        AutomationElement overview = WaitForVisibleElement(window, "ProfileModeOverviewItem");
        AutomationElement repositories = WaitForVisibleElement(window, "ProfileModeRepositoriesItem");
        _ = WaitForVisibleElement(window, neutralFocusId);
        AssertSelected(overview, description);
        CaptureKeyboardSelectionState(
            window,
            overview,
            repositories,
            VirtualKeyShort.RIGHT,
            VirtualKeyShort.LEFT,
            palette,
            screenshotPath,
            description);
    }

    internal static void ValidateContributionGraph(
        Window window,
        UIA3Automation automation,
        string outputDirectory,
        SystemColorPalette palette)
    {
        Resize(window, Wide);
        AutomationElement graph = WaitForElement(
            "populated Profile contribution graph",
            () =>
            {
                AutomationElement? candidate = window.FindFirstDescendant(
                    cf => cf.ByAutomationId("ProfileContributionGraph"));
                string name = candidate?.Name ?? string.Empty;
                return IsVisible(candidate) &&
                       name.StartsWith("Contribution calendar. ", StringComparison.Ordinal) &&
                       !name.Contains("No contribution data", StringComparison.OrdinalIgnoreCase)
                    ? candidate
                    : null;
            });
        AutomationElement neutralFocus = WaitForVisibleElement(window, "ProfileEditButton");
        Assert(graph.ControlType == ControlType.Calendar, "Profile contribution graph is not exposed as a Calendar.");
        Assert(
            graph.Properties.IsKeyboardFocusable.ValueOrDefault,
            "Profile contribution graph is not keyboard focusable.");

        using CapturedFrame before = CaptureWithFocus(window, neutralFocus);
        Focus(graph, "Profile contribution graph");
        WaitForElement(
            "Profile contribution keyboard tooltip",
            () => automation.GetDesktop()
                .FindAllDescendants(cf => cf.ByControlType(ControlType.ToolTip))
                .FirstOrDefault(IsVisible));
        using CapturedFrame after = CaptureWindow(window);
        after.Save(Path.Combine(outputDirectory, "high-contrast-live-profile-graph-focus-1366x900.png"));
        AssertFocusPixelsChanged(before, after, graph.BoundingRectangle, "Profile contribution graph");
        AssertSystemColorTreatment(after, graph.BoundingRectangle, palette, "Profile contribution graph");

        string lastDayName = graph.Name;
        ActivateForKeyboard(window, graph, "Profile contribution graph Home navigation");
        Keyboard.Press(VirtualKeyShort.HOME);
        WaitUntil(
            "Profile contribution graph Home navigation to reach the first day",
            () => !string.Equals(graph.Name, lastDayName, StringComparison.Ordinal));
        string firstDayName = graph.Name;
        ActivateForKeyboard(window, graph, "Profile contribution graph End navigation");
        Keyboard.Press(VirtualKeyShort.END);
        WaitUntil(
            "Profile contribution graph End navigation to restore the last day",
            () => !string.Equals(graph.Name, firstDayName, StringComparison.Ordinal));
        Assert(
            graph.Name.StartsWith("Contribution calendar. ", StringComparison.Ordinal),
            "Profile contribution graph lost its accessible selected-day identity after keyboard navigation.");
    }

    private static void CaptureFocusedState(
        Window window,
        AutomationElement neutralFocus,
        AutomationElement target,
        SystemColorPalette palette,
        string screenshotPath,
        string description)
    {
        Assert(
            target.Properties.IsKeyboardFocusable.ValueOrDefault,
            $"{description} is not keyboard focusable.");
        using CapturedFrame before = CaptureWithFocus(window, neutralFocus);
        Focus(target, description);
        using CapturedFrame after = CaptureWindow(window);
        after.Save(screenshotPath);
        AssertFocusPixelsChanged(before, after, target.BoundingRectangle, description);
        AssertSystemColorTreatment(after, target.BoundingRectangle, palette, description);
    }

    private static void CaptureKeyboardSelectionState(
        Window window,
        AutomationElement initial,
        AutomationElement target,
        VirtualKeyShort moveKey,
        VirtualKeyShort restoreKey,
        SystemColorPalette palette,
        string screenshotPath,
        string description)
    {
        AssertSelected(initial, $"{description} initial item");
        Assert(target.Patterns.SelectionItem.IsSupported, $"{description} target has no native selection state.");
        using CapturedFrame before = CaptureWithFocus(window, initial);
        Keyboard.Press(moveKey);
        WaitUntil(
            $"{description} keyboard selection",
            () => target.Properties.HasKeyboardFocus.ValueOrDefault &&
                target.Patterns.SelectionItem.Pattern.IsSelected.Value);
        Thread.Sleep(150);
        using CapturedFrame after = CaptureWindow(window);
        after.Save(screenshotPath);
        AssertFocusPixelsChanged(before, after, target.BoundingRectangle, description);
        AssertSystemColorTreatment(after, target.BoundingRectangle, palette, description);
        Keyboard.Press(restoreKey);
        WaitUntil(
            $"{description} selection restoration",
            () => initial.Properties.HasKeyboardFocus.ValueOrDefault &&
                initial.Patterns.SelectionItem.Pattern.IsSelected.Value);
    }

    private static CapturedFrame CaptureWithFocus(Window window, AutomationElement target)
    {
        Focus(target, target.AutomationId ?? target.Name ?? "baseline focus target");
        return CaptureWindow(window);
    }
}
