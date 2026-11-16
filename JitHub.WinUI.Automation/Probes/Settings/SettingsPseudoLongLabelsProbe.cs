using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Settings;

/// <summary>
/// Drives the settings-pseudo-long probe.
/// </summary>
internal static class SettingsPseudoLongLabelsProbe
{
    internal static void RunSettingsPseudoLongLabelsProbe(CaptureOptions options)
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(options.AppPath, "--page=settings", "--theme=dark", "--scenario=settings-pseudo-long-labels");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "settings pseudo-long labels probe");
            ResizeWindow(window, 900, 700);
            AutomationElement rail = WaitForElement(
                "pseudo-long Settings rail",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList")),
                TimeSpan.FromSeconds(8));
            foreach (AutomationElement item in rail.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Where(IsVisible))
            {
                AssertProbe(IsInsideWindowBounds(item, window), $"Pseudo-long Settings rail item '{item.Name}' was clipped.");
            }
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "settings-pseudo-long-900x700.png"));

            ResizeWindow(window, 640, 600);
            AutomationElement picker = WaitForElement(
                "pseudo-long Settings compact picker",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsCompactSectionPicker")),
                TimeSpan.FromSeconds(5));
            picker.AsComboBox().Expand();
            string[] expectedLabels = new[] { "Appearance", "General", "Privacy", "Data & Cache", "Diagnostics", "About" }
                .Select(label => string.Join(" ", Enumerable.Repeat(label, 4)))
                .ToArray();
            AssertProbe(
                WaitUntilAvailable(
                    () => picker.AsComboBox().Items.Length == expectedLabels.Length,
                    TimeSpan.FromSeconds(5)),
                "Pseudo-long compact Settings picker did not realize all six sections.");
            FlaUI.Core.AutomationElements.ComboBoxItem[] items = picker.AsComboBox().Items;
            for (int index = 0; index < expectedLabels.Length; index++)
            {
                FlaUI.Core.AutomationElements.ComboBoxItem item = items[index];
                AssertProbe(
                    string.Equals(item.Text, expectedLabels[index], StringComparison.Ordinal),
                    $"Pseudo-long compact Settings item {index} exposed '{item.Text}' instead of its full label.");
                AssertProbe(
                    IsInsideWindowBounds(item, window),
                    $"Pseudo-long compact Settings label '{expectedLabels[index]}' was clipped: " +
                    $"item={item.BoundingRectangle}; window={window.BoundingRectangle}.");
            }
            CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "settings-pseudo-long-640x600-open.png"));
            picker.AsComboBox().Collapse();
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
