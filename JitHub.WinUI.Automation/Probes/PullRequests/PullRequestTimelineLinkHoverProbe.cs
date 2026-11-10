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

namespace JitHub.WinUI.Automation.Probes.PullRequests;

/// <summary>
/// Drives the pr-timeline-link-hover probe.
/// </summary>
internal static class PullRequestTimelineLinkHoverProbe
{
    internal static void RunPullRequestTimelineLinkHoverProbe(CaptureOptions options)
    {
        using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
            ? LaunchApplication(options.AppPath, "--page=design-lab", "--scenario=pr-timeline", "--theme=dark")
            : CreateProbeApplication(options);
        using var automation = new UIA3Automation();

        Window window = GetReadyWindow(app, automation, "pull request timeline link hover probe");
        var linkRetry = Retry.WhileNull(
            () => window.FindAllDescendants()
                .Where(element =>
                    IsVisible(element)
                    && (element.ControlType == ControlType.Hyperlink || element.ControlType == ControlType.Text))
                .FirstOrDefault(element =>
                    GetElementName(element).Contains("9648d21", StringComparison.OrdinalIgnoreCase)),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(200));
        if (!linkRetry.Success || linkRetry.Result is null)
        {
            throw new InvalidOperationException("Unable to find a pull request timeline inline link for hover probe.");
        }

        AutomationElement link = linkRetry.Result;
        var bounds = link.BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(
            (int)Math.Round(bounds.X + bounds.Width / 2d),
            (int)Math.Round(bounds.Y + bounds.Height / 2d)));
        Thread.Sleep(700);

        string hoverPath = Path.Combine(options.OutputDirectory, "probe-pr-timeline-link-hover.png");
        CaptureWindowWithPopups(window, hoverPath);

        Console.WriteLine($"pr-timeline-link-hover probe: link='{GetElementName(link)}', screenshot={hoverPath}");
        if (string.IsNullOrWhiteSpace(options.AttachProcess))
        {
            TryClose(app);
        }
    }
}
