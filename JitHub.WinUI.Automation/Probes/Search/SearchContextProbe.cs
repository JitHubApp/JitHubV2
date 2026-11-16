using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;

namespace JitHub.WinUI.Automation.Probes.Search;

/// <summary>
/// Drives the search-context probe.
/// </summary>
internal static class SearchContextProbe
{
    internal static void RunSearchContextProbe(CaptureOptions options)
    {
        using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
            ? LaunchApplication(options.AppPath, "--page=shell", "--scenario=search-suggestions", "--theme=dark")
            : CreateProbeApplication(options);
        using var automation = new UIA3Automation();

        Window window = GetReadyWindow(app, automation, "search-context probe");

        var searchRetry = Retry.WhileNull(
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchTextBox")),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(200));
        if (!searchRetry.Success || searchRetry.Result is null)
        {
            throw new InvalidOperationException("Unable to find ShellSearchTextBox for search-context probe.");
        }

        AutomationElement searchBox = searchRetry.Result;
        searchBox.FocusNative();
        Thread.Sleep(200);

        var searchBounds = searchBox.BoundingRectangle;
        var clickablePoint = new System.Drawing.Point(
            (int)Math.Round(searchBounds.X + (searchBounds.Width / 2d)),
            (int)Math.Round(searchBounds.Y + (searchBounds.Height / 2d)));
        Console.WriteLine(
            $"search-context probe target: bounds=({searchBounds.X},{searchBounds.Y},{searchBounds.Width},{searchBounds.Height}) " +
            $"click=({clickablePoint.X},{clickablePoint.Y}) windowTitle={window.Title}");

        Mouse.RightClick(clickablePoint);

        var menuRetry = Retry.WhileTrue(
            () =>
            {
                AutomationElement? undo = automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Undo"));
                AutomationElement? paste = automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Paste"));
                return undo is null || paste is null;
            },
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(100));

        Thread.Sleep(900);
        AutomationElement? undoMenuItem = automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Undo"));
        AutomationElement? pasteMenuItem = automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Paste"));

        string filePath = Path.Combine(options.OutputDirectory, "probe-search-context.png");
        CaptureWindowWithPopups(window, filePath);

        bool menuStillOpen = undoMenuItem is not null && pasteMenuItem is not null;
        Console.WriteLine($"search-context probe: initialMenu={!menuRetry.Result}, menuStillOpen={menuStillOpen}, screenshot={filePath}");

        if (!menuRetry.Success || !menuStillOpen)
        {
            throw new InvalidOperationException("Search context menu did not remain open during the automation probe.");
        }

        if (string.IsNullOrWhiteSpace(options.AttachProcess))
        {
            TryClose(app);
        }
    }
}
