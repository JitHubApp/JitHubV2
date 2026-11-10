using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Shell.ShellSearch;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Search;

/// <summary>
/// Drives the repo-search-responsive probe.
/// </summary>
internal static class RepositorySearchResponsiveProbe
{
    // This method stays whole because it opens Repository Search once through Ctrl+K and the shell search submit, then
    // at each of five sizes finds the query, filter, sort, and results again and checks clipping, that resubmitting a
    // query keeps the page width, and keyboard reachability on that same page.
    internal static void RunRepositorySearchResponsiveProbe(CaptureOptions options)
    {
        using var app = LaunchApplication(options.AppPath, "--page=shell", "--scenario=search-suggestions", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "repo-search-responsive probe");
            ResizeWindow(window, 1366, 900);
            PressCtrlK();
            AutomationElement shellSearch = WaitForElement(
                "ShellSearchTextBox for repository search",
                () => FindShellSearchTextBox(window),
                TimeSpan.FromSeconds(5));
            shellSearch.AsTextBox().Text = string.Empty;
            shellSearch.AsTextBox().Enter("JitHubApp/JitHubV2");
            AutomationElement submit = WaitForElement(
                "ShellSearchSubmitButton for repository search",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchSubmitButton")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(submit);

            AutomationElement root = WaitForElement(
                "RepoSearchPageRoot",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchPageRoot")),
                TimeSpan.FromSeconds(10));
            AutomationElement query = WaitForElement(
                "RepoSearchQueryTextBox",
                () => root.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchQueryTextBox")),
                TimeSpan.FromSeconds(5));
            AutomationElement filter = WaitForElement(
                "RepoSearchFilterButton",
                () => root.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchFilterButton")),
                TimeSpan.FromSeconds(5));
            AutomationElement sort = WaitForElement(
                "RepoSearchSortComboBox",
                () => root.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchSortComboBox")),
                TimeSpan.FromSeconds(5));
            AutomationElement results = WaitForElement(
                "RepoSearchResultsList",
                () => root.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchResultsList")),
                TimeSpan.FromSeconds(5));

            (int Width, int Height)[] sizes =
            [
                (1366, 900),
                (1180, 800),
                (900, 700),
                (760, 650),
                (640, 600)
            ];
            foreach ((int width, int height) in sizes)
            {
                Rectangle resizedBounds = ResizeWindow(window, width, height);
                int actualWidth = resizedBounds.Width;
                string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
                Thread.Sleep(300);
                root = WaitForElement(
                    $"RepoSearchPageRoot at {viewportLabel}",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchPageRoot")),
                    TimeSpan.FromSeconds(5));
                query = root.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchQueryTextBox"))!;
                filter = root.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchFilterButton"))!;
                sort = root.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchSortComboBox"))!;
                results = root.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchResultsList"))!;

                AssertProbe(IsVisible(root), $"Repository Search root was hidden at {viewportLabel}.");
                AssertProbe(IsInsideWindowBounds(query, window), $"Repository Search query was clipped at {viewportLabel}.");
                AssertProbe(IsInsideWindowBounds(filter, window), $"Repository Search filter was clipped at {viewportLabel}.");
                AssertProbe(IsInsideWindowBounds(sort, window), $"Repository Search sort was clipped at {viewportLabel}.");
                AssertProbe(IsInsideWindowBounds(results, window), $"Repository Search results escaped the window at {viewportLabel}.");

                Rectangle before = root.BoundingRectangle;
                query.AsTextBox().Text = actualWidth <= 760 ? "microsoft" : "JitHubApp/JitHubV2";
                query.Focus();
                Keyboard.Press(VirtualKeyShort.ENTER);
                Thread.Sleep(300);
                Rectangle after = root.BoundingRectangle;
                AssertProbe(
                    Math.Abs(after.Width - before.Width) <= 2,
                    $"Repository Search content changed page width at {viewportLabel} (before={before.Width}, after={after.Width}).");

                query.Focus();
                AssertProbe(query.Properties.HasKeyboardFocus.ValueOrDefault, $"Repository Search query was not keyboard reachable at {viewportLabel}.");
                filter.Focus();
                AssertProbe(filter.Properties.HasKeyboardFocus.ValueOrDefault, $"Repository Search filter was not keyboard reachable at {viewportLabel}.");
                sort.Focus();
                AssertProbe(sort.Properties.HasKeyboardFocus.ValueOrDefault, $"Repository Search sort was not keyboard reachable at {viewportLabel}.");
                CaptureWindow(window, Path.Combine(options.OutputDirectory, $"repo-search-responsive-{viewportLabel}.png"));
            }

            Console.WriteLine($"repo-search-responsive probe: {sizes.Length} responsive states, clipping, focus, and width-stability checks passed; output={options.OutputDirectory}");
        }
        finally
        {
            TryClose(app);
        }
    }
}
