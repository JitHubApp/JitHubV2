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
/// Drives the emoji-panel probe against the design lab conversation scenario.
/// </summary>
internal static class EmojiPanelProbe
{
    internal static void RunEmojiPanelProbe(CaptureOptions options)
    {
        using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
            ? LaunchApplication(options.AppPath, "--page=design-lab", "--scenario=conversation", "--theme=dark")
            : CreateProbeApplication(options);
        using var automation = new UIA3Automation();

        Window window = GetReadyWindow(app, automation, "emoji-panel probe");
        var launcherRetry = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("EmojiPanelLauncherButton")),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(200));
        if (!launcherRetry.Success || launcherRetry.Result is null)
        {
            throw new InvalidOperationException("Unable to find EmojiPanelLauncherButton for emoji-panel probe.");
        }

        AutomationElement launcher = launcherRetry.Result;
        var bounds = launcher.BoundingRectangle;
        var clickPoint = new System.Drawing.Point(
            (int)Math.Round(bounds.X + (bounds.Width / 2d)),
            (int)Math.Round(bounds.Y + (bounds.Height / 2d)));

        Console.WriteLine($"emoji-panel probe target: bounds={bounds}, click={clickPoint}");
         if (launcher.Patterns.Invoke.IsSupported)
         {
             launcher.Patterns.Invoke.Pattern.Invoke();
         }
         else
        {
            Mouse.Click(clickPoint);
         }
         Thread.Sleep(900);

         var firstReactionRetry = Retry.WhileNull(
             () => window.FindFirstDescendant(cf => cf.ByAutomationId("EmojiReactionButton_Plus1")),
             timeout: TimeSpan.FromSeconds(5),
             interval: TimeSpan.FromMilliseconds(200));
         if (firstReactionRetry.Success && firstReactionRetry.Result is not null)
         {
             var reactionBounds = firstReactionRetry.Result.BoundingRectangle;
             Mouse.MoveTo(new System.Drawing.Point(
                 (int)Math.Round(reactionBounds.X + (reactionBounds.Width / 2d)),
                 (int)Math.Round(reactionBounds.Y + (reactionBounds.Height / 2d))));
            Thread.Sleep(300);
        }

         string filePath = Path.Combine(options.OutputDirectory, "probe-emoji-panel.png");
        CaptureWindowWithPopups(window, filePath);

        Console.WriteLine($"emoji-panel probe: screenshot={filePath}");
        if (string.IsNullOrWhiteSpace(options.AttachProcess))
        {
            TryClose(app);
        }
    }
}
