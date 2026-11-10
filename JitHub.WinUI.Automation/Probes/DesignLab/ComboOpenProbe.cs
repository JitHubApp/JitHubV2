using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;

namespace JitHub.WinUI.Automation.Probes.DesignLab;

/// <summary>
/// Drives the combo-open probe against the design lab inputs scenario.
/// </summary>
internal static class ComboOpenProbe
{
    internal static void RunComboOpenProbe(CaptureOptions options)
    {
        using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
            ? LaunchApplication(options.AppPath, "--page=design-lab", "--scenario=inputs", "--theme=dark")
            : CreateProbeApplication(options);
        using var automation = new UIA3Automation();

        Window window = GetReadyWindow(app, automation, "combo-open probe");
        var comboRetry = Retry.WhileNull(
            () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
                .FirstOrDefault(element => string.Equals(GetElementName(element), "Open", StringComparison.OrdinalIgnoreCase))
                ?? window.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox)),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100));
        if (!comboRetry.Success || comboRetry.Result is null)
        {
            throw new InvalidOperationException("Unable to find ComboBox for combo-open probe.");
        }

        AutomationElement combo = comboRetry.Result;
        Console.WriteLine($"combo-open probe target: name='{GetElementName(combo)}', bounds={combo.BoundingRectangle}");
        combo.FocusNative();
        Thread.Sleep(150);
        combo.AsComboBox().Expand();

        var openedItemRetry = Retry.WhileNull(
            () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Closed")),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100));
        bool expanded = openedItemRetry.Success && openedItemRetry.Result is not null && IsVisible(openedItemRetry.Result);

        string filePath = Path.Combine(options.OutputDirectory, "probe-combo-open.png");
        CaptureWindowWithPopups(window, filePath);

        Console.WriteLine($"combo-open probe: expanded={expanded}, screenshot={filePath}");

        if (!expanded)
        {
            throw new InvalidOperationException("ComboBox did not stay expanded for screenshot verification.");
        }

        if (string.IsNullOrWhiteSpace(options.AttachProcess))
        {
            TryClose(app);
        }
    }
}
