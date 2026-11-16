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
using static JitHub.WinUI.Automation.Shell.ShellSearch;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;

namespace JitHub.WinUI.Automation.Probes.Search;

/// <summary>
/// Drives the search-select-dismiss probe.
/// </summary>
internal static class SearchSelectDismissProbe
{
    // This method stays whole because it is one short search script: type a query, wait for the suggestions list and
    // its first result, click that result, then poll until the list disappears and capture the outcome, each step
    // depending on the popup the previous step opened.
    internal static void RunSearchSelectDismissProbe(CaptureOptions options)
    {
        using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
            ? LaunchApplication(options.AppPath, "--page=shell", "--scenario=search-suggestions", "--theme=dark")
            : CreateProbeApplication(options);
        using var automation = new UIA3Automation();

        Window window = GetReadyWindow(app, automation, "search-select-dismiss probe");
        var searchRetry = Retry.WhileNull(
            () => FindShellSearchTextBox(window),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(200));
        if (!searchRetry.Success || searchRetry.Result is null)
        {
            throw new InvalidOperationException("Unable to find ShellSearchTextBox for search-select-dismiss probe.");
        }

        AutomationElement searchBox = searchRetry.Result;
        Console.WriteLine($"search-select-dismiss probe target: name='{GetElementName(searchBox)}', bounds={searchBox.BoundingRectangle}");
        searchBox.FocusNative();
        Thread.Sleep(200);

        var textBox = searchBox.AsTextBox();
        textBox.Text = string.Empty;
        textBox.Enter("flutter");

        var listRetry = Retry.WhileNull(
            () =>
            {
                AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchSuggestionsList"));
                return IsVisible(list) ? list : null;
            },
            timeout: TimeSpan.FromSeconds(12),
            interval: TimeSpan.FromMilliseconds(250));
        if (!listRetry.Success || listRetry.Result is null)
        {
            throw new InvalidOperationException("Search suggestions did not open for search-select-dismiss probe.");
        }

        AutomationElement suggestionsList = listRetry.Result;
        var firstItemRetry = Retry.WhileNull(
            () => suggestionsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100));
        if (!firstItemRetry.Success || firstItemRetry.Result is null)
        {
            throw new InvalidOperationException("Search suggestions opened but no selectable result was found.");
        }

        AutomationElement firstItem = firstItemRetry.Result;
        AutomationElement suggestionAction =
            firstItem.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button))
            ?? firstItem;
        Console.WriteLine(
            $"search-select-dismiss first item bounds={firstItem.BoundingRectangle}, " +
            $"actionBounds={suggestionAction.BoundingRectangle}, actionName='{GetElementName(suggestionAction)}'");
        var actionBounds = suggestionAction.BoundingRectangle;
        var clickPoint = new System.Drawing.Point(
            (int)Math.Round(actionBounds.X + (actionBounds.Width / 2d)),
            (int)Math.Round(actionBounds.Y + (actionBounds.Height / 2d)));
        Console.WriteLine($"search-select-dismiss click={clickPoint}");
        Mouse.Click(clickPoint);

        bool dismissed = false;
        for (int attempt = 0; attempt < 25; attempt++)
        {
            Thread.Sleep(200);
            AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchSuggestionsList"));
            if (!IsVisible(list))
            {
                dismissed = true;
                break;
            }
        }

        string filePath = Path.Combine(options.OutputDirectory, "probe-search-select-dismiss.png");
        Thread.Sleep(900);
        CaptureWindowWithPopups(window, filePath);

        Console.WriteLine($"search-select-dismiss probe: dismissed={dismissed}, screenshot={filePath}");
        if (!dismissed)
        {
            throw new InvalidOperationException("Search suggestions remained visible after selecting a result.");
        }

        if (string.IsNullOrWhiteSpace(options.AttachProcess))
        {
            TryClose(app);
        }
    }
}
