using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;

namespace JitHub.WinUI.Automation.Probes.DesignLab;

/// <summary>
/// Drives the segments-hover probe against the design lab segments scenario.
/// </summary>
internal static class SegmentsHoverProbe
{
    internal static void RunSegmentsHoverProbe(CaptureOptions options)
    {
        using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
            ? LaunchApplication(options.AppPath, "--page=design-lab", "--scenario=segments", "--theme=dark")
            : CreateProbeApplication(options);
        using var automation = new UIA3Automation();

        Window window = GetReadyWindow(app, automation, "segments-hover probe");
        var publicRetry = Retry.WhileNull(
            () => window.FindAllDescendants(cf => cf.ByText("Public"))
                .Where(element => element.BoundingRectangle.Width > 20 && element.BoundingRectangle.Height > 10)
                .OrderByDescending(element => element.BoundingRectangle.X)
                .FirstOrDefault(),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(200));
        if (!publicRetry.Success || publicRetry.Result is null)
        {
            throw new InvalidOperationException("Unable to find Public segmented item for hover probe.");
        }

        var bounds = publicRetry.Result.BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(
            (int)Math.Round(bounds.X + bounds.Width / 2d),
            (int)Math.Round(bounds.Y + bounds.Height / 2d)));
        Thread.Sleep(700);

        string filePath = Path.Combine(options.OutputDirectory, "probe-segments-hover.png");
        CaptureWindow(window, filePath);

        Console.WriteLine($"segments-hover probe: screenshot={filePath}");
        if (string.IsNullOrWhiteSpace(options.AttachProcess))
        {
            TryClose(app);
        }
    }
}
