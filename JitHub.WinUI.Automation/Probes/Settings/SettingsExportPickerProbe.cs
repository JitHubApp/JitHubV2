using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Probes.Settings.SettingsDialogAssertions;
using static JitHub.WinUI.Automation.Probes.Settings.SettingsPageLayout;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowActivation;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Settings;

/// <summary>
/// Drives the settings-export-picker probe.
/// </summary>
internal static class SettingsExportPickerProbe
{
    internal static void RunSettingsExportPickerProbe(CaptureOptions options)
    {
        bool isAttached = !string.IsNullOrWhiteSpace(options.AttachProcess);
        using var app = isAttached
            ? CreateProbeApplication(options)
            : LaunchApplication(options.AppPath, "--page=settings", "--theme=light");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "settings export picker probe");
            ResizeWindow(window, 1366, 900);
            AssertSettingsExportPicker(window, automation, options.OutputDirectory);
            Console.WriteLine("settings-export-picker probe: picker canceled and settings action gate released.");
        }
        finally
        {
            if (!isAttached)
            {
                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }
    }

    // This method stays whole because it owns the full lifecycle of the system save picker opened from Diagnostics
    // export: it finds the owned window, checks Settings actions are disabled while it is open, always cancels it in
    // finally, then verifies focus restore and that the action is enabled again.
    internal static void AssertSettingsExportPicker(
        Window window,
        UIA3Automation automation,
        string outputDirectory)
    {
        OpenSettingsSection(window, "SettingsSection_diagnostics", "SettingsExportDiagnosticsButton");
        AutomationElement action = WaitForElement(
            "Settings diagnostics export",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsExportDiagnosticsButton")),
            TimeSpan.FromSeconds(5));
        RevealForInteraction(action, "Settings diagnostics export");
        bool canUseForegroundFocus = !NativeMethods.HasInvisibleForeignForegroundWindow(
            window.Properties.NativeWindowHandle.ValueOrDefault);
        if (canUseForegroundFocus)
        {
            action.Focus();
        }
        IntPtr appWindowHandle = GetNativeWindowHandle(window);
        InvokeOrClick(action);
        TryInvokeDisabledSettingsAction(action);
        AutomationElement? pickerWindow = null;
        try
        {
            pickerWindow = WaitForElement(
                "Windows diagnostics save picker window",
                () =>
                {
                    if (!NativeMethods.TryFindLargestOwnedTopLevelWindow(appWindowHandle, out IntPtr pickerHandle))
                    {
                        return null;
                    }

                    AutomationElement candidate = automation.FromHandle(pickerHandle);
                    return IsVisible(candidate) &&
                        candidate.FindFirstDescendant(
                            cf => cf.ByAutomationId("FileNameControlHost")) is not null
                        ? candidate
                        : null;
                },
                TimeSpan.FromSeconds(8));
            AutomationElement pickerSave = WaitForElement(
                "Windows diagnostics save picker primary command",
                () => pickerWindow.FindFirstDescendant(
                    cf => cf.ByAutomationId("1").And(cf.ByControlType(ControlType.Button))),
                TimeSpan.FromSeconds(3));
            AssertSettingsActionsDisabled(window);
            IntPtr pickerHandle = new(pickerWindow.Properties.NativeWindowHandle.ValueOrDefault);
            File.WriteAllText(
                Path.Combine(outputDirectory, "settings-export-picker.txt"),
                $"Picker HWND 0x{pickerHandle.ToInt64():X} in process " +
                $"{pickerWindow.Properties.ProcessId.ValueOrDefault} exposed its standard primary command while " +
                "Settings actions were disabled, then closed through its standard cancel command.");
        }
        finally
        {
            if (pickerWindow is not null)
            {
                AutomationElement? cancel = pickerWindow.FindFirstDescendant(
                    cf => cf.ByAutomationId("2").And(cf.ByControlType(ControlType.Button)));
                if (cancel is not null)
                {
                    InvokeOrClick(cancel);
                }
                else
                {
                    pickerWindow.Focus();
                    Keyboard.Press(VirtualKeyShort.ESCAPE);
                }
            }
            else
            {
                Keyboard.Press(VirtualKeyShort.ESCAPE);
            }
            if (pickerWindow is not null)
            {
                WaitUntil(
                    "diagnostics save picker closes",
                    () => !IsVisible(pickerWindow),
                    TimeSpan.FromSeconds(8));
            }
        }
        if (canUseForegroundFocus)
        {
            WaitUntil("diagnostics export restores focus", () => IsElementFocused(action), TimeSpan.FromSeconds(5));
        }
        WaitUntil(
            "diagnostics export action gate releases",
            () => action.IsEnabled,
            TimeSpan.FromSeconds(5));
    }
}
