using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
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
using static JitHub.WinUI.Automation.Windows.WindowActivation;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Search;

/// <summary>
/// Drives the command-search probe.
/// </summary>
internal static class CommandSearchProbe
{
    // This method stays whole because it is one live-UI script on a single shell instance: Ctrl+K search, the
    // Notifications route, button and Enter submission into repository search, filter chips, result keyboard traversal
    // and the Settings suggestion each start from the focus and page left by the step before.
    internal static void RunCommandSearchProbe(CaptureOptions options)
    {
        using var app = LaunchApplication(options.AppPath, "--page=shell", "--scenario=search-suggestions", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "command-search probe");
            ResizeWindow(window, 1366, 900);
            AutomationElement searchBox = WaitForElement("ShellSearchTextBox", () => FindShellSearchTextBox(window), TimeSpan.FromSeconds(5));
            TryActivateWindow(window);
            PressCtrlK();
            WaitUntil(
                "Ctrl+K focuses command search",
                () => IsSearchActuallyFocused(automation, searchBox),
                TimeSpan.FromSeconds(3));
            AssertProbe(IsSearchActuallyFocused(automation, searchBox), "Ctrl+K did not focus command search.");
            searchBox.AsTextBox().Text = string.Empty;
            searchBox.AsTextBox().Enter("settings");
            AutomationElement suggestions = WaitForElement("ShellSearchSuggestionsList", () =>
            {
                AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchSuggestionsList"));
                return IsVisible(list) ? list : null;
            }, TimeSpan.FromSeconds(10));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "command-search-open.png"));
            searchBox.Focus();
            WaitUntil(
                "search regains keyboard focus before Escape",
                () => IsSearchActuallyFocused(automation, searchBox),
                TimeSpan.FromSeconds(3));
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(350);
            AutomationElement? focusedAfterEscape = automation.FocusedElement();
            Console.WriteLine(
                $"command-search: focus after Escape name='{GetElementName(focusedAfterEscape)}'.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "command-search-after-escape.png"));
            WaitUntil("search suggestions dismissed", () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchSuggestionsList"))), TimeSpan.FromSeconds(5));

            PressCtrlK();
            searchBox = WaitForElement("ShellSearchTextBox notifications command", () => FindShellSearchTextBox(window), TimeSpan.FromSeconds(5));
            searchBox.AsTextBox().Text = string.Empty;
            searchBox.AsTextBox().Enter("notifications");
            suggestions = WaitForElement("Notifications command suggestions", () =>
            {
                AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchSuggestionsList"));
                return IsVisible(list) ? list : null;
            }, TimeSpan.FromSeconds(10));
            AutomationElement notificationsResult = WaitForElement(
                "Notifications command",
                () => suggestions.FindFirstDescendant(cf => cf.ByText("Notifications")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(notificationsResult);
            WaitForElement(
                "NotificationsList from command search",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsList")),
                TimeSpan.FromSeconds(10));
            AutomationElement notificationsNav = WaitForElement(
                "ShellNav_notifications after command search",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellNav_notifications")),
                TimeSpan.FromSeconds(5));
            WaitUntil(
                "Notifications nav selected after command search",
                () => string.Equals(notificationsNav.Properties.HelpText.ValueOrDefault, "Selected", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            PressCtrlK();
            window = GetReadyWindow(app, automation, "command-search button submission");
            searchBox = WaitForElement("ShellSearchTextBox button submission", () => FindShellSearchTextBox(window), TimeSpan.FromSeconds(5));
            searchBox.AsTextBox().Text = string.Empty;
            searchBox.AsTextBox().Enter("JitHubApp/JitHubV2");
            AutomationElement submit = WaitForElement(
                "ShellSearchSubmitButton",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchSubmitButton")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(submit);
            Thread.Sleep(500);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "command-search-button-after-invoke.png"));
            AutomationElement queryBox = WaitForElement(
                "RepoSearchQueryTextBox",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchQueryTextBox")),
                TimeSpan.FromSeconds(8));
            AssertProbe(IsVisible(queryBox), "Visible search button did not open the repository search workspace.");
            AssertProbe(
                string.Equals(queryBox.AsTextBox().Text, "JitHubApp/JitHubV2", StringComparison.Ordinal),
                "Search button submission did not preserve the entered query.");
            WaitForElement(
                "RepoSearchFilterButton",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchFilterButton")),
                TimeSpan.FromSeconds(5));
            WaitForElement(
                "RepoSearchSortComboBox",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchSortComboBox")),
                TimeSpan.FromSeconds(5));
            AutomationElement filterButton = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchFilterButton"))!;
            InvokeOrClick(filterButton);
            AutomationElement ownerFilter = WaitForElement(
                "RepoSearchOwnerFilter",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchOwnerFilter")),
                TimeSpan.FromSeconds(5));
            SetTextBoxText(ownerFilter, "JitHubApp");
            Thread.Sleep(600);
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            AutomationElement ownerChip = WaitForElement(
                "RepoSearchFilterChip_owner",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchFilterChip_owner")),
                TimeSpan.FromSeconds(5));
            AssertProbe(IsVisible(ownerChip), "Repository owner filter did not project a removable filter chip.");
            InvokeOrClick(ownerChip);
            WaitUntil(
                "owner filter chip clears",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchFilterChip_owner"))),
                TimeSpan.FromSeconds(5));
            AutomationElement sortBox = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchSortComboBox"))!;
            sortBox.AsComboBox().Select("Recently updated");
            Thread.Sleep(300);
            sortBox.AsComboBox().Select("Best match");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "command-search-button-submitted.png"));

            AutomationElement home = WaitForElement(
                "ShellNav_home",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellNav_home")),
                TimeSpan.FromSeconds(5));
            AssertProbe(
                string.Equals(home.Properties.HelpText.ValueOrDefault, "Not selected", StringComparison.Ordinal),
                "Search workspace left Home highlighted in the shell rail.");
            PressCtrlK();
            window = GetReadyWindow(app, automation, "command-search return Home");
            searchBox = WaitForElement("ShellSearchTextBox return Home", () => FindShellSearchTextBox(window), TimeSpan.FromSeconds(5));
            searchBox.AsTextBox().Text = string.Empty;
            searchBox.AsTextBox().Enter("home");
            suggestions = WaitForElement("ShellSearchSuggestionsList return Home", () =>
            {
                AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchSuggestionsList"));
                return IsVisible(list) ? list : null;
            }, TimeSpan.FromSeconds(10));
            AutomationElement goHomeResult = WaitForElement(
                "Go Home suggestion",
                () => suggestions.FindFirstDescendant(cf => cf.ByText("Go Home")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(goHomeResult);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "command-search-return-home.png"));
            WaitForElement(
                "DashboardCustomizeButton before Enter submission",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")),
                TimeSpan.FromSeconds(8));
            WaitUntil(
                "Home nav selected after returning from search",
                () => string.Equals(home.Properties.HelpText.ValueOrDefault, "Selected", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            PressCtrlK();
            window = GetReadyWindow(app, automation, "command-search Enter submission");
            searchBox = WaitForElement("ShellSearchTextBox Enter submission", () => FindShellSearchTextBox(window), TimeSpan.FromSeconds(5));
            searchBox.AsTextBox().Text = string.Empty;
            searchBox.AsTextBox().Enter("JitHubApp/JitHubV2");
            Keyboard.Press(VirtualKeyShort.ENTER);
            queryBox = WaitForElement(
                "RepoSearchQueryTextBox after Enter submission",
                () =>
                {
                    AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchQueryTextBox"));
                    return IsVisible(candidate) && string.Equals(candidate!.AsTextBox().Text, "JitHubApp/JitHubV2", StringComparison.Ordinal)
                        ? candidate
                        : null;
                },
                TimeSpan.FromSeconds(10));
            AssertProbe(IsVisible(queryBox), "Enter submission did not open the same repository search workspace.");
            ResizeWindow(window, 900, 700);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "command-search-results-900x700.png"));
            ResizeWindow(window, 640, 600);
            AssertProbe(IsInsideWindowBounds(queryBox, window), "Repository search query escaped the compact window.");
            AssertProbe(
                IsInsideWindowBounds(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchFilterButton"))!, window),
                "Repository search filters escaped the compact window.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "command-search-results-640x600.png"));

            AutomationElement resultsList = WaitForElement(
                "RepoSearchResultsList",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoSearchResultsList")),
                TimeSpan.FromSeconds(5));
            queryBox.AsTextBox().Text = "microsoft";
            queryBox.Focus();
            Keyboard.Press(VirtualKeyShort.ENTER);
            WaitUntil(
                "multiple repository results for keyboard traversal",
                () => resultsList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Count(IsVisible) >= 2,
                TimeSpan.FromSeconds(15));
            AutomationElement resultRow = WaitForElement(
                "repository search result row",
                () => resultsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                TimeSpan.FromSeconds(10));
            AssertProbe(
                resultRow.AutomationId.StartsWith("RepoSearchResult_", StringComparison.Ordinal),
                "Repository search result row did not expose its stable dynamic automation id.");
            AssertProbe(
                !string.IsNullOrWhiteSpace(resultRow.Name) &&
                !resultRow.Name.Contains("RepositorySearchResultItem", StringComparison.Ordinal),
                "Repository search result row did not expose its visible repository name to UIA.");
            AssertProbe(
                resultRow.Patterns.SelectionItem.IsSupported,
                "Repository search result row did not expose the native selection-item pattern.");

            Mouse.MoveTo(resultRow.GetClickablePoint());
            Thread.Sleep(250);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "command-search-result-hover.png"));
            Mouse.Down(MouseButton.Left);
            try
            {
                Thread.Sleep(180);
                CaptureWindow(window, Path.Combine(options.OutputDirectory, "command-search-result-pressed.png"));
            }
            finally
            {
                Mouse.MoveTo(new System.Drawing.Point(
                    (int)resultsList.BoundingRectangle.Right - 8,
                    (int)resultsList.BoundingRectangle.Bottom - 8));
                Mouse.Up(MouseButton.Left);
            }

            resultRow.Patterns.SelectionItem.Pattern.Select();
            WaitUntil(
                "repository result selected state",
                () => resultRow.Patterns.SelectionItem.Pattern.IsSelected.Value,
                TimeSpan.FromSeconds(3));
            resultRow.Focus();
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "command-search-result-selected.png"));

            AutomationElement[] resultRows = resultsList
                .FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Where(IsVisible)
                .ToArray();
            AssertProbe(resultRows.Length >= 2, "Repository search did not expose enough rows to exercise keyboard traversal.");
            resultsList.Focus();
            WaitUntil(
                "repository search list receives keyboard focus",
                () => IsInsideElementBounds(automation.FocusedElement(), resultsList, 1),
                TimeSpan.FromSeconds(3));
            Keyboard.Press(VirtualKeyShort.DOWN);
            WaitUntil(
                "repository result Down traversal",
                () => resultRows[1].Patterns.SelectionItem.Pattern.IsSelected.Value,
                TimeSpan.FromSeconds(3));
            Keyboard.Press(VirtualKeyShort.UP);
            WaitUntil(
                "repository result Up traversal",
                () => resultRow.Patterns.SelectionItem.Pattern.IsSelected.Value,
                TimeSpan.FromSeconds(3));

            Keyboard.Press(VirtualKeyShort.ENTER);
            WaitForElement(
                "RepoDetailIdentity after repository search result",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailIdentity")),
                TimeSpan.FromSeconds(10));

            ResizeWindow(window, 1366, 900);
            WaitForElement("ShellNav_explore", () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellNav_explore")), TimeSpan.FromSeconds(5)).Click();
            WaitUntil("explore focuses search", () => IsSearchActuallyFocused(automation, searchBox), TimeSpan.FromSeconds(5));
            searchBox.AsTextBox().Text = string.Empty;
            searchBox.AsTextBox().Enter("settings");
            suggestions = WaitForElement("ShellSearchSuggestionsList exact invocation", () =>
            {
                AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchSuggestionsList"));
                return IsVisible(list) ? list : null;
            }, TimeSpan.FromSeconds(10));
            AutomationElement settingsResult = WaitForElement(
                "Open Settings suggestion",
                () => suggestions.FindFirstDescendant(cf => cf.ByText("Open Settings")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(settingsResult);
            WaitForElement(
                "SettingsSectionList after suggestion invocation",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList")),
                TimeSpan.FromSeconds(8));
            WaitUntil("suggestion invocation dismisses search", () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchSuggestionsList"))), TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "command-search-dismissed.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
