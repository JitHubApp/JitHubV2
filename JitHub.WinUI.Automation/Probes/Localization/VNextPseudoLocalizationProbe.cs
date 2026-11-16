using System.Drawing;
using System.Xml.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Localization;

/// <summary>
/// Drives the vnext-pseudo-localization probe.
/// </summary>
internal static class VNextPseudoLocalizationProbe
{
    // This method stays whole because it is one target-by-viewport matrix that launches a pseudo-localized app per page
    // and, at each of three sizes, checks the page root width, the required Settings checkpoints, the shell search name
    // and placeholder, and every visible command against the English fallback set loaded once up front.
    internal static void RunVNextPseudoLocalizationProbe(CaptureOptions options)
    {
        (string Page, string RootId, string[] LocalizedCheckpointIds)[] targets =
        [
            ("settings", "SettingsRoot",
            [
                "SettingsThemeHeading",
                "SettingsPaletteHeading",
                "SettingsThemeSystemLabel",
                "SettingsThemeLightLabel",
                "SettingsThemeDarkLabel",
                "SettingsThemeSystem",
                "SettingsThemeLight",
                "SettingsThemeDark",
                "SettingsCompactSectionPicker"
            ]),
            ("repo-pulls", "RepoPullRequestsPageRoot", ["RepoPullRequestsNewButton", "RepoPullRequestsOpenListPaneButton", "RepoPullRequestsSection_Conversation"]),
            ("repo-commits", "RepoCommitsPageRoot", ["RepoCommitsSection_Diff"]),
            ("stars", "StarsPageRoot", ["StarsSelectionMode", "StarsOpenCategoriesButton", "StarsSearchBox"]),
            ("repo-issues", "RepoIssuesPageRoot", ["RepoIssuesNewIssueButton", "RepoIssuesOpenListPaneButton", "RepoIssuesToggleStateButton"]),
            ("my-issues", "MyIssuesPageRoot", ["MyIssuesStateSegmented", "MyIssuesStateCompactPicker", "MyIssuesOpenListPaneButton"]),
            ("my-pull-requests", "MyPullRequestsPageRoot", ["MyPullRequestsStateSegmented", "MyPullRequestsStateCompactPicker", "MyPullRequestsOpenListPaneButton"])
        ];
        (int Width, int Height)[] viewports = [(1366, 900), (760, 650), (640, 600)];
        string[] requiredSettingsCheckpoints =
        [
            "SettingsThemeHeading",
            "SettingsPaletteHeading",
            "SettingsThemeSystemLabel",
            "SettingsThemeLightLabel",
            "SettingsThemeDarkLabel",
            "SettingsThemeSystem",
            "SettingsThemeLight",
            "SettingsThemeDark"
        ];
        IReadOnlySet<string> englishUiFallbacks = LoadEnglishUiFallbacks(options.AppPath);
        string outputDirectory = Path.Combine(options.OutputDirectory, "vnext-pseudo-localization");
        Directory.CreateDirectory(outputDirectory);

        foreach ((string page, string rootId, string[] checkpointIds) in targets)
        {
            KillExistingApplicationInstances(options.AppPath);
            string[] arguments = page.StartsWith("repo-", StringComparison.Ordinal)
                ? [$"--page={page}", "--theme=dark", "--scenario=vnext-pseudo-localized", $"--repo={options.RepositoryFullName}"]
                : [$"--page={page}", "--theme=dark", "--scenario=vnext-pseudo-localized"];
            using var app = LaunchApplication(options.AppPath, arguments);
            using var automation = new UIA3Automation();
            try
            {
                Window window = GetReadyWindow(app, automation, $"vNext pseudo-localization {page}");
                AutomationElement primary = WaitForElement(
                    $"{page} primary command",
                    () => checkpointIds
                        .Select(id => FindCurrentVisibleByAutomationId(window, id))
                        .FirstOrDefault(IsVisible),
                    TimeSpan.FromSeconds(15));

                foreach ((int width, int height) in viewports)
                {
                    Rectangle resizedBounds = ResizeWindow(window, width, height);
                    int actualWidth = resizedBounds.Width;
                    string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
                    Thread.Sleep(450);
                    primary = WaitForElement(
                        $"{page} primary command at {viewportLabel}",
                        () => checkpointIds
                            .Select(id => FindCurrentVisibleByAutomationId(window, id))
                            .FirstOrDefault(IsVisible),
                        TimeSpan.FromSeconds(8));
                    AssertProbe(IsInsideWindowBounds(primary, window), $"{page}: primary command was offscreen at {viewportLabel}.");

                    AutomationElement? root = FindCurrentVisibleByAutomationId(window, rootId);
                    if (root is not null)
                    {
                        AssertProbe(IsInsideWindowBounds(root, window), $"{page}: page root escaped the window at {viewportLabel}.");
                        AssertProbe(
                            root.BoundingRectangle.Width >= Math.Min(300, actualWidth * 0.45),
                            $"{page}: localized content collapsed the page width at {viewportLabel}.");
                    }

                    AutomationElement[] visibleDescendants = window
                        .FindAllDescendants()
                        .Where(IsVisible)
                        .ToArray();
                    AutomationElement[] localizedCheckpoints = checkpointIds
                        .Select(id => window.FindFirstDescendant(cf => cf.ByAutomationId(id)))
                        .Where(element => element is not null)
                        .Cast<AutomationElement>()
                        .ToArray();
                    AutomationElement[] visiblePrimaryCommands = localizedCheckpoints
                        .Where(IsVisible)
                        .ToArray();
                    string visibleNames = string.Join(
                        " | ",
                        visibleDescendants
                            .Select(GetElementName)
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .Distinct(StringComparer.Ordinal)
                            .Take(40));
                    if (string.Equals(page, "settings", StringComparison.Ordinal))
                    {
                        string[] missingSettingsCheckpoints = requiredSettingsCheckpoints
                            .Where(id => localizedCheckpoints.All(element =>
                                !string.Equals(GetAutomationId(element), id, StringComparison.Ordinal)))
                            .ToArray();
                        AssertProbe(
                            missingSettingsCheckpoints.Length == 0,
                            $"settings: required localization checkpoints were absent at {viewportLabel}: " +
                            string.Join(", ", missingSettingsCheckpoints));
                    }
                    AssertProbe(
                        visiblePrimaryCommands.Length > 0,
                        $"{page}: no canonical localization checkpoint was visible at {viewportLabel}.");
                    foreach (AutomationElement command in localizedCheckpoints)
                    {
                        string commandName = GetElementName(command);
                        AssertProbe(
                            commandName.StartsWith("⟦", StringComparison.Ordinal),
                            $"{page}: primary command '{command.AutomationId}' used the English fallback " +
                            $"'{commandName}' at {viewportLabel}. Visible names: {visibleNames}");
                    }

                    AutomationElement shellSearch = WaitForElement(
                        $"{page} localized shell search",
                        () => FindCurrentVisibleByAutomationId(window, "ShellSearchTextBox"),
                        TimeSpan.FromSeconds(3));
                    string shellSearchName = GetElementName(shellSearch);
                    string shellSearchPlaceholder = shellSearch.Properties.HelpText.ValueOrDefault ?? string.Empty;
                    AssertProbe(
                        shellSearchName.StartsWith("⟦", StringComparison.Ordinal),
                        $"{page}: shell search used the English fallback '{shellSearchName}' at {viewportLabel}.");
                    AssertProbe(
                        shellSearchPlaceholder.StartsWith("⟦", StringComparison.Ordinal),
                        $"{page}: visible shell search placeholder used the English fallback " +
                        $"'{shellSearchPlaceholder}' at {viewportLabel}.");
                    AssertProbe(
                        shellSearch.BoundingRectangle.Width >= 128,
                        $"{page}: compact shell search text viewport was too narrow at {viewportLabel}: " +
                        $"{shellSearch.BoundingRectangle.Width:0.#}px.");

                    foreach (AutomationElement command in window.FindAllDescendants().Where(element =>
                        IsVisible(element) &&
                        element.ControlType is ControlType.Button or ControlType.Edit or ControlType.ComboBox))
                    {
                        string commandName = GetElementName(command).Trim();
                        AssertProbe(
                            !englishUiFallbacks.Contains(commandName),
                            $"{page}: visible {command.ControlType} '{GetAutomationId(command)}' retained the English " +
                            $"fallback '{commandName}' at {viewportLabel}.");
                        AssertProbe(
                            IsInsideWindowBounds(command, window),
                            $"{page}: visible {command.ControlType} '{commandName}' was clipped or offscreen at {viewportLabel}.");
                    }

                    CaptureWindow(
                        window,
                        Path.Combine(outputDirectory, $"{page}-{viewportLabel}.png"));
                }
            }
            finally
            {
                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }
    }

    private static IReadOnlySet<string> LoadEnglishUiFallbacks(string appPath)
    {
        string? appDirectory = Path.GetDirectoryName(Path.GetFullPath(appPath));
        DirectoryInfo? directory = appDirectory is null ? null : new DirectoryInfo(appDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        string resourcePath = directory is null
            ? string.Empty
            : Path.Combine(directory.FullName, "JitHub.WinUI", "Strings", "en-US", "Resources.resw");
        AssertProbe(
            File.Exists(resourcePath),
            $"Pseudo-localization probe could not find the English resource catalog from '{appPath}'.");

        return XDocument.Load(resourcePath)
            .Root!
            .Elements("data")
            .Select(element => ((string?)element.Element("value") ?? string.Empty).Trim())
            .Where(value => value.Length >= 2 && value.Any(char.IsLetter))
            .ToHashSet(StringComparer.Ordinal);
    }
}
