using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Probes.Authentication.LoginAuthUiProbe;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;

namespace JitHub.WinUI.Automation.Probes.TargetCapture;

/// <summary>
/// Captures the configured themes and design-lab targets to PNG files and writes the capture manifest (the default mode when no probe is named).
/// </summary>
internal static class TargetCaptureProbe
{
    internal static void RunTargetCaptureProbe(CaptureOptions options)
    {
        var captures = new List<CaptureResult>();
        foreach (string theme in options.Themes)
        {
            foreach (CaptureTarget target in options.Targets)
            {
                KillExistingApplicationInstances(options.AppPath);
                string[] launchArguments = BuildLaunchArguments(target, theme, options.RepositoryFullName);
                Console.WriteLine($"Launching {target.Name}: {string.Join(' ', launchArguments)}");
                using var app = LaunchApplication(options.AppPath, launchArguments);
                using var automation = new UIA3Automation();

                try
                {
                    Window window = GetReadyWindow(app, automation, $"target '{target.Name}'");
                    Thread.Sleep(GetSettleDelay(target));
                    PrepareTargetForCapture(window, target);

                    if (string.Equals(target.Name, "login", StringComparison.OrdinalIgnoreCase))
                    {
                        AssertLoginLiveUi(window);
                    }

                    AutomationElement element = window;
                    if (!string.IsNullOrWhiteSpace(target.AutomationId))
                    {
                        var elementRetry = Retry.WhileNull(
                            () => window.FindFirstDescendant(cf => cf.ByAutomationId(target.AutomationId)),
                            timeout: TimeSpan.FromSeconds(10),
                            interval: TimeSpan.FromMilliseconds(200));
                        if (elementRetry.Success && elementRetry.Result is not null)
                        {
                            element = elementRetry.Result;
                            if (element.Patterns.ScrollItem.IsSupported)
                            {
                                element.Patterns.ScrollItem.Pattern.ScrollIntoView();
                                Thread.Sleep(500);
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException($"Unable to find screenshot target '{target.AutomationId}' for '{target.Name}'.");
                        }
                    }

                    string fileName = $"{theme}-{target.Name}.png";
                    string filePath = Path.Combine(options.OutputDirectory, fileName);
                    AutomationElement captureTarget = string.Equals(target.Page, "design-lab", StringComparison.OrdinalIgnoreCase)
                        ? window
                        : element;
                    if (ReferenceEquals(captureTarget, window) || IsAppPreviewTarget(target))
                    {
                        CaptureWindow(window, filePath);
                    }
                    else
                    {
                        CaptureElement(window, captureTarget, filePath);
                    }

                    if (string.Equals(target.Name, "login", StringComparison.OrdinalIgnoreCase))
                    {
                        AssertLoginThemeScreenshot(filePath, theme);
                    }

                    captures.Add(new CaptureResult(theme, target.Name, fileName));
                }
                finally
                {
                    TryClose(app);
                    KillExistingApplicationInstances(options.AppPath);
                }
            }
        }

        WriteManifest(options.OutputDirectory, captures);
        Console.WriteLine($"Captured {captures.Count} screenshots to {options.OutputDirectory}");
    }

    private static string[] BuildLaunchArguments(CaptureTarget target, string theme, string repoFullName)
    {
        var arguments = new List<string>
        {
            $"--page={target.Page}",
            $"--theme={theme}"
        };

        if (!string.IsNullOrWhiteSpace(target.Scenario))
        {
            arguments.Add($"--scenario={target.Scenario}");
        }

        if (target.Page.StartsWith("repo", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add($"--repo={repoFullName}");
        }

        return arguments.ToArray();
    }

    private static void PrepareTargetForCapture(Window window, CaptureTarget target)
    {
        if (IsRepoCodeTarget(target))
        {
            ClickReadmeByCoordinates(window);
            Thread.Sleep(4000);
            return;
        }

        if (!IsPullRequestTarget(target))
        {
            return;
        }

        Thread.Sleep(5000);
    }

    private static bool ClickReadmeByCoordinates(Window window)
    {
        var windowBounds = window.BoundingRectangle;
        Mouse.DoubleClick(new System.Drawing.Point(
            windowBounds.X + 220,
            windowBounds.Y + 430));
        return true;
    }

    private static int GetSettleDelay(CaptureTarget target)
    {
        if (string.Equals(target.Page, "repo-code", StringComparison.OrdinalIgnoreCase))
        {
            return 11500;
        }

        if (IsPullRequestTarget(target))
        {
            return 11500;
        }

        if (target.Page.StartsWith("repo", StringComparison.OrdinalIgnoreCase))
        {
            return 6500;
        }

        if (string.Equals(target.Page, "home", StringComparison.OrdinalIgnoreCase))
        {
            return 2500;
        }

        return 900;
    }

    private static bool IsPullRequestTarget(CaptureTarget target) =>
        string.Equals(target.Page, "repo-pulls", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(target.Page, "repo-pull-requests", StringComparison.OrdinalIgnoreCase);

    private static bool IsRepoCodeTarget(CaptureTarget target) =>
        string.Equals(target.Page, "repo-code", StringComparison.OrdinalIgnoreCase);

    private static bool IsAppPreviewTarget(CaptureTarget target) =>
        target.Page.StartsWith("repo", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(target.Page, "home", StringComparison.OrdinalIgnoreCase);

    private static void WriteManifest(string outputDirectory, IReadOnlyList<CaptureResult> captures)
    {
        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html><head><meta charset='utf-8'><title>WinUI captures</title><style>body{font-family:Segoe UI, sans-serif;background:#f5f1e7;color:#223127;padding:24px}h1{font-size:28px}section{margin:0 0 24px}ul{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:16px;list-style:none;padding:0}li{background:#fffdfc;border:1px solid #d5cbb7;border-radius:16px;padding:12px}img{width:100%;border-radius:12px;border:1px solid #e6ddcc}small{display:block;margin-top:8px;color:#4b5e52}</style></head><body>");
        html.AppendLine("<h1>JitHub WinUI screenshot manifest</h1>");

        foreach (var themeGroup in captures.GroupBy(c => c.Theme))
        {
            html.AppendLine($"<section><h2>{themeGroup.Key}</h2><ul>");
            foreach (CaptureResult capture in themeGroup)
            {
                html.AppendLine($"<li><img src='{capture.FileName}' alt='{capture.Name}' /><small>{capture.Name}</small></li>");
            }
            html.AppendLine("</ul></section>");
        }

        html.AppendLine("</body></html>");
        File.WriteAllText(Path.Combine(outputDirectory, "index.html"), html.ToString());
    }
}
