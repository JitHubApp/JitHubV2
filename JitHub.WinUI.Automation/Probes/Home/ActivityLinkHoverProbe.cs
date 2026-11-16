using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;

namespace JitHub.WinUI.Automation.Probes.Home;

/// <summary>
/// Drives the activity-link-hover probe.
/// </summary>
internal static class ActivityLinkHoverProbe
{
    internal static void RunActivityLinkHoverProbe(CaptureOptions options)
    {
        using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
            ? LaunchApplication(options.AppPath, "--page=design-lab", "--scenario=activities", "--theme=dark")
            : CreateProbeApplication(options);
        using var automation = new UIA3Automation();

        Window window = GetReadyWindow(app, automation, "activity-link-hover probe");
        var linkRetry = Retry.WhileNull(
            () => window.FindAllDescendants()
                .Where(element =>
                    IsVisible(element)
                    && (element.ControlType == ControlType.Hyperlink || element.ControlType == ControlType.Text))
                .FirstOrDefault(element =>
                {
                    string name = GetElementName(element);
                    return name.Contains("commits", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("JitHubApp/JitHubV2", StringComparison.OrdinalIgnoreCase);
                }),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(200));
        if (!linkRetry.Success || linkRetry.Result is null)
        {
            throw new InvalidOperationException("Unable to find an activity inline link for hover probe.");
        }

        AutomationElement link = linkRetry.Result;
        var bounds = link.BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(
            (int)Math.Round(bounds.X + bounds.Width / 2d),
            (int)Math.Round(bounds.Y + bounds.Height / 2d)));
        Thread.Sleep(700);

        string hoverPath = Path.Combine(options.OutputDirectory, "probe-activity-link-hover.png");
        CaptureWindowWithPopups(window, hoverPath);

        Console.WriteLine($"activity-link-hover probe: link='{GetElementName(link)}', screenshot={hoverPath}");
        if (string.IsNullOrWhiteSpace(options.AttachProcess))
        {
            TryClose(app);
        }
    }
}
