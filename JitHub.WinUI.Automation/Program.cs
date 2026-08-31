using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NativeTableElementArray = Interop.UIAutomationClient.IUIAutomationElementArray;
using NativeTableItemPattern = Interop.UIAutomationClient.IUIAutomationTableItemPattern;

NativeMethods.EnablePerMonitorV2DpiAwareness();
var options = CaptureOptions.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);
PrepareAutomationDataRoot();
AutomationLifecycleLog.Configure(Path.Combine(options.OutputDirectory, "automation-lifecycle.log"));
AutomationLifecycleLog.Write(
    "automation-start",
    $"probe={options.Probe ?? "capture"}; app={options.AppPath}; dataRoot={GetAutomationDataRoot()}");

int automationExitCode = 0;
try
{
if (string.Equals(options.Probe, "search-context", StringComparison.OrdinalIgnoreCase))
{
    RunSearchContextProbe(options);
    return;
}

if (string.Equals(options.Probe, "theme-switch", StringComparison.OrdinalIgnoreCase))
{
    RunThemeSwitchProbe(options);
    return;
}

if (string.Equals(options.Probe, "theme-palettes", StringComparison.OrdinalIgnoreCase))
{
    RunThemePaletteProbe(options);
    return;
}

if (string.Equals(options.Probe, "login-auth-ui", StringComparison.OrdinalIgnoreCase))
{
    RunLoginAuthUiProbe(options);
    return;
}

if (string.Equals(options.Probe, "auth-lifecycle", StringComparison.OrdinalIgnoreCase))
{
    RunAuthLifecycleProbe(options);
    return;
}

if (string.Equals(options.Probe, "high-contrast-live", StringComparison.OrdinalIgnoreCase))
{
    HighContrastLiveProbe.Run(options);
    return;
}

if (string.Equals(options.Probe, CompactDialogMatrixProbe.ProbeName, StringComparison.OrdinalIgnoreCase))
{
    CompactDialogMatrixProbe.Run(options);
    return;
}

if (string.Equals(options.Probe, "combo-open", StringComparison.OrdinalIgnoreCase))
{
    RunComboOpenProbe(options);
    return;
}

if (string.Equals(options.Probe, "search-select-dismiss", StringComparison.OrdinalIgnoreCase))
{
    RunSearchSelectDismissProbe(options);
    return;
}

if (string.Equals(options.Probe, "search-focus-contract", StringComparison.OrdinalIgnoreCase))
{
    RunSearchFocusContractProbe(options);
    return;
}

if (string.Equals(options.Probe, "emoji-panel", StringComparison.OrdinalIgnoreCase))
{
    RunEmojiPanelProbe(options);
    return;
}

if (string.Equals(options.Probe, "segments-hover", StringComparison.OrdinalIgnoreCase))
{
    RunSegmentsHoverProbe(options);
    return;
}

if (string.Equals(options.Probe, "activity-link-hover", StringComparison.OrdinalIgnoreCase))
{
    RunActivityLinkHoverProbe(options);
    return;
}

if (string.Equals(options.Probe, "pr-timeline-link-hover", StringComparison.OrdinalIgnoreCase))
{
    RunPullRequestTimelineLinkHoverProbe(options);
    return;
}

if (string.Equals(options.Probe, "shell-responsive", StringComparison.OrdinalIgnoreCase))
{
    RunShellResponsiveProbe(options);
    return;
}

if (string.Equals(options.Probe, "shell-nav-clicks", StringComparison.OrdinalIgnoreCase))
{
    RunShellNavClicksProbe(options);
    return;
}

if (string.Equals(options.Probe, "shell-hover-states", StringComparison.OrdinalIgnoreCase))
{
    RunShellHoverStatesProbe(options);
    return;
}

if (string.Equals(options.Probe, "shell-search-states", StringComparison.OrdinalIgnoreCase))
{
    RunShellSearchStatesProbe(options);
    return;
}

if (string.Equals(options.Probe, "shell-repo-click", StringComparison.OrdinalIgnoreCase))
{
    RunShellRepoClickProbe(options);
    return;
}

if (string.Equals(options.Probe, "home-widget-board", StringComparison.OrdinalIgnoreCase))
{
    RunHomeWidgetBoardProbe(options);
    return;
}

if (string.Equals(options.Probe, "home-customize", StringComparison.OrdinalIgnoreCase))
{
    RunHomeCustomizeProbe(options);
    return;
}

if (string.Equals(options.Probe, "home-view-all", StringComparison.OrdinalIgnoreCase))
{
    RunHomeViewAllProbe(options);
    return;
}

if (string.Equals(options.Probe, "command-search", StringComparison.OrdinalIgnoreCase))
{
    RunCommandSearchProbe(options);
    return;
}

if (string.Equals(options.Probe, "repo-search-responsive", StringComparison.OrdinalIgnoreCase))
{
    RunRepositorySearchResponsiveProbe(options);
    return;
}

if (string.Equals(options.Probe, "keyboard-accessibility-matrix", StringComparison.OrdinalIgnoreCase))
{
    RunKeyboardAccessibilityMatrixProbe(options);
    return;
}

if (string.Equals(options.Probe, "keyboard-commit-diff-search", StringComparison.OrdinalIgnoreCase))
{
    RunKeyboardCommitDiffSearchMatrix(options);
    return;
}

if (string.Equals(options.Probe, "my-issues-page", StringComparison.OrdinalIgnoreCase))
{
    RunMyIssuesPageProbe(options);
    return;
}

if (string.Equals(options.Probe, "my-pull-requests-page", StringComparison.OrdinalIgnoreCase))
{
    RunMyPullRequestsPageProbe(options);
    return;
}

if (string.Equals(options.Probe, "my-pull-requests-pseudo-long-labels", StringComparison.OrdinalIgnoreCase))
{
    RunMyPullRequestsPseudoLongLabelsProbe(options);
    return;
}

if (string.Equals(options.Probe, "my-issues-pseudo-long-labels", StringComparison.OrdinalIgnoreCase))
{
    RunMyIssuesPseudoLongLabelsProbe(options);
    return;
}

if (string.Equals(options.Probe, "repo-issues-page", StringComparison.OrdinalIgnoreCase))
{
    RunRepoIssuesPageProbe(options);
    return;
}

if (string.Equals(options.Probe, "issues-responsive-workspace", StringComparison.OrdinalIgnoreCase))
{
    RunIssuesResponsiveWorkspaceProbe(options);
    return;
}

if (string.Equals(options.Probe, "pull-requests-responsive-workspace", StringComparison.OrdinalIgnoreCase))
{
    RunPullRequestsResponsiveWorkspaceProbe(options);
    return;
}

if (string.Equals(options.Probe, "pull-request-reply-identities", StringComparison.OrdinalIgnoreCase))
{
    RunPullRequestReplyIdentityProbe(options);
    return;
}

if (string.Equals(options.Probe, "commits-responsive-workspace", StringComparison.OrdinalIgnoreCase))
{
    RunCommitsResponsiveWorkspaceProbe(options);
    return;
}

if (string.Equals(options.Probe, "repo-code-responsive-workspace", StringComparison.OrdinalIgnoreCase))
{
    RunRepoCodeResponsiveWorkspaceProbe(options);
    return;
}

if (string.Equals(options.Probe, "repo-code-content-surfaces", StringComparison.OrdinalIgnoreCase))
{
    RunRepoCodeContentSurfacesProbe(options);
    return;
}

if (string.Equals(options.Probe, "repo-code-performance", StringComparison.OrdinalIgnoreCase))
{
    RunRepoCodePerformanceProbe(options);
    return;
}

if (string.Equals(options.Probe, "repo-code-high-contrast", StringComparison.OrdinalIgnoreCase))
{
    RunRepoCodeHighContrastProbe(options);
    return;
}

if (string.Equals(options.Probe, "repository-actions", StringComparison.OrdinalIgnoreCase))
{
    RunRepositoryActionsProbe(options);
    return;
}

if (string.Equals(options.Probe, "repository-library", StringComparison.OrdinalIgnoreCase))
{
    RepositoryLibraryProbe.Run(options);
    return;
}

if (string.Equals(options.Probe, "commits-virtualized-diff", StringComparison.OrdinalIgnoreCase))
{
    RunCommitsVirtualizedDiffProbe(options);
    return;
}

if (string.Equals(options.Probe, "commits-performance", StringComparison.OrdinalIgnoreCase))
{
    RunCommitsPerformanceProbe(options);
    return;
}

if (string.Equals(options.Probe, "profile-responsive", StringComparison.OrdinalIgnoreCase))
{
    RunProfileResponsiveProbe(options);
    return;
}

if (string.Equals(options.Probe, "profile-avatar-routing", StringComparison.OrdinalIgnoreCase))
{
    RunProfileAvatarRoutingProbe(options);
    return;
}

if (string.Equals(options.Probe, "profile-avatar-routing-commits", StringComparison.OrdinalIgnoreCase))
{
    RunDirectRepositoryAvatarRouteProbe(
        options,
        "--page=repo-commits",
        "RepoCommitsPageRoot",
        "commit_list_author",
        "commit-list-author");
    return;
}

if (string.Equals(options.Probe, "settings-responsive", StringComparison.OrdinalIgnoreCase))
{
    RunSettingsResponsiveProbe(options);
    return;
}

if (string.Equals(options.Probe, "settings-export-picker", StringComparison.OrdinalIgnoreCase))
{
    RunSettingsExportPickerProbe(options);
    return;
}

if (string.Equals(options.Probe, "settings-pseudo-long", StringComparison.OrdinalIgnoreCase))
{
    RunSettingsPseudoLongLabelsProbe(options);
    return;
}

if (string.Equals(options.Probe, "vnext-pseudo-localization", StringComparison.OrdinalIgnoreCase))
{
    RunVNextPseudoLocalizationProbe(options);
    return;
}

if (string.Equals(options.Probe, "stars-library", StringComparison.OrdinalIgnoreCase))
{
    RunStarsLibraryProbe(options, includeCategoryPersistence: false);
    return;
}

if (string.Equals(options.Probe, "stars-selection-mode", StringComparison.OrdinalIgnoreCase))
{
    RunStarsSelectionModeProbe(options);
    return;
}

if (string.Equals(options.Probe, "stars-categories", StringComparison.OrdinalIgnoreCase))
{
    RunStarsCategoryPersistenceProbe(options);
    return;
}

if (string.Equals(options.Probe, "gists-workspace", StringComparison.OrdinalIgnoreCase))
{
    RunGistsWorkspaceProbe(options);
    return;
}

if (string.Equals(options.Probe, "gists-crud", StringComparison.OrdinalIgnoreCase))
{
    RunGistsWorkspaceProbe(options, skipResponsiveMatrix: true);
    return;
}

if (string.Equals(options.Probe, "notifications-workspace", StringComparison.OrdinalIgnoreCase))
{
    RunNotificationsWorkspaceProbe(options);
    return;
}

if (string.Equals(options.Probe, "markdown-host-lifecycle", StringComparison.OrdinalIgnoreCase))
{
    RunMarkdownHostLifecycleProbe(options);
    return;
}

if (string.Equals(options.Probe, "diagnostics-launch-close", StringComparison.OrdinalIgnoreCase))
{
    RunDiagnosticsLaunchCloseProbe(options);
    return;
}

if (string.Equals(options.Probe, "website-showcase", StringComparison.OrdinalIgnoreCase))
{
    RunWebsiteShowcaseProbe(options);
    return;
}

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
catch (Exception exception)
{
    automationExitCode = 1;
    Console.Error.WriteLine(exception);
}
finally
{
    Environment.ExitCode = automationExitCode;
    AutomationLifecycleLog.Write(
        "probe-completed",
        $"probe={options.Probe ?? "capture"}; automationExitCode={automationExitCode}; status={(automationExitCode == 0 ? "passed" : "failed")}");
}

static void RunWebsiteShowcaseProbe(CaptureOptions options)
{
    const int captureWidth = 3200;
    const int captureHeight = 1800;
    const int minimumLogicalWidth = 1200;
    const int minimumLogicalHeight = 675;
    (string Id, string Page, string? Scenario, string ReadyAutomationId, string SourceState)[] surfaces =
    [
        ("home-workspace", "home", "website-showcase", "DashboardWidgetBoard", "home-expanded"),
        ("pull-request-conversation", "repo-pulls", "pr-shy-header", "RepoPullRequestsDetailTitle", "pull-request-conversation"),
        ("code-editor", "repo-code", "website-showcase", "RepoCodePageRoot", "repository-source-editor"),
        ("csv-table", "repo-code", "website-showcase", "RepoCodePageRoot", "repository-csv-rich"),
        ("commit-diff", "repo-commits", "website-showcase", "RepoCommitsAdaptiveWorkspace", "commit-diff"),
        ("stars-library", "stars", "website-showcase", "StarsPageRoot", "stars-all"),
        ("gists-editor", "gists", "website-showcase", "GistsWorkspace", "gist-editor"),
        ("profile-overview", "profile", "website-showcase", "ProfilePageRoot", "profile-overview")
    ];

    if (options.ShowcaseIds.Count > 0)
    {
        string[] requestedIds = options.ShowcaseIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        surfaces = surfaces
            .Where(surface => requestedIds.Contains(surface.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        AssertProbe(
            surfaces.Length == requestedIds.Length,
            "website-showcase received an unknown --showcase-ids value.");
    }

    string[] themes = options.Themes
        .Select(static theme => theme.Trim().ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    bool isCompleteGallery = surfaces.Length == 8;
    AssertProbe(
        themes.Length > 0 && themes.All(theme => theme is "light" or "dark"),
        "website-showcase themes must be light or dark.");
    AssertProbe(
        !isCompleteGallery ||
            (themes.Length == 2 && themes.Contains("light", StringComparer.Ordinal) && themes.Contains("dark", StringComparer.Ordinal)),
        "A complete website-showcase run requires exactly the light and dark themes so every catalog asset remains paired.");

    var assets = new JsonArray();
    foreach (string theme in themes)
    {
        foreach ((string id, string page, string? scenario, string readyAutomationId, string sourceState) in surfaces)
        {
            JsonObject captured = CaptureWebsiteShowcaseSurface(
                options,
                id,
                page,
                scenario,
                readyAutomationId,
                sourceState,
                theme,
                captureWidth,
                captureHeight,
                minimumLogicalWidth,
                minimumLogicalHeight);
            assets.Add(captured);
        }
    }

    int expectedAssetCount = surfaces.Length * themes.Length;
    AssertProbe(
        assets.Count == expectedAssetCount,
        $"website-showcase captured {assets.Count} of {expectedAssetCount} requested still assets.");
    var manifest = new JsonObject
    {
        ["schemaVersion"] = 2,
        ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        ["captureWidth"] = captureWidth,
        ["captureHeight"] = captureHeight,
        ["minimumLogicalWidth"] = minimumLogicalWidth,
        ["minimumLogicalHeight"] = minimumLogicalHeight,
        ["source"] = "synthetic-public-preview",
        ["networkPolicy"] = "blocked-loopback-proxy",
        ["assets"] = assets
    };
    string manifestPath = Path.Combine(options.OutputDirectory, "media-manifest.json");
    File.WriteAllText(
        manifestPath,
        manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine(
        $"website-showcase: captured {assets.Count} exact {captureWidth}x{captureHeight} stills " +
        $"with at least {minimumLogicalWidth}x{minimumLogicalHeight} logical workspace; manifest={manifestPath}");
}

static JsonObject CaptureWebsiteShowcaseSurface(
    CaptureOptions options,
    string id,
    string page,
    string? scenario,
    string readyAutomationId,
    string sourceState,
    string theme,
    int captureWidth,
    int captureHeight,
    int minimumLogicalWidth,
    int minimumLogicalHeight)
{
    KillExistingApplicationInstances(options.AppPath);
    var arguments = new List<string>
    {
        $"--page={page}",
        $"--theme={theme}",
        "--website-showcase",
        "--network-disabled"
    };
    if (!string.IsNullOrWhiteSpace(scenario))
    {
        arguments.Add($"--scenario={scenario}");
    }
    if (page.StartsWith("repo", StringComparison.OrdinalIgnoreCase))
    {
        arguments.Add($"--repo={options.RepositoryFullName}");
    }

    using var app = LaunchApplication(options.AppPath, arguments.ToArray());
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, $"website-showcase {theme} {id}");
        Rectangle physicalBounds = ResizeWebsiteShowcaseWindow(window, captureWidth, captureHeight);
        uint windowDpi = NativeMethods.GetWindowDpi(GetNativeWindowHandle(window));
        int logicalWidth = (int)Math.Round(physicalBounds.Width * 96d / windowDpi);
        int logicalHeight = (int)Math.Round(physicalBounds.Height * 96d / windowDpi);
        AssertProbe(
            logicalWidth >= minimumLogicalWidth && logicalHeight >= minimumLogicalHeight,
            $"website-showcase requires at least {minimumLogicalWidth}x{minimumLogicalHeight} logical pixels; " +
            $"capture={physicalBounds.Width}x{physicalBounds.Height}, dpi={windowDpi}, logical={logicalWidth}x{logicalHeight}.");
        WaitForElement(
            $"website-showcase {id} root",
            () => FindCurrentVisibleByAutomationId(window, readyAutomationId),
            TimeSpan.FromSeconds(18));
        PrepareWebsiteShowcasePresentation(window, automation);
        PrepareWebsiteShowcaseSurface(window, id, options.OutputDirectory);
        if (page.StartsWith("repo", StringComparison.OrdinalIgnoreCase))
        {
            WaitForWebsiteRepositoryStatistics(window);
        }
        PrepareWebsiteShowcaseTooltips(window, automation);
        Thread.Sleep(900);

        string fileName = $"{id}-{theme}.png";
        string filePath = Path.Combine(options.OutputDirectory, fileName);
        DateTime captureStartedUtc = DateTime.UtcNow;
        TryDeleteFile(filePath);
        CaptureWindow(window, filePath);
        string hash = ValidateWebsiteShowcaseStill(filePath, captureStartedUtc, captureWidth, captureHeight);

        Console.WriteLine(
            $"website-showcase: {theme}/{id} -> {fileName} ({hash[..12]}); " +
            $"dpi={windowDpi}, logical={logicalWidth}x{logicalHeight}");
        return new JsonObject
        {
            ["id"] = id,
            ["theme"] = theme,
            ["file"] = fileName,
            ["sourceState"] = $"synthetic-public-preview/{sourceState}",
            ["width"] = captureWidth,
            ["height"] = captureHeight,
            ["windowDpi"] = windowDpi,
            ["logicalWidth"] = logicalWidth,
            ["logicalHeight"] = logicalHeight,
            ["sha256"] = hash
        };
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void WaitForWebsiteRepositoryStatistics(Window window)
{
    WaitForElement(
        "website-showcase repository watcher count",
        () => FindRepositoryStatisticWithValue(window, "RepoDetailWatchButton", "7"),
        TimeSpan.FromSeconds(12));
    WaitForElement(
        "website-showcase repository star count",
        () => FindRepositoryStatisticWithValue(window, "RepoDetailStarButton", "42"),
        TimeSpan.FromSeconds(12));
}

static AutomationElement? FindRepositoryStatisticWithValue(
    Window window,
    string automationId,
    string expectedValue)
{
    AutomationElement? statistic = FindCurrentVisibleByAutomationId(window, automationId);
    return statistic is not null && statistic.FindAllDescendants()
        .Any(element =>
            IsVisible(element) &&
            string.Equals(element.Name, expectedValue, StringComparison.Ordinal))
        ? statistic
        : null;
}

static void PrepareWebsiteShowcaseSurface(Window window, string id, string outputDirectory)
{
    switch (id)
    {
        case "home-workspace":
        {
            AutomationElement scrollHost = WaitForElement(
                "website-showcase Home scroll host",
                () => FindCurrentVisibleByAutomationId(window, "DashboardMainRailScrollViewer"),
                TimeSpan.FromSeconds(12));
            if (scrollHost.Patterns.Scroll.IsSupported)
            {
                scrollHost.Patterns.Scroll.Pattern.SetScrollPercent(
                    FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll,
                    0);
            }
            WaitForElement(
                "website-showcase expanded Home header",
                () => FindCurrentVisibleByAutomationId(window, "DashboardCustomizeButton"),
                TimeSpan.FromSeconds(8));
            WaitForElement(
                "website-showcase Home overview affordance",
                () => FindCurrentVisibleByAutomationId(window, "DashboardSideRail") ??
                    FindCurrentVisibleByAutomationId(window, "DashboardOverviewDrawerButton"),
                TimeSpan.FromSeconds(12));
            break;
        }
        case "pull-request-conversation":
        {
            EnsureWebsitePullRequestDetailVisible(window);
            AutomationElement conversation = WaitForElement(
                "website-showcase pull request conversation",
                () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsCommentsList"),
                TimeSpan.FromSeconds(18));
            if (conversation.Patterns.Scroll.IsSupported)
            {
                conversation.Patterns.Scroll.Pattern.SetScrollPercent(
                    FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll,
                    10);
            }
            CloseVisiblePane(window, "RepoPullRequestsCloseInspectorPaneButton");
            break;
        }
        case "code-editor":
            ExpandWebsiteRepoCodeFolder(window, "src");
            SelectRepoCodeFixtureFile(window, "src/App.cs", "App.cs, file");
            WaitForElement(
                "website-showcase native code editor",
                () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeEditor"))
                    .FirstOrDefault(element => IsVisible(element) && element.Patterns.Value.IsSupported),
                TimeSpan.FromSeconds(18));
            break;
        case "csv-table":
            SelectRepoCodeFixtureFile(window, "data.csv", "data.csv, file");
            WaitForElement(
                "website-showcase rich CSV table",
                () => window.FindAllDescendants()
                    .FirstOrDefault(element =>
                        IsVisible(element) &&
                        (string.Equals(GetAutomationId(element), "CsvPreviewDataGrid", StringComparison.Ordinal) ||
                         string.Equals(GetAutomationId(element), "CsvPreviewDataTable", StringComparison.Ordinal)) &&
                        element.Patterns.Grid.IsSupported &&
                        element.Patterns.Grid.Pattern.RowCount.Value == 7),
                TimeSpan.FromSeconds(18));
            break;
        case "commit-diff":
        {
            EnsureCommitDetailVisible(window);
            AutomationElement diffSelector = WaitForElement(
                "website-showcase commit Diff section",
                () => FindCurrentVisibleByAutomationId(window, "RepoCommitsSection_Diff") ??
                    FindCurrentVisibleByAutomationId(window, "RepoCommitsShySection_Diff"),
                TimeSpan.FromSeconds(10));
            if (diffSelector.Patterns.SelectionItem.IsSupported && !diffSelector.Patterns.SelectionItem.Pattern.IsSelected.Value)
            {
                diffSelector.Patterns.SelectionItem.Pattern.Select();
            }
            AutomationElement diffRows = WaitForElement(
                "website-showcase commit diff rows",
                () => FindCurrentVisibleByAutomationId(window, "CommitDiffViewerRowsScrollViewer"),
                TimeSpan.FromSeconds(18));
            WaitUntil(
                "website-showcase rendered commit diff text",
                () => FindVisibleDiffTextElements(diffRows).Length >= 2,
                TimeSpan.FromSeconds(18));
            if (diffRows.Patterns.Scroll.IsSupported)
            {
                diffRows.Patterns.Scroll.Pattern.SetScrollPercent(
                    FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll,
                    0);
            }
            CloseVisiblePane(window, "RepoCommitsCloseInspectorPaneButton");
            break;
        }
        case "stars-library":
        {
            AutomationElement list = WaitForElement(
                "website-showcase Stars list",
                () => FindCurrentVisibleByAutomationId(window, "StarsList"),
                TimeSpan.FromSeconds(15));
            WaitForElement(
                "website-showcase first Star",
                () => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                TimeSpan.FromSeconds(12));
            break;
        }
        case "gists-editor":
        {
            if (!IsVisible(FindCurrentVisibleByAutomationId(window, "GistsDetailTitle")))
            {
                AutomationElement? list = FindCurrentVisibleByAutomationId(window, "GistsList");
                if (list is null)
                {
                    AutomationElement libraryButton = WaitForElement(
                        "website-showcase Gists library button",
                        () => FindCurrentVisibleByAutomationId(window, "GistsLeadingPaneButton"),
                        TimeSpan.FromSeconds(10));
                    InvokeOrClick(libraryButton);
                    list = WaitForElement(
                        "website-showcase Gists list drawer",
                        () => FindCurrentVisibleByAutomationId(window, "GistsList"),
                        TimeSpan.FromSeconds(10));
                }

                AutomationElement firstGist = WaitForElement(
                    "website-showcase first Gist",
                    () => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                    TimeSpan.FromSeconds(12));
                InvokeOrClick(firstGist);
                WaitForElement(
                    "website-showcase Gist detail",
                    () => FindCurrentVisibleByAutomationId(window, "GistsDetailTitle"),
                    TimeSpan.FromSeconds(12));
            }

            AutomationElement editButton = WaitForElement(
                "website-showcase Edit Gist button",
                () => FindCurrentVisibleByAutomationId(window, "GistsEdit"),
                TimeSpan.FromSeconds(10));
            InvokeOrClick(editButton);
            AutomationElement editorDialog = WaitForElement(
                "website-showcase Gist editor dialog",
                () => FindCurrentVisibleByAutomationId(window, "GistEditorDialog"),
                TimeSpan.FromSeconds(12));
            AutomationElement contentEditor = WaitForElement(
                "website-showcase Gist content editor",
                () => editorDialog.FindFirstDescendant(cf => cf.ByAutomationId("GistEditorContent")),
                TimeSpan.FromSeconds(10));
            bool contentEditorVisible = WaitUntilAvailable(
                () => FindCurrentVisibleByAutomationId(window, "GistEditorDialog") is { } liveDialog &&
                    liveDialog.FindFirstDescendant(cf => cf.ByAutomationId("GistEditorContent")) is { } liveEditor &&
                    IsVisible(liveEditor) &&
                    liveEditor.Patterns.Value.IsSupported &&
                    (liveEditor.Patterns.Value.Pattern.Value.Value ?? string.Empty).Contains("# Release checklist", StringComparison.Ordinal) &&
                    Rectangle.Intersect(GetClippedAutomationBounds(liveEditor), window.BoundingRectangle) is { Width: > 240, Height: > 80 },
                TimeSpan.FromSeconds(6));
            if (!contentEditorVisible)
            {
                CaptureWindowWithPopups(window, Path.Combine(outputDirectory, "gists-editor-scroll-debug.png"));
                AutomationElement liveEditor = FindCurrentVisibleByAutomationId(window, "GistEditorDialog")?
                    .FindFirstDescendant(cf => cf.ByAutomationId("GistEditorContent")) ?? contentEditor;
                throw new InvalidOperationException(
                    "website-showcase could not expose a useful Gist content editor viewport. " +
                    $"Editor={liveEditor.BoundingRectangle}; clipped={GetClippedAutomationBounds(liveEditor)}; " +
                    $"window={window.BoundingRectangle}; ancestors=[{DescribeScrollAncestors(liveEditor)}].");
            }
            break;
        }
        case "profile-overview":
        {
            AutomationElement overview = WaitForElement(
                "website-showcase Profile overview",
                () => FindCurrentVisibleByAutomationId(window, "ProfileOverviewScrollViewer"),
                TimeSpan.FromSeconds(18));
            WaitForElement(
                "website-showcase Profile identity",
                () => FindCurrentVisibleByAutomationId(window, "ProfileDisplayName") ??
                    FindCurrentVisibleByAutomationId(window, "ProfileCompactIdentityDetailsButton"),
                TimeSpan.FromSeconds(12));
            WaitForElement(
                "website-showcase contribution graph",
                () => FindCurrentVisibleByAutomationId(window, "ProfileContributionGraph"),
                TimeSpan.FromSeconds(18));
            if (overview.Patterns.Scroll.IsSupported &&
                overview.Patterns.Scroll.Pattern.VerticallyScrollable.ValueOrDefault)
            {
                overview.Patterns.Scroll.Pattern.SetScrollPercent(
                    FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll,
                    0);
            }
            break;
        }
        default:
            throw new InvalidOperationException($"Unknown website showcase surface '{id}'.");
    }
}

static void PrepareWebsiteShowcasePresentation(Window window, UIA3Automation automation)
{
    AutomationElement[] dismissButtons = window
        .FindAllDescendants(cf => cf.ByAutomationId("CloseButton").And(cf.ByControlType(ControlType.Button)))
        .Where(IsVisible)
        .ToArray();
    foreach (AutomationElement button in dismissButtons)
    {
        InvokeOrClick(button);
    }

    if (dismissButtons.Length > 0)
    {
        WaitUntil(
            "website-showcase transient status closes",
            () => window
                .FindAllDescendants(cf => cf.ByAutomationId("CloseButton").And(cf.ByControlType(ControlType.Button)))
                .All(button => !IsVisible(button)),
            TimeSpan.FromSeconds(5));
    }

    PrepareWebsiteShowcaseTooltips(window, automation);
}

static void PrepareWebsiteShowcaseTooltips(Window window, UIA3Automation automation)
{
    NativeMethods.MoveCursorPhysical(new Point(1, 1));
    Thread.Sleep(80);
    NativeMethods.MoveCursorPhysical(new Point(2, 2));
    SendMouseInput(
        new Point(2, 2),
        MouseEventFlags.MOUSEEVENTF_MOVE | MouseEventFlags.MOUSEEVENTF_MOVE_NOCOALESCE);
    Thread.Sleep(1200);
    Rectangle windowBounds = window.BoundingRectangle;
    int appProcessId = window.Properties.ProcessId.ValueOrDefault;
    bool hasVisibleTooltip = automation.GetDesktop()
        .FindAllDescendants(cf => cf.ByControlType(ControlType.ToolTip))
        .Any(element => IsVisibleTooltipOverWindow(element, windowBounds, appProcessId));
    if (hasVisibleTooltip)
    {
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(120);
    }

    bool tooltipsClosed = WaitUntilAvailable(
        () => automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByControlType(ControlType.ToolTip))
            .All(element => !IsVisibleTooltipOverWindow(element, windowBounds, appProcessId)),
        TimeSpan.FromSeconds(3));
    if (!tooltipsClosed)
    {
        string tooltipDetails = string.Join(
            " | ",
            automation.GetDesktop()
                .FindAllDescendants(cf => cf.ByControlType(ControlType.ToolTip))
                .Where(element => IsVisibleTooltipOverWindow(element, windowBounds, appProcessId))
                .Select(element =>
                    $"name='{GetElementName(element)}',pid={element.Properties.ProcessId.ValueOrDefault}," +
                    $"hwnd={element.Properties.NativeWindowHandle.ValueOrDefault}," +
                    $"windowPattern={element.Patterns.Window.IsSupported},bounds={element.BoundingRectangle}"));
        throw new InvalidOperationException(
            $"website-showcase could not dismiss a tooltip overlapping the app window; " +
            $"cursor={NativeMethods.GetCursorPositionPhysical()}: {tooltipDetails}");
    }
    Thread.Sleep(250);
}

static bool IsVisibleTooltipOverWindow(AutomationElement element, Rectangle windowBounds, int appProcessId)
{
    try
    {
        if (element.Properties.ProcessId.ValueOrDefault != appProcessId)
        {
            return false;
        }

        Rectangle intersection = Rectangle.Intersect(element.BoundingRectangle, windowBounds);
        return IsVisible(element) && intersection.Width > 1 && intersection.Height > 1;
    }
    catch (COMException)
    {
        return false;
    }
}

static void EnsureWebsitePullRequestDetailVisible(Window window)
{
    if (IsVisible(FindCurrentVisibleByAutomationId(window, "RepoPullRequestsDetailTitle")))
    {
        return;
    }

    AutomationElement list = WaitForElement(
        "website-showcase pull request list",
        () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsList"),
        TimeSpan.FromSeconds(15));
    AutomationElement firstPullRequest = WaitForElement(
        "website-showcase first pull request",
        () => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
        TimeSpan.FromSeconds(12));
    InvokeOrClick(firstPullRequest);
    WaitForElement(
        "website-showcase pull request detail",
        () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsDetailTitle"),
        TimeSpan.FromSeconds(15));
}

static void ExpandWebsiteRepoCodeFolder(Window window, string path)
{
    AutomationElement? folder = window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
        .FirstOrDefault(element =>
            IsVisible(element) &&
            string.Equals(element.Properties.ItemStatus.ValueOrDefault, $"path:{path}", StringComparison.Ordinal));
    if (folder is null)
    {
        AutomationElement opener = WaitForElement(
            "website-showcase repository file-tree opener",
            () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeOpenFileTreeButton"))
                .FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(8));
        OpenRepoCodeFileTreeDrawer(window, opener);
        AutomationElement drawer = WaitForElement(
            "website-showcase repository file-tree drawer",
            () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer"))
                .FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(8));
        folder = WaitForElement(
            $"website-showcase repository folder {path}",
            () => drawer.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                .FirstOrDefault(element =>
                    IsVisible(element) &&
                    string.Equals(element.Properties.ItemStatus.ValueOrDefault, $"path:{path}", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(12));
    }

    if (folder.Patterns.ExpandCollapse.IsSupported &&
        folder.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value != ExpandCollapseState.Expanded)
    {
        folder.Patterns.ExpandCollapse.Pattern.Expand();
        Thread.Sleep(450);
    }
}

static void CloseVisiblePane(Window window, string closeAutomationId)
{
    AutomationElement? close = FindCurrentVisibleByAutomationId(window, closeAutomationId);
    if (IsVisible(close))
    {
        InvokeOrClick(close!);
        Thread.Sleep(350);
    }
}

static Rectangle ResizeWebsiteShowcaseWindow(Window window, int physicalWidth, int physicalHeight)
{
    Rectangle workArea = NativeMethods.GetWorkArea();
    AssertProbe(
        workArea.Width >= physicalWidth && workArea.Height >= physicalHeight,
        $"website-showcase requires a physical work area of at least {physicalWidth}x{physicalHeight}; current work area is {workArea.Width}x{workArea.Height}.");

    IntPtr handle = GetNativeWindowHandle(window);
    _ = ResizeWindow(window, physicalWidth, physicalHeight, reactivate: false);
    for (int attempt = 0; attempt < 6; attempt++)
    {
        Rectangle physical = NativeMethods.GetPhysicalWindowBounds(handle);
        if (physical.Width == physicalWidth && physical.Height == physicalHeight)
        {
            TryActivateWindow(window);
            Thread.Sleep(650);
            return physical;
        }

        Rectangle outer = NativeMethods.GetWindowBounds(handle);
        int nextWidth = outer.Width + (physicalWidth - physical.Width);
        int nextHeight = outer.Height + (physicalHeight - physical.Height);
        NativeMethods.ResizeWindow(handle, nextWidth, nextHeight);
        Thread.Sleep(260);
    }

    Rectangle actual = NativeMethods.GetPhysicalWindowBounds(handle);
    throw new InvalidOperationException(
        $"website-showcase refused a constrained capture. Requested physical DWM bounds {physicalWidth}x{physicalHeight}; actual={actual.Width}x{actual.Height}.");
}

static string ValidateWebsiteShowcaseStill(
    string filePath,
    DateTime captureStartedUtc,
    int expectedWidth,
    int expectedHeight)
{
    FileInfo file = new(filePath);
    AssertProbe(file.Exists && file.Length > 10_000, $"website-showcase produced an empty still '{filePath}'.");
    AssertProbe(
        file.LastWriteTimeUtc >= captureStartedUtc.AddSeconds(-1),
        $"website-showcase refused stale output '{filePath}'.");
    using var image = new Bitmap(filePath);
    AssertProbe(
        image.Width == expectedWidth && image.Height == expectedHeight,
        $"website-showcase expected {expectedWidth}x{expectedHeight} pixels for '{file.Name}', found {image.Width}x{image.Height}.");
    var sampledColors = new HashSet<int>();
    int horizontalStep = Math.Max(1, image.Width / 80);
    int verticalStep = Math.Max(1, image.Height / 50);
    for (int y = verticalStep / 2; y < image.Height; y += verticalStep)
    {
        for (int x = horizontalStep / 2; x < image.Width; x += horizontalStep)
        {
            sampledColors.Add(image.GetPixel(x, y).ToArgb());
        }
    }
    AssertProbe(
        sampledColors.Count >= 12,
        $"website-showcase refused a visually blank still '{filePath}' ({sampledColors.Count} sampled colors).");
    return ComputeFileSha256(filePath);
}

static void RunDiagnosticsLaunchCloseProbe(CaptureOptions options)
{
    const int expectedBurstCount = 64;
    const string burstName = "diagnostics.close.probe.burst";
    const string markerName = "diagnostics.close.probe.marker";
    KillExistingApplicationInstances(options.AppPath);
    ResetRuntimeEvidence();
    using var app = LaunchApplication(
        options.AppPath,
        "--page=login",
        "--theme=dark",
        "--scenario=diagnostics-close-probe");
    IntPtr processExitHandle = NativeMethods.OpenProcessExitHandle(app.ProcessId);
    using var automation = new UIA3Automation();
    bool closedCleanly = false;
    try
    {
        Window window = GetReadyWindow(app, automation, "diagnostics launch/close");
        AssertProbe(
            window.FindFirstDescendant(cf => cf.ByAutomationId("JitHubMainWindowRoot")) is not null,
            "The launched app did not expose the JitHub root element.");
        WaitForElement(
            "login root",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("LoginRoot")),
            TimeSpan.FromSeconds(10));

        window.Close();
        bool exited = Retry.WhileFalse(
            () => app.HasExited,
            timeout: TimeSpan.FromSeconds(12),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        AssertProbe(exited, "The app did not exit after its main window closed.");
        uint nativeExitCode = NativeMethods.GetProcessExitCode(processExitHandle);
        AssertProbe(nativeExitCode == 0, $"The app exited with native process code {nativeExitCode}.");

        string diagnosticsPath = Path.Combine(
            GetAutomationDataRoot(),
            "Local",
            "Diagnostics",
            "v1",
            "diagnostics.ndjson");
        AssertProbe(File.Exists(diagnosticsPath), "The diagnostics file was not persisted during launch/close.");
        string[] diagnostics = File.ReadAllLines(diagnosticsPath);
        DiagnosticProbeEvent[] parsed = diagnostics
            .Select((line, index) => ParseDiagnosticProbeEvent(line, index))
            .Where(static item => item is not null)
            .Select(static item => item!)
            .ToArray();
        int appStartedIndex = Array.FindIndex(
            parsed,
            static item => string.Equals(item.Name, "app.started", StringComparison.Ordinal));
        AssertProbe(appStartedIndex >= 0, "The accepted app.started diagnostic was not drained before process exit.");

        DiagnosticProbeEvent[] burst = parsed
            .Where(item => string.Equals(item.Name, burstName, StringComparison.Ordinal))
            .ToArray();
        AssertProbe(
            burst.Length == expectedBurstCount,
            $"Expected {expectedBurstCount} close-adjacent diagnostics but found {burst.Length}.");
        for (int sequence = 0; sequence < expectedBurstCount; sequence++)
        {
            AssertProbe(
                burst[sequence].Properties.TryGetValue("sequence", out string? value) &&
                int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int actual) &&
                actual == sequence,
                $"Close-adjacent diagnostic sequence {sequence} was missing or out of order.");
            if (sequence > 0)
            {
                AssertProbe(
                    burst[sequence].LineIndex == burst[sequence - 1].LineIndex + 1,
                    "Close-adjacent burst diagnostics were not persisted contiguously in enqueue order.");
            }
        }

        DiagnosticProbeEvent marker = parsed.SingleOrDefault(
                item => string.Equals(item.Name, markerName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The terminal close-adjacent diagnostics marker was not persisted.");
        AssertProbe(
            marker.LineIndex == burst[^1].LineIndex + 1,
            "The terminal diagnostics marker was not persisted immediately after the ordered burst.");
        AssertProbe(
            marker.Properties.TryGetValue("accepted", out string? accepted) &&
            string.Equals(accepted, expectedBurstCount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "The close path did not accept the complete diagnostics burst.");
        AssertProbe(
            parsed[appStartedIndex].LineIndex < burst[0].LineIndex,
            "The close-adjacent burst was persisted before app.started.");
        AssertProbe(
            marker.LineIndex == diagnostics.Length - 1,
            "The terminal close marker was not the final persisted diagnostic before exit.");

        string evidencePath = Path.Combine(options.OutputDirectory, "diagnostics-launch-close.ndjson");
        File.Copy(diagnosticsPath, evidencePath, overwrite: true);
        string manifestPath = Path.Combine(options.OutputDirectory, "diagnostics-launch-close-manifest.json");
        byte[] evidenceBytes = File.ReadAllBytes(evidencePath);
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new
                {
                    probe = "diagnostics-launch-close",
                    passed = true,
                    processId = app.ProcessId,
                    nativeExitCode,
                    appStartedLine = parsed[appStartedIndex].LineIndex,
                    burstCount = burst.Length,
                    burstFirstLine = burst[0].LineIndex,
                    burstLastLine = burst[^1].LineIndex,
                    markerLine = marker.LineIndex,
                    markerWasLast = marker.LineIndex == diagnostics.Length - 1,
                    diagnosticsSha256 = Convert.ToHexString(SHA256.HashData(evidenceBytes)),
                    evidenceFile = Path.GetFileName(evidencePath),
                    capturedAtUtc = DateTimeOffset.UtcNow
                },
                new JsonSerializerOptions { WriteIndented = true }));
        AssertNoRuntimeFailureLogs("diagnostics launch/close");
        closedCleanly = true;
        Console.WriteLine(
            $"diagnostics launch/close: ordered close burst, drain, persistence, exit, and logs verified; manifest={manifestPath}");
    }
    finally
    {
        NativeMethods.CloseProcessExitHandle(processExitHandle);
        if (!closedCleanly && !app.HasExited)
        {
            try { app.Kill(); } catch { }
        }
        KillExistingApplicationInstances(options.AppPath);
    }
}

static DiagnosticProbeEvent? ParseDiagnosticProbeEvent(string line, int lineIndex)
{
    try
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        string? name = null;
        Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name.Equals("name", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                name = property.Value.GetString();
            }
            else if (property.Name.Equals("properties", StringComparison.OrdinalIgnoreCase) &&
                     property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty value in property.Value.EnumerateObject())
                {
                    properties[value.Name] = value.Value.ValueKind == JsonValueKind.String
                        ? value.Value.GetString() ?? string.Empty
                        : value.Value.ToString();
                }
            }
        }

        return string.IsNullOrWhiteSpace(name)
            ? null
            : new DiagnosticProbeEvent(lineIndex, name, properties);
    }
    catch (JsonException)
    {
        return null;
    }
}

static void ResetRuntimeEvidence()
{
    string localRoot = Path.Combine(GetAutomationDataRoot(), "Local");
    string diagnosticsPath = Path.Combine(localRoot, "Diagnostics", "v1", "diagnostics.ndjson");
    if (File.Exists(diagnosticsPath))
    {
        File.Delete(diagnosticsPath);
    }

    string logDirectory = Path.Combine(localRoot, "logs");
    if (!Directory.Exists(logDirectory))
    {
        return;
    }

    foreach (string path in Directory.EnumerateFiles(logDirectory, "*.log"))
    {
        File.Delete(path);
    }
}

static void AssertNoRuntimeFailureLogs(string context)
{
    string logDirectory = Path.Combine(GetAutomationDataRoot(), "Local", "logs");
    if (!Directory.Exists(logDirectory))
    {
        return;
    }

    string[] failureLogs = Directory.EnumerateFiles(logDirectory, "*.log")
        .Where(path =>
        {
            string name = Path.GetFileName(path);
            return new FileInfo(path).Length > 0 &&
                 (name.EndsWith("-unhandled.log", StringComparison.OrdinalIgnoreCase) ||
                  name.Equals("activation-error.log", StringComparison.OrdinalIgnoreCase) ||
                  name.Equals("diagnostics-shutdown.log", StringComparison.OrdinalIgnoreCase) ||
                  name.Equals("diagnostics-close-probe.log", StringComparison.OrdinalIgnoreCase));
        })
        .ToArray();
    if (failureLogs.Length == 0)
    {
        return;
    }

    string details = string.Join(
        Environment.NewLine,
        failureLogs.Select(path => $"{Path.GetFileName(path)}:{Environment.NewLine}{File.ReadAllText(path)}"));
    throw new InvalidOperationException($"{context}: runtime failure logs were written.{Environment.NewLine}{details}");
}

static void RunMarkdownHostLifecycleProbe(CaptureOptions options)
{
    MarkdownLifecycleTarget[] targets =
    [
        new("issue-body", "repo-issues", "MarkdownHost_Conversation_RepoIssuesBody", false),
        new("issue-comment", "repo-issues", "MarkdownHost_Comment_IssueComment_301", false,
            RealizationContainerAutomationId: "RepoIssuesConversationScrollViewer"),
        new("issue-comment-form", "repo-issues", "MarkdownHost_EditorPreview_RepoIssuesCommentBox_Preview", false,
            LauncherControlAutomationId: "RepoIssuesOpenCommentButton"),
        new("pull-request-body", "repo-pulls", "MarkdownHost_Conversation_RepoPullRequestsBody", false,
            RealizationContainerAutomationId: "RepoPullRequestsCommentsList", RealizationStartsAtTop: true),
        new("pull-request-comment", "repo-pulls", "MarkdownHost_Comment_IssueComment_1000", false,
            RealizationContainerAutomationId: "RepoPullRequestsCommentsList"),
        new("pull-request-review", "repo-pulls", "MarkdownHost_Comment_PullRequestReview_1", false,
            SectionControlAutomationId: "RepoPullRequestsSection_Reviews",
            RealizationContainerAutomationId: "RepoPullRequestsReviewsList",
            CompactSectionPickerAutomationId: "RepoPullRequestsSectionComboBox",
            CompactSectionControlAutomationId: "RepoPullRequestsCompactSection_Reviews"),
        new("pull-request-review-comment", "repo-pulls", "MarkdownHost_Comment_PullRequestReviewThread_200", false,
            SectionControlAutomationId: "RepoPullRequestsSection_Reviews",
            RealizationContainerAutomationId: "RepoPullRequestsReviewsList",
            CompactSectionPickerAutomationId: "RepoPullRequestsSectionComboBox",
            CompactSectionControlAutomationId: "RepoPullRequestsCompactSection_Reviews"),
        new("pull-request-review-reply-form", "repo-pulls", "MarkdownHost_EditorPreview_PullRequestReviewThread_200_ReplyForm_Preview", false,
            SectionControlAutomationId: "RepoPullRequestsSection_Reviews",
            RealizationContainerAutomationId: "RepoPullRequestsReviewsList",
            CompactSectionPickerAutomationId: "RepoPullRequestsSectionComboBox",
            CompactSectionControlAutomationId: "RepoPullRequestsCompactSection_Reviews"),
        new("pull-request-comment-form", "repo-pulls", "MarkdownHost_EditorPreview_RepoPullRequestsCompactCommentBox_Preview", false,
            LauncherControlAutomationId: "RepoPullRequestsOpenCompactCommentButton"),
        new("commit-body", "repo-commits", "MarkdownHost_Conversation_RepoCommitsBody", false,
            SectionControlAutomationId: "RepoCommitsSection_Comments"),
        new("commit-comment", "repo-commits", "MarkdownHost_Comment_CommitComment_1", false,
            SectionControlAutomationId: "RepoCommitsSection_Comments"),
        new("commit-comment-form", "repo-commits", "MarkdownHost_EditorPreview_RepoCommitsCommentBox_Preview", false,
            SectionControlAutomationId: "RepoCommitsSection_Comments"),
        new("my-issues-body", "my-issues", "MarkdownHost_Conversation_MyIssuesBody", false,
            RealizationContainerAutomationId: "MyIssuesCommentsList", RealizationStartsAtTop: true),
        new("my-issues-comment", "my-issues", "MarkdownHost_Comment_IssueComment_11", false,
            RealizationContainerAutomationId: "MyIssuesCommentsList"),
        new("my-pull-requests-body", "my-pull-requests", "MarkdownHost_Conversation_MyPullRequestsBody", false,
            RealizationContainerAutomationId: "MyPullRequestsCommentsList", RealizationStartsAtTop: true),
        new("my-pull-requests-comment", "my-pull-requests", "MarkdownHost_Comment_IssueComment_100", false,
            RealizationContainerAutomationId: "MyPullRequestsCommentsList"),
        new("my-pull-requests-review", "my-pull-requests", "MarkdownHost_Comment_MyPullRequestsReview_review_1", false,
            SectionControlAutomationId: "MyPullRequestsSection_Reviews",
            RealizationContainerAutomationId: "MyPullRequestsReviewsList"),
        new("my-pull-requests-review-comment", "my-pull-requests", "MarkdownHost_Comment_MyPullRequestsReviewComment_ReviewComment_200", false,
            SectionControlAutomationId: "MyPullRequestsSection_Reviews",
            RealizationContainerAutomationId: "MyPullRequestsReviewsList"),
        new("repository-readme", "repo-code", "MarkdownHost_RepositoryReadme_RepoCodeReadme", false),
        new("profile-readme", "profile", "MarkdownHost_ProfileReadme_ProfileReadme", false,
            SectionControlAutomationId: "ProfileModeReadmeItem"),
    ];

    MarkdownLifecycleViewport[] viewports =
    [
        new("wide", 1366, 900),
        new("snapped", 760, 650),
        new("compact", 640, 600),
    ];
    Rectangle workArea = NativeMethods.GetWorkArea();
    if (workArea.Width > 0 && workArea.Height > 0)
    {
        viewports = viewports
            .Select(viewport => new MarkdownLifecycleViewport(
                viewport.Name,
                Math.Min(viewport.Width, workArea.Width),
                Math.Min(viewport.Height, workArea.Height)))
            .ToArray();
        foreach (MarkdownLifecycleViewport viewport in viewports.Where(viewport =>
                     string.Equals(viewport.Name, "wide", StringComparison.OrdinalIgnoreCase) &&
                     (viewport.Width < 1366 || viewport.Height < 900)))
        {
            Console.WriteLine(
                $"Markdown lifecycle viewport '{viewport.Name}' is constrained to " +
                $"{viewport.Width}x{viewport.Height} by work area {workArea.Width}x{workArea.Height}.");
        }
    }

    double[] textScales = [1, 1.5, 2];
    if (string.Equals(
            Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_MARKDOWN_SMOKE_CASE"),
            "1",
            StringComparison.Ordinal))
    {
        viewports = [viewports[0]];
        textScales = [textScales[0]];
    }
    if (int.TryParse(
            Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_MARKDOWN_TEXT_SCALE_PERCENT"),
            out int requestedTextScalePercent) &&
        requestedTextScalePercent is >= 100 and <= 300)
    {
        textScales = [requestedTextScalePercent / 100d];
    }
    string? requestedViewport = Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_MARKDOWN_VIEWPORT");
    if (!string.IsNullOrWhiteSpace(requestedViewport))
    {
        viewports = viewports
            .Where(viewport => string.Equals(viewport.Name, requestedViewport, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (viewports.Length == 0)
        {
            throw new InvalidOperationException($"Unknown Markdown lifecycle viewport '{requestedViewport}'.");
        }
    }

    string configuration = options.Configuration ?? InferConfiguration(options.AppPath);
    string appAssemblyPath = Path.ChangeExtension(options.AppPath, ".dll");
    if (!File.Exists(appAssemblyPath))
    {
        throw new FileNotFoundException(
            "The managed JitHub assembly required for lifecycle source evidence was not found.",
            appAssemblyPath);
    }
    string automationAssemblyPath = typeof(CaptureOptions).Assembly.Location;
    string manifestPath = Path.Combine(options.OutputDirectory, "markdown-lifecycle-manifest.json");
    string appSha256 = ComputeFileSha256(options.AppPath);
    string appAssemblySha256 = ComputeFileSha256(appAssemblyPath);
    string automationAssemblySha256 = ComputeFileSha256(automationAssemblyPath);
    string? requestedTarget = Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_MARKDOWN_TARGET");
    MarkdownLifecycleTarget[] selectedTargets = string.IsNullOrWhiteSpace(requestedTarget)
        ? targets
        : targets.Where(target => string.Equals(target.Name, requestedTarget, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (!string.IsNullOrWhiteSpace(requestedTarget) && selectedTargets.Length == 0)
    {
        throw new InvalidOperationException($"Unknown Markdown lifecycle target '{requestedTarget}'.");
    }

    string runScope = string.IsNullOrWhiteSpace(requestedTarget) ? "full-matrix" : "target";
    bool requiresSupplementalCases = string.Equals(runScope, "full-matrix", StringComparison.Ordinal);
    int expectedCaseCount = selectedTargets.Sum(target =>
            textScales.Sum(textScale => viewports.Count(viewport => target.AppliesTo(viewport, textScale)))) *
        options.Themes.Count;
    bool resume = string.Equals(
        Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_MARKDOWN_RESUME"),
        "1",
        StringComparison.Ordinal);
    int maxCases = int.TryParse(
        Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_MARKDOWN_MAX_CASES"),
        out int parsedMaxCases) && parsedMaxCases > 0
            ? parsedMaxCases
            : int.MaxValue;

    MarkdownLifecycleManifest manifest = resume && File.Exists(manifestPath)
        ? LoadMarkdownLifecycleManifest(manifestPath)
        : new MarkdownLifecycleManifest
        {
            Version = 5,
            RunScope = runScope,
            RequestedTarget = requestedTarget,
            RequiresSupplementalCases = requiresSupplementalCases,
            Configuration = configuration,
            AppPath = options.AppPath,
            AppSha256 = appSha256,
            AppLastWriteUtc = File.GetLastWriteTimeUtc(options.AppPath),
            AppAssemblyPath = appAssemblyPath,
            AppAssemblySha256 = appAssemblySha256,
            AppAssemblyLastWriteUtc = File.GetLastWriteTimeUtc(appAssemblyPath),
            AutomationAssemblyPath = automationAssemblyPath,
            AutomationAssemblySha256 = automationAssemblySha256,
            AutomationAssemblyLastWriteUtc = File.GetLastWriteTimeUtc(automationAssemblyPath),
            StartedAtUtc = DateTimeOffset.UtcNow,
            ExpectedHostCount = selectedTargets.Length,
            ExpectedCaseCount = expectedCaseCount,
            Hosts = selectedTargets.Select(target => target.Name).ToArray(),
            Themes = options.Themes.ToArray(),
            TextScalePercents = textScales.Select(scale => (int)Math.Round(scale * 100)).ToArray(),
            Viewports = viewports.Select(viewport => $"{viewport.Name}:{viewport.Width}x{viewport.Height}").ToArray(),
        };
    ValidateResumedMarkdownLifecycleManifest(
        manifest,
        configuration,
        appSha256,
        appAssemblySha256,
        automationAssemblySha256,
        runScope,
        requestedTarget,
        requiresSupplementalCases,
        selectedTargets.Select(target => target.Name).ToArray(),
        expectedCaseCount,
        options.Themes,
        textScales,
        viewports);
    manifest.Completed = false;
    manifest.CompletedAtUtc = null;
    manifest.Failures = [];
    PersistMarkdownLifecycleManifest(manifestPath, manifest);

    List<string> failures = [];
    int executedCases = 0;
    string? requestedSupplemental =
        Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_MARKDOWN_SUPPLEMENTAL");
    IEnumerable<(string Theme, MarkdownLifecycleTarget Target, double TextScale, MarkdownLifecycleViewport Viewport)> pendingCases =
        from theme in options.Themes
        from target in selectedTargets
        from textScale in textScales
        from viewport in viewports
        where target.AppliesTo(viewport, textScale)
        let caseId = $"{configuration}-{theme}-{target.Name}-scale-{textScale * 100:0}-{viewport.Name}".ToLowerInvariant()
        where !manifest.Cases.Any(existing =>
            string.Equals(existing.CaseId, caseId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Status, "passed", StringComparison.Ordinal) &&
            existing.CleanClose)
        select (theme, target, textScale, viewport);
    if (!string.IsNullOrWhiteSpace(requestedSupplemental))
    {
        pendingCases = [];
    }

    foreach ((string theme, MarkdownLifecycleTarget target, double textScale, MarkdownLifecycleViewport viewport) in
        pendingCases.Take(maxCases))
    {
        executedCases++;
        string caseId =
            $"{configuration}-{theme}-{target.Name}-scale-{textScale * 100:0}-{viewport.Name}".ToLowerInvariant();
        manifest.Cases.RemoveAll(existing =>
            string.Equals(existing.CaseId, caseId, StringComparison.OrdinalIgnoreCase));
        MarkdownLifecycleRunPaths runPaths = CreateMarkdownLifecycleRunPaths(options.OutputDirectory, caseId);
        MarkdownLifecycleApplication? lifecycle = null;
        MarkdownLifecycleCaseResult? result = null;
        int? exitCode = null;
        string? caseError = null;
        try
        {
            ResetMarkdownLifecycleExceptionLogs();
            WriteMarkdownLifecycleRuntimeSettings(runPaths.RuntimeSettingsPath, textScale, revision: 1);
            lifecycle = LaunchMarkdownLifecycleApplication(
                options,
                target,
                theme,
                runPaths,
                forceResourceMapAbsent: false);
            using var automation = new UIA3Automation();
            Window window = GetReadyWindow(
                lifecycle.Application,
                automation,
                $"markdown lifecycle {caseId}");
            ResizeWindow(window, viewport.Width, viewport.Height, reactivate: false);
            try
            {
                PrepareRealMarkdownHost(window, target, caseId);
            }
            catch
            {
                CaptureMarkdownLifecycleFailureState(
                    window,
                    options.OutputDirectory,
                    caseId,
                    "host preparation failed");
                throw;
            }
            try
            {
                WaitForMarkdownLifecycleSignal(
                    runPaths.HostReadyPath,
                    target.HostAutomationId,
                    target.PrefixMatch,
                    TimeSpan.FromSeconds(30));
            }
            catch
            {
                CaptureMarkdownLifecycleFailureState(
                    window,
                    options.OutputDirectory,
                    caseId,
                    "host readiness failed");
                throw;
            }

            MarkdownRelayoutMetrics relayout = RunRepeatedMarkdownRelayout(
                ref window,
                lifecycle.Application,
                automation,
                lifecycle.Process,
                target,
                viewport,
                caseId,
                runPaths.RuntimeSettingsPath,
                textScale);

            result = ExerciseMarkdownLifecycleState(
                window,
                target,
                theme,
                configuration,
                textScale,
                viewport,
                caseId,
                options.OutputDirectory,
                runPaths.LinkEvidencePath);
            RecordMarkdownRelayoutMetrics(result, relayout);
            AssertAndRecordMarkdownMemoryBudget(lifecycle.Process, result, caseId);
            AssertMarkdownLifecycleCloseState(window, target, caseId);

            window.Close();
            bool exited = lifecycle.Process.WaitForExit(12_000);
            exitCode = exited ? checked((int)lifecycle.GetExitCode()) : null;
            if (!exited)
            {
                throw new InvalidOperationException(
                    $"{caseId}: app did not exit after closing an active Markdown host.");
            }
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"{caseId}: app exited with process code {exitCode}.");
            }

            AssertNoMarkdownLifecycleExceptionLogs(caseId);
            Console.WriteLine($"markdown lifecycle matrix: {caseId} closed cleanly");
        }
        catch (Exception exception)
        {
            caseError = exception.ToString();
            failures.Add($"{caseId}: {exception.Message}");
        }
        finally
        {
            if (lifecycle is not null && !lifecycle.Process.HasExited)
            {
                try { lifecycle.Process.Kill(entireProcessTree: true); } catch { }
            }
            try
            {
                lifecycle?.Dispose();
            }
            catch (Exception cleanupException)
            {
                Exception combined = caseError is null
                    ? cleanupException
                    : new AggregateException(
                        "Markdown lifecycle execution and cleanup both failed.",
                        new InvalidOperationException(caseError),
                        cleanupException);
                caseError = combined.ToString();
                failures.Add($"{caseId}: cleanup failed: {cleanupException.Message}");
            }

            int unhandledLogCount = CountMarkdownLifecycleExceptionLogs();
            if (unhandledLogCount > 0)
            {
                CopyMarkdownLifecycleExceptionLogs(options.OutputDirectory, caseId);
            }
            bool cleanClose = caseError is null && exitCode == 0 && unhandledLogCount == 0;
            result ??= MarkdownLifecycleCaseResult.Failed(
                configuration,
                target.Name,
                theme,
                textScale,
                viewport,
                caseId,
                caseError ?? "Lifecycle case did not produce a result.");
            result.CleanClose = cleanClose;
            result.HostUnloadOnClose = cleanClose;
            result.ExitCode = exitCode;
            result.UnhandledLogCount = unhandledLogCount;
            if (!cleanClose)
            {
                result.Status = "failed";
                result.Error ??= caseError ?? "Lifecycle close gate failed.";
            }

            manifest.Cases.Add(result);
            PersistMarkdownLifecycleManifest(manifestPath, manifest);
        }
    }

    bool matrixCasesComplete =
        manifest.Cases.Count == manifest.ExpectedCaseCount &&
        manifest.Cases.Select(result => result.CaseId).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
            manifest.ExpectedCaseCount &&
        manifest.Cases.All(result => string.Equals(result.Status, "passed", StringComparison.Ordinal) && result.CleanClose);
    bool requestResourceMap = string.Equals(
        requestedSupplemental,
        "resource-map-absent",
        StringComparison.OrdinalIgnoreCase);
    bool requestSecurityPolicy = string.Equals(
        requestedSupplemental,
        "security-policy",
        StringComparison.OrdinalIgnoreCase);
    if (!string.IsNullOrWhiteSpace(requestedSupplemental) && !requestResourceMap && !requestSecurityPolicy)
    {
        throw new InvalidOperationException($"Unknown Markdown supplemental case '{requestedSupplemental}'.");
    }
    if (!requiresSupplementalCases && !string.IsNullOrWhiteSpace(requestedSupplemental))
    {
        throw new InvalidOperationException("Supplemental Markdown lifecycle cases require full-matrix scope.");
    }

    if (requiresSupplementalCases && executedCases == 0 && (matrixCasesComplete || requestResourceMap) &&
        (manifest.ResourceMapAbsentCase is null ||
         !string.Equals(manifest.ResourceMapAbsentCase.Status, "passed", StringComparison.Ordinal) ||
         !manifest.ResourceMapAbsentCase.CleanClose))
    {
        RunForcedResourceMapAbsentLifecycleCase(options, configuration, manifest, manifestPath, failures);
    }
    else if (requiresSupplementalCases && executedCases == 0 && (matrixCasesComplete || requestSecurityPolicy) &&
        (manifest.SecurityPolicyCase is null ||
         !string.Equals(manifest.SecurityPolicyCase.Status, "passed", StringComparison.Ordinal) ||
         !manifest.SecurityPolicyCase.CleanClose))
    {
        RunMarkdownSecurityPolicyLifecycleCase(options, configuration, manifest, manifestPath, failures);
    }

    bool supplementalCasesComplete = !requiresSupplementalCases ||
        (manifest.ResourceMapAbsentCase is { Status: "passed", CleanClose: true } &&
         manifest.SecurityPolicyCase is { Status: "passed", CleanClose: true });
    manifest.Completed = matrixCasesComplete &&
        supplementalCasesComplete &&
        failures.Count == 0;
    manifest.CompletedAtUtc = manifest.Completed ? DateTimeOffset.UtcNow : null;
    manifest.Failures = failures;
    PersistMarkdownLifecycleManifest(manifestPath, manifest);

    Console.WriteLine(
        $"Markdown lifecycle batch executed {executedCases} case(s); " +
        $"{manifest.Cases.Count}/{manifest.ExpectedCaseCount} matrix cases are persisted; completed={manifest.Completed}.");

    if (failures.Count > 0)
    {
        throw new InvalidOperationException(
            $"Markdown lifecycle matrix failed {failures.Count} gate(s). See '{manifestPath}'.{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures.Take(20)));
    }
}

static void CaptureMarkdownLifecycleFailureState(
    Window window,
    string outputDirectory,
    string caseId,
    string context)
{
    try
    {
        string failureScreenshot = Path.Combine(
            outputDirectory,
            "failure-screenshots",
            $"{caseId}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(failureScreenshot)!);
        CaptureWindow(window, failureScreenshot);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"{caseId}: diagnostic screenshot failed without replacing the product failure: {exception.Message}");
    }

    try
    {
        PrintVisibleAutomationIds(window, $"{caseId} {context}");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"{caseId}: diagnostic UIA dump failed without replacing the product failure: {exception.Message}");
    }
}

static MarkdownLifecycleRunPaths CreateMarkdownLifecycleRunPaths(string outputDirectory, string launchId)
{
    string directory = Path.Combine(outputDirectory, ".runtime", launchId);
    Directory.CreateDirectory(directory);
    var paths = new MarkdownLifecycleRunPaths(
        directory,
        Path.Combine(directory, "app-ready.json"),
        Path.Combine(directory, "host-ready.json"),
        Path.Combine(directory, "runtime-settings.json"),
        Path.Combine(directory, "resource-map-fallback.json"),
        Path.Combine(directory, "link-routes.ndjson"),
        Path.Combine(directory, "image-unavailable.ndjson"));
    foreach (string path in paths.SignalPaths)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    return paths;
}

static MarkdownLifecycleApplication LaunchMarkdownLifecycleApplication(
    CaptureOptions options,
    MarkdownLifecycleTarget target,
    string theme,
    MarkdownLifecycleRunPaths paths,
    bool forceResourceMapAbsent,
    bool securityFixture = false)
{
    string launchTheme = string.Equals(theme, "highcontrast", StringComparison.OrdinalIgnoreCase)
        ? "dark"
        : theme;
    string[] arguments =
    [
        $"--page={target.Page}",
        $"--theme={launchTheme}",
        $"--repo={options.RepositoryFullName}",
        "--markdown-lifecycle-fixture",
        $"--markdown-lifecycle-host={target.HostAutomationId}",
    ];
    var processStartInfo = new ProcessStartInfo(options.AppPath)
    {
        WorkingDirectory = Path.GetDirectoryName(options.AppPath) ?? Environment.CurrentDirectory,
        UseShellExecute = false,
    };
    foreach (string argument in arguments)
    {
        processStartInfo.ArgumentList.Add(argument);
    }

    AddPreviewEnvironment(processStartInfo, arguments);
    processStartInfo.Environment["JITHUB_MARKDOWN_APP_READY_PATH"] = paths.AppReadyPath;
    processStartInfo.Environment["JITHUB_MARKDOWN_HOST_READY_PATH"] = paths.HostReadyPath;
    processStartInfo.Environment["JITHUB_MARKDOWN_RUNTIME_SETTINGS_PATH"] = paths.RuntimeSettingsPath;
    processStartInfo.Environment["JITHUB_MARKDOWN_LINK_EVIDENCE_PATH"] = paths.LinkEvidencePath;
    processStartInfo.Environment["JITHUB_MARKDOWN_IMAGE_EVIDENCE_PATH"] = paths.ImageEvidencePath;
    if (string.Equals(theme, "highcontrast", StringComparison.OrdinalIgnoreCase))
    {
        processStartInfo.Environment["JITHUB_AUTOMATION_HIGH_CONTRAST"] = "1";
    }
    if (forceResourceMapAbsent)
    {
        processStartInfo.Environment["JITHUB_AUTOMATION_RESOURCE_MAP_ABSENT"] = "1";
        processStartInfo.Environment["JITHUB_AUTOMATION_RESOURCE_MAP_EVIDENCE_PATH"] = paths.ResourceMapEvidencePath;
    }
    if (securityFixture)
    {
        processStartInfo.Environment["JITHUB_MARKDOWN_SECURITY_LIVE_FIXTURE"] = "1";
    }

    Process launcher = Process.Start(processStartInfo)
        ?? throw new InvalidOperationException($"Failed to start lifecycle app '{options.AppPath}'.");
    IntPtr processExitHandle = IntPtr.Zero;
    try
    {
        int processId = WaitForMarkdownLifecycleProcess(paths.AppReadyPath, launcher, TimeSpan.FromSeconds(25));
        Process process = Process.GetProcessById(processId);
        processExitHandle = NativeMethods.OpenProcessExitHandle(processId);
        Application application = Application.Attach(processId);
        return new MarkdownLifecycleApplication(application, process, launcher, processExitHandle);
    }
    catch
    {
        NativeMethods.CloseProcessExitHandle(processExitHandle);
        if (!launcher.HasExited)
        {
            try { launcher.Kill(entireProcessTree: true); } catch { }
        }
        launcher.Dispose();
        throw;
    }
}

static int WaitForMarkdownLifecycleProcess(string signalPath, Process launcher, TimeSpan timeout)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    while (stopwatch.Elapsed < timeout)
    {
        if (TryReadMarkdownLifecycleSignal(signalPath, out int processId, out _))
        {
            return processId;
        }

        Thread.Sleep(50);
    }

    string launcherState = launcher.HasExited
        ? $"launcher exited with code {launcher.ExitCode}"
        : $"launcher {launcher.Id} remained active";
    throw new InvalidOperationException(
        $"Timed out waiting for deterministic app readiness signal '{signalPath}' ({launcherState}).");
}

static void PrepareRealMarkdownHost(Window window, MarkdownLifecycleTarget target, string caseId)
{
    TryActivateWindow(window);
    AssertProbe(
        window.FindFirstDescendant(cf => cf.ByAutomationId("MarkdownLifecycleFixturePage")) is null,
        $"{caseId}: synthetic Markdown lifecycle fixture replaced the real product page.");
    if (target.RequiresCompactWidth)
    {
        AssertProbe(
            window.BoundingRectangle.Width <= 900,
            $"{caseId}: compact-only Markdown composition launched at {window.BoundingRectangle.Width}px.");
    }

    if (!string.IsNullOrWhiteSpace(target.SectionControlAutomationId))
    {
        AutomationElement? section = window.FindAllDescendants(cf =>
                cf.ByAutomationId(target.SectionControlAutomationId))
            .FirstOrDefault(IsVisible);
        if (section is null &&
            !string.IsNullOrWhiteSpace(target.CompactSectionPickerAutomationId) &&
            !string.IsNullOrWhiteSpace(target.CompactSectionControlAutomationId))
        {
            AutomationElement picker = WaitForElement(
                $"{caseId} compact section picker",
                () => window.FindAllDescendants(cf =>
                        cf.ByAutomationId(target.CompactSectionPickerAutomationId))
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(20));
            picker.AsComboBox().Expand();
            section = WaitForElement(
                $"{caseId} compact real host section",
                () => window.FindAllDescendants(cf =>
                        cf.ByAutomationId(target.CompactSectionControlAutomationId))
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(5));
        }
        else
        {
            section ??= WaitForElement(
                $"{caseId} real host section",
                () => window.FindAllDescendants(cf =>
                        cf.ByAutomationId(target.SectionControlAutomationId))
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(20));
        }

        if (section.Patterns.SelectionItem.IsSupported)
        {
            section.Patterns.SelectionItem.Pattern.Select();
            WaitUntil(
                $"{caseId} real host section selection",
                () =>
                {
                    try
                    {
                        return section.Patterns.SelectionItem.Pattern.IsSelected.Value;
                    }
                    catch (COMException)
                    {
                        return false;
                    }
                },
                TimeSpan.FromSeconds(5));
        }
        else
        {
            InvokeOrClick(section);
        }

        Thread.Sleep(220);
    }

    if (!string.IsNullOrWhiteSpace(target.LauncherControlAutomationId))
    {
        AutomationElement launcher;
        try
        {
            launcher = WaitForElement(
                $"{caseId} real host launcher",
                () => window.FindFirstDescendant(cf =>
                    cf.ByAutomationId(target.LauncherControlAutomationId)),
                TimeSpan.FromSeconds(20));
        }
        catch
        {
            PrintVisibleAutomationIds(window, $"{caseId} real host launcher missing");
            throw;
        }

        FocusForKeyboardActivation(window, launcher);
        InvokeOrClick(launcher);
        Thread.Sleep(260);
    }

    const string editorPreviewHostPrefix = "MarkdownHost_EditorPreview_";
    const string previewHostSuffix = "_Preview";
    if (target.HostAutomationId.StartsWith(editorPreviewHostPrefix, StringComparison.Ordinal) &&
        target.HostAutomationId.EndsWith(previewHostSuffix, StringComparison.Ordinal))
    {
        // Real MarkdownForm instances select Preview themselves when the lifecycle
        // bridge targets their preview host. Prefer that real state; list/form
        // virtualization may legitimately omit an offscreen mode selector even
        // though the loaded preview document is already ready for interaction.
        AutomationElement? activePreview = window.FindAllDescendants(cf =>
                cf.ByAutomationId(target.HostAutomationId))
            .FirstOrDefault(element => element.Patterns.Text.IsSupported);
        if (activePreview is null)
        {
            string instanceId = target.HostAutomationId[
                editorPreviewHostPrefix.Length..
                ^previewHostSuffix.Length];
            string previewModeId = $"{instanceId}_Mode_Preview";
            AutomationElement previewMode = WaitForElement(
                $"{caseId} Markdown preview mode",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId(previewModeId)),
                TimeSpan.FromSeconds(20));
            RevealForInteraction(previewMode, $"{caseId} Markdown preview mode");
            if (previewMode.Patterns.SelectionItem.IsSupported)
            {
                previewMode.Patterns.SelectionItem.Pattern.Select();
            }
            else
            {
                InvokeOrClick(previewMode);
            }
            Thread.Sleep(260);
        }
    }

    RealizeMarkdownHost(window, target, caseId);
}

static void RealizeMarkdownHost(Window window, MarkdownLifecycleTarget target, string caseId)
{
    AutomationElement? FindHost() => window.FindAllDescendants().FirstOrDefault(element =>
    {
        string automationId = GetAutomationId(element);
        return target.PrefixMatch
            ? automationId.StartsWith(target.HostAutomationId, StringComparison.Ordinal)
            : string.Equals(automationId, target.HostAutomationId, StringComparison.Ordinal);
    });

    AutomationElement? host = FindHost();
    if (string.IsNullOrWhiteSpace(target.RealizationContainerAutomationId))
    {
        if (host is not null)
        {
            RevealForInteraction(host, $"{caseId} Markdown host");
        }

        return;
    }

    AutomationElement container = WaitForElement(
        $"{caseId} Markdown realization container",
        () => window.FindFirstDescendant(cf =>
            cf.ByAutomationId(target.RealizationContainerAutomationId)),
        TimeSpan.FromSeconds(20));

    if (target.RealizationStartsAtTop && container.Patterns.Scroll.IsSupported)
    {
        var scroll = container.Patterns.Scroll.Pattern;
        if (scroll.VerticallyScrollable.ValueOrDefault)
        {
            scroll.SetScrollPercent(
                FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll,
                0);
            Thread.Sleep(180);
            host = FindHost();
        }
    }

    // WinUI virtualizes comments and review threads. Walk the owning list instead of
    // waiting for an offscreen renderer that cannot exist in the UIA tree yet.
    for (int attempt = 0; host is null && attempt < 18; attempt++)
    {
        bool scrolled = false;
        try
        {
            if (container.Patterns.Scroll.IsSupported)
            {
                var scroll = container.Patterns.Scroll.Pattern;
                if (scroll.VerticallyScrollable.ValueOrDefault)
                {
                    scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                    scrolled = true;
                }
            }
        }
        catch (COMException)
        {
            container = WaitForElement(
                $"{caseId} refreshed Markdown realization container",
                () => window.FindFirstDescendant(cf =>
                    cf.ByAutomationId(target.RealizationContainerAutomationId)),
                TimeSpan.FromSeconds(5));
        }

        if (!scrolled)
        {
            Rectangle bounds = container.BoundingRectangle;
            Mouse.MoveTo(new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2)));
            Mouse.Scroll(-6);
        }

        Thread.Sleep(160);
        host = FindHost();
    }

    if (host is null)
    {
        PrintVisibleAutomationIds(window, $"{caseId} Markdown host realization failed");
        throw new InvalidOperationException(
            $"{caseId}: '{target.HostAutomationId}' was not realized by " +
            $"'{target.RealizationContainerAutomationId}'.");
    }

    RevealForInteraction(host, $"{caseId} Markdown host");
    Thread.Sleep(180);
}

static void WaitForMarkdownLifecycleSignal(
    string signalPath,
    string expectedHost,
    bool prefixMatch,
    TimeSpan timeout)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    while (stopwatch.Elapsed < timeout)
    {
        if (TryReadMarkdownLifecycleSignal(signalPath, out _, out string stage) &&
            (prefixMatch
                ? stage.StartsWith(expectedHost, StringComparison.Ordinal)
                : string.Equals(stage, expectedHost, StringComparison.Ordinal)))
        {
            return;
        }
        Thread.Sleep(50);
    }

    throw new InvalidOperationException(
        $"Timed out waiting for deterministic host readiness signal '{expectedHost}' at '{signalPath}'.");
}

static bool TryReadMarkdownLifecycleSignal(string path, out int processId, out string stage)
{
    processId = 0;
    stage = string.Empty;
    try
    {
        if (!File.Exists(path))
        {
            return false;
        }
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using JsonDocument document = JsonDocument.Parse(stream);
        processId = document.RootElement.GetProperty("ProcessId").GetInt32();
        stage = document.RootElement.GetProperty("Stage").GetString() ?? string.Empty;
        return processId > 0 && stage.Length > 0;
    }
    catch (IOException)
    {
        return false;
    }
    catch (JsonException)
    {
        return false;
    }
}

static void WriteMarkdownLifecycleRuntimeSettings(string path, double textScaleFactor, int revision)
{
    string temporaryPath = path + ".tmp";
    File.WriteAllText(
        temporaryPath,
        JsonSerializer.Serialize(new { TextScaleFactor = textScaleFactor, Revision = revision }));
    File.Move(temporaryPath, path, overwrite: true);
}

static MarkdownLifecycleCaseResult ExerciseMarkdownLifecycleState(
    Window window,
    MarkdownLifecycleTarget target,
    string theme,
    string configuration,
    double textScale,
    MarkdownLifecycleViewport viewport,
    string caseId,
    string outputDirectory,
    string linkEvidencePath)
{
    ResizeWindow(window, viewport.Width, viewport.Height, reactivate: false);
    Thread.Sleep(180);

    AutomationElement FindCurrentHost()
    {
        MarkdownLifecycleHostAcquisition acquisition = AcquireMarkdownLifecycleHost(
            window,
            target,
            caseId,
            TimeSpan.FromSeconds(8));
        window = acquisition.Window;
        return acquisition.Host;
    }

    AutomationElement host = FindCurrentHost();
    RevealForInteraction(host, $"{target.Name} Markdown host");
    var textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: Markdown host did not expose TextPattern.");
    string documentText = textPattern.DocumentRange.GetText(-1);
    int taskIndex = documentText.IndexOf("Lifecycle task list complete", StringComparison.Ordinal);
    int tableIndex = documentText.IndexOf("Feature", StringComparison.Ordinal);
    int quoteIndex = documentText.IndexOf("Lifecycle quote level three", StringComparison.Ordinal);
    int codeIndex = documentText.IndexOf("LifecycleCode", StringComparison.Ordinal);
    int finalIndex = documentText.IndexOf("Lifecycle long document final marker", StringComparison.Ordinal);
    AssertProbe(
        taskIndex >= 0 && tableIndex > taskIndex && quoteIndex > tableIndex && codeIndex > quoteIndex && finalIndex > codeIndex,
        $"{target.Name}: Markdown TextPattern reading order omitted or reordered lifecycle content.");

    const string pointerSelectionStart = "Markdown audit pointer selection starts here on the first line.";
    const string pointerDragStart = "selection starts here on the first line.";
    const string pointerDragEnd = "selection ends here on the second line.";
    var pointerStartRange = textPattern.DocumentRange.FindText(
        pointerDragStart, backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: pointer selection start was not exposed through TextPattern.");
    pointerStartRange.ScrollIntoView(alignToTop: true);
    Thread.Sleep(120);
    host = FindCurrentHost();
    textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: Markdown host lost TextPattern after realization.");
    pointerStartRange = textPattern.DocumentRange.FindText(
        pointerDragStart, backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: pointer selection start disappeared after realization.");
    var pointerEndRange = textPattern.DocumentRange.FindText(
        pointerDragEnd, backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: pointer selection end disappeared after realization.");
    ActivateWindowForMarkdownHost(window, target);
    FocusMarkdownHostIfInline(host, target);
    if (string.IsNullOrWhiteSpace(target.LauncherControlAutomationId))
    {
        WaitUntil(
            $"{caseId} pointer host focus",
            () => IsMarkdownHostOrDescendantFocused(window, FindCurrentHost()),
            TimeSpan.FromSeconds(3));
    }
    else
    {
        AssertProbe(IsVisible(FindCurrentHost()), $"{caseId}: popup Markdown host was not visible before pointer selection.");
    }
    host = FindCurrentHost();
    textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: Markdown host lost TextPattern after pointer focus.");
    pointerStartRange = textPattern.DocumentRange.FindText(
        pointerDragStart, backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: pointer selection start disappeared after focus.");
    pointerEndRange = textPattern.DocumentRange.FindText(
        pointerDragEnd, backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: pointer selection end disappeared after focus.");
    Console.WriteLine($"{caseId}: pointer scroll ancestors=[{DescribeScrollAncestors(host)}]");
    Rectangle pointerStartRect = FindVisibleMarkdownRangeRect(host, pointerStartRange, $"{caseId} pointer selection start");
    host = FindCurrentHost();
    textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: Markdown host lost TextPattern after realizing the pointer start.");
    pointerEndRange = textPattern.DocumentRange.FindText(
        pointerDragEnd, backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: pointer selection end disappeared after realizing the pointer start.");
    Rectangle pointerEndRect = FindVisibleMarkdownRangeRect(
        host,
        pointerEndRange,
        $"{caseId} pointer selection end",
        preferLastRectangle: true);
    host = FindCurrentHost();
    textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: Markdown host lost TextPattern before the pointer drag.");
    pointerStartRange = textPattern.DocumentRange.FindText(
        pointerDragStart, backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: pointer selection start disappeared before the pointer drag.");
    pointerEndRange = textPattern.DocumentRange.FindText(
        pointerDragEnd, backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: pointer selection end disappeared before the pointer drag.");
    bool pointerEndpointsVisible =
        TryGetVisibleMarkdownRangeRect(host, pointerStartRange, preferLastRectangle: false, out pointerStartRect) &&
        TryGetVisibleMarkdownRangeRect(host, pointerEndRange, preferLastRectangle: true, out pointerEndRect);
    for (int attempt = 0; !pointerEndpointsVisible && attempt < 12; attempt++)
    {
        // At 200% text scale a snapped page can expose less than two complete
        // body lines. ScrollIntoView aligns the second line to the viewport top;
        // a small reverse nudge exposes the preceding line tail so the same
        // physical drag can still prove selection across the source newline.
        ScrollNearestVerticalAncestor(host, ScrollAmount.SmallDecrement);
        Thread.Sleep(120);
        host = FindCurrentHost();
        textPattern = host.Patterns.Text.PatternOrDefault
            ?? throw new InvalidOperationException($"{target.Name}: Markdown host lost TextPattern while realizing pointer endpoints.");
        pointerStartRange = textPattern.DocumentRange.FindText(
            pointerDragStart, backward: false, ignoreCase: false)
            ?? throw new InvalidOperationException($"{target.Name}: pointer selection start disappeared while realizing both endpoints.");
        pointerEndRange = textPattern.DocumentRange.FindText(
            pointerDragEnd, backward: false, ignoreCase: false)
            ?? throw new InvalidOperationException($"{target.Name}: pointer selection end disappeared while realizing both endpoints.");
        pointerEndpointsVisible =
            TryGetVisibleMarkdownRangeRect(host, pointerStartRange, preferLastRectangle: false, out pointerStartRect) &&
            TryGetVisibleMarkdownRangeRect(host, pointerEndRange, preferLastRectangle: true, out pointerEndRect);
    }

    AssertProbe(pointerEndpointsVisible, $"{caseId}: pointer endpoints were not simultaneously visible after realization.");
    Point pointerStart = new(
        pointerStartRect.Left + Math.Min(8, Math.Max(4, pointerStartRect.Width / 12)),
        pointerStartRect.Top + Math.Max(2, pointerStartRect.Height / 2));
    Point pointerEnd = new(
        Math.Min(host.BoundingRectangle.Right - 4, pointerEndRect.Right + 4),
        pointerEndRect.Top + Math.Max(2, pointerEndRect.Height / 2));
    Point pointerAttemptStart = pointerStart;
    Point pointerAttemptEnd = pointerEnd;
    string selectedText = string.Empty;
    bool pointerSelected = false;
    for (int attempt = 0; attempt < 3 && !pointerSelected; attempt++)
    {
        if (attempt > 0)
        {
            pointerAttemptStart = new Point(
                pointerStartRect.Left + Math.Clamp(pointerStartRect.Width / 4, 4, pointerStartRect.Width - 4),
                pointerStartRect.Top + Math.Max(2, pointerStartRect.Height / 2));
            pointerAttemptEnd = new Point(
                pointerEndRect.Left + Math.Clamp((pointerEndRect.Width * 3) / 4, 4, pointerEndRect.Width - 4),
                pointerEndRect.Top + Math.Max(2, pointerEndRect.Height / 2));
        }

        ActivateWindowForMarkdownHost(window, target);
        SendMarkdownPointerDrag(pointerAttemptStart, pointerAttemptEnd);
        pointerSelected = WaitUntilAvailable(
            () =>
            {
                AutomationElement currentHost = FindCurrentHost();
                var currentTextPattern = currentHost.Patterns.Text.PatternOrDefault;
                selectedText = currentTextPattern is null
                    ? string.Empty
                    : string.Concat(currentTextPattern.GetSelection().Select(range => range.GetText(-1)));
                return HasCrossLineMarkdownSelection(selectedText);
            },
            TimeSpan.FromSeconds(2));
    }
    AssertProbe(pointerSelected, $"{caseId}: raw pointer drag did not create a cross-line text selection.");
    Console.WriteLine(
        $"{caseId}: pointer drag start={pointerAttemptStart} end={pointerAttemptEnd} " +
        $"selection='{TruncateForLog(selectedText.ReplaceLineEndings("\\n"), 180)}'");
    string pointerDiagnosticPath = Path.Combine(outputDirectory, "pointer-diagnostics", $"{caseId}.png");
    Directory.CreateDirectory(Path.GetDirectoryName(pointerDiagnosticPath)!);
    CaptureMarkdownHostWindow(window, target, pointerDiagnosticPath);

    host = FindCurrentHost();
    FocusMarkdownHostIfInline(host, target);
    if (string.IsNullOrWhiteSpace(target.LauncherControlAutomationId))
    {
        WaitUntil(
            $"{caseId} selected host focus",
            () =>
            {
                AutomationElement currentHost = FindCurrentHost();
                if (!IsMarkdownHostOrDescendantFocused(window, currentHost))
                {
                    currentHost.FocusNative();
                }
                host = currentHost;
                return IsMarkdownHostOrDescendantFocused(window, currentHost);
            },
            TimeSpan.FromSeconds(6));
    }
    textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: Markdown host lost TextPattern after focus.");
    selectedText = string.Concat(textPattern.GetSelection().Select(range => range.GetText(-1)));
    AssertProbe(
        HasCrossLineMarkdownSelection(selectedText),
        $"{target.Name}: focusing the selected host revoked its text selection.");

    NativeMethods.SetClipboardText("__jithub_markdown_ctrl_c_pending__");
    Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
    WaitUntil(
        $"{caseId} Ctrl+C",
        () => HasCrossLineMarkdownSelection(NativeMethods.GetClipboardText()),
        TimeSpan.FromSeconds(3));

    NativeMethods.SetClipboardText("__jithub_markdown_context_copy_pending__");
    host = FindCurrentHost();
    textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: Markdown host lost TextPattern before context Copy.");
    ITextRange contextSelectionRange = textPattern.GetSelection().FirstOrDefault()
        ?? throw new InvalidOperationException($"{target.Name}: active selection was unavailable before right-click.");
    Rectangle contextSelectionRect = FindVisibleMarkdownRangeRect(
        host,
        contextSelectionRange,
        $"{caseId} context selection");
    Point contextSelectionPoint = new(
        contextSelectionRect.Left + (contextSelectionRect.Width / 2),
        contextSelectionRect.Top + (contextSelectionRect.Height / 2));
    ActivateWindowForMarkdownHost(window, target);
    Mouse.MoveTo(contextSelectionPoint);
    Thread.Sleep(75);
    Mouse.RightClick();
    AutomationElement copy = WaitForElement(
        $"{caseId} context Copy",
        () => host.Automation.GetDesktop().FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
            .FirstOrDefault(item => IsVisible(item) && string.Equals(item.Name, "Copy", StringComparison.OrdinalIgnoreCase)),
        TimeSpan.FromSeconds(5));
    host = FindCurrentHost();
    textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: Markdown host lost TextPattern after opening context Copy.");
    selectedText = string.Concat(textPattern.GetSelection().Select(range => range.GetText(-1)));
    AssertProbe(
        HasCrossLineMarkdownSelection(selectedText),
        $"{target.Name}: right-clicking selected text revoked the selection before context Copy.");
    InvokeOrClick(copy);
    WaitUntil(
        $"{caseId} context Copy clipboard",
        () => HasCrossLineMarkdownSelection(NativeMethods.GetClipboardText()),
        TimeSpan.FromSeconds(3));

    host = FindCurrentHost();
    AutomationElement keyboardLink = host.FindAllDescendants(cf => cf.ByControlType(ControlType.Hyperlink))
        .FirstOrDefault(link => (link.Name ?? string.Empty).Contains("keyboard link", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"{target.Name}: fixture keyboard link was not exposed as a Hyperlink.");
    keyboardLink.FocusNative();
    WaitUntil($"{caseId} keyboard link focus", () => IsElementFocused(keyboardLink), TimeSpan.FromSeconds(3));

    AssertMarkdownLinkRoute(
        FindCurrentHost,
        target,
        "internal repository route",
        "repository",
        linkEvidencePath,
        caseId);
    AssertMarkdownLinkRoute(
        FindCurrentHost,
        target,
        "internal user route",
        "user",
        linkEvidencePath,
        caseId);
    AssertMarkdownLinkRoute(
        FindCurrentHost,
        target,
        "external browser route",
        "external-browser",
        linkEvidencePath,
        caseId);
    RestoreMarkdownPointerSelection(window, FindCurrentHost, target, caseId);

    host = FindCurrentHost();
    textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: Markdown host lost TextPattern before image checks.");
    var blockedImage = textPattern.DocumentRange.FindText(
        "Lifecycle blocked remote image", backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: blocked remote-image range was unavailable.");
    blockedImage.ScrollIntoView(alignToTop: true);
    WaitForElement(
        $"{caseId} inline SVG",
        () => FindCurrentHost().FindAllDescendants(cf => cf.ByControlType(ControlType.Image))
            .FirstOrDefault(element => (element.Name ?? string.Empty).Contains("Lifecycle inline SVG", StringComparison.Ordinal)),
        TimeSpan.FromSeconds(6));
    AutomationElement remoteImageNotice = WaitForElement(
        $"{caseId} remote-image privacy notice",
        () => window.FindAllDescendants(cf => cf.ByAutomationId("MarkdownRemoteImageInfoBar"))
            .FirstOrDefault(),
        TimeSpan.FromSeconds(6));
    RevealForInteraction(remoteImageNotice, $"{caseId} remote-image privacy notice");

    // The privacy notice and renderer live in separate rows inside a MarkdownViewer
    // that may itself be hosted by a page ScrollViewer. Restore the host to the outer
    // viewport before asking TextPattern to move ranges inside the renderer again.
    host = FindCurrentHost();
    if (host.Patterns.ScrollItem.IsSupported)
    {
        host.Patterns.ScrollItem.Pattern.ScrollIntoView();
        Thread.Sleep(100);
    }

    host = FindCurrentHost();
    textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: Markdown host lost TextPattern before scale checks.");
    var scaledText = textPattern.DocumentRange.FindText(
        "Markdown audit selection marker", backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: scaled marker was unavailable.");
    scaledText.ScrollIntoView(alignToTop: true);
    _ = FindVisibleMarkdownRangeRect(host, scaledText, $"{caseId} scaled marker");
    host = FindCurrentHost();
    textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: Markdown host lost TextPattern after realizing the scale marker.");
    scaledText = textPattern.DocumentRange.FindText(
        "Markdown audit selection marker", backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: scaled marker disappeared after realization.");
    double measuredLineHeight = scaledText.GetBoundingRectangles()
        .Where(rectangle => rectangle.Height > 1)
        .Select(rectangle => (double)rectangle.Height)
        .DefaultIfEmpty(0)
        .Max();
    object fontSizeValue = scaledText.GetAttributeValue(host.Automation.TextAttributeLibrary.FontSize);
    double measuredFontSize = fontSizeValue switch
    {
        double value => value,
        float value => value,
        int value => value,
        _ => 0,
    };
    double minimumScaledFontSize = 14 * textScale;
    AssertProbe(measuredFontSize >= minimumScaledFontSize,
        $"{target.Name}: {textScale * 100:0}% text scaling was not reflected " +
        $"(FontSize={measuredFontSize:0.#}, visible line height={measuredLineHeight:0.#}px).");

    var finalMarker = textPattern.DocumentRange.FindText(
        "Lifecycle long document final marker", backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: large-document final marker was missing.");
    finalMarker.ScrollIntoView(alignToTop: false);
    _ = FindVisibleMarkdownRangeRect(
        host,
        finalMarker,
        $"{caseId} large-document final marker",
        preferLastRectangle: true,
        searchFromBottom: true);

    host = FindCurrentHost();
    textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: Markdown host lost TextPattern before capture.");
    var captureMarker = textPattern.DocumentRange.FindText(
        pointerSelectionStart, backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: pointer-selection capture marker was unavailable.");
    captureMarker.ScrollIntoView(alignToTop: true);
    Thread.Sleep(100);
    Rectangle captureMarkerRect = FindVisibleMarkdownRangeRect(
        host,
        captureMarker,
        $"{caseId} capture marker",
        minimumVisibleHeight: 14);
    string screenshotPath = Path.Combine(
        outputDirectory,
        configuration.ToLowerInvariant(),
        theme.ToLowerInvariant(),
        target.Name,
        $"scale-{textScale * 100:0}-{viewport.Name}.png");
    Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
    CaptureMarkdownHostWindow(window, target, screenshotPath);
    AssertMarkdownSelectionForegroundVisible(
        screenshotPath,
        window.BoundingRectangle,
        NativeMethods.GetPhysicalWindowBounds(GetNativeWindowHandle(window)),
        captureMarkerRect,
        caseId);

    return MarkdownLifecycleCaseResult.Passed(
        configuration,
        target.Name,
        theme,
        textScale,
        viewport,
        caseId,
        Path.GetRelativePath(outputDirectory, screenshotPath),
        measuredLineHeight,
        measuredFontSize);
}

static void AssertMarkdownLinkRoute(
    Func<AutomationElement> findCurrentHost,
    MarkdownLifecycleTarget target,
    string linkName,
    string expectedDisposition,
    string evidencePath,
    string caseId)
{
    AutomationElement host = findCurrentHost();
    AutomationElement link = host.FindAllDescendants(cf => cf.ByControlType(ControlType.Hyperlink))
        .FirstOrDefault(candidate =>
            (candidate.Name ?? string.Empty).Contains(linkName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"{target.Name}: '{linkName}' was not exposed as a Hyperlink.");
    RevealForInteraction(link, $"{caseId} {linkName}");
    link.FocusNative();
    WaitUntil($"{caseId} {linkName} focus", () => IsElementFocused(link), TimeSpan.FromSeconds(3));
    try
    {
        link.Patterns.Invoke.Pattern.Invoke();
    }
    catch (Exception)
    {
        // Compact popup content can recycle the focused Hyperlink peer while it settles.
        // Enter activates the native focused element without relying on a stale click point.
        Keyboard.Press(VirtualKeyShort.ENTER);
    }
    WaitUntil(
        $"{caseId} {linkName} route evidence",
        () => HasMarkdownLinkRouteEvidence(
            evidencePath,
            target.HostAutomationId,
            target.PrefixMatch,
            expectedDisposition),
        TimeSpan.FromSeconds(5));
}

static void RestoreMarkdownPointerSelection(
    Window window,
    Func<AutomationElement> findCurrentHost,
    MarkdownLifecycleTarget target,
    string caseId)
{
    const string startText = "Markdown audit pointer selection starts here on the first line.";
    const string endText = "Markdown audit pointer selection ends here on the second line.";
    AutomationElement host = findCurrentHost();
    var textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: host lost TextPattern before restoring pointer selection.");
    var startRange = textPattern.DocumentRange.FindText(startText, backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: pointer selection start was unavailable after link routing.");
    startRange.ScrollIntoView(alignToTop: true);
    Thread.Sleep(100);
    host = findCurrentHost();
    textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{target.Name}: host lost TextPattern after restoring pointer position.");
    startRange = textPattern.DocumentRange.FindText(startText, backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: pointer selection start disappeared after link routing.");
    var endRange = textPattern.DocumentRange.FindText(endText, backward: false, ignoreCase: false)
        ?? throw new InvalidOperationException($"{target.Name}: pointer selection end disappeared after link routing.");
    ActivateWindowForMarkdownHost(window, target);
    FocusMarkdownHostIfInline(host, target);
    if (string.IsNullOrWhiteSpace(target.LauncherControlAutomationId))
    {
        WaitUntil(
            $"{caseId} restored pointer host focus",
            () => IsMarkdownHostOrDescendantFocused(window, findCurrentHost()),
            TimeSpan.FromSeconds(3));
    }
    // The case already proved physical cross-line pointer selection before exercising
    // the links. Restore the same selection through TextPattern so nested page/renderer
    // scroll viewers cannot turn this setup step into a second timing-sensitive drag.
    startRange.MoveEndpointByRange(
        TextPatternRangeEndpoint.End,
        endRange,
        TextPatternRangeEndpoint.End);
    startRange.Select();

    WaitUntil(
        $"{caseId} restored pointer selection",
        () =>
        {
            var currentPattern = findCurrentHost().Patterns.Text.PatternOrDefault;
            string selection = currentPattern is null
                ? string.Empty
                : string.Concat(currentPattern.GetSelection().Select(range => range.GetText(-1)));
            return HasCrossLineMarkdownSelection(selection);
        },
        TimeSpan.FromSeconds(5));
}

static bool HasCrossLineMarkdownSelection(string text)
{
    int firstLineEnd = text.IndexOf("first line.", StringComparison.Ordinal);
    if (firstLineEnd < 0)
    {
        return false;
    }

    // A snapped or scaled host can wrap the second source line before its "second line"
    // suffix. Requiring the next line's unique prefix proves the range crossed the source
    // boundary without depending on how much of that wrapped line fits at the drag endpoint.
    return text.IndexOf(
        "Markdown audit pointer selection",
        firstLineEnd + "first line.".Length,
        StringComparison.Ordinal) >= 0;
}

static bool HasMarkdownLinkRouteEvidence(
    string path,
    string expectedHost,
    bool prefixMatch,
    string expectedDisposition)
{
    try
    {
        if (!File.Exists(path))
        {
            return false;
        }

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string host = root.GetProperty("Host").GetString() ?? string.Empty;
            string disposition = root.GetProperty("Disposition").GetString() ?? string.Empty;
            bool hostMatches = prefixMatch
                ? host.StartsWith(expectedHost, StringComparison.Ordinal)
                : string.Equals(host, expectedHost, StringComparison.Ordinal);
            if (hostMatches && string.Equals(disposition, expectedDisposition, StringComparison.Ordinal))
            {
                return true;
            }
        }
    }
    catch (IOException)
    {
    }
    catch (JsonException)
    {
    }

    return false;
}

static MarkdownRelayoutMetrics RunRepeatedMarkdownRelayout(
    ref Window window,
    Application application,
    UIA3Automation automation,
    Process process,
    MarkdownLifecycleTarget target,
    MarkdownLifecycleViewport viewport,
    string caseId,
    string runtimeSettingsPath,
    double textScale)
{
    const int relayoutCycles = 6;
    const long retainedGrowthBudgetBytes = 96L * 1024 * 1024;
    int alternateWidth = viewport.Width >= 1000
        ? Math.Max(1040, viewport.Width - 96)
        : Math.Max(560, viewport.Width - 72);
    int alternateHeight = viewport.Height >= 800
        ? Math.Max(640, viewport.Height - 48)
        : Math.Max(520, viewport.Height - 32);

    process.Refresh();
    long baselinePrivateBytes = process.PrivateMemorySize64;
    if (!string.IsNullOrWhiteSpace(target.LauncherControlAutomationId))
    {
        // Keep the verified app HWND stable across popup dismissal/reopen cycles.
        // WinUI popup UIA peers can temporarily present as top-level windows and
        // must never become the resize or containment reference for the app.
        IntPtr appWindowHandle = GetNativeWindowHandle(window);
        double baselineFontSize = ReadMarkdownMarkerFontSize(window, target);
        AssertProbe(
            baselineFontSize > 0,
            $"{caseId}: popup Markdown host did not expose a baseline font size before relayout.");
        double alternateTextScale = textScale >= 1.5
            ? Math.Max(1, textScale - 0.2)
            : Math.Min(3, textScale + 0.2);
        for (int cycle = 0; cycle < relayoutCycles; cycle++)
        {
            bool useAlternate = (cycle & 1) == 0;
            double cycleScale = (cycle & 1) == 0 ? alternateTextScale : textScale;
            ActivateWindowForMarkdownHost(window, target);
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Window dismissalWindow = window;
            WaitUntil(
                $"{caseId} popup dismissal {cycle + 1}",
                () => FindUsableMarkdownLifecycleHost(dismissalWindow, target) is null,
                TimeSpan.FromSeconds(5));
            window = GetReadyWindowByHandle(
                automation,
                appWindowHandle,
                process.Id,
                $"{caseId} popup dismissal {cycle + 1} root");
            Window restoredWindow = window;
            AutomationElement restoredLauncher = WaitForElement(
                $"{caseId} popup restored launcher {cycle + 1}",
                () => restoredWindow.FindAllDescendants(cf =>
                        cf.ByAutomationId(target.LauncherControlAutomationId))
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(5));
            WaitUntil(
                $"{caseId} popup focus restoration {cycle + 1}",
                () => IsElementOrDescendantFocused(restoredWindow, restoredLauncher),
                TimeSpan.FromSeconds(5));

            WriteMarkdownLifecycleRuntimeSettings(runtimeSettingsPath, cycleScale, cycle + 2);
            window = GetReadyWindowByHandle(
                automation,
                appWindowHandle,
                process.Id,
                $"{caseId} popup relayout {cycle + 1} resize root");
            ResizeWindow(
                window,
                useAlternate ? alternateWidth : viewport.Width,
                useAlternate ? alternateHeight : viewport.Height,
                reactivate: false);
            Thread.Sleep(220);
            window = ReacquireJitHubWindow(
                application,
                automation,
                process.Id,
                $"{caseId} popup relayout {cycle + 1}",
                appWindowHandle);
            Window relayoutWindow = window;
            AutomationElement launcher = WaitForElement(
                $"{caseId} popup launcher {cycle + 1}",
                () => relayoutWindow.FindAllDescendants(cf =>
                        cf.ByAutomationId(target.LauncherControlAutomationId))
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(5));
            FocusForKeyboardActivation(relayoutWindow, launcher);
            InvokeOrClick(launcher);
            MarkdownLifecycleHostAcquisition acquisition = AcquireMarkdownLifecycleHost(
                relayoutWindow,
                target,
                $"{caseId} popup reopen {cycle + 1}",
                TimeSpan.FromSeconds(10));
            window = acquisition.Window;
            Rectangle hostBounds = acquisition.Host.BoundingRectangle;
            Rectangle appBounds = NativeMethods.GetPhysicalWindowBounds(appWindowHandle);
            AssertProbe(
                hostBounds.Left >= appBounds.Left - 2 &&
                hostBounds.Top >= appBounds.Top - 2 &&
                hostBounds.Right <= appBounds.Right + 2 &&
                hostBounds.Bottom <= appBounds.Bottom + 2,
                $"{caseId}: popup Markdown host escaped the app after relayout cycle {cycle + 1}. " +
                $"Host={hostBounds}; App={appBounds}.");
            WaitForMarkdownRuntimeScale(
                window,
                target,
                cycleScale,
                baselineFontSize * (cycleScale / textScale),
                $"{caseId} relayout {cycle + 1}");
            AssertNoMarkdownLifecycleExceptionLogs($"{caseId} relayout cycle {cycle + 1}");
        }

        WriteMarkdownLifecycleRuntimeSettings(runtimeSettingsPath, textScale, revision: 100);
        AutomationElement? finalLauncher = FindVisibleByAutomationIdWithComRetry(
            window,
            target.LauncherControlAutomationId,
            TimeSpan.FromSeconds(3));
        if (FindUsableMarkdownLifecycleHost(window, target) is null && finalLauncher is not null)
        {
            InvokeOrClick(finalLauncher);
        }
        WaitForMarkdownRuntimeScale(
            window,
            target,
            textScale,
            baselineFontSize,
            $"{caseId} restored scale");
    }
    else
    {
        for (int cycle = 0; cycle < relayoutCycles; cycle++)
        {
            bool useAlternate = (cycle & 1) == 0;
            ResizeWindow(
                window,
                useAlternate ? alternateWidth : viewport.Width,
                useAlternate ? alternateHeight : viewport.Height,
                reactivate: false);
            Thread.Sleep(220);
            window = ReacquireJitHubWindow(
                application,
                automation,
                process.Id,
                $"{caseId} relayout {cycle + 1}");
            MarkdownLifecycleHostAcquisition acquisition = AcquireMarkdownLifecycleHost(
                window,
                target,
                $"{caseId} relayout {cycle + 1}",
                TimeSpan.FromSeconds(6));
            window = acquisition.Window;
            AutomationElement host = acquisition.Host;
            var textPattern = host.Patterns.Text.PatternOrDefault
                ?? throw new InvalidOperationException($"{caseId}: host lost TextPattern during relayout cycle {cycle + 1}.");
            AssertProbe(
                textPattern.DocumentRange.GetText(-1).Contains(
                    "Lifecycle long document final marker",
                    StringComparison.Ordinal),
                $"{caseId}: real Markdown content disappeared during relayout cycle {cycle + 1}.");
            AssertNoMarkdownLifecycleExceptionLogs($"{caseId} relayout cycle {cycle + 1}");
        }
    }

    ResizeWindow(window, viewport.Width, viewport.Height, reactivate: false);
    Thread.Sleep(420);
    window = ReacquireJitHubWindow(
        application,
        automation,
        process.Id,
        $"{caseId} final relayout");
    long retainedPrivateBytes = long.MaxValue;
    for (int sample = 0; sample < 3; sample++)
    {
        process.Refresh();
        retainedPrivateBytes = Math.Min(retainedPrivateBytes, process.PrivateMemorySize64);
        Thread.Sleep(180);
    }

    long retainedGrowthBytes = Math.Max(0, retainedPrivateBytes - baselinePrivateBytes);
    AssertProbe(
        retainedGrowthBytes <= retainedGrowthBudgetBytes,
        $"{caseId}: repeated real-host relayout retained {retainedGrowthBytes / (1024 * 1024):N0} MiB " +
        $"above baseline, exceeding the {retainedGrowthBudgetBytes / (1024 * 1024):N0} MiB budget.");
    return new MarkdownRelayoutMetrics(
        relayoutCycles,
        baselinePrivateBytes,
        retainedPrivateBytes,
        retainedGrowthBytes,
        retainedGrowthBudgetBytes);
}

static void WaitForMarkdownRuntimeScale(
    Window window,
    MarkdownLifecycleTarget target,
    double expectedScale,
    double expectedFontSize,
    string context)
{
    double lastObservedFontSize = 0;
    Stopwatch stopwatch = Stopwatch.StartNew();
    while (stopwatch.Elapsed < TimeSpan.FromSeconds(8))
    {
        try
        {
            lastObservedFontSize = ReadMarkdownMarkerFontSize(window, target);
            if (lastObservedFontSize <= 0)
            {
                Thread.Sleep(100);
                continue;
            }
            if (Math.Abs(lastObservedFontSize - expectedFontSize) <= 1.25)
            {
                return;
            }
        }
        catch (COMException)
        {
        }

        Thread.Sleep(100);
    }

    throw new InvalidOperationException(
        $"Timed out waiting for {context} runtime text scale. " +
        $"Expected {expectedScale * 100:0}% / FontSize {expectedFontSize:0.##}; " +
        $"last observed {lastObservedFontSize:0.##}.");
}

static double ReadMarkdownMarkerFontSize(Window window, MarkdownLifecycleTarget target)
{
    const string marker = "Markdown audit selection marker";
    AutomationElement? host = FindUsableMarkdownLifecycleHost(window, target);
    var textPattern = host?.Patterns.Text.PatternOrDefault;
    var range = textPattern?.DocumentRange.FindText(marker, backward: false, ignoreCase: false);
    if (host is null || range is null)
    {
        return 0;
    }

    object value = range.GetAttributeValue(host.Automation.TextAttributeLibrary.FontSize);
    return value switch
    {
        double number => number,
        float number => number,
        int number => number,
        _ => 0,
    };
}

static IEnumerable<AutomationElement> FindMarkdownLifecycleHosts(
    AutomationElement root,
    MarkdownLifecycleTarget target)
    => root.FindAllDescendants().Where(element =>
    {
        string automationId = GetAutomationId(element);
        return target.PrefixMatch
            ? automationId.StartsWith(target.HostAutomationId, StringComparison.Ordinal)
            : string.Equals(automationId, target.HostAutomationId, StringComparison.Ordinal);
    });

static MarkdownLifecycleHostAcquisition AcquireMarkdownLifecycleHost(
    Window window,
    MarkdownLifecycleTarget target,
    string context,
    TimeSpan timeout)
{
    try
    {
        AutomationElement host = WaitForElement(
            $"{context} real Markdown host",
            () => FindUsableMarkdownLifecycleHost(window, target),
            timeout);
        return new MarkdownLifecycleHostAcquisition(window, host);
    }
    catch (InvalidOperationException) when (!string.IsNullOrWhiteSpace(target.LauncherControlAutomationId))
    {
        // Compact editors are popup compositions. A native resize can
        // light-dismiss the popup after UIA has observed its old peer, so
        // reacquire it through the product launcher instead of accepting a
        // transient element or making the lifecycle matrix timing-dependent.
        PrepareRealMarkdownHost(window, target, context);
        AutomationElement host = WaitForElement(
            $"{context} reopened real Markdown host",
            () => FindUsableMarkdownLifecycleHost(window, target),
            TimeSpan.FromSeconds(20));
        return new MarkdownLifecycleHostAcquisition(window, host);
    }
}

static AutomationElement? FindUsableMarkdownLifecycleHost(
    AutomationElement root,
    MarkdownLifecycleTarget target)
{
    foreach (AutomationElement host in FindMarkdownLifecycleHosts(root, target))
    {
        if (!string.IsNullOrWhiteSpace(target.LauncherControlAutomationId) && !IsVisible(host))
        {
            continue;
        }

        try
        {
            var textPattern = host.Patterns.Text.PatternOrDefault;
            if (textPattern is not null &&
                textPattern.DocumentRange.GetText(-1).Contains(
                    "Lifecycle long document final marker",
                    StringComparison.Ordinal))
            {
                return host;
            }
        }
        catch (COMException)
        {
            // WinUI can leave a recycled UIA peer in the tree briefly. Keep
            // looking for the live peer with the same deterministic ID.
        }
    }

    return null;
}

static AutomationElement? FindVisibleByAutomationIdWithComRetry(
    AutomationElement root,
    string automationId,
    TimeSpan timeout)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    do
    {
        try
        {
            return root.FindAllDescendants(cf => cf.ByAutomationId(automationId))
                .FirstOrDefault(IsVisible);
        }
        catch (COMException)
        {
            Thread.Sleep(100);
        }
    }
    while (stopwatch.Elapsed < timeout);

    return null;
}

static void RecordMarkdownRelayoutMetrics(
    MarkdownLifecycleCaseResult result,
    MarkdownRelayoutMetrics metrics)
{
    result.RepeatedRelayout = metrics.Cycles >= 6;
    result.RelayoutCycles = metrics.Cycles;
    result.RelayoutBaselinePrivateBytes = metrics.BaselinePrivateBytes;
    result.RelayoutRetainedPrivateBytes = metrics.RetainedPrivateBytes;
    result.RetainedMemoryGrowthBytes = metrics.RetainedGrowthBytes;
    result.RetainedMemoryBudgetBytes = metrics.RetainedGrowthBudgetBytes;
    result.RetainedMemoryBudget = metrics.RetainedGrowthBytes <= metrics.RetainedGrowthBudgetBytes;
}

static void AssertAndRecordMarkdownMemoryBudget(
    Process process,
    MarkdownLifecycleCaseResult result,
    string caseId)
{
    const long memoryBudgetBytes = 768L * 1024 * 1024;
    process.Refresh();
    long peakWorkingSetBytes = process.PeakWorkingSet64;
    result.PeakWorkingSetBytes = peakWorkingSetBytes;
    result.MemoryBudgetBytes = memoryBudgetBytes;
    result.MemoryBudget = peakWorkingSetBytes is > 0 and <= memoryBudgetBytes;
    AssertProbe(
        result.MemoryBudget,
        $"{caseId}: peak working set {peakWorkingSetBytes / (1024 * 1024):N0} MiB exceeded " +
        $"the {memoryBudgetBytes / (1024 * 1024):N0} MiB Markdown lifecycle budget.");
}

static void AssertMarkdownLifecycleCloseState(
    Window window,
    MarkdownLifecycleTarget target,
    string caseId)
{
    AutomationElement FindCurrentHost() => WaitForElement(
        $"{caseId} active close host",
        () => window.FindAllDescendants().FirstOrDefault(element =>
        {
            string automationId = GetAutomationId(element);
            return target.PrefixMatch
                ? automationId.StartsWith(target.HostAutomationId, StringComparison.Ordinal)
                : string.Equals(automationId, target.HostAutomationId, StringComparison.Ordinal);
        }),
        TimeSpan.FromSeconds(6));

    AutomationElement host = FindCurrentHost();
    var textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{caseId}: close-state host lost TextPattern.");
    string selectedText = string.Concat(textPattern.GetSelection().Select(range => range.GetText(-1)));
    AssertProbe(
        HasCrossLineMarkdownSelection(selectedText),
        $"{caseId}: selection was not active immediately before window close.");

    var inlineSvgRange = textPattern.DocumentRange.FindText(
        "Lifecycle inline SVG",
        backward: false,
        ignoreCase: false)
        ?? throw new InvalidOperationException($"{caseId}: inline SVG range was unavailable before close.");
    inlineSvgRange.ScrollIntoView(alignToTop: false);
    _ = WaitForElement(
        $"{caseId} active inline SVG",
        () => FindCurrentHost().FindAllDescendants(cf => cf.ByControlType(ControlType.Image))
            .FirstOrDefault(element =>
                IsVisible(element) &&
                (element.Name ?? string.Empty).Contains("Lifecycle inline SVG", StringComparison.Ordinal)),
        TimeSpan.FromSeconds(6));

    AutomationElement keyboardLink = FindCurrentHost()
        .FindAllDescendants(cf => cf.ByControlType(ControlType.Hyperlink))
        .FirstOrDefault(link => (link.Name ?? string.Empty).Contains("keyboard link", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"{caseId}: keyboard link disappeared before close.");
    keyboardLink.FocusNative();
    WaitUntil(
        $"{caseId} active keyboard link before close",
        () => IsElementFocused(keyboardLink),
        TimeSpan.FromSeconds(3));
}

static void RunForcedResourceMapAbsentLifecycleCase(
    CaptureOptions options,
    string configuration,
    MarkdownLifecycleManifest manifest,
    string manifestPath,
    List<string> failures)
{
    var target = new MarkdownLifecycleTarget(
        "resource-map-absent",
        "repo-issues",
        "MarkdownHost_Conversation_RepoIssuesBody",
        false);
    Rectangle workArea = NativeMethods.GetWorkArea();
    var viewport = new MarkdownLifecycleViewport(
        "wide",
        workArea.Width > 0 ? Math.Min(1366, workArea.Width) : 1366,
        workArea.Height > 0 ? Math.Min(900, workArea.Height) : 900);
    MarkdownLifecycleRunPaths paths = CreateMarkdownLifecycleRunPaths(
        options.OutputDirectory,
        $"{configuration}-resource-map-absent".ToLowerInvariant());
    MarkdownLifecycleApplication? lifecycle = null;
    try
    {
        ResetMarkdownLifecycleExceptionLogs();
        WriteMarkdownLifecycleRuntimeSettings(paths.RuntimeSettingsPath, 1, 1);
        lifecycle = LaunchMarkdownLifecycleApplication(options, target, "dark", paths, forceResourceMapAbsent: true);
        using var automation = new UIA3Automation();
        Window window = GetReadyWindow(lifecycle.Application, automation, $"{configuration} forced resource-map absence");
        ResizeWindow(window, viewport.Width, viewport.Height, reactivate: false);
        PrepareRealMarkdownHost(window, target, $"{configuration.ToLowerInvariant()}-resource-map-absent");
        WaitForMarkdownLifecycleSignal(paths.HostReadyPath, target.HostAutomationId, false, TimeSpan.FromSeconds(30));
        WaitForResourceMapFallbackEvidence(paths.ResourceMapEvidencePath, lifecycle.Process.Id, TimeSpan.FromSeconds(10));
        MarkdownRelayoutMetrics relayout = RunRepeatedMarkdownRelayout(
            ref window,
            lifecycle.Application,
            automation,
            lifecycle.Process,
                target,
                viewport,
                $"{configuration.ToLowerInvariant()}-resource-map-absent",
                paths.RuntimeSettingsPath,
                1);
        MarkdownLifecycleCaseResult state = ExerciseMarkdownLifecycleState(
            window,
            target,
            "resource-map-absent",
            configuration,
            1,
            viewport,
            $"{configuration.ToLowerInvariant()}-resource-map-absent",
            options.OutputDirectory,
            paths.LinkEvidencePath);
        RecordMarkdownRelayoutMetrics(state, relayout);
        AssertAndRecordMarkdownMemoryBudget(
            lifecycle.Process,
            state,
            $"{configuration.ToLowerInvariant()}-resource-map-absent");
        AssertMarkdownLifecycleCloseState(
            window,
            target,
            $"{configuration.ToLowerInvariant()}-resource-map-absent");
        window.Close();
        bool exited = lifecycle.Process.WaitForExit(12_000);
        int? exitCode = exited ? checked((int)lifecycle.GetExitCode()) : null;
        AssertProbe(exited && exitCode == 0, "Forced resource-map-absent app did not close cleanly.");
        AssertNoMarkdownLifecycleExceptionLogs($"{configuration} forced resource-map absence");
        state.CleanClose = true;
        state.HostUnloadOnClose = true;
        state.ExitCode = exitCode;
        state.UnhandledLogCount = 0;
        manifest.ResourceMapAbsentCase = state;
    }
    catch (Exception exception)
    {
        failures.Add($"{configuration}-resource-map-absent: {exception.Message}");
        manifest.ResourceMapAbsentCase = MarkdownLifecycleCaseResult.Failed(
            configuration,
            target.Name,
            "resource-map-absent",
            1,
            viewport,
            $"{configuration.ToLowerInvariant()}-resource-map-absent",
            exception.ToString());
    }
    finally
    {
        if (lifecycle is not null && !lifecycle.Process.HasExited)
        {
            try { lifecycle.Process.Kill(entireProcessTree: true); } catch { }
        }
        lifecycle?.Dispose();
        PersistMarkdownLifecycleManifest(manifestPath, manifest);
    }
}

static void RunMarkdownSecurityPolicyLifecycleCase(
    CaptureOptions options,
    string configuration,
    MarkdownLifecycleManifest manifest,
    string manifestPath,
    List<string> failures)
{
    var target = new MarkdownLifecycleTarget(
        "security-policy",
        "repo-issues",
        "MarkdownHost_Conversation_RepoIssuesBody",
        false);
    var viewport = new MarkdownLifecycleViewport("snapped", 760, 650);
    string caseId = $"{configuration.ToLowerInvariant()}-security-policy";
    MarkdownLifecycleRunPaths paths = CreateMarkdownLifecycleRunPaths(options.OutputDirectory, caseId);
    MarkdownLifecycleApplication? lifecycle = null;
    try
    {
        ResetMarkdownLifecycleExceptionLogs();
        WriteMarkdownLifecycleRuntimeSettings(paths.RuntimeSettingsPath, 1.5, 1);
        lifecycle = LaunchMarkdownLifecycleApplication(
            options,
            target,
            "highcontrast",
            paths,
            forceResourceMapAbsent: false,
            securityFixture: true);
        using var automation = new UIA3Automation();
        Window window = GetReadyWindow(lifecycle.Application, automation, $"{configuration} Markdown security policy");
        ResizeWindow(window, viewport.Width, viewport.Height, reactivate: false);
        PrepareRealMarkdownHost(window, target, caseId);
        WaitForMarkdownLifecycleSignal(paths.HostReadyPath, target.HostAutomationId, false, TimeSpan.FromSeconds(30));
        MarkdownRelayoutMetrics relayout = RunRepeatedMarkdownRelayout(
            ref window,
            lifecycle.Application,
            automation,
            lifecycle.Process,
                target,
                viewport,
                caseId,
                paths.RuntimeSettingsPath,
                1.5);
        MarkdownLifecycleCaseResult state = ExerciseMarkdownLifecycleState(
            window,
            target,
            "security-policy",
            configuration,
            1.5,
            viewport,
            caseId,
            options.OutputDirectory,
            paths.LinkEvidencePath);
        RecordMarkdownRelayoutMetrics(state, relayout);
        AssertMarkdownSecurityPolicyState(window, target, caseId, state);
        AssertAndRecordMarkdownMemoryBudget(lifecycle.Process, state, caseId);
        AssertMarkdownLifecycleCloseState(window, target, caseId);
        window.Close();
        bool exited = lifecycle.Process.WaitForExit(12_000);
        int? exitCode = exited ? checked((int)lifecycle.GetExitCode()) : null;
        AssertProbe(exited && exitCode == 0, "Markdown security-policy app did not close cleanly.");
        AssertNoMarkdownLifecycleExceptionLogs($"{configuration} Markdown security policy");
        state.CleanClose = true;
        state.HostUnloadOnClose = true;
        state.ExitCode = exitCode;
        state.UnhandledLogCount = 0;
        manifest.SecurityPolicyCase = state;
    }
    catch (Exception exception)
    {
        failures.Add($"{caseId}: {exception.Message}");
        manifest.SecurityPolicyCase = MarkdownLifecycleCaseResult.Failed(
            configuration,
            target.Name,
            "security-policy",
            1.5,
            viewport,
            caseId,
            exception.ToString());
    }
    finally
    {
        if (lifecycle is not null && !lifecycle.Process.HasExited)
        {
            try { lifecycle.Process.Kill(entireProcessTree: true); } catch { }
        }
        lifecycle?.Dispose();
        PersistMarkdownLifecycleManifest(manifestPath, manifest);
    }
}

static void AssertMarkdownSecurityPolicyState(
    Window window,
    MarkdownLifecycleTarget target,
    string caseId,
    MarkdownLifecycleCaseResult state)
{
    AutomationElement host = WaitForElement(
        $"{caseId} host",
        () => window.FindAllDescendants().FirstOrDefault(element =>
            string.Equals(GetAutomationId(element), target.HostAutomationId, StringComparison.Ordinal)),
        TimeSpan.FromSeconds(8));
    var textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{caseId}: security fixture did not expose TextPattern.");
    string text = textPattern.DocumentRange.GetText(-1);
    string[] requiredMarkers =
    [
        "Lifecycle hostile font SVG",
        "Lifecycle hostile depth SVG",
        "Lifecycle oversized SVG",
        "Lifecycle insecure remote image",
        "Lifecycle redirect-policy remote image",
        "Lifecycle security fixture final marker",
    ];
    foreach (string marker in requiredMarkers)
    {
        AssertProbe(text.Contains(marker, StringComparison.Ordinal), $"{caseId}: missing security marker '{marker}'.");
    }

    state.HostileSvgBudget = true;
    state.OversizedSvgBudget = true;
    state.RedirectPolicyFixture = true;
    state.RemoteImagePolicy = true;
}

static void WaitForResourceMapFallbackEvidence(string path, int expectedProcessId, TimeSpan timeout)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    while (stopwatch.Elapsed < timeout)
    {
        try
        {
            if (File.Exists(path))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                int processId = document.RootElement.GetProperty("ProcessId").GetInt32();
                string fallback = document.RootElement.GetProperty("Fallback").GetString() ?? string.Empty;
                if (processId == expectedProcessId &&
                    string.Equals(fallback, "Resource map fallback active", StringComparison.Ordinal))
                {
                    return;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        Thread.Sleep(50);
    }

    throw new InvalidOperationException($"Forced resource-map fallback evidence was not written to '{path}'.");
}

static string InferConfiguration(string appPath) =>
    appPath.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        ? "Release"
        : "Debug";

static string ComputeFileSha256(string path)
{
    using FileStream stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static void PersistMarkdownLifecycleManifest(string path, MarkdownLifecycleManifest manifest)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string temporaryPath = path + $".{Environment.ProcessId}.tmp";
    string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    });
    File.WriteAllText(temporaryPath, json);

    const int maximumAttempts = 9;
    try
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maximumAttempts)
            {
                Thread.Sleep(attempt * 25);
            }
            catch (UnauthorizedAccessException) when (attempt < maximumAttempts)
            {
                // Antivirus and indexers can briefly hold the previous manifest after
                // an atomic replace. Keep the per-case evidence write bounded while
                // still surfacing persistent access failures to the runner.
                Thread.Sleep(attempt * 25);
            }
        }
    }
    finally
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

static MarkdownLifecycleManifest LoadMarkdownLifecycleManifest(string path) =>
    JsonSerializer.Deserialize<MarkdownLifecycleManifest>(
        File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException($"Markdown lifecycle manifest '{path}' was empty or invalid.");

static void ValidateResumedMarkdownLifecycleManifest(
    MarkdownLifecycleManifest manifest,
    string configuration,
    string appSha256,
    string appAssemblySha256,
    string automationAssemblySha256,
    string runScope,
    string? requestedTarget,
    bool requiresSupplementalCases,
    IReadOnlyList<string> hosts,
    int expectedCaseCount,
    IReadOnlyList<string> themes,
    IReadOnlyList<double> textScales,
    IReadOnlyList<MarkdownLifecycleViewport> viewports)
{
    string[] expectedThemes = themes.ToArray();
    int[] expectedScales = textScales.Select(scale => (int)Math.Round(scale * 100)).ToArray();
    string[] expectedViewports = viewports
        .Select(viewport => $"{viewport.Name}:{viewport.Width}x{viewport.Height}")
        .ToArray();
    if (manifest.Version != 5 ||
        !string.Equals(manifest.RunScope, runScope, StringComparison.Ordinal) ||
        !string.Equals(manifest.RequestedTarget, requestedTarget, StringComparison.OrdinalIgnoreCase) ||
        manifest.RequiresSupplementalCases != requiresSupplementalCases ||
        !string.Equals(manifest.Configuration, configuration, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(manifest.AppSha256, appSha256, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(manifest.AppAssemblySha256, appAssemblySha256, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(manifest.AutomationAssemblySha256, automationAssemblySha256, StringComparison.OrdinalIgnoreCase) ||
        manifest.ExpectedHostCount != hosts.Count ||
        manifest.ExpectedCaseCount != expectedCaseCount ||
        !manifest.Hosts.SequenceEqual(hosts, StringComparer.OrdinalIgnoreCase) ||
        !manifest.Themes.SequenceEqual(expectedThemes, StringComparer.OrdinalIgnoreCase) ||
        !manifest.TextScalePercents.SequenceEqual(expectedScales) ||
        !manifest.Viewports.SequenceEqual(expectedViewports, StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "The existing Markdown lifecycle manifest does not match the current binary or matrix. " +
            "Use a new output directory rather than combining incompatible evidence.");
    }
}

static void ResetMarkdownLifecycleExceptionLogs()
{
    string logDirectory = Path.Combine(GetAutomationDataRoot(), "Local", "logs");
    if (!Directory.Exists(logDirectory))
    {
        return;
    }

    foreach (string path in Directory.EnumerateFiles(logDirectory, "*-unhandled.log"))
    {
        File.Delete(path);
    }
}

static int CountMarkdownLifecycleExceptionLogs()
{
    string logDirectory = Path.Combine(GetAutomationDataRoot(), "Local", "logs");
    return Directory.Exists(logDirectory)
        ? Directory.EnumerateFiles(logDirectory, "*-unhandled.log")
            .Count(path => new FileInfo(path).Length > 0)
        : 0;
}

static void CopyMarkdownLifecycleExceptionLogs(string outputDirectory, string caseId)
{
    string sourceDirectory = Path.Combine(GetAutomationDataRoot(), "Local", "logs");
    if (!Directory.Exists(sourceDirectory))
    {
        return;
    }

    string destinationDirectory = Path.Combine(outputDirectory, "failure-logs", caseId);
    Directory.CreateDirectory(destinationDirectory);
    foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectory, "*-unhandled.log"))
    {
        if (new FileInfo(sourcePath).Length == 0)
        {
            continue;
        }

        File.Copy(
            sourcePath,
            Path.Combine(destinationDirectory, Path.GetFileName(sourcePath)),
            overwrite: true);
    }
}

static void AssertNoMarkdownLifecycleExceptionLogs(string hostName)
{
    string logDirectory = Path.Combine(GetAutomationDataRoot(), "Local", "logs");
    if (!Directory.Exists(logDirectory))
    {
        return;
    }

    string[] logs = Directory.EnumerateFiles(logDirectory, "*-unhandled.log")
        .Where(path => new FileInfo(path).Length > 0)
        .ToArray();
    if (logs.Length == 0)
    {
        return;
    }

    string details = string.Join(
        Environment.NewLine,
        logs.Select(path => $"{Path.GetFileName(path)}:{Environment.NewLine}{File.ReadAllText(path)}"));
    throw new InvalidOperationException(
        $"{hostName}: unhandled exception log was written while closing a live Markdown host.{Environment.NewLine}{details}");
}

static Rectangle FindVisibleMarkdownRangeRect(
    AutomationElement host,
    ITextRange range,
    string description,
    bool preferLastRectangle = false,
    bool searchFromBottom = false,
    int minimumVisibleHeight = 8)
{
    string rangeText = range.GetText(-1);
    Rectangle lastViewportBounds = Rectangle.Empty;
    Rectangle lastRangeBounds = Rectangle.Empty;
    string lastRangeRectangles = string.Empty;
    const int fineSearchAttempts = 24;
    const int totalSearchAttempts = 64;
    for (int attempt = 0; attempt < totalSearchAttempts; attempt++)
    {
        Rectangle viewportBounds = GetClippedAutomationBounds(host);
        Rectangle[] rangeBounds = range.GetBoundingRectangles();
        IEnumerable<Rectangle> visibleRangeBounds = rangeBounds
            .Where(rect => rect.Width > 1 && rect.Height > 1 && viewportBounds.IntersectsWith(rect));
        Rectangle rectangle = preferLastRectangle
            ? visibleRangeBounds.LastOrDefault()
            : visibleRangeBounds.FirstOrDefault();
        Rectangle visibleRectangle = Rectangle.Intersect(rectangle, viewportBounds);
        if (visibleRectangle.Width > 8 && visibleRectangle.Height >= minimumVisibleHeight)
        {
            return visibleRectangle;
        }

        Rectangle[] measurableRangeBounds = rangeBounds
            .Where(rect => rect.Width > 1 && rect.Height > 1)
            .ToArray();
        Rectangle rangeBoundsUnion = measurableRangeBounds.Length == 0
            ? Rectangle.Empty
            : measurableRangeBounds.Aggregate(Rectangle.Union);
        lastViewportBounds = viewportBounds;
        lastRangeBounds = rangeBoundsUnion;
        lastRangeRectangles = string.Join(", ", rangeBounds.Select(rect => rect.ToString()));
        if (!rangeBoundsUnion.IsEmpty)
        {
            bool targetBelow = rangeBoundsUnion.Bottom >=
                viewportBounds.Bottom - Math.Max(2, minimumVisibleHeight / 2);
            double verticalGap = targetBelow
                ? rangeBoundsUnion.Top - viewportBounds.Bottom
                : viewportBounds.Top - rangeBoundsUnion.Bottom;
            ScrollAmount amount = verticalGap > Math.Max(24, viewportBounds.Height / 2d)
                ? targetBelow ? ScrollAmount.LargeIncrement : ScrollAmount.LargeDecrement
                : targetBelow ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement;
            ScrollNearestVerticalAncestor(
                host,
                amount);
        }
        else
        {
            double searchPercent = attempt < fineSearchAttempts
                ? attempt
                : fineSearchAttempts +
                    ((attempt - fineSearchAttempts) * (100d - fineSearchAttempts) /
                     (totalSearchAttempts - fineSearchAttempts - 1));
            SetNearestVerticalAncestorScrollPercent(
                host,
                searchFromBottom ? 100 - searchPercent : searchPercent);
        }

        range.ScrollIntoView(alignToTop: true);
        Thread.Sleep(200);
        try
        {
            var currentTextPattern = host.Patterns.Text.PatternOrDefault;
            range = currentTextPattern?.DocumentRange.FindText(
                    rangeText,
                    backward: false,
                    ignoreCase: false)
                ?? range;
        }
        catch (COMException)
        {
        }
    }

    throw new InvalidOperationException(
        $"{description}: TextPattern range had no visible glyph bounds after realization retries. " +
        $"Viewport={lastViewportBounds}; Range={lastRangeBounds}; Rectangles=[{lastRangeRectangles}]. " +
        $"Ancestors=[{DescribeScrollAncestors(host)}].");
}

static string DescribeScrollAncestors(AutomationElement element)
{
    var descriptions = new List<string>();
    ITreeWalker walker = element.Automation.TreeWalkerFactory.GetControlViewWalker();
    AutomationElement? candidate = TryGetAutomationParent(walker, element);
    for (int depth = 0; candidate is not null && depth < 16; depth++)
    {
        string scroll = "scroll=none";
        try
        {
            if (candidate.Patterns.Scroll.IsSupported)
            {
                var pattern = candidate.Patterns.Scroll.Pattern;
                scroll = $"scroll=v:{pattern.VerticallyScrollable.ValueOrDefault}," +
                    $"p:{pattern.VerticalScrollPercent.ValueOrDefault:0.#}," +
                    $"view:{pattern.VerticalViewSize.ValueOrDefault:0.#}";
            }
        }
        catch (COMException)
        {
            scroll = "scroll=stale";
        }

        try
        {
            descriptions.Add(
                $"{depth}:{candidate.ControlType}/{GetAutomationId(candidate)}/{candidate.BoundingRectangle}/{scroll}");
        }
        catch (COMException)
        {
            descriptions.Add($"{depth}:stale/{scroll}");
            break;
        }

        candidate = TryGetAutomationParent(walker, candidate);
    }

    return string.Join(" | ", descriptions);
}

static bool TryGetVisibleMarkdownRangeRect(
    AutomationElement host,
    ITextRange range,
    bool preferLastRectangle,
    out Rectangle visibleRectangle)
{
    Rectangle viewportBounds = GetClippedAutomationBounds(host);
    Rectangle[] visibleRangeBounds = range.GetBoundingRectangles()
        .Where(rect => rect.Width > 1 && rect.Height > 1 && viewportBounds.IntersectsWith(rect))
        .ToArray();
    Rectangle rectangle = preferLastRectangle
        ? visibleRangeBounds.LastOrDefault()
        : visibleRangeBounds.FirstOrDefault();
    visibleRectangle = Rectangle.Intersect(rectangle, viewportBounds);
    return visibleRectangle.Width > 8 && visibleRectangle.Height >= Math.Min(8, rectangle.Height);
}

static Rectangle GetClippedAutomationBounds(AutomationElement element)
{
    Rectangle clippedBounds = element.BoundingRectangle;
    ITreeWalker walker = element.Automation.TreeWalkerFactory.GetControlViewWalker();
    AutomationElement? candidate = TryGetAutomationParent(walker, element);
    for (int depth = 0; candidate is not null && depth < 16 && !clippedBounds.IsEmpty; depth++)
    {
        Rectangle parentBounds;
        try
        {
            parentBounds = candidate.BoundingRectangle;
        }
        catch (COMException)
        {
            break;
        }

        if (parentBounds.Width > 1 && parentBounds.Height > 1)
        {
            clippedBounds = Rectangle.Intersect(clippedBounds, parentBounds);
        }

        candidate = TryGetAutomationParent(walker, candidate);
    }

    return clippedBounds;
}

static void ScrollNearestVerticalAncestor(AutomationElement element, ScrollAmount amount)
{
    ITreeWalker walker = element.Automation.TreeWalkerFactory.GetControlViewWalker();
    AutomationElement? candidate = TryGetAutomationParent(walker, element);
    for (int depth = 0; candidate is not null && depth < 16; depth++)
    {
        try
        {
            if (candidate.Patterns.Scroll.IsSupported)
            {
                var scroll = candidate.Patterns.Scroll.Pattern;
                if (scroll.VerticallyScrollable.ValueOrDefault)
                {
                    scroll.Scroll(ScrollAmount.NoAmount, amount);
                    return;
                }
            }
        }
        catch (COMException)
        {
        }

        candidate = TryGetAutomationParent(walker, candidate);
    }
}

static void SetNearestVerticalAncestorScrollPercent(AutomationElement element, double verticalPercent)
{
    ITreeWalker walker = element.Automation.TreeWalkerFactory.GetControlViewWalker();
    AutomationElement? candidate = TryGetAutomationParent(walker, element);
    for (int depth = 0; candidate is not null && depth < 16; depth++)
    {
        try
        {
            if (candidate.Patterns.Scroll.IsSupported)
            {
                var scroll = candidate.Patterns.Scroll.Pattern;
                if (scroll.VerticallyScrollable.ValueOrDefault)
                {
                    scroll.SetScrollPercent(
                        FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll,
                        Math.Clamp(verticalPercent, 0, 100));
                    return;
                }
            }
        }
        catch (COMException)
        {
        }

        candidate = TryGetAutomationParent(walker, candidate);
    }
}


static AutomationElement? TryGetAutomationParent(ITreeWalker walker, AutomationElement element)
{
    try
    {
        return walker.GetParent(element);
    }
    catch (COMException)
    {
        return null;
    }
}

static void RunSearchContextProbe(CaptureOptions options)
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

static void RunThemeSwitchProbe(CaptureOptions options)
{
    using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
        ? LaunchApplication(
            options.AppPath,
            "--page=settings",
            "--theme=light",
            "--palette=visual-studio-code")
        : CreateProbeApplication(options);
    using var automation = new UIA3Automation();

    Window window = GetReadyWindow(app, automation, "theme-switch probe");

    AutomationElement lightCard = WaitForElement(
        "SettingsThemeLight",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsThemeLight")),
        TimeSpan.FromSeconds(10));
    AutomationElement darkCard = WaitForElement(
        "SettingsThemeDark",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsThemeDark")),
        TimeSpan.FromSeconds(10));

    string beforePath = Path.Combine(options.OutputDirectory, "probe-theme-before.png");
    CaptureWindow(window, beforePath);

    SelectAutomationItem(darkCard, "dark theme card");
    WaitUntil(
        "dark theme card selected",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsThemeDark")) is { } currentDark &&
            currentDark.Patterns.SelectionItem.IsSupported &&
            currentDark.Patterns.SelectionItem.Pattern.IsSelected.Value,
        TimeSpan.FromSeconds(5));
    Thread.Sleep(1200);

    string afterPath = Path.Combine(options.OutputDirectory, "probe-theme-after.png");
    CaptureWindow(window, afterPath);

    SelectAutomationItem(lightCard, "light theme card");
    WaitUntil(
        "light theme card selected again",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsThemeLight")) is { } currentLight &&
            currentLight.Patterns.SelectionItem.IsSupported &&
            currentLight.Patterns.SelectionItem.Pattern.IsSelected.Value,
        TimeSpan.FromSeconds(5));
    Thread.Sleep(300);
    string restoredPath = Path.Combine(options.OutputDirectory, "probe-theme-restored-light.png");
    CaptureWindow(window, restoredPath);

    AssertLiveThemeAppearanceRepaint(beforePath, afterPath, restoredPath);

    Console.WriteLine($"theme-switch probe: before={beforePath}, dark={afterPath}, restored={restoredPath}");
    if (string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        TryClose(app);
    }
}

static void AssertLiveThemeAppearanceRepaint(string lightPath, string darkPath, string restoredLightPath)
{
    using var light = new Bitmap(lightPath);
    using var dark = new Bitmap(darkPath);
    using var restoredLight = new Bitmap(restoredLightPath);
    AssertProbe(
        light.Size == dark.Size && dark.Size == restoredLight.Size,
        "Live appearance repaint captures must use identical window dimensions.");

    double lightDarkDifference = AverageRgbDistance(
        AverageThemePaletteChrome(light),
        AverageThemePaletteChrome(dark));
    double restoredDifference = AverageRgbDistance(
        AverageThemePaletteChrome(light),
        AverageThemePaletteChrome(restoredLight));
    int minimumAccentPixels = Math.Max(12, (int)Math.Round(light.Width / 100d));
    int lightAccentPixels = CountThemePalettePixelsNearAnySurface(light, Color.FromArgb(0x00, 0x5F, 0xB8));
    int darkAccentPixels = CountThemePalettePixelsNearAnySurface(dark, Color.FromArgb(0x00, 0x78, 0xD4));
    int restoredAccentPixels = CountThemePalettePixelsNearAnySurface(restoredLight, Color.FromArgb(0x00, 0x5F, 0xB8));

    AssertProbe(
        lightDarkDifference >= 60,
        $"Live Light-to-Dark switching did not repaint existing chrome (RGB delta {lightDarkDifference:F2}).");
    AssertProbe(
        restoredDifference <= 3,
        $"The restored Light chrome differs from its initial frame (RGB delta {restoredDifference:F2}).");
    AssertProbe(
        lightAccentPixels >= minimumAccentPixels && restoredAccentPixels >= minimumAccentPixels,
        $"The Visual Studio Code Light accent did not survive the live appearance round trip " +
        $"({lightAccentPixels}/{restoredAccentPixels} pixels; expected at least {minimumAccentPixels}).");
    AssertProbe(
        darkAccentPixels >= minimumAccentPixels,
        $"The Visual Studio Code Dark accent did not repaint during the live appearance switch " +
        $"({darkAccentPixels} pixels; expected at least {minimumAccentPixels}).");
}

static void RunThemePaletteProbe(CaptureOptions options)
{
    AssertProbe(
        string.IsNullOrWhiteSpace(options.AttachProcess),
        "theme-palettes requires app ownership so it can verify persistence across a clean restart.");
    (string Id, string Name)[] palettes =
    [
        ("jithub", "JitHub (default)"),
        ("windows-11", "Windows 11"),
        ("visual-studio-code", "Visual Studio Code"),
        ("github", "GitHub"),
        ("solarized", "Solarized")
    ];
    string dataRoot = GetAutomationDataRoot();
    string initialPath = Path.Combine(options.OutputDirectory, "theme-palette-live-jithub-dark.png");
    string livePath = Path.Combine(options.OutputDirectory, "theme-palette-live-visual-studio-code-dark.png");
    string persistedPath = Path.Combine(options.OutputDirectory, "theme-palette-persisted-visual-studio-code-dark.png");

    KillExistingApplicationInstances(options.AppPath);
    using (var app = LaunchApplication(
        options.AppPath,
        "--page=settings",
        "--theme=dark",
        "--palette=jithub"))
    using (var automation = new UIA3Automation())
    {
        try
        {
            Window window = GetReadyWindow(app, automation, "theme-palettes live switch");
            ResizeLogicalWindow(window, 1180, 700);
            AssertSettingsPaletteCardSemantics(window, palettes);
            AssertThemePaletteKeyboardNavigation(window, dataRoot);
            Thread.Sleep(200);
            CaptureWindow(window, initialPath);

            AutomationElement visualStudioCode = WaitForElement(
                "SettingsPalette_visual-studio-code",
                () => window.FindFirstDescendant(
                    cf => cf.ByAutomationId("SettingsPalette_visual-studio-code")),
                TimeSpan.FromSeconds(10));
            RevealForInteraction(visualStudioCode, "Visual Studio Code palette");
            SelectAutomationItem(visualStudioCode, "Visual Studio Code palette");
            WaitUntil(
                "Visual Studio Code palette native selection",
                () => window.FindFirstDescendant(
                        cf => cf.ByAutomationId("SettingsPalette_visual-studio-code")) is { } current &&
                    current.Patterns.SelectionItem.IsSupported &&
                    current.Patterns.SelectionItem.Pattern.IsSelected.Value,
                TimeSpan.FromSeconds(5));
            WaitUntil(
                "Visual Studio Code palette persistence",
                () => string.Equals(
                    ReadAuthSetting(dataRoot, "APPLICATION_PALETTE_KEY"),
                    "visual-studio-code",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            Thread.Sleep(350);
            CaptureWindow(window, livePath);

            ResizeLogicalWindow(window, 640, 650);
            AutomationElement narrowSelection = WaitForElement(
                "Visual Studio Code palette at narrow width",
                () => window.FindFirstDescendant(
                    cf => cf.ByAutomationId("SettingsPalette_visual-studio-code")),
                TimeSpan.FromSeconds(5));
            RevealForInteraction(narrowSelection, "Visual Studio Code palette at narrow width");
            AssertProbe(
                IsInsideWindowBounds(narrowSelection, window),
                "The selected palette card escaped the Settings viewport at 640x700.");
            CaptureWindow(
                window,
                Path.Combine(options.OutputDirectory, "theme-palette-live-visual-studio-code-dark-640x700.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    using (var app = LaunchApplication(options.AppPath, "--page=settings", "--theme=dark"))
    using (var automation = new UIA3Automation())
    {
        try
        {
            Window window = GetReadyWindow(app, automation, "theme-palettes persistence restart");
            ResizeLogicalWindow(window, 1180, 700);
            AutomationElement persisted = WaitForElement(
                "persisted Visual Studio Code palette",
                () => window.FindFirstDescendant(
                    cf => cf.ByAutomationId("SettingsPalette_visual-studio-code")),
                TimeSpan.FromSeconds(10));
            WaitUntil(
                "persisted Visual Studio Code palette selection",
                () => persisted.Patterns.SelectionItem.IsSupported &&
                    persisted.Patterns.SelectionItem.Pattern.IsSelected.Value,
                TimeSpan.FromSeconds(5));
            CaptureWindow(window, persistedPath);
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    AssertThemePaletteChromeRepaint(initialPath, livePath, persistedPath);

    foreach (string requestedTheme in options.Themes)
    {
        string theme = requestedTheme.Trim().ToLowerInvariant();
        AssertProbe(theme is "light" or "dark", "theme-palettes themes must be light or dark.");
        foreach ((string paletteId, string paletteName) in palettes)
        {
            using var app = LaunchApplication(
                options.AppPath,
                "--page=settings",
                $"--theme={theme}",
                $"--palette={paletteId}");
            using var automation = new UIA3Automation();
            try
            {
                Window window = GetReadyWindow(app, automation, $"theme-palettes {theme}/{paletteId}");
                ResizeLogicalWindow(window, 1180, 700);
                AutomationElement selected = WaitForElement(
                    $"SettingsPalette_{paletteId}",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId($"SettingsPalette_{paletteId}")),
                    TimeSpan.FromSeconds(10));
                RevealForInteraction(selected, $"{paletteName} palette");
                AssertProbe(
                    selected.Patterns.SelectionItem.IsSupported &&
                    selected.Patterns.SelectionItem.Pattern.IsSelected.Value,
                    $"{paletteName} was not selected for the {theme} startup matrix.");
                Thread.Sleep(250);
                CaptureWindow(
                    window,
                    Path.Combine(options.OutputDirectory, $"theme-palette-{paletteId}-{theme}.png"));
            }
            finally
            {
                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }
    }

    RunThemePaletteHomeMatrix(options, palettes);

    Console.WriteLine(
        $"theme-palettes probe: verified keyboard and live switching, visual repaint, restart persistence, " +
        $"narrow layout, and {palettes.Length * options.Themes.Count} Settings plus Home palette/theme combinations.");
}

static void AssertThemePaletteKeyboardNavigation(Window window, string dataRoot)
{
    AutomationElement jithub = WaitForElement(
        "JitHub palette for keyboard navigation",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsPalette_jithub")),
        TimeSpan.FromSeconds(5));
    AutomationElement windows11 = WaitForElement(
        "Windows 11 palette for keyboard navigation",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsPalette_windows-11")),
        TimeSpan.FromSeconds(5));

    RevealForInteraction(jithub, "JitHub palette for keyboard navigation");
    FocusForKeyboardActivation(window, jithub);
    PressKeyForWindow(window, VirtualKeyShort.RIGHT);
    WaitUntil(
        "Right Arrow selects Windows 11 palette",
        () => windows11.Patterns.SelectionItem.Pattern.IsSelected.Value &&
            string.Equals(
                ReadAuthSetting(dataRoot, "APPLICATION_PALETTE_KEY"),
                "windows-11",
                StringComparison.Ordinal),
        TimeSpan.FromSeconds(5));

    PressKeyForWindow(window, VirtualKeyShort.LEFT);
    WaitUntil(
        "Left Arrow restores JitHub palette",
        () => jithub.Patterns.SelectionItem.Pattern.IsSelected.Value &&
            string.Equals(
                ReadAuthSetting(dataRoot, "APPLICATION_PALETTE_KEY"),
                "jithub",
                StringComparison.Ordinal),
        TimeSpan.FromSeconds(5));
}

static void AssertThemePaletteChromeRepaint(string initialPath, string livePath, string persistedPath)
{
    using var initial = new Bitmap(initialPath);
    using var live = new Bitmap(livePath);
    using var persisted = new Bitmap(persistedPath);
    AssertProbe(
        initial.Size == live.Size && live.Size == persisted.Size,
        "Theme palette repaint captures must use identical window dimensions.");

    (double R, double G, double B) initialColor = AverageThemePaletteChrome(initial);
    (double R, double G, double B) liveColor = AverageThemePaletteChrome(live);
    (double R, double G, double B) persistedColor = AverageThemePaletteChrome(persisted);
    double liveChange = AverageRgbDistance(initialColor, liveColor);
    double restartDifference = AverageRgbDistance(liveColor, persistedColor);
    int changedAccentPixels = CountSignificantThemePaletteChromeChanges(initial, live);
    int minimumChangedAccentPixels = Math.Max(12, (int)Math.Round(initial.Width / 60d));
    int initialJitHubAccentPixels = CountThemePalettePixelsNear(initial, Color.FromArgb(0x77, 0xB5, 0x9A));
    int liveVisualStudioCodeAccentPixels = CountThemePalettePixelsNear(live, Color.FromArgb(0x00, 0x78, 0xD4));
    int persistedVisualStudioCodeAccentPixels = CountThemePalettePixelsNear(persisted, Color.FromArgb(0x00, 0x78, 0xD4));
    int minimumAccentPixels = Math.Max(12, (int)Math.Round(initial.Width / 100d));

    AssertProbe(
        liveChange >= 3.5,
        $"Live palette switching did not repaint existing chrome (RGB delta {liveChange:F2}).");
    AssertProbe(
        changedAccentPixels >= minimumChangedAccentPixels,
        $"Live palette switching did not repaint the existing navigation accent " +
        $"({changedAccentPixels} significant pixels; expected at least {minimumChangedAccentPixels}).");
    AssertProbe(
        initialJitHubAccentPixels >= minimumAccentPixels,
        $"The initial navigation indicator did not render the JitHub dark accent " +
        $"({initialJitHubAccentPixels} pixels; expected at least {minimumAccentPixels}).");
    AssertProbe(
        liveVisualStudioCodeAccentPixels >= minimumAccentPixels,
        $"The live navigation indicator did not render the Visual Studio Code dark accent " +
        $"({liveVisualStudioCodeAccentPixels} pixels; expected at least {minimumAccentPixels}).");
    AssertProbe(
        persistedVisualStudioCodeAccentPixels >= minimumAccentPixels,
        $"The persisted navigation indicator did not render the Visual Studio Code dark accent " +
        $"({persistedVisualStudioCodeAccentPixels} pixels; expected at least {minimumAccentPixels}).");
    AssertProbe(
        restartDifference <= 3,
        $"Live palette chrome differs from a clean-start frame (RGB delta {restartDifference:F2}).");
}

static (double R, double G, double B) AverageThemePaletteChrome(Bitmap bitmap)
{
    int left = (int)(bitmap.Width * 0.22);
    int right = (int)(bitmap.Width * 0.28);
    int top = (int)(bitmap.Height * 0.025);
    int bottom = (int)(bitmap.Height * 0.075);
    long red = 0;
    long green = 0;
    long blue = 0;
    long count = 0;

    for (int y = top; y < bottom; y += 3)
    {
        for (int x = left; x < right; x += 3)
        {
            Color color = bitmap.GetPixel(x, y);
            red += color.R;
            green += color.G;
            blue += color.B;
            count++;
        }
    }

    return (red / (double)count, green / (double)count, blue / (double)count);
}

static int CountSignificantThemePaletteChromeChanges(Bitmap initial, Bitmap live)
{
    int left = (int)(initial.Width * 0.04);
    int right = (int)(initial.Width * 0.2);
    int top = (int)(initial.Height * 0.2);
    int bottom = (int)(initial.Height * 0.38);
    int changedPixels = 0;

    for (int y = top; y < bottom; y++)
    {
        for (int x = left; x < right; x++)
        {
            Color before = initial.GetPixel(x, y);
            Color after = live.GetPixel(x, y);
            double distance =
                (Math.Abs(before.R - after.R) +
                 Math.Abs(before.G - after.G) +
                 Math.Abs(before.B - after.B)) / 3d;
            if (distance >= 18)
            {
                changedPixels++;
            }
        }
    }

    return changedPixels;
}

static int CountThemePalettePixelsNear(Bitmap bitmap, Color expected)
{
    int left = (int)(bitmap.Width * 0.04);
    int right = (int)(bitmap.Width * 0.22);
    int top = (int)(bitmap.Height * 0.2);
    int bottom = (int)(bitmap.Height * 0.38);
    int matchingPixels = 0;

    for (int y = top; y < bottom; y++)
    {
        for (int x = left; x < right; x++)
        {
            Color actual = bitmap.GetPixel(x, y);
            if (Math.Abs(actual.R - expected.R) <= 10 &&
                Math.Abs(actual.G - expected.G) <= 10 &&
                Math.Abs(actual.B - expected.B) <= 10)
            {
                matchingPixels++;
            }
        }
    }

    return matchingPixels;
}

static int CountThemePalettePixelsNearAnySurface(Bitmap bitmap, Color expected)
{
    int matchingPixels = 0;
    for (int y = 0; y < bitmap.Height; y++)
    {
        for (int x = 0; x < bitmap.Width; x++)
        {
            Color actual = bitmap.GetPixel(x, y);
            if (Math.Abs(actual.R - expected.R) <= 10 &&
                Math.Abs(actual.G - expected.G) <= 10 &&
                Math.Abs(actual.B - expected.B) <= 10)
            {
                matchingPixels++;
            }
        }
    }

    return matchingPixels;
}

static double AverageRgbDistance(
    (double R, double G, double B) left,
    (double R, double G, double B) right) =>
    (Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B)) / 3d;

static void RunThemePaletteHomeMatrix(
    CaptureOptions options,
    IReadOnlyList<(string Id, string Name)> palettes)
{
    foreach (string requestedTheme in options.Themes)
    {
        string theme = requestedTheme.Trim().ToLowerInvariant();
        foreach ((string paletteId, _) in palettes)
        {
            using var app = LaunchApplication(
                options.AppPath,
                "--page=home",
                "--scenario=website-showcase",
                "--website-showcase",
                $"--theme={theme}",
                $"--palette={paletteId}");
            using var automation = new UIA3Automation();
            try
            {
                Window window = GetReadyWindow(app, automation, $"theme-palettes Home {theme}/{paletteId}");
                ResizeLogicalWindow(window, 1180, 700);
                WaitForElement(
                    $"Home widget board for {theme}/{paletteId}",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardWidgetBoard")),
                    TimeSpan.FromSeconds(12));
                WaitUntil(
                    $"Home palette surface ready for {theme}/{paletteId}",
                    () => IsVisible(window.FindFirstDescendant(
                        cf => cf.ByAutomationId("DashboardRepository_1"))),
                    TimeSpan.FromSeconds(12));
                Thread.Sleep(200);
                CaptureWindow(
                    window,
                    Path.Combine(options.OutputDirectory, $"theme-palette-{paletteId}-{theme}-home.png"));
            }
            finally
            {
                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }
    }
}

static void RunComboOpenProbe(CaptureOptions options)
{
    using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
        ? LaunchApplication(options.AppPath, "--page=design-lab", "--scenario=inputs", "--theme=dark")
        : CreateProbeApplication(options);
    using var automation = new UIA3Automation();

    Window window = GetReadyWindow(app, automation, "combo-open probe");
    var comboRetry = Retry.WhileNull(
        () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
            .FirstOrDefault(element => string.Equals(GetElementName(element), "Open", StringComparison.OrdinalIgnoreCase))
            ?? window.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox)),
        timeout: TimeSpan.FromSeconds(5),
        interval: TimeSpan.FromMilliseconds(100));
    if (!comboRetry.Success || comboRetry.Result is null)
    {
        throw new InvalidOperationException("Unable to find ComboBox for combo-open probe.");
    }

    AutomationElement combo = comboRetry.Result;
    Console.WriteLine($"combo-open probe target: name='{GetElementName(combo)}', bounds={combo.BoundingRectangle}");
    combo.FocusNative();
    Thread.Sleep(150);
    combo.AsComboBox().Expand();

    var openedItemRetry = Retry.WhileNull(
        () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Closed")),
        timeout: TimeSpan.FromSeconds(5),
        interval: TimeSpan.FromMilliseconds(100));
    bool expanded = openedItemRetry.Success && openedItemRetry.Result is not null && IsVisible(openedItemRetry.Result);

    string filePath = Path.Combine(options.OutputDirectory, "probe-combo-open.png");
    CaptureWindowWithPopups(window, filePath);

    Console.WriteLine($"combo-open probe: expanded={expanded}, screenshot={filePath}");

    if (!expanded)
    {
        throw new InvalidOperationException("ComboBox did not stay expanded for screenshot verification.");
    }

    if (string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        TryClose(app);
    }
}

static void RunSearchSelectDismissProbe(CaptureOptions options)
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

static void RunSearchFocusContractProbe(CaptureOptions options)
{
    string[] requirements =
    [
        "Startup leaves no visible or real keyboard focus inside the search text box.",
        "The Ctrl and K shortcut badge is visible while search is unfocused and does not cover the placeholder.",
        "Hovering empty titlebar space does not show a Ctrl+K tooltip.",
        "Ctrl+K focuses the real search text box, selects it as the active input, and hides the shortcut badge.",
        "Esc from search dismisses suggestions, removes the light ring state, and moves real keyboard focus away from search.",
        "Esc must not move visible focus to the titlebar menu button.",
        "Typing after Esc must not modify search text, proving the caret is gone from the text field."
    ];

    Console.WriteLine("search-focus-contract requirements:");
    foreach (string requirement in requirements)
    {
        Console.WriteLine($" - {requirement}");
    }

    using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
        ? LaunchApplication(options.AppPath, "--page=shell", "--scenario=search-suggestions", "--theme=dark")
        : CreateProbeApplication(options);
    using var automation = new UIA3Automation();

    try
    {
        Window window = GetReadyWindow(app, automation, "search-focus-contract probe");
        AutomationElement searchBox = WaitForElement(
            "ShellSearchTextBox",
            () => FindShellSearchTextBox(window),
            TimeSpan.FromSeconds(10));
        AutomationElement? searchBadge = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchShortcutBadge"));
        AutomationElement? shellMenuButton = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellMenuButton"));

        WaitUntil(
            "startup search focus is cleared",
            () => !IsSearchActuallyFocused(automation, searchBox),
            TimeSpan.FromSeconds(6));
        AssertProbe(!IsSearchActuallyFocused(automation, searchBox), "Startup left keyboard focus inside the search box.");
        AssertProbe(!IsElementFocused(shellMenuButton), "Startup moved visible keyboard focus to the titlebar menu button.");
        AssertProbe(IsVisible(searchBadge), "Shortcut badge was not visible while search was unfocused.");

        MoveMouseToEmptyTitleBar(window, searchBox);
        Thread.Sleep(1200);
        AssertProbe(!HasCtrlKTooltip(automation), "Hovering empty titlebar space displayed a Ctrl+K tooltip.");

        TryActivateWindow(window);
        Thread.Sleep(150);
        PressCtrlK();
        WaitUntil(
            "Ctrl+K focuses search",
            () => IsSearchActuallyFocused(automation, searchBox),
            TimeSpan.FromSeconds(3));
        AssertProbe(IsSearchActuallyFocused(automation, searchBox), "Ctrl+K did not put real keyboard focus in the search box.");
        AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchShortcutBadge"))), "Shortcut badge stayed visible while search was focused.");

        TextBox textBox = searchBox.AsTextBox();
        textBox.Text = string.Empty;
        Keyboard.Type("focusprobe");
        WaitUntil(
            "search text accepted typed input",
            () => string.Equals(GetTextBoxText(searchBox), "focusprobe", StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitUntil(
            "Esc removes search focus",
            () => !IsSearchActuallyFocused(automation, searchBox),
            TimeSpan.FromSeconds(3));
        AssertProbe(!IsSearchActuallyFocused(automation, searchBox), "Esc left real keyboard focus in the search text box.");
        AssertProbe(!IsElementFocused(shellMenuButton), "Esc moved visible keyboard focus to the titlebar menu button.");
        AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchShortcutBadge"))), "Shortcut badge did not return after Esc dismissed search focus.");

        string textAfterEscape = GetTextBoxText(searchBox);
        Keyboard.Type("x");
        Thread.Sleep(400);
        AssertProbe(
            string.Equals(GetTextBoxText(searchBox), textAfterEscape, StringComparison.Ordinal),
            "Typing after Esc changed the search text, so the caret was still in the search box.");

        MoveMouseToEmptyTitleBar(window, searchBox);
        Thread.Sleep(1200);
        AssertProbe(!HasCtrlKTooltip(automation), "Ctrl+K tooltip appeared over empty titlebar space after Esc.");

        string filePath = Path.Combine(options.OutputDirectory, "probe-search-focus-contract.png");
        CaptureWindowWithPopups(window, filePath);

        string focusedId = GetFocusedAutomationId(automation);
        Console.WriteLine($"search-focus-contract probe: passed, focusedAutomationId='{focusedId}', screenshot={filePath}");
    }
    finally
    {
        if (string.IsNullOrWhiteSpace(options.AttachProcess))
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}

static void RunEmojiPanelProbe(CaptureOptions options)
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

static void RunSegmentsHoverProbe(CaptureOptions options)
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

static void RunActivityLinkHoverProbe(CaptureOptions options)
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

static void RunPullRequestTimelineLinkHoverProbe(CaptureOptions options)
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

static void RunShellResponsiveProbe(CaptureOptions options)
{
    using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "shell-responsive probe");
        Rectangle initialWideBounds = ResizeWindow(window, 1366, 900);
        if (initialWideBounds.Width >= AutomationResponsiveLayout.ShellRailCollapseWidth)
        {
            WaitForElement(
                "DashboardCustomizeButton",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")),
                TimeSpan.FromSeconds(8));
            AutomationElement wideRailButton = WaitForElement(
                "wide ShellRailDrawerButton",
                () =>
                {
                    AutomationElement? button = window.FindFirstDescendant(
                        cf => cf.ByAutomationId("ShellRailDrawerButton"));
                    return IsVisible(button) ? button : null;
                },
                TimeSpan.FromSeconds(5));
            WaitUntil(
                "wide shell rail starts inline",
                () => IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRail"))),
                TimeSpan.FromSeconds(5));

            InvokeOrClick(wideRailButton);
            WaitUntil(
                "user collapses wide shell rail",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRail"))),
                TimeSpan.FromSeconds(5));
            CaptureWindow(
                window,
                Path.Combine(options.OutputDirectory, "shell-responsive-user-collapsed-wide.png"));

            ResizeWindow(window, 900, 700);
            ResizeWindow(window, 1366, 900);
            WaitUntil(
                "user-collapsed shell rail remains collapsed after resize",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRail"))),
                TimeSpan.FromSeconds(5));
            CaptureWindow(
                window,
                Path.Combine(options.OutputDirectory, "shell-responsive-user-collapse-persisted.png"));

            wideRailButton = WaitForElement(
                "wide ShellRailDrawerButton after resize",
                () =>
                {
                    AutomationElement? button = window.FindFirstDescendant(
                        cf => cf.ByAutomationId("ShellRailDrawerButton"));
                    return IsVisible(button) ? button : null;
                },
                TimeSpan.FromSeconds(5));
            InvokeOrClick(wideRailButton);
            WaitUntil(
                "user expands wide shell rail",
                () => IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRail"))),
                TimeSpan.FromSeconds(5));
        }

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
            WaitForElement("DashboardCustomizeButton", () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")), TimeSpan.FromSeconds(8));
            if (actualWidth >= AutomationResponsiveLayout.ShellRailCollapseWidth)
            {
                AssertProbe(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellNav_home")) is not null, "Combined shell nav was not present.");
                AssertProbe(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRepoFilter_Public")) is not null, "Combined shell repository filter was not present.");
                AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRailDrawerButton"))), "Wide shell did not expose the persistent navigation toggle.");
                AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRail"))), "Wide shell rail did not return inline after the user expanded it.");
            }
            else
            {
                AutomationElement railButton = WaitForElement("ShellRailDrawerButton", () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRailDrawerButton")), TimeSpan.FromSeconds(5));
                AssertProbe(IsVisible(railButton), "Compact shell did not expose the navigation drawer button.");
                AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRail"))), "Shell rail stayed visible at compact width.");
                AutomationElement? existingDrawer = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRailDrawer"));
                if (!IsVisible(existingDrawer))
                {
                    InvokeOrClick(railButton);
                }

                WaitForElement("ShellRailDrawer", () =>
                {
                    AutomationElement? drawer = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRailDrawer"));
                    return IsVisible(drawer) ? drawer : null;
                }, TimeSpan.FromSeconds(5));
                WaitForElement($"Shell navigation reachable at {actualWidth}px", () =>
                {
                    AutomationElement? item = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellNav_home"));
                    return IsVisible(item) && IsInsideWindowBounds(item!, window) ? item : null;
                }, TimeSpan.FromSeconds(5));
                WaitForElement($"Repository filters reachable at {actualWidth}px", () =>
                {
                    AutomationElement? item = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRepoFilter_Public"));
                    return IsVisible(item) && IsInsideWindowBounds(item!, window) ? item : null;
                }, TimeSpan.FromSeconds(5));
                Thread.Sleep(150);
                InvokeOrClick(railButton);
                Thread.Sleep(80);
                InvokeOrClick(railButton);
                WaitForElement("ShellRailDrawer reopens", () =>
                {
                    AutomationElement? drawer = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRailDrawer"));
                    return IsVisible(drawer) ? drawer : null;
                }, TimeSpan.FromSeconds(5));
                Thread.Sleep(350);
            }

            AssertProbe(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchTextBox")) is not null, "Command search was not present.");
            AssertProbe(window.FindFirstDescendant(cf => cf.ByAutomationId("WorkspaceTabs")) is null, "Visible workspace tabs still exist in the shell.");
            string path = Path.Combine(options.OutputDirectory, $"shell-responsive-{viewportLabel}.png");
            CaptureWindow(window, path);
            Console.WriteLine($"shell-responsive: captured {path}");
        }
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunShellNavClicksProbe(CaptureOptions options)
{
    using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "shell-nav-clicks probe");
        ResizeWindow(window, 1366, 900);
        (string NavId, string PageId)[] routes =
        [
            ("ShellNav_home", "DashboardCustomizeButton"),
            ("ShellNav_issues", "MyIssuesList"),
            ("ShellNav_pull-requests", "MyPullRequestsList"),
            ("ShellNav_notifications", "NotificationsList"),
            ("ShellNav_stars", "StarsList"),
            ("ShellNav_gists", "GistsList"),
            ("ShellNav_home", "DashboardCustomizeButton")
        ];

        foreach ((string navId, string pageId) in routes)
        {
            AutomationElement nav = GetShellNavigationElement(window, navId);
            InvokeOrClick(nav);
            WaitForElement(pageId, () => window.FindFirstDescendant(cf => cf.ByAutomationId(pageId)), TimeSpan.FromSeconds(10));
            string path = Path.Combine(options.OutputDirectory, $"shell-nav-clicks-{navId}.png");
            CaptureWindow(window, path);
            Console.WriteLine($"shell-nav-clicks: {navId} -> {pageId}");
        }

        ResizeWindow(window, 1366, 700);
        AutomationElement homeScroll = WaitForElement(
            "DashboardMainRailScrollViewer before history",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardMainRailScrollViewer")),
            TimeSpan.FromSeconds(8));
        ScrollElementToBottom(homeScroll);
        double homeScrollBeforeNavigation = GetVerticalScrollPercent(homeScroll);

        AutomationElement settingsNav = AssertNamedAutomationElement(window, "ShellSettingsTopButton", ControlType.Button);
        InvokeOrClick(settingsNav);
        WaitForElement("SettingsSectionList before history", () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList")), TimeSpan.FromSeconds(8));
        AutomationElement settingsSections = window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList"))!;
        AutomationElement privacySectionItem = WaitForElement(
            "SettingsSection_privacy",
            () => settingsSections.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSection_privacy")),
            TimeSpan.FromSeconds(5));
        privacySectionItem.Click();
        WaitForElement("SettingsDiagnosticsToggle before history", () =>
        {
            AutomationElement? privacyControl = window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsDiagnosticsToggle"));
            return IsVisible(privacyControl) ? privacyControl : null;
        }, TimeSpan.FromSeconds(8));

        AutomationElement back = AssertNamedAutomationElement(window, "ShellBackButton", ControlType.Button);
        AutomationElement forward = AssertNamedAutomationElement(window, "ShellForwardButton", ControlType.Button);
        AssertProbe(back.IsEnabled, "Shell Back command did not enable after navigation.");
        InvokeOrClick(back);
        WaitForElement("Dashboard after Back", () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")), TimeSpan.FromSeconds(8));
        homeScroll = WaitForElement(
            "DashboardMainRailScrollViewer after Back",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardMainRailScrollViewer")),
            TimeSpan.FromSeconds(8));
        WaitUntil(
            "Home scroll state restores after Back",
            () => Math.Abs(GetVerticalScrollPercent(homeScroll) - homeScrollBeforeNavigation) <= 2,
            TimeSpan.FromSeconds(5));
        double homeScrollAfterBack = GetVerticalScrollPercent(homeScroll);
        AssertProbe(
            Math.Abs(homeScrollAfterBack - homeScrollBeforeNavigation) <= 2,
            $"Back navigation changed Home scroll from {homeScrollBeforeNavigation:0.0}% to {homeScrollAfterBack:0.0}%.");
        AssertProbe(forward.IsEnabled, "Shell Forward command did not enable after Back.");
        InvokeOrClick(forward);
        WaitForElement("Settings after Forward", () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList")), TimeSpan.FromSeconds(8));
        WaitForElement("Settings Privacy selection after Forward", () =>
        {
            AutomationElement? privacyControl = window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsDiagnosticsToggle"));
            return IsVisible(privacyControl) ? privacyControl : null;
        }, TimeSpan.FromSeconds(8));

        using (Keyboard.Pressing(VirtualKeyShort.LMENU))
        {
            Keyboard.Press(VirtualKeyShort.LEFT);
        }
        WaitForElement("Dashboard after Alt Left", () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")), TimeSpan.FromSeconds(8));
        using (Keyboard.Pressing(VirtualKeyShort.LMENU))
        {
            Keyboard.Press(VirtualKeyShort.RIGHT);
        }
        WaitForElement("Settings after Alt Right", () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList")), TimeSpan.FromSeconds(8));

        SendMouseXButton(window, forward: false);
        bool mouseBackDelivered = WaitUntilAvailable(
            () => IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton"))),
            TimeSpan.FromSeconds(5));
        if (mouseBackDelivered)
        {
            SendMouseXButton(window, forward: true);
        }
        bool mouseForwardDelivered = WaitUntilAvailable(
            () => IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList"))),
            TimeSpan.FromSeconds(5));
        Console.WriteLine(
            mouseBackDelivered && mouseForwardDelivered
                ? "shell-nav-clicks: mouse XButton1/XButton2 history navigation passed."
                : "shell-nav-clicks: mouse X-button delivery is unavailable in this automation session; button and keyboard history coverage passed.");

        AutomationElement settingsTop = AssertNamedAutomationElement(window, "ShellSettingsTopButton", ControlType.Button);
        InvokeOrClick(settingsTop);
        WaitForElement("Settings after mouse capability probe", () =>
        {
            AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList"));
            return IsVisible(list) ? list : null;
        }, TimeSpan.FromSeconds(8));

        AssertNamedAutomationElement(window, "ShellNewRepositoryButton", ControlType.Button);
        AssertNamedAutomationElement(window, "ShellSettingsTopButton", ControlType.Button);
        AssertNamedAutomationElement(window, "ShellProfileTopButton", ControlType.Button);

        AutomationElement newRepository = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellNewRepositoryButton"))!;
        newRepository.Focus();
        InvokeOrClick(newRepository);
        AutomationElement modalContent = WaitForElement(
            "ShellModalContent",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellModalContent")),
            TimeSpan.FromSeconds(5));
        AssertProbe(!string.IsNullOrWhiteSpace(GetElementName(modalContent)), "ShellModalContent did not expose a meaningful accessible name.");
        AutomationElement modalClose = AssertNamedAutomationElement(window, "ShellModalCloseButton", ControlType.Button);
        WaitUntil(
            "modal receives keyboard focus",
            () => IsInsideElementBounds(automation.FocusedElement(), modalContent, 1),
            TimeSpan.FromSeconds(5));
        for (int tab = 0; tab < 8; tab++)
        {
            Keyboard.Press(VirtualKeyShort.TAB);
            AssertProbe(
                IsInsideElementBounds(automation.FocusedElement(), modalContent, 1),
                $"Modal focus escaped after Tab {tab + 1}.");
        }

        AssertProbe(string.Equals(GetElementName(modalClose), "Close dialog", StringComparison.Ordinal), "Modal close command exposed the wrong accessible name.");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitUntil("modal closes on Escape", () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellModalContent"))), TimeSpan.FromSeconds(5));
        WaitUntil("modal restores opener focus", () => IsElementFocused(newRepository), TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-nav-history.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunShellHoverStatesProbe(CaptureOptions options)
{
    using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "shell-hover-states probe");
        ResizeWindow(window, 1600, 960);
        string[] ids =
        [
            "ShellBackButton",
            "ShellForwardButton",
            "ShellNav_home",
            "ShellNav_issues",
            "ShellNav_pull-requests",
            "ShellNav_notifications",
            "ShellNav_stars",
            "ShellNav_gists",
            "ShellNav_explore",
            "ShellSearchSubmitButton",
            "ShellNewRepositoryButton",
            "ShellSettingsTopButton",
            "ShellProfileTopButton",
            "ShellSearchTextBox",
            "ShellRepoFilter_Public",
            "ShellRepoFilter_Private",
            "ShellRepoFilter_Forked",
            "DashboardCustomizeButton"
        ];

        CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-hover-states-before.png"));
        foreach (string id in ids)
        {
            AutomationElement element = WaitForElement(
                id,
                () =>
                {
                    AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
                    return IsVisible(candidate) ? candidate : null;
                },
                TimeSpan.FromSeconds(8));
            AssertProbe(
                !string.IsNullOrWhiteSpace(element.Name),
                $"shell-hover-states: {id} does not expose an accessible name.");

            Mouse.MoveTo(CenterPoint(element, window));
            Thread.Sleep(250);
            string path = Path.Combine(options.OutputDirectory, $"shell-hover-states-{SanitizeFileName(id)}.png");
            CaptureWindow(window, path);
            Console.WriteLine($"shell-hover-states: {id}");
        }

        ResizeWindow(window, 760, 650);
        AutomationElement drawerButton = WaitForElement(
            "ShellRailDrawerButton",
            () =>
            {
                AutomationElement? candidate = window.FindFirstDescendant(
                    cf => cf.ByAutomationId("ShellRailDrawerButton"));
                return IsVisible(candidate) ? candidate : null;
            },
            TimeSpan.FromSeconds(8));
        AssertProbe(
            !string.IsNullOrWhiteSpace(drawerButton.Name),
            "shell-hover-states: ShellRailDrawerButton does not expose an accessible name.");
        Mouse.MoveTo(CenterPoint(drawerButton, window));
        Thread.Sleep(250);
        CaptureWindow(
            window,
            Path.Combine(options.OutputDirectory, "shell-hover-states-ShellRailDrawerButton.png"));
        Console.WriteLine("shell-hover-states: ShellRailDrawerButton");
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunShellSearchStatesProbe(CaptureOptions options)
{
    using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "shell-search-states probe");
        ResizeWindow(window, 1366, 900);
        AutomationElement commandSearch = WaitForElement(
            "ShellSearchTextBox",
            () => FindShellSearchTextBox(window),
            TimeSpan.FromSeconds(5));

        CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-search-states-normal.png"));
        Mouse.MoveTo(CenterPoint(commandSearch, window));
        Thread.Sleep(350);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-search-states-hover.png"));
        commandSearch.Click();
        WaitUntil("command search gets focus", () => IsSearchActuallyFocused(automation, commandSearch), TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-search-states-focused.png"));

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(250);
        AutomationElement repoFilter = WaitForElement(
            "ShellRepoFilter_Public",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRepoFilter_Public")),
            TimeSpan.FromSeconds(5));
        Mouse.MoveTo(CenterPoint(repoFilter, window));
        Thread.Sleep(350);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-search-states-repo-filter-hover.png"));
        AutomationElement privateFilter = WaitForElement(
            "ShellRepoFilter_Private",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRepoFilter_Private")),
            TimeSpan.FromSeconds(5));
        privateFilter.Click();
        Thread.Sleep(350);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-search-states-repo-filter-selected.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunShellRepoClickProbe(CaptureOptions options)
{
    using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "shell-repo-click probe");
        ResizeWindow(window, 1366, 900);
        AutomationElement repositoryList = WaitForElement("ShellRepositoryList", () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRepositoryList")), TimeSpan.FromSeconds(10));
        AutomationElement repoButton = WaitForElement(
            "first repository",
            () => repositoryList.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button)),
            TimeSpan.FromSeconds(10));
        repoButton.Click();
        WaitForElement("RepoDetailBranchPicker", () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailBranchPicker")), TimeSpan.FromSeconds(14));
        AutomationElement codeRoot = WaitForElement(
            "RepoCodePageRoot",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodePageRoot")),
            TimeSpan.FromSeconds(14));
        AssertProbe(IsVisible(codeRoot), "Repository navigation did not expose the Code workspace root.");
        AssertNamedAutomationElement(window, "RepoDetailBranchPicker", ControlType.Button);
        AssertNamedAutomationElement(window, "RepoDetailWatchButton", ControlType.Button);
        AssertNamedAutomationElement(window, "RepoDetailStarButton", ControlType.Button);
        AssertNamedAutomationElement(window, "RepoDetailForkButton", ControlType.Button);
        AssertNamedAutomationElement(window, "RepoCodeFileFilter", ControlType.Edit);
        AssertNamedAutomationElement(window, "RepoCodeFileTree", ControlType.Group);

        AutomationElement fileTree = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeFileTree"))!;
        AutomationElement treeItem = WaitForElement(
            "realized repository tree item",
            () => fileTree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                .FirstOrDefault(element =>
                    IsVisible(element) &&
                    element.AutomationId.StartsWith("RepoCodeTreeItem_", StringComparison.Ordinal) &&
                    (element.Name.EndsWith(", file", StringComparison.Ordinal) ||
                     element.Name.EndsWith(", folder", StringComparison.Ordinal))),
            TimeSpan.FromSeconds(14));
        AssertProbe(!string.IsNullOrWhiteSpace(treeItem.Name), "Repository tree item did not expose a meaningful accessible name.");
        treeItem.Focus();
        Keyboard.Press(VirtualKeyShort.ENTER);
        Thread.Sleep(500);

        AutomationElement breadcrumbSegment = WaitForElement(
            "realized repository breadcrumb segment",
            () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(element =>
                    IsVisible(element) &&
                    element.AutomationId.StartsWith("RepoCodeBreadcrumbSegment_", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10));
        AssertProbe(
            breadcrumbSegment.Name.StartsWith("Open ", StringComparison.Ordinal),
            "Repository breadcrumb segment did not expose its navigation action.");
        AssertProbe(breadcrumbSegment.Patterns.Invoke.IsSupported, "Repository breadcrumb segment did not expose the Invoke pattern.");
        breadcrumbSegment.Focus();
        Keyboard.Press(VirtualKeyShort.ENTER);
        WaitUntil(
            "breadcrumb keyboard invocation keeps Code workspace active",
            () => IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodePageRoot"))),
            TimeSpan.FromSeconds(5));

        AssertNamedAutomationElement(window, "RepoCodeBackButton", ControlType.Button);
        AssertNamedAutomationElement(window, "RepoCodeForwardButton", ControlType.Button);
        AssertNamedAutomationElement(window, "RepoCodeCopyPathButton", ControlType.Button);
        AssertNamedAutomationElement(window, "RepoCodeCopyRawUrlButton", ControlType.Button);
        AssertNamedAutomationElement(window, "RepoCodeOpenOnGitHubButton", ControlType.Button);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "shell-repo-click.png"));
        ResizeWindow(window, 640, 600);
        AssertNamedAutomationElement(window, "RepoDetailCompactCommandsButton", ControlType.Button);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-accessibility-compact.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunHomeWidgetBoardProbe(CaptureOptions options)
{
    bool isAttached = !string.IsNullOrWhiteSpace(options.AttachProcess);
    using var app = isAttached
        ? CreateProbeApplication(options)
        : LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "home-widget-board probe");
        ResizeWindow(window, 1366, 900);
        WaitForElement("DashboardCustomizeButton", () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")), TimeSpan.FromSeconds(8));
        AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideRailScrollViewer"))), "Home side rail content was not visible in wide layout.");
        AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardWidgetViewAll_overview"))), "Home exposed a semantically false Overview View all action.");
        AssertProbe(window.FindFirstDescendant(cf => cf.ByAutomationId("WorkspaceTabs")) is null, "Home or shell exposed a TabView.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "home-widget-board-wide.png"));

        ResizeWindow(window, 760, 650);
        Thread.Sleep(350);
        AutomationElement drawerButton = WaitForElement("DashboardOverviewDrawerButton", () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardOverviewDrawerButton")), TimeSpan.FromSeconds(8));
        AssertProbe(IsVisible(drawerButton), "Compact Home did not expose the Overview drawer button.");
        AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideRailScrollViewer"))), "Home side rail content stayed visible at compact width.");
        if (drawerButton.Patterns.Invoke.IsSupported)
        {
            drawerButton.Patterns.Invoke.Pattern.Invoke();
        }
        else
        {
            drawerButton.Click();
        }
        AutomationElement closeButton = WaitForElement("DashboardSideDrawerCloseButton", () =>
        {
            AutomationElement? close = window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"));
            return IsVisible(close) ? close : null;
        }, TimeSpan.FromSeconds(8));
        FocusForKeyboardActivation(window, closeButton);

        WaitUntil(
            "Home overview drawer focuses its close button",
            () => string.Equals(
                GetFocusedAutomationId(automation),
                "DashboardSideDrawerCloseButton",
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "home-widget-board-drawer-close-focused.png"));

        AutomationElement drawerPanel = WaitForElement(
            "DashboardSideDrawerPanel",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerPanel")),
            TimeSpan.FromSeconds(5));
        System.Drawing.Rectangle drawerBounds = drawerPanel.BoundingRectangle;
        FocusForKeyboardActivation(window, closeButton);

        using (Keyboard.Pressing(VirtualKeyShort.SHIFT))
        {
            Keyboard.Press(VirtualKeyShort.TAB);
        }
        Thread.Sleep(250);
        Console.WriteLine($"Home drawer raw focus after Shift+Tab: {GetFocusedAutomationDescription(automation)}; " +
            $"inside={IsFocusedElementWithin(automation, drawerPanel)}");
        WaitUntil(
            "Shift+Tab wraps to the last Home overview drawer control",
            () => IsFocusedElementWithin(automation, drawerPanel),
            TimeSpan.FromSeconds(5));
        Console.WriteLine($"Home drawer focus after Shift+Tab: {GetFocusedAutomationDescription(automation)}");

        Keyboard.Press(VirtualKeyShort.TAB);
        Thread.Sleep(250);
        Console.WriteLine($"Home drawer focus after wrap Tab: {GetFocusedAutomationDescription(automation)}");
        WaitUntil(
            "Tab wraps to the Home overview drawer close button",
            () => string.Equals(
                GetFocusedAutomationId(automation),
                "DashboardSideDrawerCloseButton",
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitUntil(
            "Escape closes the Home overview drawer",
            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"))),
            TimeSpan.FromSeconds(5));
        WaitUntil(
            "Escape restores exact Home overview opener focus",
            () => string.Equals(
                GetFocusedAutomationId(automation),
                "DashboardOverviewDrawerButton",
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "home-widget-board-drawer-escape-focus-restored.png"));

        drawerButton = WaitForElement(
            "DashboardOverviewDrawerButton after Escape",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardOverviewDrawerButton")),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(drawerButton);
        closeButton = WaitForElement("DashboardSideDrawerCloseButton before light dismiss", () =>
        {
            AutomationElement? close = window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"));
            return IsVisible(close) ? close : null;
        }, TimeSpan.FromSeconds(8));
        WaitUntil(
            "Home overview drawer refocuses close before light dismiss",
            () => string.Equals(
                GetFocusedAutomationId(automation),
                "DashboardSideDrawerCloseButton",
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        drawerPanel = WaitForElement(
            "DashboardSideDrawerPanel before light dismiss",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerPanel")),
            TimeSpan.FromSeconds(5));
        drawerBounds = drawerPanel.BoundingRectangle;
        System.Drawing.Rectangle windowBounds = window.BoundingRectangle;
        int dismissX = Math.Max(windowBounds.Left + 8, drawerBounds.Left - 24);
        int dismissY = Math.Clamp(
            drawerBounds.Top + Math.Max(24, drawerBounds.Height / 2),
            windowBounds.Top + 8,
            windowBounds.Bottom - 8);
        Mouse.Click(new System.Drawing.Point(dismissX, dismissY));
        WaitUntil(
            "outside click light-dismisses the Home overview drawer",
            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"))),
            TimeSpan.FromSeconds(5));
        WaitUntil(
            "light dismiss restores exact Home overview opener focus",
            () => string.Equals(
                GetFocusedAutomationId(automation),
                "DashboardOverviewDrawerButton",
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "home-widget-board-drawer-light-dismiss-focus-restored.png"));

        drawerButton = WaitForElement(
            "DashboardOverviewDrawerButton before rapid cycles",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardOverviewDrawerButton")),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(drawerButton);
        closeButton = WaitForElement("DashboardSideDrawerCloseButton before rapid cycles", () =>
        {
            AutomationElement? close = window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"));
            return IsVisible(close) ? close : null;
        }, TimeSpan.FromSeconds(8));
        for (int cycle = 0; cycle < 5; cycle++)
        {
            InvokeOrClick(closeButton);
            WaitUntil(
                $"Dashboard side drawer closes on rapid cycle {cycle + 1}",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"))),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(drawerButton);
            closeButton = WaitForElement($"DashboardSideDrawerCloseButton rapid reopen {cycle + 1}", () =>
            {
                AutomationElement? close = window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"));
                return IsVisible(close) ? close : null;
            }, TimeSpan.FromSeconds(8));
        }
        Thread.Sleep(350);
        AutomationElement visibleOverviewMetric = WaitForElement(
            "visible Overview metric after repeated drawer cycles",
            () => window.FindAllDescendants(cf => cf.ByAutomationId("DashboardOverviewMetricRepositories"))
                .FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(5));
        AssertProbe(
            IsVisible(visibleOverviewMetric),
            "Repeated Home drawer cycles reopened below the Overview metrics.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "home-widget-board-compact-drawer.png"));
    }
    finally
    {
        if (!isAttached)
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}

static void RunHomeCustomizeProbe(CaptureOptions options)
{
    using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "home-customize probe");
        ResizeWindow(window, 1366, 900);
        AutomationElement customizeButton = WaitForElement("DashboardCustomizeButton", () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")), TimeSpan.FromSeconds(8));
        var customizeBounds = customizeButton.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(
            (int)Math.Round(customizeBounds.X + customizeBounds.Width / 2d),
            (int)Math.Round(customizeBounds.Y + customizeBounds.Height / 2d)));
        AutomationElement dialog = WaitForElement("DashboardCustomizeSaveButton", () =>
        {
            AutomationElement? element = FindElementInWindowOrDialog(window, automation, "DashboardCustomizeSaveButton");
            return IsVisible(element) ? element : null;
        }, TimeSpan.FromSeconds(5));
        AutomationElement dialogRoot = WaitForElement("DashboardCustomizeDialog", () =>
        {
            AutomationElement? element = FindElementInWindowOrDialog(window, automation, "DashboardCustomizeDialog");
            return IsVisible(element) ? element : null;
        }, TimeSpan.FromSeconds(5));
        AssertProbe(IsInsideWindowBounds(dialogRoot, window), "Customize dialog escaped the app window.");
        AssertProbe(IsHorizontallyCentered(dialogRoot, window, 44), "Customize dialog was not centered in the app window.");
        Thread.Sleep(500);
        CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "home-customize-open.png"));

        AutomationElement reset = WaitForElement("DashboardCustomizeResetButton", () => FindElementInWindowOrDialog(window, automation, "DashboardCustomizeResetButton"), TimeSpan.FromSeconds(5));
        InvokeOrClick(reset);
        Thread.Sleep(250);
        InvokeOrClick(dialog);
        WaitUntil("customize dialog closes", () => !IsVisible(FindElementInWindowOrDialog(window, automation, "DashboardCustomizeSaveButton")), TimeSpan.FromSeconds(5));

        for (int cycle = 0; cycle < 3; cycle++)
        {
            customizeButton = WaitForElement(
                $"DashboardCustomizeButton rapid cycle {cycle + 1}",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardCustomizeButton")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(customizeButton);
            AutomationElement cancel = WaitForElement(
                $"DashboardCustomizeCancelButton rapid cycle {cycle + 1}",
                () => FindElementInWindowOrDialog(window, automation, "DashboardCustomizeCancelButton"),
                TimeSpan.FromSeconds(5));
            AssertProbe(IsVisible(cancel), $"Customize dialog did not open on rapid cycle {cycle + 1}.");
            InvokeOrClick(cancel);
            WaitUntil(
                $"customize dialog closes on rapid cycle {cycle + 1}",
                () => !IsVisible(FindElementInWindowOrDialog(window, automation, "DashboardCustomizeCancelButton")),
                TimeSpan.FromSeconds(5));
        }

        CaptureWindow(window, Path.Combine(options.OutputDirectory, "home-customize-saved.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunHomeViewAllProbe(CaptureOptions options)
{
    (string ViewAllId, string WideDestinationId, string CompactDestinationId, bool IsSideWidget)[] viewAllRoutes =
    [
        ("DashboardWidgetViewAll_notifications", "NotificationsList", "NotificationsList", true)
    ];
    string[] unavailableFalseRoutes =
    [
        "DashboardWidgetViewAll_recent_activity",
        "DashboardWidgetViewAll_repositories",
        "DashboardWidgetViewAll_overview",
        "DashboardWidgetViewAll_recommended_repositories"
    ];

    (int Width, int Height, string Label)[] sizes = [(1366, 900, "wide"), (640, 600, "compact")];
    foreach ((int width, int height, string label) in sizes)
    {
        foreach ((string viewAllId, string wideDestinationId, string compactDestinationId, bool isSideWidget) in viewAllRoutes)
        {
            using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
            using var automation = new UIA3Automation();
            try
            {
                Window window = GetReadyWindow(app, automation, $"home-view-all probe {label} {viewAllId}");
                Rectangle resizedBounds = ResizeWindow(window, width, height);
                string actualLayout = resizedBounds.Width < 1040 ? "compact" : "wide";
                AssertProbe(
                    string.Equals(label, actualLayout, StringComparison.Ordinal),
                    $"Home layout was {actualLayout} at native width {resizedBounds.Width}, but the probe expected {label}.");
                IntPtr mainWindowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
                int expectedProcessId = app.ProcessId;
                if (actualLayout == "compact" && isSideWidget)
                {
                    AutomationElement drawerButton = WaitForElement(
                        "DashboardOverviewDrawerButton",
                        () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardOverviewDrawerButton")),
                        TimeSpan.FromSeconds(8));
                    InvokeOrClick(drawerButton);
                    WaitForElement(
                        "DashboardSideDrawerCloseButton",
                        () =>
                        {
                            AutomationElement? close = window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardSideDrawerCloseButton"));
                            return IsVisible(close) ? close : null;
                        },
                        TimeSpan.FromSeconds(8));
                }

                foreach (string falseRoute in unavailableFalseRoutes)
                {
                    AssertProbe(
                        !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId(falseRoute))),
                        $"Home exposed the semantically false action {falseRoute} at {label} width.");
                }

                AutomationElement button = WaitForElement(viewAllId, () =>
                {
                    AutomationElement[] matches = window.FindAllDescendants(cf => cf.ByAutomationId(viewAllId));
                    return matches.FirstOrDefault(IsVisible)
                        ?? matches.FirstOrDefault(element => element.Patterns.ScrollItem.IsSupported)
                        ?? matches.FirstOrDefault();
                }, TimeSpan.FromSeconds(10));
                RevealForInteraction(button, $"{label} {viewAllId}");
                Console.WriteLine($"Invoking {label} Home widget action {viewAllId} at {button.BoundingRectangle}.");
                button.Click();
                Thread.Sleep(900);
                window = GetReadyWindowByHandle(
                    automation,
                    mainWindowHandle,
                    expectedProcessId,
                    $"home-view-all destination {label} {viewAllId}");
                Console.WriteLine($"Destination {viewAllId} window bounds: {window.BoundingRectangle} handle={window.Properties.NativeWindowHandle.ValueOrDefault} process={window.Properties.ProcessId.ValueOrDefault}.");
                CaptureWindow(window, Path.Combine(options.OutputDirectory, $"home-view-all-{label}-{SanitizeFileName(viewAllId)}.png"));
                string destinationId = actualLayout == "compact" ? compactDestinationId : wideDestinationId;
                WaitForElement(destinationId, () => window.FindFirstDescendant(cf => cf.ByAutomationId(destinationId)), TimeSpan.FromSeconds(8));
            }
            finally
            {
                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }
    }
}

static void RunLoginAuthUiProbe(CaptureOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        throw new InvalidOperationException("The login-auth-ui probe requires fresh light and dark app processes.");
    }

    var screenshotPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (string theme in new[] { "light", "dark" })
    {
        using var app = LaunchApplication(options.AppPath, "--page=login", $"--theme={theme}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, $"login-auth-ui {theme} theme");
            ResizeWindow(window, 1180, 760);

            AutomationElement root = WaitForElement(
                "LoginRoot",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("LoginRoot")),
                TimeSpan.FromSeconds(8));
            AutomationElement card = WaitForElement(
                "LoginCard",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("LoginCard")),
                TimeSpan.FromSeconds(8));
            AutomationElement signInButton = AssertNamedAutomationElement(
                window,
                "LoginSignInButton",
                ControlType.Button);
            AutomationElement status = AssertNamedAutomationElement(
                window,
                "LoginStatusText",
                ControlType.Text);

            AssertProbe(string.Equals(signInButton.Name, "Continue with GitHub", StringComparison.Ordinal),
                $"The {theme} login button exposed the unexpected accessible name '{signInButton.Name}'.");
            AssertProbe(signInButton.IsEnabled, $"The {theme} login button was not enabled.");
            AssertProbe(status.Name.Contains("opens GitHub", StringComparison.OrdinalIgnoreCase),
                $"The {theme} login status did not describe the browser sign-in flow.");
            AssertProbe(IsInsideWindowBounds(root, window), $"The {theme} login root escaped the app window.");
            AssertProbe(IsInsideElementBounds(signInButton, card, 1.5),
                $"The {theme} login button escaped the sign-in card.");
            AssertProbe(signInButton.BoundingRectangle.Height >= 32,
                $"The {theme} login button was too short for a reliable touch target.");
            AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("LoginErrorInfoBar"))),
                $"The {theme} login page started with an error visible.");

            string screenshotPath = Path.Combine(options.OutputDirectory, $"{theme}-login.png");
            CaptureWindow(window, screenshotPath);
            AssertLoginThemeScreenshot(screenshotPath, theme);
            screenshotPaths[theme] = screenshotPath;
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    AssertLoginThemeContrast(screenshotPaths["light"], screenshotPaths["dark"]);

    using (var app = LaunchApplication(
        options.AppPath,
        "--page=login",
        "--scenario=login-launch-failure",
        "--theme=dark"))
    using (var automation = new UIA3Automation())
    {
        try
        {
            Window window = GetReadyWindow(app, automation, "login-auth-ui launch failure");
            ResizeWindow(window, 1180, 760);
            AutomationElement signInButton = AssertNamedAutomationElement(
                window,
                "LoginSignInButton",
                ControlType.Button);

            InvokeOrClick(signInButton);
            AutomationElement error = WaitForElement(
                "LoginErrorInfoBar",
                () =>
                {
                    AutomationElement? candidate = window.FindFirstDescendant(
                        cf => cf.ByAutomationId("LoginErrorInfoBar"));
                    return IsVisible(candidate) ? candidate : null;
                },
                TimeSpan.FromSeconds(8));

            string errorText = string.Join(
                " ",
                error.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Select(GetElementName)
                    .Where(static text => !string.IsNullOrWhiteSpace(text)));
            AssertProbe(errorText.Contains("could not open GitHub sign-in", StringComparison.OrdinalIgnoreCase),
                $"Login launch failure did not expose the expected accessible error. Actual: '{errorText}'.");
            WaitUntil("login retry button to re-enable", () => signInButton.IsEnabled, TimeSpan.FromSeconds(5));

            string errorPath = Path.Combine(options.OutputDirectory, "dark-login-launch-error.png");
            CaptureWindow(window, errorPath);
            AssertLoginThemeScreenshot(errorPath, "dark");
            Console.WriteLine(
                $"login-auth-ui probe: light={screenshotPaths["light"]}, dark={screenshotPaths["dark"]}, error={errorPath}");
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}

static void RunAuthLifecycleProbe(CaptureOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        throw new InvalidOperationException("The auth-lifecycle probe requires isolated fresh app processes.");
    }

    RunAuthCancelScenario(options, "light");
    RunAuthCancelScenario(options, "dark");
    RunAuthInvalidStateScenario(options);
    RunAuthExpiredTokenScenario(options);
    RunAuthNotificationReconnectScenario(options);
    RunAuthOfflineLaunchScenario(options);
    RunAuthProtocolReactivationScenario(options);
    RunAuthMultiAccountCleanupScenario(options);
    Console.WriteLine("auth-lifecycle probe completed all deterministic production-path scenarios.");
}

static void RunAuthCancelScenario(CaptureOptions options, string theme)
{
    RunIsolatedAuthScenario(options, "auth-cancel", theme, (window, _, root) =>
    {
        AutomationElement signIn = WaitForVisibleAutomationElement(window, "LoginSignInButton");
        InvokeOrClick(signIn);
        WaitUntil(
            "cancelled sign-in status",
            () => AutomationElementText(window.FindFirstDescendant(cf => cf.ByAutomationId("LoginStatusText")))
                .Contains("canceled", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(8));
        AssertProbe(signIn.IsEnabled, "Sign-in was not recoverable after cancellation.");
        AssertAuthMarker(root, "oauth.launch.cancelled");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, $"auth-cancel-{theme}.png"));
    });
}

static void RunAuthInvalidStateScenario(CaptureOptions options)
{
    string scheme = GetAuthProtocolScheme(options.AppPath);
    RunIsolatedAuthScenario(
        options,
        "auth-invalid-state",
        "dark",
        (window, _, root) =>
        {
            AutomationElement error = WaitForVisibleAutomationElement(window, "LoginErrorInfoBar");
            string text = AutomationElementText(error);
            AssertProbe(
                text.Contains("verify", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("No token was accepted", StringComparison.OrdinalIgnoreCase),
                $"Invalid OAuth state did not expose a recoverable explanation. Actual: '{text}'.");
            IReadOnlyDictionary<string, string> credentials = ReadAuthCredentials(root);
            AssertProbe(!credentials.ContainsKey("__pending__"), "Invalid callback retained a pending OAuth token.");
            AssertProbe(!credentials.ContainsKey("__pending_state__"), "Invalid callback retained pending OAuth state.");
            AssertProbe(!credentials.ContainsKey("101"), "Invalid callback accepted an account token.");
            AssertAuthMarker(root, "protocol.authorization.rejected");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-invalid-state-dark.png"));
        },
        $"--automation-protocol={scheme}://auth/v3?handoff=automation-protocol-handoff&state=automation-invalid-state");
}

static void RunAuthExpiredTokenScenario(CaptureOptions options)
{
    RunIsolatedAuthScenario(options, "auth-expired-token", "dark", (window, _, root) =>
    {
        AutomationElement error = WaitForVisibleAutomationElement(window, "LoginErrorInfoBar");
        AssertProbe(
            AutomationElementText(error).Contains("expired", StringComparison.OrdinalIgnoreCase),
            "Expired-token launch did not explain that the session expired.");
        AssertProbe(!ReadAuthCredentials(root).ContainsKey("101"), "Expired account token was not removed.");
        AssertProbe(ReadAuthSetting(root, "USER_ID") is null or "0", "Expired launch retained the current account id.");
        AssertAuthMarker(root, "http.unauthorized");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-expired-token-dark.png"));
    });
}

static void RunAuthOfflineLaunchScenario(CaptureOptions options)
{
    RunIsolatedAuthScenario(options, "auth-offline-launch", "light", (window, _, root) =>
    {
        WaitForVisibleAutomationElement(window, "ShellRoot");
        WaitUntil(
            "offline launch status",
            () => AutomationElementText(window.FindFirstDescendant(cf => cf.ByAutomationId("AppStatusText")))
                .Contains("offline", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(12));
        AssertProbe(ReadAuthCredentials(root).ContainsKey("101"), "Offline launch removed the reusable account token.");
        AssertProbe(ReadAuthSetting(root, "USER_ID") == "101", "Offline launch lost the active account id.");
        AssertAuthMarker(root, "http.offline");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-offline-launch-light.png"));
    });
}

static void RunAuthNotificationReconnectScenario(CaptureOptions options)
{
    RunIsolatedAuthScenario(options, "auth-notification-reconnect", "dark", (window, _, root) =>
    {
        AutomationElement reconnect = WaitForVisibleAutomationElement(window, "DashboardReconnectButton", TimeSpan.FromSeconds(20));
        InvokeOrClick(reconnect);
        string authorizationUri = WaitForAuthMarkerValue(root, "oauth.launch.requested", TimeSpan.FromSeconds(8));
        Uri uri = new(authorizationUri);
        string query = Uri.UnescapeDataString(uri.Query);
        AssertProbe(query.Contains("notifications", StringComparison.OrdinalIgnoreCase),
            $"Reconnect OAuth request omitted notifications scope: '{authorizationUri}'.");
        AssertProbe(ReadAuthCredentials(root).ContainsKey("101"), "Notification reconnect removed the current session.");
        AssertAuthMarker(root, "notifications.scope.required");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-notification-reconnect-dark.png"));
    });
}

static void RunAuthProtocolReactivationScenario(CaptureOptions options)
{
    const string scenario = "auth-protocol-reactivation";
    string root = PrepareAuthScenarioRoot(scenario);
    using var app = LaunchApplicationWithDataRoot(
        options.AppPath,
        root,
        killExisting: true,
        $"--scenario={scenario}",
        "--theme=light");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, scenario);
        ResizeWindow(window, 1180, 760);
        InvokeOrClick(WaitForVisibleAutomationElement(window, "LoginSignInButton"));
        string state = WaitForCredential(root, "__pending_state__", TimeSpan.FromSeconds(8));
        string callback = $"{GetAuthProtocolScheme(options.AppPath)}://auth/v3?handoff=automation-protocol-handoff&state={Uri.EscapeDataString(state)}";
        StartRedirectedAuthActivation(options.AppPath, root, scenario, callback);

        WaitForVisibleAutomationElement(window, "ShellRoot", TimeSpan.FromSeconds(20));
        WaitUntil(
            "protocol completion status",
            () => AutomationElementText(window.FindFirstDescendant(cf => cf.ByAutomationId("AppStatusText")))
                .Contains("completed", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10));
        AssertProbe(ReadAuthCredentials(root).TryGetValue("101", out string? token) && token == "automation-protocol-token",
            "Protocol reactivation did not persist the authenticated account token.");
        AssertProbe(!ReadAuthCredentials(root).ContainsKey("__pending_state__"),
            "Protocol reactivation retained consumed OAuth state.");
        AssertAuthMarker(root, "protocol.authorization.completed");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-protocol-reactivation-light.png"));
        WaitUntil(
            "protocol completion status dismissed",
            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("AppStatusHost"))),
            TimeSpan.FromSeconds(8));
        CaptureWindow(window, Path.Combine(
            options.OutputDirectory,
            "auth-protocol-reactivation-status-dismissed-light.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunAuthMultiAccountCleanupScenario(CaptureOptions options)
{
    RunIsolatedAuthScenario(options, "auth-multi-account-cleanup", "dark", (window, automation, root) =>
    {
        WaitForVisibleAutomationElement(window, "ShellRoot", TimeSpan.FromSeconds(20));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-multi-account-route-dark.png"));
        WaitForVisibleAutomationElement(window, "SettingsPageTitle", TimeSpan.FromSeconds(10));
        AutomationElement sectionList = WaitForVisibleAutomationElement(window, "SettingsSectionList");
        AutomationElement general = WaitForElement(
            "General settings section",
            () => sectionList.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .FirstOrDefault(item => string.Equals(item.Name, "General", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(8));
        general.Click();
        InvokeOrClick(WaitForVisibleAutomationElement(window, "SettingsSignOutButton"));

        AutomationElement dialog = WaitForVisibleAutomationElement(window, "SignOutConfirmationDialog");
        AutomationElement removeData = WaitForVisibleAutomationElement(window, "SignOutRemoveAccountDataCheckBox");
        if (!removeData.Patterns.Toggle.IsSupported || removeData.Patterns.Toggle.Pattern.ToggleState.Value != ToggleState.On)
        {
            InvokeOrClick(removeData);
        }
        AssertProbe(removeData.Patterns.Toggle.IsSupported && removeData.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On,
            "The remove-account-data checkbox could not be selected.");
        CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "auth-multi-account-confirmation-dark.png"));
        InvokeOrClick(FindDialogButton(dialog, automation, "Sign out"));

        WaitForVisibleAutomationElement(window, "LoginRoot", TimeSpan.FromSeconds(20));
        IReadOnlyDictionary<string, string> credentials = ReadAuthCredentials(root);
        AssertProbe(!credentials.ContainsKey("101"), "Signing out with cleanup retained the active account token.");
        AssertProbe(credentials.TryGetValue("202", out string? secondary) && secondary == "automation-secondary-token",
            "Signing out one account removed the other account's token.");
        AssertProbe(ReadAuthSetting(root, "USER_ID") is null or "0", "Sign-out cleanup retained the active account id.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "auth-multi-account-cleanup-dark.png"));
    }, "--page=settings");
}

static void RunIsolatedAuthScenario(
    CaptureOptions options,
    string scenario,
    string theme,
    Action<Window, UIA3Automation, string> assertion,
    params string[] additionalArguments)
{
    string root = PrepareAuthScenarioRoot(scenario);
    string[] arguments = [$"--scenario={scenario}", $"--theme={theme}", .. additionalArguments];
    using var app = LaunchApplicationWithDataRoot(options.AppPath, root, killExisting: true, arguments);
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, scenario);
        ResizeWindow(window, 1180, 760);
        assertion(window, automation, root);
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static AutomationElement WaitForVisibleAutomationElement(
    Window window,
    string automationId,
    TimeSpan? timeout = null) =>
    WaitForElement(
        automationId,
        () =>
        {
            AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            return IsVisible(candidate) ? candidate : null;
        },
        timeout ?? TimeSpan.FromSeconds(10));

static string AutomationElementText(AutomationElement? element)
{
    if (element is null)
    {
        return string.Empty;
    }

    return string.Join(
        " ",
        new[] { GetElementName(element) }
            .Concat(element.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(GetElementName))
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal));
}

static string PrepareAuthScenarioRoot(string scenario)
{
    string root = Path.Combine(GetAutomationDataRoot(), "auth-lifecycle", scenario);
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
    Directory.CreateDirectory(root);
    return root;
}

static string GetAuthProtocolScheme(string appPath)
{
    _ = appPath;
#if DEBUG
    return "jithub-dev";
#else
    return "jithub";
#endif
}

static void StartRedirectedAuthActivation(string appPath, string dataRoot, string scenario, string callback)
{
    var startInfo = new ProcessStartInfo(appPath)
    {
        WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory,
        UseShellExecute = false
    };
    string[] arguments = [$"--scenario={scenario}", $"--automation-protocol={callback}"];
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }
    AddPreviewEnvironment(startInfo, arguments, dataRoot);
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start the protocol reactivation process.");
    AssertProbe(process.WaitForExit(15000), "The redirected protocol activation process did not exit.");
    AssertProbe(process.ExitCode == 0, $"The redirected protocol activation process exited with {process.ExitCode}.");
}

static IReadOnlyDictionary<string, string> ReadAuthCredentials(string dataRoot)
{
    string path = Path.Combine(dataRoot, "Local", "AuthLifecycle", "credentials.vault");
    Dictionary<string, string> values = new(StringComparer.Ordinal);
    if (!File.Exists(path))
    {
        return values;
    }
    foreach (string line in File.ReadLines(path))
    {
        string[] parts = line.Split('\t');
        if (parts.Length != 3)
        {
            continue;
        }
        try
        {
            values[DecodeBase64(parts[1])] = DecodeBase64(parts[2]);
        }
        catch (FormatException)
        {
        }
    }
    return values;
}

static string? ReadAuthSetting(string dataRoot, string key)
{
    string path = Path.Combine(dataRoot, "Local", "Settings", "settings.json");
    if (!File.Exists(path))
    {
        return null;
    }
    foreach (string line in File.ReadLines(path))
    {
        int separator = line.IndexOf('|');
        if (separator <= 0)
        {
            continue;
        }
        try
        {
            if (string.Equals(DecodeBase64(line[..separator]), key, StringComparison.Ordinal))
            {
                return DecodeBase64(line[(separator + 1)..]);
            }
        }
        catch (FormatException)
        {
        }
    }
    return null;
}

static string WaitForCredential(string dataRoot, string userName, TimeSpan timeout)
{
    string? value = null;
    WaitUntil(
        $"credential '{userName}'",
        () => ReadAuthCredentials(dataRoot).TryGetValue(userName, out value),
        timeout);
    return value!;
}

static void AssertAuthMarker(string dataRoot, string marker) =>
    _ = WaitForAuthMarkerValue(dataRoot, marker, TimeSpan.FromSeconds(8));

static string WaitForAuthMarkerValue(string dataRoot, string marker, TimeSpan timeout)
{
    string? value = null;
    WaitUntil(
        $"auth marker '{marker}'",
        () => TryReadAuthMarker(dataRoot, marker, out value),
        timeout);
    return value ?? string.Empty;
}

static bool TryReadAuthMarker(string dataRoot, string marker, out string? value)
{
    value = null;
    string path = Path.Combine(dataRoot, "Local", "AuthLifecycle", "scenario-state.ndjson");
    if (!File.Exists(path))
    {
        return false;
    }
    foreach (string line in File.ReadLines(path).Reverse())
    {
        string[] parts = line.Split('\t');
        if (parts.Length == 3 && string.Equals(parts[1], marker, StringComparison.Ordinal))
        {
            try
            {
                value = DecodeBase64(parts[2]);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
    return false;
}

static string DecodeBase64(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

static void AssertLoginLiveUi(Window window)
{
    AutomationElement root = WaitForElement(
        "LoginRoot",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("LoginRoot")),
        TimeSpan.FromSeconds(8));
    AutomationElement signInButton = AssertNamedAutomationElement(
        window,
        "LoginSignInButton",
        ControlType.Button);
    AutomationElement status = AssertNamedAutomationElement(
        window,
        "LoginStatusText",
        ControlType.Text);

    AssertProbe(IsVisible(root), "The login root was not visible in the live UIA tree.");
    AssertProbe(string.Equals(signInButton.Name, "Continue with GitHub", StringComparison.Ordinal),
        $"The login button exposed the unexpected accessible name '{signInButton.Name}'.");
    AssertProbe(signInButton.IsEnabled, "The login button was not enabled.");
    AssertProbe(status.Name.Contains("opens GitHub", StringComparison.OrdinalIgnoreCase),
        "The login status did not describe the browser sign-in flow.");
}

static void AssertLoginThemeScreenshot(string filePath, string theme)
{
    using var bitmap = new Bitmap(filePath);
    AssertProbe(bitmap.Width >= 900 && bitmap.Height >= 600,
        $"The {theme} login artifact was unexpectedly small ({bitmap.Width}x{bitmap.Height}).");

    double backgroundLuminance = SampleLoginBackgroundLuminance(bitmap);
    if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase))
    {
        AssertProbe(backgroundLuminance >= 170,
            $"The light login artifact did not render a light app canvas (luminance={backgroundLuminance:0.0}).");
    }
    else if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
    {
        AssertProbe(backgroundLuminance <= 90,
            $"The dark login artifact did not render a dark app canvas (luminance={backgroundLuminance:0.0}).");
    }
    else
    {
        throw new InvalidOperationException($"Unsupported login screenshot theme '{theme}'.");
    }
}

static void AssertMarkdownSelectionForegroundVisible(
    string screenshotPath,
    Rectangle automationWindowBounds,
    Rectangle physicalWindowBounds,
    Rectangle selectedTextBounds,
    string hostName)
{
    using var bitmap = new Bitmap(screenshotPath);
    double scaleX = physicalWindowBounds.Width / (double)Math.Max(1, automationWindowBounds.Width);
    double scaleY = physicalWindowBounds.Height / (double)Math.Max(1, automationWindowBounds.Height);
    Rectangle localSelection = new(
        (int)Math.Round((selectedTextBounds.Left - automationWindowBounds.Left) * scaleX),
        (int)Math.Round((selectedTextBounds.Top - automationWindowBounds.Top) * scaleY),
        Math.Max(1, (int)Math.Round(selectedTextBounds.Width * scaleX)),
        Math.Max(1, (int)Math.Round(selectedTextBounds.Height * scaleY)));
    localSelection.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
    AssertProbe(localSelection.Width > 8 && localSelection.Height > 8,
        $"{hostName}: selected Markdown marker was outside the captured app window. " +
        $"Selection={selectedTextBounds}; automationWindow={automationWindowBounds}; " +
        $"physicalWindow={physicalWindowBounds}; local={localSelection}; " +
        $"bitmap={bitmap.Width}x{bitmap.Height}.");

    int brightForegroundPixels = 0;
    for (int y = localSelection.Top; y < localSelection.Bottom; y++)
    {
        for (int x = localSelection.Left; x < localSelection.Right; x++)
        {
            Color color = bitmap.GetPixel(x, y);
            double luminance = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
            int channelSpread = Math.Max(color.R, Math.Max(color.G, color.B)) -
                                Math.Min(color.R, Math.Min(color.G, color.B));
            if (luminance >= 210 && channelSpread <= 45)
            {
                brightForegroundPixels++;
            }
        }
    }

    AssertProbe(brightForegroundPixels >= 24,
        $"{hostName}: selected Markdown highlight did not contain visible foreground glyphs " +
        $"({brightForegroundPixels} bright foreground pixels).");
}

static void AssertLoginThemeContrast(string lightPath, string darkPath)
{
    using var light = new Bitmap(lightPath);
    using var dark = new Bitmap(darkPath);
    double lightLuminance = SampleLoginBackgroundLuminance(light);
    double darkLuminance = SampleLoginBackgroundLuminance(dark);
    AssertProbe(lightLuminance - darkLuminance >= 90,
        $"Light and dark login artifacts were not visually distinct enough (light={lightLuminance:0.0}, dark={darkLuminance:0.0}).");
}

static double SampleLoginBackgroundLuminance(Bitmap bitmap)
{
    (double X, double Y)[] points =
    [
        (0.12, 0.18),
        (0.24, 0.30),
        (0.82, 0.22),
        (0.88, 0.52),
        (0.18, 0.78),
        (0.76, 0.82)
    ];

    return points.Average(point =>
    {
        int x = Math.Clamp((int)Math.Round((bitmap.Width - 1) * point.X), 0, bitmap.Width - 1);
        int y = Math.Clamp((int)Math.Round((bitmap.Height - 1) * point.Y), 0, bitmap.Height - 1);
        Color color = bitmap.GetPixel(x, y);
        return (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
    });
}

static void RunCommandSearchProbe(CaptureOptions options)
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

static void RunRepositorySearchResponsiveProbe(CaptureOptions options)
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

static void RunKeyboardAccessibilityMatrixProbe(CaptureOptions options)
{
    // Keep each keyboard surface in its own deterministic preview process. A route or
    // popup failure cannot leave focus in an unknown window for the following pass.
    RunKeyboardListTraversalMatrix(options);
    RunKeyboardModeSelectorMatrix(options);
    RunKeyboardAdaptiveDrawerMatrix(options);
    RunKeyboardContextMenuMatrix(options);
    RunKeyboardDialogMatrix(options);
    RunKeyboardMarkdownMatrix(options);
    RunKeyboardCommitDiffSearchMatrix(options);
}

static void RunKeyboardListTraversalMatrix(CaptureOptions options)
{
    using var app = LaunchApplication(options.AppPath, "--page=my-issues", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "keyboard matrix list traversal");
        ResizeWindow(window, 1366, 900);
        AutomationElement list = WaitForElement(
            "MyIssuesList keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "MyIssuesList"),
            TimeSpan.FromSeconds(10));
        AutomationElement[] rows = list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .Where(IsVisible)
            .Take(3)
            .ToArray();
        AssertProbe(rows.Length >= 2, "Keyboard list traversal requires at least two My Issues rows.");
        AssertProbe(
            rows.All(row => row.Patterns.SelectionItem.IsSupported && row.Properties.IsKeyboardFocusable.ValueOrDefault),
            "My Issues rows did not expose native selectable, keyboard-focusable list-item semantics.");

        rows[0].Patterns.SelectionItem.Pattern.Select();
        rows[0].FocusNative();
        WaitUntil("first issue row focus", () => IsElementFocused(rows[0]), TimeSpan.FromSeconds(3));
        Keyboard.Press(VirtualKeyShort.DOWN);
        WaitUntil(
            "Down selects the next issue row",
            () => rows[1].Patterns.SelectionItem.Pattern.IsSelected.Value,
            TimeSpan.FromSeconds(3));
        Keyboard.Press(VirtualKeyShort.SPACE);
        AssertProbe(
            rows[1].Patterns.SelectionItem.Pattern.IsSelected.Value,
            "Space did not preserve the active native issue-row selection.");
        Keyboard.Press(VirtualKeyShort.UP);
        WaitUntil(
            "Up returns to the previous issue row",
            () => rows[0].Patterns.SelectionItem.Pattern.IsSelected.Value,
            TimeSpan.FromSeconds(3));
        Keyboard.Press(VirtualKeyShort.DOWN);
        Keyboard.Press(VirtualKeyShort.ENTER);
        WaitForElement(
            "MyIssuesDetailTitle after Enter",
            () => FindCurrentVisibleByAutomationId(window, "MyIssuesDetailTitle"),
            TimeSpan.FromSeconds(8));

        AutomationElement scopeSelector = WaitForElement(
            "MyIssuesFilter_Assigned keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "MyIssuesFilter_Assigned"),
            TimeSpan.FromSeconds(5));
        scopeSelector.FocusNative();
        WaitUntil("issue scope receives focus", () => IsElementFocused(scopeSelector), TimeSpan.FromSeconds(3));
        Keyboard.Press(VirtualKeyShort.TAB);
        WaitUntil(
            "Tab advances from the My Issues scope selector",
            () => !IsElementFocused(scopeSelector),
            TimeSpan.FromSeconds(3));
        using (Keyboard.Pressing(VirtualKeyShort.LSHIFT))
        {
            Keyboard.Press(VirtualKeyShort.TAB);
        }
        WaitUntil(
            "Shift+Tab returns to issue scope",
            () => IsElementFocused(scopeSelector) ||
                string.Equals(GetAutomationId(automation.FocusedElement()), "MyIssuesFilter_Assigned", StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "keyboard-matrix-list-traversal.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunKeyboardModeSelectorMatrix(CaptureOptions options)
{
    using var app = LaunchApplication(options.AppPath, "--page=profile", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "keyboard matrix mode selectors");
        ResizeWindow(window, 1180, 800);
        AutomationElement overview = WaitForElement(
            "ProfileModeOverviewItem keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "ProfileModeOverviewItem"),
            TimeSpan.FromSeconds(10));
        AutomationElement repositories = WaitForElement(
            "ProfileModeRepositoriesItem keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "ProfileModeRepositoriesItem"),
            TimeSpan.FromSeconds(5));
        AssertProbe(
            overview.Patterns.SelectionItem.IsSupported && repositories.Patterns.SelectionItem.IsSupported,
            "Profile mode selector did not expose native SelectionItem semantics.");

        overview.FocusNative();
        WaitUntil("Profile overview receives focus", () => IsElementFocused(overview), TimeSpan.FromSeconds(3));
        Keyboard.Press(VirtualKeyShort.RIGHT);
        WaitUntil(
            "Right selects repositories mode",
            () => repositories.Patterns.SelectionItem.Pattern.IsSelected.Value &&
                IsVisible(FindCurrentVisibleByAutomationId(window, "ProfileRepositoriesList")),
            TimeSpan.FromSeconds(8));
        Keyboard.Press(VirtualKeyShort.LEFT);
        WaitUntil(
            "Left returns to profile overview",
            () => overview.Patterns.SelectionItem.Pattern.IsSelected.Value &&
                IsVisible(FindCurrentVisibleByAutomationId(window, "ProfileOverviewScrollViewer")),
            TimeSpan.FromSeconds(8));

        AutomationElement activity = WaitForElement(
            "ProfileModeActivityItem keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "ProfileModeActivityItem"),
            TimeSpan.FromSeconds(5));
        activity.FocusNative();
        Keyboard.Press(VirtualKeyShort.SPACE);
        WaitUntil(
            "Space selects profile activity",
            () => activity.Patterns.SelectionItem.Pattern.IsSelected.Value &&
                IsVisible(FindCurrentVisibleByAutomationId(window, "ProfileActivityList")),
            TimeSpan.FromSeconds(8));

        AutomationElement readme = WaitForElement(
            "ProfileModeReadmeItem keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "ProfileModeReadmeItem"),
            TimeSpan.FromSeconds(5));
        readme.FocusNative();
        Keyboard.Press(VirtualKeyShort.ENTER);
        WaitUntil(
            "Enter selects profile README",
            () => readme.Patterns.SelectionItem.Pattern.IsSelected.Value &&
                IsVisible(FindCurrentVisibleByAutomationId(window, "ProfileReadmeScrollViewer")),
            TimeSpan.FromSeconds(8));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "keyboard-matrix-mode-selector.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunKeyboardAdaptiveDrawerMatrix(CaptureOptions options)
{
    using var app = LaunchApplication(options.AppPath, "--page=my-issues", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "keyboard matrix adaptive drawers");
        ResizeWindow(window, 640, 600);
        _ = WaitForElement(
            "MyIssuesAdaptiveWorkspace keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "MyIssuesAdaptiveWorkspace"),
            TimeSpan.FromSeconds(10));
        ExerciseAdaptivePaneWithKeyboard(
            window,
            "MyIssues",
            leading: true,
            "MyIssuesList",
            VirtualKeyShort.ENTER);
        ExerciseAdaptivePaneWithKeyboard(
            window,
            "MyIssues",
            leading: false,
            "MyIssuesInspector",
            VirtualKeyShort.SPACE);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "keyboard-matrix-adaptive-drawers.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void ExerciseAdaptivePaneWithKeyboard(
    Window window,
    string prefix,
    bool leading,
    string paneContentId,
    VirtualKeyShort activationKey)
{
    AutomationElement opener = WaitForElement(
        $"{prefix} {(leading ? "leading" : "trailing")} keyboard opener",
        () => FindAdaptivePaneButton(window, prefix, leading),
        TimeSpan.FromSeconds(8));
    FocusForKeyboardActivation(window, opener);
    Keyboard.Type([activationKey]);
    string drawerId = $"{prefix}{(leading ? "Left" : "Right")}Drawer";
    _ = WaitForElement(
        drawerId,
        () => FindCurrentVisibleByAutomationId(window, drawerId),
        TimeSpan.FromSeconds(8));
    _ = WaitForElement(
        paneContentId,
        () => FindCurrentVisibleByAutomationId(window, paneContentId) ??
            FindAdaptivePaneCloseButton(window, prefix, leading),
        TimeSpan.FromSeconds(8));
    WaitForDrawerSettled(window, prefix, leading);
    AssertDrawerKeyboardFocusContained(window, drawerId, $"{prefix} keyboard drawer");
    Keyboard.Press(VirtualKeyShort.ESCAPE);
    WaitUntil(
        $"{prefix} keyboard drawer closes with Escape",
        () => !IsVisible(FindCurrentVisibleByAutomationId(window, drawerId)),
        TimeSpan.FromSeconds(5));
    AssertPaneFocusReturned(window, prefix, leading, $"{prefix} keyboard drawer Escape");
}

static void RunKeyboardContextMenuMatrix(CaptureOptions options)
{
    using var app = LaunchApplication(options.AppPath, "--page=gists", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "keyboard matrix context menus");
        ResizeWindow(window, 1180, 800);
        AutomationElement list = WaitForElement(
            "GistsList keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "GistsList"),
            TimeSpan.FromSeconds(10));
        AutomationElement row = WaitForElement(
            "Gist row keyboard matrix",
            () => list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(10));
        row.FocusNative();
        WaitUntil("Gist row receives focus", () => IsElementFocused(row), TimeSpan.FromSeconds(3));
        using (Keyboard.Pressing(VirtualKeyShort.LSHIFT))
        {
            Keyboard.Press(VirtualKeyShort.F10);
        }
        AutomationElement edit = WaitForElement(
            "GistsContextEdit from Shift+F10",
            () => automation.GetDesktop().FindAllDescendants(cf => cf.ByAutomationId("GistsContextEdit"))
                .FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(5));
        AssertProbe(IsVisible(edit), "Shift+F10 did not expose the Gist row context menu.");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitUntil("Gist context menu closes with Escape", () => !IsVisible(edit), TimeSpan.FromSeconds(4));
        WaitUntil("Gist context menu restores row focus", () => IsElementFocused(row), TimeSpan.FromSeconds(4));
        Thread.Sleep(600);

        row = WaitForElement(
            "current Gist row after context-menu dismissal",
            () => list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(4));
        FocusForKeyboardActivation(window, row);
        using (Keyboard.Pressing(VirtualKeyShort.LSHIFT))
        {
            Keyboard.Press(VirtualKeyShort.F10);
        }
        Thread.Sleep(350);
        CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "keyboard-matrix-context-menu-reopen.png"));
        foreach (AutomationElement menuElement in automation.GetDesktop().FindAllDescendants()
            .Where(element => GetAutomationId(element).StartsWith("GistsContext", StringComparison.Ordinal)))
        {
            Console.WriteLine($"keyboard context reopen: {GetAutomationId(menuElement)} visible={IsVisible(menuElement)}");
        }
        AutomationElement copy = WaitForElement(
            "GistsContextCopyLink from keyboard context menu",
            () => automation.GetDesktop().FindAllDescendants(cf => cf.ByAutomationId("GistsContextCopyLink"))
                .FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(5));
        NativeMethods.SetClipboardText("__jithub_keyboard_context_copy_pending__");
        FocusForKeyboardActivation(window, copy);
        Keyboard.Type([VirtualKeyShort.ENTER]);
        WaitUntil(
            "Gist context Copy link keyboard activation",
            () => NativeMethods.GetClipboardText().Contains("gist.github.com", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(5));
        WaitUntil("Gist Copy link restores row focus", () => IsElementFocused(row), TimeSpan.FromSeconds(4));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "keyboard-matrix-context-menu.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunKeyboardDialogMatrix(CaptureOptions options)
{
    using var app = LaunchApplication(options.AppPath, "--page=settings", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "keyboard matrix dialogs");
        ResizeWindow(window, 1180, 800);
        OpenSettingsSection(window, "SettingsSection_general", "SettingsSignOutButton");
        AutomationElement opener = WaitForElement(
            "SettingsSignOutButton keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "SettingsSignOutButton"),
            TimeSpan.FromSeconds(5));
        FocusForKeyboardActivation(window, opener);
        Keyboard.Type([VirtualKeyShort.ENTER]);
        Thread.Sleep(350);
        CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "keyboard-matrix-dialog-after-enter.png"));
        AutomationElement dialog = WaitForElement(
            "SignOutConfirmationDialog keyboard matrix",
            () => FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog"),
            TimeSpan.FromSeconds(5));
        AssertDialogFocusContained(automation, dialog, "Keyboard matrix sign-out dialog");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitUntil(
            "Sign-out dialog closes with Escape",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog")),
            TimeSpan.FromSeconds(5));
        WaitUntil("Sign-out dialog restores opener focus", () => IsElementFocused(opener), TimeSpan.FromSeconds(5));

        FocusForKeyboardActivation(window, opener);
        Keyboard.Type([VirtualKeyShort.SPACE]);
        dialog = WaitForElement(
            "SignOutConfirmationDialog Space cycle",
            () => FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog"),
            TimeSpan.FromSeconds(5));
        AutomationElement cancel = FindDialogButton(dialog, automation, "Cancel");
        FocusForKeyboardActivation(window, cancel);
        Keyboard.Type([VirtualKeyShort.ENTER]);
        WaitUntil(
            "Sign-out dialog Cancel closes with Enter",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog")),
            TimeSpan.FromSeconds(5));
        WaitUntil("Sign-out Cancel restores opener focus", () => IsElementFocused(opener), TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "keyboard-matrix-dialog.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunKeyboardMarkdownMatrix(CaptureOptions options)
{
    const string hostId = "MarkdownHost_Conversation_RepoIssuesBody";
    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-issues",
        "--theme=dark",
        $"--repo={options.RepositoryFullName}",
        "--markdown-lifecycle-fixture",
        $"--markdown-lifecycle-host={hostId}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "keyboard matrix Markdown");
        ResizeWindow(window, 900, 700);
        PrepareRealMarkdownHost(
            window,
            new MarkdownLifecycleTarget("issue-body", "repo-issues", hostId, false),
            "keyboard-markdown");
        AutomationElement host = WaitForElement(
            "Markdown keyboard matrix host",
            () => window.FindAllDescendants().FirstOrDefault(element =>
                string.Equals(GetAutomationId(element), hostId, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(12));
        RevealForInteraction(host, "Markdown keyboard matrix host");
        var textPattern = host.Patterns.Text.PatternOrDefault
            ?? throw new InvalidOperationException("Markdown keyboard matrix host did not expose TextPattern.");
        var range = textPattern.DocumentRange.FindText(
            "Markdown audit selection marker",
            backward: false,
            ignoreCase: false)
            ?? throw new InvalidOperationException("Markdown keyboard matrix selection marker was missing.");
        range.ScrollIntoView(alignToTop: true);
        range.Select();
        host.FocusNative();
        NativeMethods.SetClipboardText("__jithub_keyboard_markdown_copy_pending__");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        WaitUntil(
            "Markdown Ctrl+C copies selected text",
            () => NativeMethods.GetClipboardText().Contains("Markdown", StringComparison.Ordinal),
            TimeSpan.FromSeconds(4));

        Keyboard.Press((VirtualKeyShort)0x5D);
        AutomationElement copy = WaitForElement(
            "Markdown keyboard context Copy",
            () => automation.GetDesktop().FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                .FirstOrDefault(item => IsVisible(item) && string.Equals(GetElementName(item), "Copy", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(5));
        AssertProbe(
            string.Concat(textPattern.GetSelection().Select(selection => selection.GetText(-1)))
                .Contains("Markdown", StringComparison.Ordinal),
            "Opening the Markdown context menu key revoked the text selection.");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitUntil("Markdown context menu closes with Escape", () => !IsVisible(copy), TimeSpan.FromSeconds(4));
        WaitUntil("Markdown context menu restores host focus", () => IsElementFocused(host), TimeSpan.FromSeconds(4));

        Keyboard.Press((VirtualKeyShort)0x5D);
        copy = WaitForElement(
            "Markdown context Copy Enter cycle",
            () => automation.GetDesktop().FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                .FirstOrDefault(item => IsVisible(item) && string.Equals(GetElementName(item), "Copy", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(5));
        NativeMethods.SetClipboardText("__jithub_keyboard_markdown_context_pending__");
        FocusForKeyboardActivation(window, copy);
        Keyboard.Type([VirtualKeyShort.ENTER]);
        WaitUntil(
            "Markdown context Copy activates with Enter",
            () => NativeMethods.GetClipboardText().Contains("Markdown", StringComparison.Ordinal),
            TimeSpan.FromSeconds(4));

        host = WaitForElement(
            "Markdown keyboard matrix host after copy",
            () => window.FindAllDescendants().FirstOrDefault(element =>
                string.Equals(GetAutomationId(element), hostId, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        RevealForInteraction(host, "Markdown keyboard matrix host after copy");
        AutomationElement link = host.FindAllDescendants(cf => cf.ByControlType(ControlType.Hyperlink))
            .FirstOrDefault(candidate => GetElementName(candidate).Contains("keyboard link", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Markdown keyboard matrix did not expose its keyboard link.");
        AssertProbe(
            link.Properties.IsKeyboardFocusable.ValueOrDefault && link.Patterns.Invoke.IsSupported,
            "Markdown link did not expose keyboard-focusable hyperlink invocation semantics.");
        link.FocusNative();
        WaitUntil("Markdown link receives focus", () => IsElementFocused(link), TimeSpan.FromSeconds(3));
        Keyboard.Press(VirtualKeyShort.ENTER);
        WaitUntil(
            "Markdown link responds to Enter",
            () => !IsElementFocused(link) || IsVisible(FindCurrentVisibleByAutomationId(window, "RepoDetailIdentity")),
            TimeSpan.FromSeconds(5));
        AssertProbe(!app.HasExited, "Keyboard activation of a Markdown link terminated JitHub.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "keyboard-matrix-markdown.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunKeyboardCommitDiffSearchMatrix(CaptureOptions options)
{
    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-commits",
        "--theme=dark",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "keyboard matrix commit diff search");
        ResizeWindow(window, 1180, 800);
        EnsureCommitDetailVisible(window);
        EnsureCommitDiffVisible(window);
        AutomationElement search = WaitForElement(
            "RepoCommitsDiffSearchBox keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "RepoCommitsDiffSearchBox"),
            TimeSpan.FromSeconds(10));
        search.FocusNative();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("a");
        AutomationElement count = WaitForElement(
            "RepoCommitsDiffSearchMatchCount keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "RepoCommitsDiffSearchMatchCount"),
            TimeSpan.FromSeconds(8));
        AutomationElement previous = WaitForElement(
            "RepoCommitsPreviousDiffMatchButton keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "RepoCommitsPreviousDiffMatchButton"),
            TimeSpan.FromSeconds(8));
        AutomationElement next = WaitForElement(
            "RepoCommitsNextDiffMatchButton keyboard matrix",
            () => FindCurrentVisibleByAutomationId(window, "RepoCommitsNextDiffMatchButton"),
            TimeSpan.FromSeconds(8));
        WaitUntil(
            "commit diff search produces keyboard-navigable matches",
            () => previous.IsEnabled && next.IsEnabled && !string.IsNullOrWhiteSpace(GetElementName(count)),
            TimeSpan.FromSeconds(8));

        search.FocusNative();
        Keyboard.Press(VirtualKeyShort.TAB);
        WaitUntil(
            "Tab advances from diff search to previous match",
            () => IsElementFocused(previous),
            TimeSpan.FromSeconds(3));
        using (Keyboard.Pressing(VirtualKeyShort.LSHIFT))
        {
            Keyboard.Press(VirtualKeyShort.TAB);
        }
        WaitUntil("Shift+Tab returns to diff search", () => IsElementFocused(search), TimeSpan.FromSeconds(3));

        string before = GetElementName(count);
        next.FocusNative();
        WaitUntil("Next diff match button receives focus", () => IsElementFocused(next), TimeSpan.FromSeconds(3));
        Keyboard.Press(VirtualKeyShort.ENTER);
        Thread.Sleep(500);
        string afterEnter = FindCurrentVisibleByAutomationId(window, "RepoCommitsDiffSearchMatchCount") is { } liveCount
            ? GetElementName(liveCount)
            : string.Empty;
        Console.WriteLine($"commit diff keyboard next: before='{before}', after-enter='{afterEnter}', focused={IsElementFocused(next)}, invoke={next.Patterns.Invoke.IsSupported}");
        if (string.Equals(afterEnter, before, StringComparison.Ordinal))
        {
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "keyboard-matrix-commit-diff-search-enter-failed.png"));
            if (next.Patterns.Invoke.IsSupported)
            {
                next.Patterns.Invoke.Pattern.Invoke();
                Thread.Sleep(500);
                string afterInvoke = FindCurrentVisibleByAutomationId(window, "RepoCommitsDiffSearchMatchCount") is { } invokedCount
                    ? GetElementName(invokedCount)
                    : string.Empty;
                Console.WriteLine($"commit diff keyboard next: after-invoke='{afterInvoke}'");
            }

            throw new InvalidOperationException("Enter did not advance commit diff search.");
        }
        previous.FocusNative();
        WaitUntil("Previous diff match button receives focus", () => IsElementFocused(previous), TimeSpan.FromSeconds(3));
        Keyboard.Press(VirtualKeyShort.SPACE);
        WaitUntil(
            "Space returns to previous commit diff match",
            () => FindCurrentVisibleByAutomationId(window, "RepoCommitsDiffSearchMatchCount") is { } liveCount &&
                string.Equals(GetElementName(liveCount), before, StringComparison.Ordinal),
            TimeSpan.FromSeconds(4));
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        AssertProbe(!app.HasExited, "Escape from commit diff search terminated JitHub.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "keyboard-matrix-commit-diff-search.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunMyIssuesPageProbe(CaptureOptions options)
{
    bool isAttached = !string.IsNullOrWhiteSpace(options.AttachProcess);
    using var app = isAttached
        ? CreateProbeApplication(options)
        : LaunchApplication(options.AppPath, "--page=my-issues", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "my-issues-page probe");
        ResizeWindow(window, 1366, 900);
        AutomationElement list;
        try
        {
            list = WaitForElement(
                "MyIssuesList",
                () => FindCurrentVisibleByAutomationId(window, "MyIssuesList"),
                TimeSpan.FromSeconds(10));
        }
        catch
        {
            PrintVisibleAutomationIds(window, "my-issues-list-missing");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-list-missing.png"));
            throw;
        }
        AutomationElement issueToSelect = WaitForElement(
            "unselected issue row",
            () => list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Where(IsVisible)
                .Skip(1)
                .FirstOrDefault()
                ?? list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(10));
        AutomationElement stableRowIdentity = WaitForElement(
            "stable My Issues row identity",
            () => issueToSelect.FindAllDescendants()
                .FirstOrDefault(element => IsVisible(element) && GetAutomationId(element).StartsWith("MyWorkItem_", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        AssertProbe(!string.IsNullOrWhiteSpace(GetElementName(stableRowIdentity)), "My Issues row did not expose a stable accessible name.");
        Mouse.MoveTo(CenterPoint(issueToSelect, window));
        Thread.Sleep(350);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-row-hover.png"));
        issueToSelect.Click();
        Thread.Sleep(900);
        WaitForElement(
            "MyIssuesDetailTitle",
            () => FindCurrentVisibleByAutomationId(window, "MyIssuesDetailTitle"),
            TimeSpan.FromSeconds(10));
        AutomationElement? inspector = FindCurrentVisibleByAutomationId(window, "MyIssuesInspector");
        Console.WriteLine(IsVisible(inspector)
            ? "my-issues-page: inspector visible in desktop layout"
            : "my-issues-page: inspector collapsed in desktop layout");
        WaitForElement(
            "MyIssuesOpenInRepositoryButton",
            () => FindCurrentVisibleByAutomationId(window, "MyIssuesOpenInRepositoryButton"),
            TimeSpan.FromSeconds(8));
        AssertMarkdownTextVisible(window, "This preview item represents", "The cached detail path");
        ExerciseMyWorkItemMarkdownCopy(
            window,
            automation,
            "MarkdownHost_Conversation_MyIssuesBody",
            "This preview item represents",
            "My Issues body");
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
            string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
            AutomationElement workspace = WaitForElement(
                $"MyIssuesAdaptiveWorkspace at {viewportLabel}",
                () => FindCurrentVisibleByAutomationId(window, "MyIssuesAdaptiveWorkspace"),
                TimeSpan.FromSeconds(8));
            AssertProbe(IsVisible(workspace), $"My Issues workspace was not visible at {viewportLabel}.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, $"my-issues-page-{viewportLabel}.png"));
        }

        ResizeWindow(window, 1366, 900);
        ExerciseIssueListScrollSelection(window, list, options.OutputDirectory, "my-issues-page");

        ResizeWindow(window, 760, 650);
        WaitForElement(
            "MyIssuesDetailTitle",
            () => FindCurrentVisibleByAutomationId(window, "MyIssuesDetailTitle"),
            TimeSpan.FromSeconds(8));
        AssertProbe(!IsVisible(FindCurrentVisibleByAutomationId(window, "MyIssuesInspector")), "My Issues inspector stayed visible in compact layout.");
        ExerciseAdaptiveWorkspaceDrawers(window, "MyIssues", "MyIssuesList", "MyIssuesInspector");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-compact.png"));

        ResizeWindow(window, 1366, 900);

        AutomationElement createdFilter = WaitForElement(
            "MyIssuesFilter_Created",
            () => FindCurrentVisibleByAutomationId(window, "MyIssuesFilter_Created"),
            TimeSpan.FromSeconds(5));
        createdFilter.Click();
        Thread.Sleep(500);
        AutomationElement closedFilter = WaitForElement(
            "MyIssuesState_Closed",
            () => FindCurrentVisibleByAutomationId(window, "MyIssuesState_Closed"),
            TimeSpan.FromSeconds(5));
        closedFilter.Click();
        Thread.Sleep(900);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-filters.png"));
    }
    finally
    {
        if (!isAttached)
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    if (!isAttached)
    {
        RunMyIssuesPseudoLongLabelsProbe(options);
    }
}

static void RunMyPullRequestsPageProbe(CaptureOptions options)
{
    bool isAttached = !string.IsNullOrWhiteSpace(options.AttachProcess);
    using var app = isAttached
        ? CreateProbeApplication(options)
        : LaunchApplication(options.AppPath, "--page=my-pull-requests", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "my-pull-requests-page probe");
        ResizeWindow(window, 1366, 900);
        AutomationElement list;
        try
        {
            list = WaitForElement(
                "MyPullRequestsList",
                () => FindCurrentVisibleByAutomationId(window, "MyPullRequestsList"),
                TimeSpan.FromSeconds(20));
        }
        catch
        {
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-pull-requests-page-missing-list.png"));
            PrintVisibleAutomationIds(window, "my-pull-requests-page-missing-list");
            throw;
        }
        AutomationElement pullRequestToSelect = WaitForElement(
            "unselected My Pull Requests row",
            () => list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Where(IsVisible)
                .Skip(1)
                .FirstOrDefault()
                ?? list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(10));
        AutomationElement stableRowIdentity = WaitForElement(
            "stable My Pull Requests row identity",
            () => pullRequestToSelect.FindAllDescendants()
                .FirstOrDefault(element => IsVisible(element) && GetAutomationId(element).StartsWith("MyWorkItem_", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        AssertProbe(
            !string.IsNullOrWhiteSpace(GetElementName(stableRowIdentity)),
            "My Pull Requests row did not expose a stable accessible name.");

        Mouse.MoveTo(CenterPoint(pullRequestToSelect, window));
        Thread.Sleep(300);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-pull-requests-page-row-hover.png"));
        pullRequestToSelect.Click();
        WaitForElement(
            "MyPullRequestsDetailTitle",
            () => FindCurrentVisibleByAutomationId(window, "MyPullRequestsDetailTitle"),
            TimeSpan.FromSeconds(10));
        ExerciseMyWorkItemMarkdownCopy(
            window,
            automation,
            "MarkdownHost_Conversation_MyPullRequestsBody",
            "This preview item represents",
            "My Pull Requests body");

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
            string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
            AutomationElement workspace = WaitForElement(
                $"MyPullRequestsAdaptiveWorkspace at {viewportLabel}",
                () => FindCurrentVisibleByAutomationId(window, "MyPullRequestsAdaptiveWorkspace"),
                TimeSpan.FromSeconds(8));
            AssertProbe(IsVisible(workspace), $"My Pull Requests workspace was not visible at {viewportLabel}.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, $"my-pull-requests-page-{viewportLabel}.png"));
        }

        ResizeWindow(window, 1180, 800);
        AutomationElement commitsSection = WaitForElement(
            "MyPullRequestsSection_Commits",
            () => FindCurrentVisibleByAutomationId(window, "MyPullRequestsSection_Commits"),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(commitsSection);
        WaitForElement(
            "MyPullRequestsCommitsList",
            () =>
            {
                return FindCurrentVisibleByAutomationId(window, "MyPullRequestsCommitsList");
            },
            TimeSpan.FromSeconds(8));
        commitsSection.Focus();
        Keyboard.Press(VirtualKeyShort.RIGHT);
        WaitForElement(
            "MyPullRequestsReviewsList after keyboard section traversal",
            () =>
            {
                return FindCurrentVisibleByAutomationId(window, "MyPullRequestsReviewsList");
            },
            TimeSpan.FromSeconds(8));

        ResizeWindow(window, 760, 650);
        AssertProbe(
            !IsVisible(FindCurrentVisibleByAutomationId(window, "MyPullRequestsInspector")),
            "My Pull Requests inspector stayed inline in compact layout.");
        ExerciseAdaptiveWorkspaceDrawers(
            window,
            "MyPullRequests",
            "MyPullRequestsList",
            "MyPullRequestsInspector",
            requireAlignedCloseControls: false);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-pull-requests-page-keyboard-and-drawers.png"));
    }
    finally
    {
        if (!isAttached)
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    if (!isAttached)
    {
        RunMyPullRequestsPseudoLongLabelsProbe(options);
    }
}

static void RunMyPullRequestsPseudoLongLabelsProbe(CaptureOptions options)
{
    using var app = LaunchApplication(
        options.AppPath,
        "--page=my-pull-requests",
        "--scenario=my-pull-requests-pseudo-long-labels",
        "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "my-pull-requests pseudo-long-label probe");
        ResizeWindow(window, 640, 600);
        AutomationElement? compactPicker = FindCurrentVisibleByAutomationId(window, "MyPullRequestsStateCompactPicker");
        if (!IsVisible(compactPicker))
        {
            AutomationElement opener = new[] { "MyPullRequestsOpenListPaneButton", "MyPullRequestsLeadingPaneButton" }
                .Select(id => FindCurrentVisibleByAutomationId(window, id))
                .FirstOrDefault(IsVisible)
                ?? throw new InvalidOperationException("My Pull Requests list drawer opener was not visible for pseudo-long labels.");
            opener.Click();
        }
        try
        {
            compactPicker = WaitForElement(
                "visible My Pull Requests pseudo-long picker in compact drawer",
                () => FindCurrentVisibleByAutomationId(window, "MyPullRequestsStateCompactPicker"),
                TimeSpan.FromSeconds(5));
        }
        catch
        {
            PrintVisibleAutomationIds(window, "my-pull-requests-pseudo-long-picker-missing");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-pull-requests-page-pseudo-long-picker-missing.png"));
            throw;
        }
        TryActivateWindow(window);
        Thread.Sleep(600);
        compactPicker.FocusNative();
        Thread.Sleep(150);
        CaptureWindow(
            window,
            Path.Combine(options.OutputDirectory, "my-pull-requests-page-pseudo-long-initial.png"));
        Func<string> selectedStateName = () =>
        {
            AutomationElement? currentPicker = FindCurrentVisibleByAutomationId(
                window,
                "MyPullRequestsStateCompactPicker");
            if (currentPicker is null)
            {
                return string.Empty;
            }

            AutomationElement? selected = currentPicker.AsComboBox().SelectedItem;
            if (selected is null)
            {
                return string.Empty;
            }

            return selected.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Where(IsVisible)
                .Select(GetElementName)
                .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text))
                ?? GetElementName(selected);
        };
        AssertProbe(
            selectedStateName().Contains("Currently open pull requests involving", StringComparison.Ordinal),
            "My Pull Requests compact picker did not expose its full pseudo-long selected label.");
        compactPicker.FocusNative();
        Keyboard.Press(VirtualKeyShort.DOWN);
        WaitUntil(
            "pseudo-long My Pull Requests closed selection",
            () => selectedStateName().Contains("Previously closed pull requests involving", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-pull-requests-page-pseudo-long-closed.png"));
        compactPicker = WaitForElement(
            "current My Pull Requests pseudo-long picker",
            () => FindCurrentVisibleByAutomationId(window, "MyPullRequestsStateCompactPicker"),
            TimeSpan.FromSeconds(5));
        compactPicker.FocusNative();
        Keyboard.Press(VirtualKeyShort.UP);
        WaitUntil(
            "pseudo-long My Pull Requests open selection",
            () => selectedStateName().Contains("Currently open pull requests involving", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-pull-requests-page-pseudo-long-640x600.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunMyIssuesPseudoLongLabelsProbe(CaptureOptions options)
{
    using var app = LaunchApplication(
        options.AppPath,
        "--page=my-issues",
        "--scenario=my-issues-pseudo-long-labels",
        "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "my-issues pseudo-long-label probe");
        ResizeWindow(window, 1366, 900);
        AutomationElement scopePicker;
        try
        {
            scopePicker = WaitForElement(
                "MyIssuesScopeCompactPicker for pseudo-long labels",
                () =>
                {
                    AutomationElement? picker = window.FindFirstDescendant(cf => cf.ByAutomationId("MyIssuesScopeCompactPicker"));
                    return IsVisible(picker) ? picker : null;
                },
                TimeSpan.FromSeconds(10));
        }
        catch
        {
            PrintVisibleAutomationIds(window, "my-issues-pseudo-label-picker-missing");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-pseudo-label-picker-missing.png"));
            throw;
        }
        WaitForElement(
            "MyIssuesStateCompactPicker for pseudo-long labels",
            () =>
            {
                AutomationElement? picker = window.FindFirstDescendant(cf => cf.ByAutomationId("MyIssuesStateCompactPicker"));
                return IsVisible(picker) ? picker : null;
            },
            TimeSpan.FromSeconds(5));
        AssertProbe(
            !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("MyIssuesScopeSegmented"))),
            "Pseudo-long My Issues labels did not switch away from the segmented control.");
        scopePicker.AsComboBox().Expand();
        WaitForElement(
            "full pseudo-long My Issues scope option",
            () =>
            {
                AutomationElement? option = automation.GetDesktop().FindFirstDescendant(
                    cf => cf.ByText("Assigned to the authenticated account"));
                return IsVisible(option) ? option : null;
            },
            TimeSpan.FromSeconds(5));
        CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "my-issues-page-pseudo-long-picker-open.png"));
        scopePicker.AsComboBox().Collapse();
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-pseudo-long-wide.png"));

        ResizeWindow(window, 760, 650);
        AutomationElement? compactPicker = window.FindFirstDescendant(cf => cf.ByAutomationId("MyIssuesScopeCompactPicker"));
        if (!IsVisible(compactPicker))
        {
            AutomationElement opener = new[] { "MyIssuesOpenListPaneButton", "MyIssuesLeadingPaneButton" }
                .Select(id => window.FindFirstDescendant(cf => cf.ByAutomationId(id)))
                .FirstOrDefault(IsVisible)
                ?? throw new InvalidOperationException("My Issues list drawer opener was not visible for pseudo-long labels.");
            opener.Click();
        }

        WaitForElement(
            "visible pseudo-long My Issues picker in compact drawer",
            () =>
            {
                AutomationElement? picker = window.FindFirstDescendant(cf => cf.ByAutomationId("MyIssuesScopeCompactPicker"));
                return IsVisible(picker) ? picker : null;
            },
            TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "my-issues-page-pseudo-long-compact.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunRepoIssuesPageProbe(CaptureOptions options)
{
    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-issues",
        "--theme=dark",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "repo-issues-page probe");
        ResizeWindow(window, 1600, 1000);

        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-issues-page-launch.png"));

        AutomationElement list;
        try
        {
            list = WaitForElement(
                "RepoIssuesList",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesList")),
                TimeSpan.FromSeconds(18));
        }
        catch
        {
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-issues-page-missing-list.png"));
            PrintVisibleAutomationIds(window, "repo-issues-page");
            throw;
        }
        AutomationElement detailTitle = WaitForElement(
            "RepoIssuesDetailTitle",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesDetailTitle")),
            TimeSpan.FromSeconds(18));
        AutomationElement? firstIssue = list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem));
        if (firstIssue is not null)
        {
            Mouse.MoveTo(CenterPoint(firstIssue, window));
            Thread.Sleep(350);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-issues-page-row-hover.png"));
            firstIssue.Click();
            Thread.Sleep(900);
        }

        AssertProbe(IsVisible(detailTitle), "Repository issue detail title was not visible.");
        AssertMarkdownTextVisible(
            window,
            "This public preview issue demonstrates cached, responsive repository issue navigation.",
            "The cached issue detail stays visible while its discussion refreshes.");
        AssertRepoIssuePermissionSurface(window, automation, options.OutputDirectory);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-issues-page-wide.png"));
        ExerciseIssueListScrollSelection(window, list, options.OutputDirectory, "repo-issues-page");
        ExerciseRepoIssueFiltersAndCommentEditor(window, options.OutputDirectory);

        ResizeWindow(window, 760, 650);
        WaitForElement(
            "RepoIssuesDetailTitle compact",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesDetailTitle")),
            TimeSpan.FromSeconds(8));
        AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesInspector"))), "Repository issue inspector stayed visible in compact layout.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-issues-page-compact-before-drawers.png"));
        AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesList"))), "Repository issue list stayed inline below its coordinated compact breakpoint.");
        AssertRepoIssueCompactActionOverflow(window, automation);
        ExerciseAdaptiveWorkspaceDrawers(
            window,
            "RepoIssues",
            "RepoIssuesList",
            "RepoIssuesInspector");
        ResizeWindow(window, 640, 600);
        WaitForElement(
            "RepoIssuesDetailTitle narrow",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesDetailTitle")),
            TimeSpan.FromSeconds(8));
        AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesList"))), "Repository issue list stayed inline at 640px.");
        ExerciseAdaptiveWorkspaceDrawers(window, "RepoIssues", "RepoIssuesList", "RepoIssuesInspector");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-issues-page-compact.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void AssertRepoIssuePermissionSurface(Window window, UIA3Automation automation, string outputDirectory)
{
    bool openedInspectorDrawer = !IsVisible(
        FindCurrentVisibleByAutomationId(window, "RepoIssuesInspectorMetadataButton"));
    if (openedInspectorDrawer)
    {
        AutomationElement inspectorOpener = WaitForElement(
            "repository issue inspector opener",
            () => FindCurrentVisibleByAutomationId(window, "RepoIssuesOpenInspectorPaneButton") ??
                  FindCurrentVisibleByAutomationId(window, "RepoIssuesCompactOpenInspectorPaneButton"),
            TimeSpan.FromSeconds(8));
        InvokeOrClick(inspectorOpener);
        WaitForElement(
            "RepoIssuesInspectorMetadataButton in drawer",
            () => FindCurrentVisibleByAutomationId(window, "RepoIssuesInspectorMetadataButton"),
            TimeSpan.FromSeconds(8));
    }

    string[] actionIds =
    [
        "RepoIssuesNewIssueButton",
        "RepoIssuesEditButton",
        "RepoIssuesToggleStateButton",
        "RepoIssuesInspectorMetadataButton",
        "RepoIssuesOpenCommentButton"
    ];

    foreach (string actionId in actionIds)
    {
        AutomationElement action = WaitForElement(
            actionId,
            () => FindCurrentVisibleByAutomationId(window, actionId),
            TimeSpan.FromSeconds(8));
        AssertProbe(
            !action.IsEnabled,
            $"Repository Issues write action '{actionId}' was enabled for the read-only public preview viewer.");
    }

    if (openedInspectorDrawer)
    {
        AutomationElement closeInspector = WaitForElement(
            "RepoIssuesCloseInspectorPaneButton",
            () => FindCurrentVisibleByAutomationId(window, "RepoIssuesCloseInspectorPaneButton"),
            TimeSpan.FromSeconds(8));
        InvokeOrClick(closeInspector);
        WaitUntil(
            "repository issue inspector drawer to close",
            () => !IsVisible(FindCurrentVisibleByAutomationId(window, "RepoIssuesInspectorMetadataButton")),
            TimeSpan.FromSeconds(8));
    }

    AutomationElement edit = FindCurrentVisibleByAutomationId(window, "RepoIssuesEditButton")!;
    Mouse.MoveTo(CenterPoint(edit, window));
    Thread.Sleep(250);
    CaptureWindow(window, Path.Combine(outputDirectory, "repo-issues-page-action-hover.png"));

    AutomationElement bodyInteractions = WaitForElement(
        "RepoIssuesBodyInteractionBar",
        () => FindCurrentVisibleByAutomationId(window, "RepoIssuesBodyInteractionBar"),
        TimeSpan.FromSeconds(8));
    AssertProbe(
        string.Equals(bodyInteractions.Name, "Issue body actions", StringComparison.Ordinal),
        "Repository Issues body did not expose the shared interaction surface.");
    AssertProbe(
        FindCurrentVisibleByAutomationId(window, "CommentAddReactionButton") is null,
        "Repository Issues exposed an enabled reaction picker for the read-only public preview viewer.");
    Console.WriteLine("repo-issues: public preview exposes issue content while every write capability remains disabled.");
    _ = automation;
}

static void AssertRepoIssueCompactActionOverflow(Window window, UIA3Automation automation)
{
    AutomationElement overflow = WaitForElement(
        "RepoIssuesCompactActionOverflowButton",
        () => FindCurrentVisibleByAutomationId(window, "RepoIssuesCompactActionOverflowButton"),
        TimeSpan.FromSeconds(8));
    AssertProbe(
        string.Equals(overflow.Name, "More issue actions", StringComparison.Ordinal),
        $"Compact issue overflow exposed the unexpected name '{overflow.Name}'.");

    InvokeOrClick(overflow);
    string[] menuActionIds =
    [
        "RepoIssuesCompactEditAction",
        "RepoIssuesCompactMetadataAction",
        "RepoIssuesCompactToggleStateAction"
    ];
    foreach (string actionId in menuActionIds)
    {
        WaitForElement(
            actionId,
            () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId(actionId)),
            TimeSpan.FromSeconds(5));
    }

    Keyboard.Press(VirtualKeyShort.ESCAPE);
    Thread.Sleep(150);
}

static void RunIssuesResponsiveWorkspaceProbe(CaptureOptions options)
{
    (string Page, string Prefix, string ListId, string DetailId, string InspectorId)[] targets =
    [
        ("my-issues", "MyIssues", "MyIssuesList", "MyIssuesDetailTitle", "MyIssuesInspectorTitle"),
        ("repo-issues", "RepoIssues", "RepoIssuesList", "RepoIssuesDetailTitle", "RepoIssuesInspectorTitle")
    ];

    (int Width, int Height)[] sizes =
    [
        (1600, 1000),
        (1366, 900),
        (1180, 800),
        (900, 700),
        (760, 650),
        (640, 600)
    ];
    foreach ((string page, string prefix, string listId, string detailId, string inspectorId) in targets)
    {
        using var app = page == "repo-issues"
            ? LaunchApplication(options.AppPath, $"--page={page}", "--theme=dark", $"--repo={options.RepositoryFullName}")
            : LaunchApplication(options.AppPath, $"--page={page}", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, $"issues-responsive-workspace {page}");
            bool? previousRailInline = null;
            bool? previousLeadingInline = null;
            bool? previousTrailingInline = null;
            foreach ((int width, int height) in sizes)
            {
                Rectangle resizedBounds = ResizeWindow(window, width, height);
                int actualWidth = resizedBounds.Width;
                string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
                WaitForElement(detailId, () => window.FindFirstDescendant(cf => cf.ByAutomationId(detailId)), TimeSpan.FromSeconds(12));
                AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId(detailId))), $"{page}: primary detail was not visible at {viewportLabel}.");

                bool railInline = IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellNav_home")));
                bool leadingInline = IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId(listId)));
                bool trailingInline = window.FindAllDescendants(cf => cf.ByAutomationId(inspectorId)).Any(IsVisible);
                AssertProbe(
                    previousRailInline is not false || !railInline,
                    $"{page}: narrowing to {viewportLabel} made the shell rail reappear.");
                AssertProbe(
                    previousLeadingInline is not false || !leadingInline,
                    $"{page}: narrowing to {viewportLabel} made the leading pane reappear.");
                AssertProbe(
                    previousTrailingInline is not false || !trailingInline,
                    $"{page}: narrowing to {viewportLabel} made the inspector reappear.");
                AssertProbe(
                    !trailingInline || (railInline && leadingInline),
                    $"{page}: inspector remained inline after an earlier-priority pane collapsed at {viewportLabel}.");
                AssertProbe(
                    leadingInline || !railInline,
                    $"{page}: leading pane collapsed before the app rail at {viewportLabel}.");
                previousRailInline = railInline;
                previousLeadingInline = leadingInline;
                previousTrailingInline = trailingInline;

                if (actualWidth >= AutomationResponsiveLayout.ShellRailCollapseWidth && leadingInline)
                {
                    AutomationElement inlineList = WaitForElement(
                        listId,
                        () => FindCurrentVisibleByAutomationId(window, listId),
                        TimeSpan.FromSeconds(8));
                    ExerciseIssueListScrollSelection(
                        window,
                        inlineList,
                        options.OutputDirectory,
                        $"{page}-responsive");
                }

                AutomationElement? leadingButton = FindAdaptivePaneButton(window, prefix, leading: true);
                if (IsVisible(leadingButton))
                {
                    ExerciseAdaptiveWorkspaceDrawers(window, prefix, listId, inspectorId);
                }

                if (actualWidth is >= 756 and <= 764)
                {
                    AutomationElement detail = WaitForElement(
                        detailId,
                        () => FindCurrentVisibleByAutomationId(window, detailId),
                        TimeSpan.FromSeconds(8));
                    AssertProbe(IsInsideWindowBounds(detail, window), $"{page}: detail title was clipped at 760px.");
                    AssertProbe(
                        detail.BoundingRectangle.Width >= 180,
                        $"{page}: detail title retained only {detail.BoundingRectangle.Width:0.0}px at 760px.");

                    string primaryActionId = page == "repo-issues"
                        ? "RepoIssuesCompactActionOverflowButton"
                        : "MyIssuesOpenInRepositoryButton";
                    AutomationElement? primaryAction = FindCurrentVisibleByAutomationId(window, primaryActionId);
                    if (IsVisible(primaryAction))
                    {
                        AssertProbe(IsInsideWindowBounds(primaryAction!, window), $"{page}: primary detail action was clipped at 760px.");
                    }
                }
                else
                {
                    if (leadingInline)
                    {
                        AssertProbe(
                            IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId(listId))),
                            $"{page}: leading list was not inline at {viewportLabel}.");
                    }

                    if (!IsVisible(leadingButton))
                    {
                        AutomationElement? trailingButton = FindAdaptivePaneButton(window, prefix, leading: false);
                        if (IsVisible(trailingButton))
                        {
                            ExerciseAdaptiveWorkspaceDrawers(
                                window,
                                prefix,
                                listId,
                                inspectorId,
                                exerciseLeading: false);
                        }
                    }
                }

                CaptureWindow(window, Path.Combine(options.OutputDirectory, $"{page}-adaptive-{viewportLabel}.png"));
            }
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}

static void RunPullRequestsResponsiveWorkspaceProbe(CaptureOptions options)
{
    (int Width, int Height)[] sizes =
    [
        (1366, 900),
        (1180, 800),
        (900, 700),
        (760, 650),
        (640, 600)
    ];

    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-pulls",
        "--scenario=pr-shy-header",
        "--theme=dark",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "pull-requests-responsive-workspace probe");
        foreach ((int width, int height) in sizes)
        {
            Rectangle resizedBounds = ResizeWindow(window, width, height);
            string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
            try
            {
                WaitForElement(
                    "RepoPullRequestsDetailTitle",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoPullRequestsDetailTitle")),
                    TimeSpan.FromSeconds(14));
            }
            catch
            {
                string failurePath = Path.Combine(options.OutputDirectory, $"pull-requests-launch-timeout-{viewportLabel}.png");
                CaptureWindow(window, failurePath);
                string[] visibleTexts = window
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Where(IsVisible)
                    .Select(GetElementName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Take(40)
                    .ToArray();
                Console.WriteLine($"pull-requests-responsive-workspace timeout screenshot={failurePath}");
                Console.WriteLine($"visible text: {string.Join(" | ", visibleTexts)}");
                throw;
            }
            AssertProbe(
                IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoPullRequestsDetailTitle"))),
                $"pull-requests: primary detail was not visible at {viewportLabel}.");
            AssertProbe(
                window.FindFirstDescendant(cf => cf.ByAutomationId("WorkspaceTabs")) is null,
                "Pull requests exposed workspace tabs.");

            bool compactWorkspace = IsVisible(FindCurrentVisibleByAutomationId(
                window,
                "RepoPullRequestsSectionComboBox"));
            AssertProbe(
                IsVisible(FindCurrentVisibleByAutomationId(
                    window,
                    compactWorkspace ? "RepoPullRequestsSectionComboBox" : "RepoPullRequestsSectionSegmented")),
                $"pull-requests: expected {(compactWorkspace ? "compact" : "expanded")} detail header was not visible at {viewportLabel}.");

            AutomationElement? compactRepositoryCommands =
                FindCurrentVisibleByAutomationId(window, "RepoDetailCompactCommandsButton");
            if (IsVisible(compactRepositoryCommands))
            {
                AssertProbe(
                    IsVisible(FindCurrentVisibleByAutomationId(window, "RepoDetailIdentity")),
                    $"pull-requests: compact repository identity was not visible at {viewportLabel}.");
            }

            AutomationElement? leadingButton = FindAdaptivePaneButton(window, "RepoPullRequests", leading: true);
            if (IsVisible(leadingButton))
            {
                ExerciseAdaptiveWorkspaceDrawers(window, "RepoPullRequests", "RepoPullRequestsList", "RepoPullRequestsInspector");
            }
            else
            {
                AssertProbe(
                    IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoPullRequestsList"))),
                    $"pull-requests: leading list was not inline at {viewportLabel}.");
            }

            CaptureWindow(window, Path.Combine(options.OutputDirectory, $"pull-requests-adaptive-{viewportLabel}.png"));
        }

        ResizeWindow(window, 1366, 900);
        Thread.Sleep(350);
        AutomationElement list = WaitForElement(
            "RepoPullRequestsList",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoPullRequestsList")),
            TimeSpan.FromSeconds(8));
        ExerciseIssueListScrollSelection(window, list, options.OutputDirectory, "pull-requests-page");

        (string SelectorId, string ContentId, string Slug)[] sections =
        [
            ("RepoPullRequestsSection_Conversation", "RepoPullRequestsCommentsList", "conversation"),
            ("RepoPullRequestsSection_Files", "CommitDiffViewerRowsScrollViewer", "files"),
            ("RepoPullRequestsSection_Commits", "RepoPullRequestsCommitsList", "commits"),
            ("RepoPullRequestsSection_Reviews", "RepoPullRequestsReviewsList", "reviews"),
            ("RepoPullRequestsSection_Timeline", "RepoPullRequestsTimelineList", "timeline")
        ];
        foreach ((string selectorId, string contentId, string slug) in sections)
        {
            ExercisePullRequestShyHeaderSection(
                window,
                selectorId,
                contentId,
                slug,
                options.OutputDirectory);
        }
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void ExercisePullRequestShyHeaderSection(
    Window window,
    string selectorAutomationId,
    string contentAutomationId,
    string slug,
    string outputDirectory)
{
    AutomationElement selector = WaitForElement(
        selectorAutomationId,
        () => FindCurrentVisibleByAutomationId(window, selectorAutomationId),
        TimeSpan.FromSeconds(8));
    InvokeOrClick(selector);

    AutomationElement scrollHost = WaitForElement(
        contentAutomationId,
        () => FindCurrentVisibleByAutomationId(window, contentAutomationId),
        TimeSpan.FromSeconds(12));
    WaitUntil(
        $"{slug} section vertical scroll contract",
        () => scrollHost.Patterns.Scroll.IsSupported &&
            scrollHost.Patterns.Scroll.Pattern.VerticallyScrollable.ValueOrDefault,
        TimeSpan.FromSeconds(12));

    var scroll = scrollHost.Patterns.Scroll.Pattern;
    scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 0);
    WaitForPullRequestHeaderState(window, shy: false, $"{slug} section top restoration");
    Thread.Sleep(300);
    CaptureWindow(window, Path.Combine(outputDirectory, $"pull-requests-section-{slug}.png"));

    scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 85);
    WaitForPullRequestHeaderState(window, shy: true, $"{slug} section shy header");
    Thread.Sleep(300);
    CaptureWindow(window, Path.Combine(outputDirectory, $"pull-requests-section-{slug}-shy.png"));

    scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 45);
    WaitForPullRequestHeaderState(window, shy: false, $"{slug} section upward reveal");
    AssertProbe(
        scroll.VerticalScrollPercent.ValueOrDefault > 1,
        $"{slug} section only restored its expanded header at the top instead of after upward travel.");
    Thread.Sleep(300);
    CaptureWindow(window, Path.Combine(outputDirectory, $"pull-requests-section-{slug}-revealed.png"));

    scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 65);
    WaitForPullRequestHeaderState(window, shy: true, $"{slug} section downward re-hide");
    Thread.Sleep(300);
    CaptureWindow(window, Path.Combine(outputDirectory, $"pull-requests-section-{slug}-rehidden.png"));

    scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 0);
    WaitForPullRequestHeaderState(window, shy: false, $"{slug} section final top restoration");
}

static void WaitForPullRequestHeaderState(Window window, bool shy, string context)
{
    string visibleHeaderId = shy ? "RepoPullRequestsShySectionComboBox" : "RepoPullRequestsSectionSegmented";
    string hiddenHeaderId = shy ? "RepoPullRequestsSectionSegmented" : "RepoPullRequestsShySectionComboBox";
    WaitUntil(
        context,
        () => IsVisible(FindCurrentVisibleByAutomationId(window, visibleHeaderId)) &&
            !IsVisible(FindCurrentVisibleByAutomationId(window, hiddenHeaderId)),
        TimeSpan.FromSeconds(5));
}

static void RunPullRequestReplyIdentityProbe(CaptureOptions options)
{
    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-pulls",
        "--scenario=pr-reply-identities",
        "--theme=dark",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "pull-request reply identity probe");
        ResizeWindow(window, 1366, 900);
        WaitForElement(
            "pull request detail",
            () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsDetailTitle"),
            TimeSpan.FromSeconds(15));
        AutomationElement reviewsSection = WaitForElement(
            "pull request reviews section",
            () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsSection_Reviews"),
            TimeSpan.FromSeconds(8));
        AssertProbe(!string.IsNullOrWhiteSpace(GetElementName(reviewsSection)), "Reviews section had no accessible name.");
        AutomationElement commitsSection = WaitForElement(
            "pull request commits section",
            () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsSection_Commits"),
            TimeSpan.FromSeconds(5));
        commitsSection.Click();
        Thread.Sleep(250);
        commitsSection.Focus();
        Keyboard.Press(VirtualKeyShort.RIGHT);
        Thread.Sleep(250);
        Keyboard.Press(VirtualKeyShort.ENTER);
        Thread.Sleep(400);

        CaptureWindow(
            window,
            Path.Combine(options.OutputDirectory, "pull-request-reviews-activation.png"));

        static AutomationElement[] FindIdentitylessReplyEditors(Window currentWindow) => currentWindow
            .FindAllDescendants()
            .Where(element =>
            {
                string id = GetAutomationId(element);
                return
                id.StartsWith("PullRequestReviewThread_context_", StringComparison.Ordinal) &&
                id.EndsWith("_ReplyForm_Editor", StringComparison.Ordinal);
            })
            .ToArray();

        string[] OpenReviewsAndReadEditors()
        {
            WaitUntil(
                "first identityless pull request reply editor",
                () => FindIdentitylessReplyEditors(window).Length >= 1,
                TimeSpan.FromSeconds(12));
            AutomationElement contentScrollViewer = WaitForElement(
                "pull request content scroll viewer",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoPullRequestsContentScrollViewer")),
                TimeSpan.FromSeconds(8));
            bool supportsScrolling = contentScrollViewer.Patterns.Scroll.IsSupported;
            bool TryScroll(ScrollAmount amount)
            {
                if (!supportsScrolling)
                {
                    return false;
                }

                try
                {
                    contentScrollViewer.Patterns.Scroll.Pattern.Scroll(ScrollAmount.NoAmount, amount);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }

            for (int attempt = 0; attempt < 12; attempt++)
            {
                if (!TryScroll(ScrollAmount.LargeDecrement))
                {
                    break;
                }

                Thread.Sleep(75);
            }

            HashSet<string> editorIds = new(StringComparer.Ordinal);
            for (int attempt = 0; attempt < 16 && editorIds.Count < 2; attempt++)
            {
                foreach (AutomationElement editor in FindIdentitylessReplyEditors(window))
                {
                    string editorId = GetAutomationId(editor);
                    AssertProbe(editor.ControlType == ControlType.Edit, $"{editorId} did not expose an Edit peer.");
                    AssertProbe(!string.IsNullOrWhiteSpace(GetElementName(editor)), $"{editorId} had no accessible name.");
                    editorIds.Add(editorId);
                }

                if (!TryScroll(ScrollAmount.LargeIncrement))
                {
                    break;
                }

                Thread.Sleep(150);
            }

            string[] editors = editorIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (editors.Length < 2)
            {
                string[] realizedReviewIds = window
                    .FindAllDescendants()
                    .Select(element => GetAutomationId(element))
                    .Where(id =>
                        id.Contains("PullRequestReview", StringComparison.Ordinal) ||
                        id.Contains("ReplyForm", StringComparison.Ordinal))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();
                CaptureWindow(
                    window,
                    Path.Combine(options.OutputDirectory, "pull-request-reply-identities-failure.png"));
                throw new InvalidOperationException(
                    $"Expected two simultaneous identityless reply editors but found {editors.Length}. " +
                    $"Realized review IDs: {string.Join(", ", realizedReviewIds)}");
            }

            return editors;
        }

        string[] initialEditors = OpenReviewsAndReadEditors();
        AssertProbe(initialEditors.Length == 2, $"Expected two identityless reply forms, found {initialEditors.Length}.");
        AssertProbe(
            initialEditors.Distinct(StringComparer.Ordinal).Count() == initialEditors.Length,
            "Simultaneous null/zero-ID reply forms exposed duplicate editor identities.");

        // Navigate away and back so the review item models and their DataTemplate containers are recreated.
        AutomationElement pullRequestList = WaitForElement(
            "repository pull request list",
            () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsList"),
            TimeSpan.FromSeconds(8));
        AutomationElement[] rows = pullRequestList
            .FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .Where(IsVisible)
            .Take(2)
            .ToArray();
        AssertProbe(rows.Length == 2, "The identity probe requires two visible pull request rows.");
        string originalTitle = GetElementName(WaitForElement(
            "original pull request title",
            () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsDetailTitle"),
            TimeSpan.FromSeconds(5)));
        InvokeOrClick(rows[1]);
        WaitUntil(
            "second pull request detail",
            () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsDetailTitle") is AutomationElement detailTitle &&
                !string.Equals(GetElementName(detailTitle), originalTitle, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        InvokeOrClick(rows[0]);
        WaitUntil(
            "original pull request detail after recycling",
            () => FindCurrentVisibleByAutomationId(window, "RepoPullRequestsDetailTitle") is AutomationElement detailTitle &&
                string.Equals(GetElementName(detailTitle), originalTitle, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        string[] recreatedEditors = OpenReviewsAndReadEditors();
        AssertProbe(
            initialEditors.SequenceEqual(recreatedEditors, StringComparer.Ordinal),
            "Identityless reply form identities changed after item/container recreation.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "pull-request-reply-identities.png"));
        Console.WriteLine("pull-request reply identities: simultaneous, identityless, and recreated containers verified");
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunRepoCodeResponsiveWorkspaceProbe(CaptureOptions options)
{
    (int Width, int Height)[] sizes =
    [
        (1366, 900),
        (1180, 800),
        (900, 700),
        (760, 650),
        (640, 600)
    ];

    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-code",
        "--theme=dark",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "repo-code-responsive-workspace probe");
        foreach ((int width, int height) in sizes)
        {
            Rectangle resizedBounds = ResizeWindow(window, width, height);
            int actualWidth = resizedBounds.Width;
            string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
            AutomationElement workspace = WaitForElement(
                "RepoCodeAdaptiveWorkspace",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeAdaptiveWorkspace")),
                TimeSpan.FromSeconds(14));
            AutomationElement readingHost = WaitForElement(
                "RepoCodeReadingHost",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeReadingHost")),
                TimeSpan.FromSeconds(14));
            AssertProbe(IsVisible(readingHost), $"repo-code: reading surface was not visible at {viewportLabel}.");

            AutomationElement? tree = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeFileTree"));
            AutomationElement? openTree = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeOpenFileTreeButton"));
            bool expectsDrawer = actualWidth <= 900 || IsVisible(openTree);
            if (!expectsDrawer)
            {
                AssertProbe(IsVisible(tree), $"repo-code: file tree was not inline at {viewportLabel}.");
                AssertProbe(!IsVisible(openTree), $"repo-code: file-tree opener was visible while the tree was inline at {viewportLabel}.");
            }
            else
            {
                AssertProbe(!IsVisible(tree), $"repo-code: file tree remained inline at {viewportLabel}.");
                openTree = WaitForElement(
                    "RepoCodeOpenFileTreeButton",
                    () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeOpenFileTreeButton")).FirstOrDefault(IsVisible),
                    TimeSpan.FromSeconds(8));
                AssertProbe(
                    readingHost.BoundingRectangle.Width >= workspace.BoundingRectangle.Width - 4,
                    $"repo-code: hidden file tree still reduced the reading width at {viewportLabel}.");

                System.Drawing.Rectangle openerBounds = openTree.BoundingRectangle;
                OpenRepoCodeFileTreeDrawer(window, openTree);
                AutomationElement drawer = WaitForElement(
                    "RepoCodeLeftDrawer",
                    () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer")).FirstOrDefault(IsVisible),
                    TimeSpan.FromSeconds(8));
                AutomationElement closeTree = WaitForElement(
                    "RepoCodeCloseFileTreeButton",
                    () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeCloseFileTreeButton")).FirstOrDefault(IsVisible),
                    TimeSpan.FromSeconds(8));
                Thread.Sleep(320);
                AssertDrawerInsideWorkspace(window, "RepoCode", leading: true);
                AssertElementBoundsAligned(openerBounds, closeTree, "RepoCode file-tree toggle");
                closeTree.Focus();
                AssertDrawerKeyboardFocusContained(window, "RepoCodeLeftDrawer", "Repo Code file-tree drawer");
                CaptureWindow(window, Path.Combine(options.OutputDirectory, $"repo-code-adaptive-{viewportLabel}-drawer.png"));

                if (actualWidth <= 640)
                {
                    AutomationElement file = WaitForElement(
                        "visible repository file",
                        () => drawer.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                            .FirstOrDefault(element =>
                                IsVisible(element) &&
                                element.Name.EndsWith(", file", StringComparison.Ordinal)),
                        TimeSpan.FromSeconds(14));
                    file.Click();
                    WaitUntil(
                        "file selection closes repository tree drawer",
                        () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeLeftDrawer"))),
                        TimeSpan.FromSeconds(6));

                    openTree = WaitForElement(
                        "RepoCodeOpenFileTreeButton after selection",
                        () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeOpenFileTreeButton")).FirstOrDefault(IsVisible),
                        TimeSpan.FromSeconds(5));
                    OpenRepoCodeFileTreeDrawer(window, openTree);
                    drawer = WaitForElement(
                        "RepoCodeLeftDrawer for light dismiss",
                        () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer")).FirstOrDefault(IsVisible),
                        TimeSpan.FromSeconds(5));
                    System.Drawing.Rectangle workspaceBounds = workspace.BoundingRectangle;
                    Mouse.Click(new System.Drawing.Point(
                        workspaceBounds.Right - 24,
                        workspaceBounds.Top + Math.Max(80, workspaceBounds.Height / 2)));
                    WaitUntil(
                        "light dismiss closes repository tree drawer",
                        () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeLeftDrawer"))),
                        TimeSpan.FromSeconds(6));
                    WaitUntil(
                        "light dismiss restores focus to file-tree opener",
                        () => IsElementFocused(openTree),
                        TimeSpan.FromSeconds(5));

                    OpenRepoCodeFileTreeDrawer(window, openTree);
                    WaitForElement(
                        "RepoCodeLeftDrawer for Escape",
                        () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer")).FirstOrDefault(IsVisible),
                        TimeSpan.FromSeconds(5));
                    Keyboard.Press(VirtualKeyShort.ESCAPE);
                    WaitUntil(
                        "Escape closes repository tree drawer",
                        () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeLeftDrawer"))),
                        TimeSpan.FromSeconds(6));
                    WaitUntil(
                        "Escape restores focus to file-tree opener",
                        () => IsElementFocused(openTree),
                        TimeSpan.FromSeconds(5));
                }
                else
                {
                    InvokeOrClick(closeTree);
                    WaitUntil(
                        "repository tree close button dismisses drawer",
                        () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeLeftDrawer"))),
                        TimeSpan.FromSeconds(6));
                }
            }

            AutomationElement? compactFileName = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeCompactFileName"));
            AutomationElement? overflow = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeFileActionsOverflowButton"));
            AutomationElement? copyPath = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeCopyPathButton"));
            if (actualWidth <= 640)
            {
                AssertProbe(IsVisible(compactFileName), "repo-code: compact filename was not visible at 640px.");
                AssertProbe(IsVisible(overflow), "repo-code: named file-actions overflow was not visible at 640px.");
                AssertProbe(!IsVisible(copyPath), "repo-code: low-priority direct actions clipped the compact breadcrumb.");
                AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeBackButton"))), "repo-code: Back was not preserved at 640px.");
                AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeForwardButton"))), "repo-code: Forward was not preserved at 640px.");

                AutomationElement overflowButton = overflow!;
                FocusForKeyboardActivation(window, overflowButton);
                System.Drawing.Rectangle overflowBounds = overflowButton.BoundingRectangle;
                AssertProbe(
                    overflowBounds.Width > 0 && overflowBounds.Height > 0,
                    "repo-code: compact file-actions overflow did not expose clickable bounds.");
                Mouse.MoveTo(new System.Drawing.Point(
                    overflowBounds.Left + overflowBounds.Width / 2,
                    overflowBounds.Top + overflowBounds.Height / 2));
                Mouse.Click();
                WaitForElement(
                    "RepoCodeOverflowCopyPath",
                    () => FindElementInWindowOrDialog(window, automation, "RepoCodeOverflowCopyPath"),
                    TimeSpan.FromSeconds(5));
                WaitForElement(
                    "RepoCodeOverflowCopyRawLink",
                    () => FindElementInWindowOrDialog(window, automation, "RepoCodeOverflowCopyRawLink"),
                    TimeSpan.FromSeconds(5));
                AutomationElement openOnGitHub = WaitForElement(
                    "RepoCodeOverflowOpenOnGitHub",
                    () => FindElementInWindowOrDialog(window, automation, "RepoCodeOverflowOpenOnGitHub"),
                    TimeSpan.FromSeconds(5));
                if (!IsVisible(openOnGitHub))
                {
                    Keyboard.Press(VirtualKeyShort.END);
                    WaitUntil(
                        "RepoCodeOverflowOpenOnGitHub is keyboard-revealed",
                        () => IsVisible(openOnGitHub) || IsElementFocused(openOnGitHub),
                        TimeSpan.FromSeconds(5));
                }
                AssertProbe(
                    IsVisible(openOnGitHub) || IsElementFocused(openOnGitHub),
                    "repo-code: Open on GitHub overflow command was not reachable at 640px.");
                CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "repo-code-adaptive-640x600-overflow.png"));
                Keyboard.Press(VirtualKeyShort.ESCAPE);
            }
            else if (actualWidth >= 1180)
            {
                AssertProbe(!IsVisible(compactFileName), $"repo-code: compact filename replaced the full breadcrumb at {actualWidth}px.");
                AssertProbe(!IsVisible(overflow), $"repo-code: overflow replaced direct actions at {actualWidth}px.");
                AssertProbe(IsVisible(copyPath), $"repo-code: direct file actions were missing at {actualWidth}px.");
            }

            CaptureWindow(window, Path.Combine(options.OutputDirectory, $"repo-code-adaptive-{viewportLabel}.png"));
        }

        ResizeWindow(window, 1366, 900);
        Thread.Sleep(350);
        string beforePath = WaitForRepoCodeRoutePath(window, TimeSpan.FromSeconds(8));
        AutomationElement rootBreadcrumb = WaitForElement(
            "repository-root breadcrumb",
            () => window.FindAllDescendants()
                .Where(element =>
                    element.ControlType == ControlType.Button &&
                    GetAutomationId(element).StartsWith("RepoCodeBreadcrumbSegment_", StringComparison.Ordinal) &&
                    GetElementName(element).StartsWith("Open repository root ", StringComparison.Ordinal))
                .OrderBy(element => element.BoundingRectangle.Left)
                .FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(8));

        string rootPath = rootBreadcrumb.Properties.ItemStatus.ValueOrDefault ??
            GetElementName(rootBreadcrumb)["Open repository root ".Length..];
        if (string.Equals(beforePath, rootPath, StringComparison.Ordinal))
        {
            AutomationElement fileRoute = WaitForElement(
                "repository file used to establish breadcrumb history",
                () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                    .FirstOrDefault(element =>
                        IsVisible(element) &&
                        element.Name.EndsWith(", file", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(12));
            InvokeOrClick(fileRoute);
            WaitUntil(
                "file selection establishes a non-root code route",
                () => !string.Equals(GetRepoCodeRoutePath(window), rootPath, StringComparison.Ordinal),
                TimeSpan.FromSeconds(12));
            beforePath = WaitForRepoCodeRoutePath(window, TimeSpan.FromSeconds(8));
            rootBreadcrumb = WaitForElement(
                "repository-root breadcrumb after file selection",
                () => window.FindAllDescendants()
                    .Where(element =>
                        element.ControlType == ControlType.Button &&
                        GetAutomationId(element).StartsWith("RepoCodeBreadcrumbSegment_", StringComparison.Ordinal) &&
                        GetElementName(element).StartsWith("Open repository root ", StringComparison.Ordinal))
                    .OrderBy(element => element.BoundingRectangle.Left)
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(8));
        }

        InvokeOrClick(rootBreadcrumb);
        WaitUntil(
            "breadcrumb root changes the code route",
            () => string.Equals(GetRepoCodeRoutePath(window), rootPath, StringComparison.Ordinal),
            TimeSpan.FromSeconds(6));
        AssertProbe(
            string.Equals(GetRepoCodeRoutePath(window), rootPath, StringComparison.Ordinal),
            "repo-code: breadcrumb invocation did not change the current path.");
        AutomationElement back = WaitForElement(
            "RepoCodeBackButton after breadcrumb navigation",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeBackButton")),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(back);
        WaitUntil(
            "Back restores the pre-breadcrumb file route",
            () => string.Equals(GetRepoCodeRoutePath(window), beforePath, StringComparison.Ordinal),
            TimeSpan.FromSeconds(6));

        ExerciseRepoCodeContentSurfaces(window, options);

        Console.WriteLine($"repo-code-responsive-workspace probe: {sizes.Length} responsive states, focus containment, drawer behavior, overflow access, breadcrumb routing, CSV rich/plain semantics, and SVG zoom passed; output={options.OutputDirectory}");
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void ExerciseRepoCodeContentSurfaces(Window window, CaptureOptions options)
{
    ResizeWindow(window, 1366, 900);
    Thread.Sleep(320);

    SelectRepoCodeFixtureFile(window, "data.csv", "data.csv, file");
    AutomationElement dataTable = WaitForElement(
        "ready CSV data table",
        () =>
        {
            AutomationElement? candidate = window.FindAllDescendants()
                .FirstOrDefault(element =>
                    IsVisible(element) &&
                    (string.Equals(GetAutomationId(element), "CsvPreviewDataGrid", StringComparison.Ordinal) ||
                     string.Equals(GetAutomationId(element), "CsvPreviewDataTable", StringComparison.Ordinal)));
            if (candidate is null ||
                !candidate.Patterns.Grid.IsSupported ||
                !candidate.Patterns.Table.IsSupported)
            {
                return null;
            }

            try
            {
                return candidate.Patterns.Grid.Pattern.RowCount.Value == 7 &&
                    candidate.Patterns.Grid.Pattern.ColumnCount.Value == 5
                    ? candidate
                    : null;
            }
            catch (COMException)
            {
                return null;
            }
        },
        TimeSpan.FromSeconds(12));

    Grid csvGrid = dataTable.AsGrid();
    AssertProbe(csvGrid.RowCount == 7, $"repo-code CSV: expected 7 data rows, found {csvGrid.RowCount}.");
    AssertProbe(csvGrid.ColumnCount == 5, $"repo-code CSV: expected 5 columns, found {csvGrid.ColumnCount}.");
    AutomationElement[] visibleHeaders = window.FindAllDescendants()
        .Where(element =>
            IsVisible(element) &&
            GetAutomationId(element).StartsWith("CsvPreviewDataTableSortColumn_", StringComparison.Ordinal))
        .ToArray();
    AssertProbe(visibleHeaders.Length == 5, $"repo-code CSV: visual tree exposed {visibleHeaders.Length} of five column headers.");
    var tablePattern = dataTable.Patterns.Table.Pattern;
    AssertProbe(
        tablePattern.RowOrColumnMajor.IsSupported &&
        tablePattern.RowOrColumnMajor.Value == FlaUI.Core.Definitions.RowOrColumnMajor.RowMajor,
        "repo-code CSV: TablePattern omitted its row-major traversal contract.");

    AutomationElement firstCell = dataTable.Patterns.Grid.Pattern.GetItem(0, 0);
    AssertProbe(firstCell.Patterns.GridItem.IsSupported, "repo-code CSV: cells omitted GridItem semantics.");
    AssertProbe(firstCell.Patterns.TableItem.IsSupported, "repo-code CSV: cells omitted TableItem semantics.");
    for (int column = 0; column < csvGrid.ColumnCount; column++)
    {
        AutomationElement cell = dataTable.Patterns.Grid.Pattern.GetItem(0, column);
        AssertProbe(cell.Patterns.TableItem.IsSupported, $"repo-code CSV: column {column} omitted TableItem semantics.");
        UIA3FrameworkAutomationElement nativeCell =
            (UIA3FrameworkAutomationElement)cell.FrameworkAutomationElement;
        NativeTableItemPattern nativeTableItemPattern =
            (NativeTableItemPattern)nativeCell.NativeElement.GetCurrentPattern(10013);
        NativeTableElementArray nativeColumnHeaders =
            nativeTableItemPattern.GetCurrentColumnHeaderItems();
        string nativeHeaderId = nativeColumnHeaders?.Length == 1
            ? nativeColumnHeaders.GetElement(0).CurrentAutomationId
            : string.Empty;
        AssertProbe(
            string.Equals(nativeHeaderId, $"CsvPreviewDataTableSortColumn_{column}", StringComparison.Ordinal),
            $"repo-code CSV: column {column} did not resolve to its native column header.");
    }

    AssertProbe(
        string.Equals(GetElementName(firstCell), "Repository: JitHub", StringComparison.Ordinal),
        $"repo-code CSV: unexpected initial first cell '{GetElementName(firstCell)}'.");

    AutomationElement repositorySort = WaitForElement(
        "CSV Repository sort header",
        () => window.FindAllDescendants(cf => cf.ByAutomationId("CsvPreviewDataTableSortColumn_0"))
            .FirstOrDefault(IsVisible),
        TimeSpan.FromSeconds(5));
    AssertProbe(repositorySort.Patterns.Invoke.IsSupported, "repo-code CSV: sort header omitted Invoke semantics.");
    repositorySort.Patterns.Invoke.Pattern.Invoke();
    WaitUntil(
        "CSV ascending Repository sort",
        () => string.Equals(
            GetElementName(dataTable.Patterns.Grid.Pattern.GetItem(0, 0)),
            "Repository: AppDataTable",
            StringComparison.Ordinal),
        TimeSpan.FromSeconds(5));
    CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-csv-rich-1366x900.png"));

    SelectSegmentedItem(window, "CsvPreviewViewMode_Plain", "Plain CSV view");
    AutomationElement plainEditor = WaitForElement(
        "read-only plain CSV editor",
        () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeEditor"))
            .FirstOrDefault(element =>
                IsVisible(element) &&
                element.Patterns.Value.IsSupported &&
                (element.Patterns.Value.Pattern.Value.Value ?? string.Empty)
                    .Contains("Repository,Language,Open issues,Status,Notes", StringComparison.Ordinal)),
        TimeSpan.FromSeconds(10));
    AssertProbe(
        plainEditor.Patterns.Value.Pattern.IsReadOnly.Value,
        "repo-code CSV: Plain mode exposed an editable source control.");
    AssertProbe(
        !window.FindAllDescendants()
            .Any(element =>
                IsVisible(element) &&
                (string.Equals(GetAutomationId(element), "CsvPreviewDataGrid", StringComparison.Ordinal) ||
                 string.Equals(GetAutomationId(element), "CsvPreviewDataTable", StringComparison.Ordinal))),
        "repo-code CSV: Rich table remained visible behind Plain mode.");
    CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-csv-plain-1366x900.png"));

    SelectSegmentedItem(window, "CsvPreviewViewMode_Rich", "Rich CSV view");
    dataTable = WaitForElement(
        "restored rich CSV table",
        () => window.FindAllDescendants()
            .FirstOrDefault(element =>
                IsVisible(element) &&
                (string.Equals(GetAutomationId(element), "CsvPreviewDataGrid", StringComparison.Ordinal) ||
                 string.Equals(GetAutomationId(element), "CsvPreviewDataTable", StringComparison.Ordinal)) &&
                element.Patterns.Grid.IsSupported),
        TimeSpan.FromSeconds(10));

    ResizeWindow(window, 640, 600);
    Thread.Sleep(420);
    AssertProbe(IsInsideWindowBounds(dataTable, window), "repo-code CSV: compact table escaped the 640px viewport.");
    AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("CsvPreviewViewMode"))), "repo-code CSV: view switcher disappeared at 640px.");
    CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-csv-rich-640x600.png"));

    ResizeWindow(window, 1366, 900);
    Thread.Sleep(320);
    SelectRepoCodeFixtureFile(window, "architecture.svg", "architecture.svg, file");
    AutomationElement svgViewport = WaitForElement(
        "rendered SVG viewport",
        () =>
        {
            AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId("SvgPreviewViewport"));
            string status = candidate?.Properties.ItemStatus.ValueOrDefault ?? string.Empty;
            return candidate is not null &&
                IsVisible(candidate) &&
                status.StartsWith("rendered:tiles:", StringComparison.Ordinal)
                ? candidate
                : null;
        },
        TimeSpan.FromSeconds(14));
    AutomationElement renderedImage = WaitForElement(
        "rendered SVG image",
        () => window.FindAllDescendants(cf => cf.ByAutomationId("SvgPreviewRenderedImage"))
            .FirstOrDefault(IsVisible),
        TimeSpan.FromSeconds(8));
    AssertProbe(
        renderedImage.BoundingRectangle.Width > 0 && renderedImage.BoundingRectangle.Height > 0,
        "repo-code SVG: rendered image had empty bounds.");
    AutomationElement svgScrollViewport = WaitForElement(
        "SVG scroll viewport",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("SvgPreviewScrollViewer")),
        TimeSpan.FromSeconds(5));

    AssertProbe(svgViewport.Patterns.Transform2.IsSupported, "repo-code SVG: zoom surface omitted Transform2 semantics.");
    var zoom = svgViewport.Patterns.Transform2.Pattern;
    AssertProbe(zoom.CanZoom.Value, "repo-code SVG: Transform2 reported that zoom was unavailable.");
    AssertProbe(Math.Abs(zoom.ZoomMinimum.Value - 10) < 0.1, $"repo-code SVG: expected 0.1x minimum zoom, found {zoom.ZoomMinimum.Value / 100:F2}x.");
    AssertProbe(Math.Abs(zoom.ZoomMaximum.Value - 800) < 0.1, $"repo-code SVG: expected 8x maximum zoom, found {zoom.ZoomMaximum.Value / 100:F2}x.");

    VerifyRepoCodeSvgZoom(window, svgViewport, svgScrollViewport, zoom, 100, "repo-code-svg-zoom-1x.png", options.OutputDirectory);
    VerifyRepoCodeSvgZoom(window, svgViewport, svgScrollViewport, zoom, 800, "repo-code-svg-zoom-8x.png", options.OutputDirectory);
    VerifyRepoCodeSvgZoom(window, svgViewport, svgScrollViewport, zoom, 10, "repo-code-svg-zoom-0.1x.png", options.OutputDirectory);
    VerifyRepoCodeSvgZoom(window, svgViewport, svgScrollViewport, zoom, 100, "repo-code-svg-zoom-restored-1x.png", options.OutputDirectory);

    ResizeWindow(window, 640, 600);
    Thread.Sleep(420);
    AssertProbe(IsVisible(svgViewport), "repo-code SVG: viewport disappeared at 640px.");
    AssertProbe(IsInsideWindowBounds(svgViewport, window), "repo-code SVG: compact viewport escaped the 640px window.");
    CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-svg-640x600.png"));
}

static void RunRepoCodeContentSurfacesProbe(CaptureOptions options)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-code",
        "--theme=dark",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "repo-code-content-surfaces probe");
        ExerciseRepoCodeContentSurfaces(window, options);
        Console.WriteLine($"repo-code-content-surfaces probe: CSV and SVG behaviors passed; output={options.OutputDirectory}");
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void SelectRepoCodeFixtureFile(Window window, string path, string accessibleName)
{
    AutomationElement? file = window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
        .FirstOrDefault(element =>
            IsVisible(element) &&
            string.Equals(element.Properties.ItemStatus.ValueOrDefault, $"path:{path}", StringComparison.Ordinal) &&
            string.Equals(GetElementName(element), accessibleName, StringComparison.OrdinalIgnoreCase));
    if (file is null)
    {
        AutomationElement opener = WaitForElement(
            "RepoCode file-tree opener for fixture selection",
            () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeOpenFileTreeButton"))
                .FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(5));
        OpenRepoCodeFileTreeDrawer(window, opener);
        AutomationElement drawer = WaitForElement(
            "RepoCode fixture-selection drawer",
            () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer"))
                .FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(5));
        file = WaitForElement(
            $"repository fixture {path}",
            () => drawer.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                .FirstOrDefault(element =>
                    IsVisible(element) &&
                    string.Equals(element.Properties.ItemStatus.ValueOrDefault, $"path:{path}", StringComparison.Ordinal) &&
                    string.Equals(GetElementName(element), accessibleName, StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10));
    }

    SelectAutomationItem(file, $"repository fixture {path}");
    WaitUntil(
        $"repository fixture route {path}",
        () => IsRepoCodeFixtureRouteVisible(window, path),
        TimeSpan.FromSeconds(12));
}

static bool IsRepoCodeFixtureRouteVisible(Window window, string path)
{
    if (string.Equals(GetRepoCodeRoutePath(window), path, StringComparison.Ordinal))
    {
        return true;
    }

    AutomationElement? transitionPath = FindCurrentVisibleByAutomationId(window, "RepoCodeTransitionPath");
    if (transitionPath is not null &&
        string.Equals(GetElementName(transitionPath), path, StringComparison.Ordinal))
    {
        return true;
    }

    AutomationElement? compactFileName = FindCurrentVisibleByAutomationId(window, "RepoCodeCompactFileName");
    return compactFileName is not null && string.Equals(
        GetElementName(compactFileName),
        Path.GetFileName(path),
        StringComparison.Ordinal);
}

static void SelectSegmentedItem(Window window, string automationId, string accessibleName)
{
    AutomationElement item = WaitForElement(
        accessibleName,
        () => window.FindAllDescendants(cf => cf.ByAutomationId(automationId)).FirstOrDefault(IsVisible),
        TimeSpan.FromSeconds(5));
    AssertProbe(item.Patterns.SelectionItem.IsSupported, $"{accessibleName} omitted SelectionItem semantics.");
    item.Patterns.SelectionItem.Pattern.Select();
    WaitUntil(
        $"{accessibleName} selected",
        () => item.Patterns.SelectionItem.Pattern.IsSelected.Value,
        TimeSpan.FromSeconds(5));
}

static void VerifyRepoCodeSvgZoom(
    Window window,
    AutomationElement svgViewport,
    AutomationElement svgScrollViewport,
    FlaUI.Core.Patterns.ITransform2Pattern zoom,
    double percent,
    string artifactName,
    string outputDirectory)
{
    zoom.Zoom(percent);
    WaitUntil(
        $"SVG zoom settles at {percent / 100:F1}x",
        () => Math.Abs(zoom.ZoomLevel.Value - percent) < 0.1,
        TimeSpan.FromSeconds(5));
    Thread.Sleep(520);
    AssertProbe(
        (svgViewport.Properties.ItemStatus.ValueOrDefault ?? string.Empty)
            .StartsWith("rendered:tiles:", StringComparison.Ordinal),
        $"repo-code SVG: render did not recover after zooming to {percent / 100:F1}x.");
    AssertProbe(
        !window.FindAllDescendants(cf => cf.ByName("Unable to render this SVG."))
            .Any(IsVisible),
        $"repo-code SVG: error state appeared after zooming to {percent / 100:F1}x.");
    string artifactPath = Path.Combine(outputDirectory, artifactName);
    CaptureWindow(window, artifactPath);
    AssertSvgViewportContainsRenderedColor(window, svgScrollViewport, artifactPath, percent);
}

static void AssertSvgViewportContainsRenderedColor(
    Window window,
    AutomationElement svgViewport,
    string screenshotPath,
    double percent)
{
    Rectangle captureBounds = NativeMethods.GetPhysicalWindowBounds(GetNativeWindowHandle(window));
    Rectangle viewportBounds = svgViewport.BoundingRectangle;
    using var screenshot = new Bitmap(screenshotPath);

    int left = Math.Clamp(viewportBounds.Left - captureBounds.Left, 0, screenshot.Width);
    int top = Math.Clamp(viewportBounds.Top - captureBounds.Top, 0, screenshot.Height);
    int right = Math.Clamp(viewportBounds.Right - captureBounds.Left, 0, screenshot.Width);
    int bottom = Math.Clamp(viewportBounds.Bottom - captureBounds.Top, 0, screenshot.Height);
    AssertProbe(right > left && bottom > top, "repo-code SVG: viewport had no capturable pixel area.");

    const int sampleStep = 2;
    int colorfulPixels = 0;
    int sampledPixels = 0;
    for (int y = top; y < bottom; y += sampleStep)
    {
        for (int x = left; x < right; x += sampleStep)
        {
            Color pixel = screenshot.GetPixel(x, y);
            int maximum = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
            int minimum = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
            if (maximum >= 96 && maximum - minimum >= 32)
            {
                colorfulPixels++;
            }

            sampledPixels++;
        }
    }

    int requiredPixels = Math.Max(32, sampledPixels / 10_000);
    AssertProbe(
        colorfulPixels >= requiredPixels,
        $"repo-code SVG: the {percent / 100:F1}x viewport was blank or framed outside the rendered fixture " +
        $"({colorfulPixels} colorful pixels; required {requiredPixels}).");
}

static void RunRepoCodePerformanceProbe(CaptureOptions options)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-code",
        "--scenario=repo-code-performance",
        "--theme=dark",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "repo-code-performance probe");
        ResizeWindow(window, 1366, 900);
        AutomationElement pageRoot = WaitForElement(
            "RepoCodePageRoot performance heartbeat",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodePageRoot")),
            TimeSpan.FromSeconds(14));
        string initialHeartbeat = WaitForChangingItemStatus(pageRoot, null, TimeSpan.FromSeconds(2));

        AutomationElement sourceFile = FindMandatoryRepoCodeSourceFile(window);
        Stopwatch firstContent = Stopwatch.StartNew();
        Stopwatch inputLatency = Stopwatch.StartNew();
        InvokeOrClick(sourceFile);
        inputLatency.Stop();
        AssertProbe(
            inputLatency.Elapsed <= TimeSpan.FromMilliseconds(50),
            $"repo-code-performance: source invocation blocked for {inputLatency.Elapsed.TotalMilliseconds:F1} ms (budget 50 ms).");

        string nextHeartbeat = WaitForChangingItemStatus(
            pageRoot,
            initialHeartbeat,
            TimeSpan.FromMilliseconds(50));
        AssertProbe(
            !string.Equals(initialHeartbeat, nextHeartbeat, StringComparison.Ordinal),
            "repo-code-performance: dispatcher heartbeat did not advance within 50 ms of source selection.");

        AutomationElement editor;
        try
        {
            editor = WaitForElementWithItemStatus(
                window,
                "RepoCodeEditor",
                "Source loaded",
                TimeSpan.FromSeconds(5));
        }
        catch
        {
            CaptureWindow(
                window,
                Path.Combine(options.OutputDirectory, "repo-code-performance-editor-timeout.png"));
            throw;
        }
        firstContent.Stop();
        AssertProbe(
            firstContent.Elapsed <= TimeSpan.FromMilliseconds(150),
            $"repo-code-performance: first editor content took {firstContent.Elapsed.TotalMilliseconds:F1} ms (budget 150 ms).");

        AssertProbe(IsVisible(editor), "repo-code-performance: large source editor was not visible.");
        ExerciseRepoCodeFindOutlineAndTraversal(window, automation, editor, options.OutputDirectory, "repo-code-performance");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-performance-large-fixture.png"));
        Console.WriteLine(
            $"repo-code-performance probe: input={inputLatency.Elapsed.TotalMilliseconds:F1}ms, " +
            $"first-content={firstContent.Elapsed.TotalMilliseconds:F1}ms, heartbeat={nextHeartbeat}; output={options.OutputDirectory}");
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunRepoCodeHighContrastProbe(CaptureOptions options)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-code",
        "--scenario=repo-code-high-contrast",
        "--theme=dark",
        "--high-contrast",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "repo-code-high-contrast probe");
        ResizeWindow(window, 1366, 900);
        AutomationElement sourceFile = FindMandatoryRepoCodeSourceFile(window);
        InvokeOrClick(sourceFile);
        AutomationElement editor = WaitForElementWithItemStatus(
            window,
            "RepoCodeEditor",
            "Source loaded",
            TimeSpan.FromSeconds(5));
        AssertProbe(
            string.Equals(
                editor.Properties.HelpText.ValueOrDefault,
                "High contrast editor colors active",
                StringComparison.Ordinal),
            "repo-code-high-contrast: native editor did not publish its system-color treatment.");
        AssertProbe(IsVisible(editor), "repo-code-high-contrast: editor was not visible at wide width.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-high-contrast-1366x900.png"));

        ResizeWindow(window, 760, 650);
        Thread.Sleep(350);
        editor = WaitForElementWithItemStatus(
            window,
            "RepoCodeEditor",
            "Source loaded",
            TimeSpan.FromSeconds(5));
        AssertProbe(IsVisible(editor), "repo-code-high-contrast: editor was not visible at compact width.");
        AssertProbe(
            string.Equals(
                editor.Properties.HelpText.ValueOrDefault,
                "High contrast editor colors active",
                StringComparison.Ordinal),
            "repo-code-high-contrast: editor lost high contrast treatment after resize.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repo-code-high-contrast-760x650.png"));

        Console.WriteLine(
            $"repo-code-high-contrast probe: system-color editor treatment passed at wide and compact widths; output={options.OutputDirectory}");
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static AutomationElement FindMandatoryRepoCodeSourceFile(Window window)
{
    AutomationElement tree = WaitForElement(
        "ready RepoCodeFileTree",
        () =>
        {
            AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeFileTree"));
            return candidate is not null &&
                (candidate.Properties.ItemStatus.ValueOrDefault ?? string.Empty)
                    .StartsWith("ready", StringComparison.Ordinal)
                    ? candidate
                    : null;
        },
        TimeSpan.FromSeconds(15));

    if (!string.Equals(
            tree.Properties.ItemStatus.ValueOrDefault,
            "ready:path:src/App.cs",
            StringComparison.Ordinal))
    {
        AutomationElement sourceDirectory = WaitForElement(
            "deterministic src fixture directory",
            () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                .FirstOrDefault(element =>
                    IsVisible(element) &&
                    string.Equals(
                        element.Properties.ItemStatus.ValueOrDefault,
                        "path:src",
                        StringComparison.Ordinal)),
            TimeSpan.FromSeconds(8));
        ExpandRepoCodeTreeItem(sourceDirectory);
        WaitUntil(
            "deterministic src/App.cs fixture is loaded",
            () => string.Equals(
                tree.Properties.ItemStatus.ValueOrDefault,
                "ready:path:src/App.cs",
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));
    }

    bool appFileVisible = window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
        .Any(element =>
            IsVisible(element) &&
            string.Equals(element.Properties.ItemStatus.ValueOrDefault, "path:src/App.cs", StringComparison.Ordinal));
    if (!appFileVisible)
    {
        AutomationElement sourceDirectory = WaitForElement(
            "src fixture directory containing App.cs",
            () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                .FirstOrDefault(element =>
                    IsVisible(element) &&
                    string.Equals(element.Properties.ItemStatus.ValueOrDefault, "path:src", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        ExpandRepoCodeTreeItem(sourceDirectory);
    }

    return WaitForElement(
        "deterministic App.cs source fixture",
        () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(element =>
                IsVisible(element) &&
                string.Equals(
                    element.Properties.ItemStatus.ValueOrDefault,
                    "path:src/App.cs",
                    StringComparison.Ordinal) &&
                string.Equals(GetElementName(element), "App.cs, file", StringComparison.OrdinalIgnoreCase)),
        TimeSpan.FromSeconds(15));
}

static void ExpandRepoCodeTreeItem(AutomationElement treeItem)
{
    if (treeItem.Patterns.ExpandCollapse.IsSupported)
    {
        treeItem.Patterns.ExpandCollapse.Pattern.Expand();
        return;
    }

    var bounds = treeItem.BoundingRectangle;
    Mouse.DoubleClick(new System.Drawing.Point(
        bounds.Left + Math.Max(12, bounds.Width / 2),
        bounds.Top + Math.Max(8, bounds.Height / 2)));
}

static void ExerciseRepoCodeFindOutlineAndTraversal(
    Window window,
    UIA3Automation automation,
    AutomationElement editor,
    string outputDirectory,
    string artifactPrefix)
{
    AutomationElement findButton = WaitForElement(
        "RepoCodeFindButton",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeFindButton")),
        TimeSpan.FromSeconds(5));
    InvokeOrClick(findButton);
    AutomationElement findBox = WaitForElement(
        "RepoCodeFindTextBox",
        () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeFindTextBox")).FirstOrDefault(IsVisible),
        TimeSpan.FromSeconds(5));
    SetTextBoxText(findBox, "Experience");
    AutomationElement findStatus = WaitForElement(
        "visible RepoCodeFindStatus",
        () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeFindStatus"))
            .FirstOrDefault(IsVisible),
        TimeSpan.FromSeconds(5));
    WaitUntil(
        "RepoCode deterministic find result",
        () => !string.IsNullOrEmpty(findStatus.Properties.ItemStatus.ValueOrDefault),
        TimeSpan.FromSeconds(5));
    string editorValue = editor.Patterns.Value.IsSupported
        ? editor.Patterns.Value.Pattern.Value.Value ?? string.Empty
        : string.Empty;
    string editorValuePrefix = editorValue.Length <= 80
        ? editorValue
        : editorValue[..80];
    AssertProbe(
        string.Equals(findStatus.Properties.ItemStatus.ValueOrDefault, "match", StringComparison.Ordinal),
        $"repo-code: deterministic Experience search did not find source content ({GetElementName(findStatus)}); " +
        $"UIA value length={editorValue.Length}, contains target={editorValue.Contains("Experience", StringComparison.Ordinal)}, " +
        $"prefix={editorValuePrefix.ReplaceLineEndings(" ")}." );
    Keyboard.Press(VirtualKeyShort.ESCAPE);

    AutomationElement symbols = WaitForElement(
        "RepoCodeSymbolsButton",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeSymbolsButton")),
        TimeSpan.FromSeconds(5));
    InvokeOrClick(symbols);
    WaitForElement(
        "deterministic Repo Code outline item",
        () => automation.GetDesktop().FindAllDescendants()
            .FirstOrDefault(element =>
                IsVisible(element) &&
                GetAutomationId(element).StartsWith("RepoCodeOutlineItem_", StringComparison.Ordinal)),
        TimeSpan.FromSeconds(5));
    CaptureWindowWithPopups(window, Path.Combine(outputDirectory, $"{artifactPrefix}-find-outline.png"));
    Keyboard.Press(VirtualKeyShort.ESCAPE);
    AssertProbe(
        WaitUntilAvailable(
            () => !automation.GetDesktop()
                .FindAllDescendants(cf => cf.ByAutomationId("RepoCodeSymbolsList"))
                .Any(IsVisible),
            TimeSpan.FromSeconds(2)),
        "repo-code: outline flyout did not close before the next editor command.");

    AutomationElement copyLineLink = AssertNamedAutomationElement(
        window,
        "RepoCodeCopyLineLinkButton",
        ControlType.Button);
    const string lineLinkClipboardSentinel = "__jithub_repo_code_line_link_pending__";
    NativeMethods.SetClipboardText(lineLinkClipboardSentinel);
    InvokeOrClick(copyLineLink);
    string copiedLineLink = string.Empty;
    AssertProbe(
        WaitUntilAvailable(
            () =>
            {
                copiedLineLink = NativeMethods.GetClipboardText();
                return !string.IsNullOrWhiteSpace(copiedLineLink) &&
                    !string.Equals(copiedLineLink, lineLinkClipboardSentinel, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(5)),
        "repo-code: copy line link did not update the clipboard.");
    AssertProbe(
        copiedLineLink.Contains("/blob/", StringComparison.Ordinal) &&
        copiedLineLink.Contains("#L", StringComparison.Ordinal),
        $"repo-code: copied line link was not a GitHub blob line URL ('{copiedLineLink}').");

    editor.Focus();
    Keyboard.Press(VirtualKeyShort.F6);
    WaitUntil(
        "F6 moves focus into repository tree",
        () =>
        {
            AutomationElement focused = automation.FocusedElement();
            return GetAutomationId(focused).StartsWith("RepoCodeTreeItem_", StringComparison.Ordinal) ||
                string.Equals(GetAutomationId(focused), "RepoCodeFileTree", StringComparison.Ordinal);
        },
        TimeSpan.FromSeconds(5));
}

static AutomationElement WaitForElementWithItemStatus(
    Window window,
    string automationId,
    string expectedStatus,
    TimeSpan timeout)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    do
    {
        AutomationElement? element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (element is not null &&
            string.Equals(element.Properties.ItemStatus.ValueOrDefault, expectedStatus, StringComparison.Ordinal))
        {
            return element;
        }

        Thread.Sleep(5);
    }
    while (stopwatch.Elapsed <= timeout);

    AutomationElement[] observed = window.FindAllDescendants(cf => cf.ByAutomationId(automationId));
    string observedStatuses = observed.Length == 0
        ? "element absent"
        : string.Join(
            ", ",
            observed.Select(element =>
                $"status='{element.Properties.ItemStatus.ValueOrDefault ?? string.Empty}', " +
                $"name='{GetElementName(element)}', visible={IsVisible(element)}"));
    throw new InvalidOperationException(
        $"Timed out waiting for {automationId} item status '{expectedStatus}' within {timeout.TotalMilliseconds:F0} ms; " +
        $"observed: {observedStatuses}.");
}

static string WaitForChangingItemStatus(
    AutomationElement element,
    string? previousStatus,
    TimeSpan timeout)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    do
    {
        string current = element.Properties.ItemStatus.ValueOrDefault ?? string.Empty;
        if (!string.IsNullOrEmpty(current) && !string.Equals(current, previousStatus, StringComparison.Ordinal))
        {
            return current;
        }

        Thread.Sleep(5);
    }
    while (stopwatch.Elapsed <= timeout);

    throw new InvalidOperationException(
        $"ItemStatus did not change from '{previousStatus ?? "<unset>"}' within {timeout.TotalMilliseconds:F0} ms.");
}

static void RunRepositoryActionsProbe(CaptureOptions options)
{
    RunInteractiveRepositoryActions("success", expectForkReady: true);
    RunInteractiveRepositoryActions("timeout", expectForkReady: false);
    RunInteractiveRepositoryActions("failure", expectForkReady: false);
    RunMutationRollback("star-failure", "RepoDetailStarButton", "Star repository");
    RunMutationRollback("watch-failure", "RepoDetailWatchButton", "Watch repository");
    RunForkOwnershipRelaunch("reconcile", expectReadyAfterRelaunch: true);
    RunForkOwnershipRelaunch("timeout", expectReadyAfterRelaunch: false);
    RunForkRateLimitUnlock();
    RunOverlappingRepositoryRoutes();
    RunUnavailableRepositoryActions();
    Console.WriteLine("repository-actions probe: watch/star/undo shared Stars handoff, branch, compact overflow, fork outcomes, keyboard, and rapid sections passed.");

    void RunInteractiveRepositoryActions(string scenario, bool expectForkReady)
    {
        ResetRepositoryForkOwnershipState();
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-code",
            $"--scenario=repository-actions-{scenario}",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, $"repository-actions-{scenario}");
            ResizeWindow(window, 1180, 800);
            AutomationElement star = WaitForElement(
                "RepoDetailStarButton",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailStarButton")),
                TimeSpan.FromSeconds(12));
            AutomationElement watch = WaitForElement(
                "RepoDetailWatchButton",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailWatchButton")),
                TimeSpan.FromSeconds(12));
            AssertProbe(star.Properties.IsEnabled.ValueOrDefault, $"{scenario}: Star was not enabled.");
            AssertProbe(watch.Properties.IsEnabled.ValueOrDefault, $"{scenario}: Watch was not enabled.");

            if (string.Equals(scenario, "success", StringComparison.Ordinal))
            {
                Mouse.MoveTo(star.BoundingRectangle.Center());
                Thread.Sleep(250);
                FocusForKeyboardActivation(window, star);
                WaitUntil("Star keyboard focus", () => IsElementFocused(star), TimeSpan.FromSeconds(3));
                Keyboard.Type([VirtualKeyShort.ENTER]);
                star = WaitForElement(
                    "Unstar state after Enter",
                    () =>
                    {
                        AutomationElement? current = window.FindFirstDescendant(
                            cf => cf.ByAutomationId("RepoDetailStarButton"));
                        return current is not null &&
                            GetElementName(current).Contains("Unstar", StringComparison.OrdinalIgnoreCase)
                                ? current
                                : null;
                    },
                    TimeSpan.FromSeconds(5));

                ExerciseSharedStarsMutation(window, automation);
                star = WaitForElement(
                    "Unstar state after Stars Undo handoff",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailStarButton")) is AutomationElement current &&
                        GetElementName(current).Contains("Unstar", StringComparison.OrdinalIgnoreCase)
                            ? current
                            : null,
                    TimeSpan.FromSeconds(12));
                watch = WaitForElement(
                    "Watch state after Stars Undo handoff",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailWatchButton")) is AutomationElement current &&
                        GetElementName(current).Contains("Watch repository", StringComparison.OrdinalIgnoreCase)
                            ? current
                            : null,
                    TimeSpan.FromSeconds(12));
                FocusForKeyboardActivation(window, star);
                Keyboard.Type([VirtualKeyShort.SPACE]);
                star = WaitForElement(
                    "Star state after Space",
                    () =>
                    {
                        AutomationElement? current = window.FindFirstDescendant(
                            cf => cf.ByAutomationId("RepoDetailStarButton"));
                        return current is not null &&
                            GetElementName(current).Contains("Star repository", StringComparison.OrdinalIgnoreCase)
                                ? current
                                : null;
                    },
                    TimeSpan.FromSeconds(5));

                FocusForKeyboardActivation(window, watch);
                Keyboard.Type([VirtualKeyShort.SPACE]);
                watch = WaitForElement(
                    "Unwatch state after Space",
                    () =>
                    {
                        AutomationElement? current = window.FindFirstDescendant(
                            cf => cf.ByAutomationId("RepoDetailWatchButton"));
                        return current is not null &&
                            GetElementName(current).Contains("Unwatch", StringComparison.OrdinalIgnoreCase)
                                ? current
                                : null;
                    },
                    TimeSpan.FromSeconds(5));
                FocusForKeyboardActivation(window, watch);
                Keyboard.Type([VirtualKeyShort.ENTER]);
                watch = WaitForElement(
                    "Watch state after Enter",
                    () =>
                    {
                        AutomationElement? current = window.FindFirstDescendant(
                            cf => cf.ByAutomationId("RepoDetailWatchButton"));
                        return current is not null &&
                            GetElementName(current).Contains("Watch repository", StringComparison.OrdinalIgnoreCase)
                                ? current
                                : null;
                    },
                    TimeSpan.FromSeconds(5));

                CaptureRepositoryActionResponsiveStates(window);

                ResizeWindow(window, 700, 650);
                AutomationElement compact = WaitForElement(
                    "RepoDetailCompactCommandsButton",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailCompactCommandsButton")),
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(compact);
                AutomationElement identityHeader = WaitForElement(
                    "RepoDetailCompactRepositoryIdentity",
                    () => FindElementInWindowOrDialog(window, automation, "RepoDetailCompactRepositoryIdentity") is AutomationElement identity &&
                        IsVisible(identity)
                            ? identity
                            : null,
                    TimeSpan.FromSeconds(5));
                AssertProbe(!identityHeader.Patterns.Toggle.IsSupported, "Compact repository identity was exposed as a second checked section item.");
                CaptureWindowWithPopups(
                    window,
                    Path.Combine(options.OutputDirectory, "repository-actions-compact-actions-open.png"));
                AutomationElement compactBranch = WaitForElement(
                    "RepoDetailCompactBranchMenu",
                    () => FindElementInWindowOrDialog(window, automation, "RepoDetailCompactBranchMenu") is AutomationElement branchItem &&
                        IsVisible(branchItem)
                            ? branchItem
                            : null,
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(compactBranch);
                AutomationElement compactBranchFlyout = WaitForElement(
                    "RepoDetailBranchFlyoutRoot from compact command",
                    () => FindElementInWindowOrDialog(window, automation, "RepoDetailBranchFlyoutRoot"),
                    TimeSpan.FromSeconds(5));
                AutomationElement compactBranchSearch = WaitForElement(
                    "RepoDetailBranchSearchBox from compact command",
                    () => compactBranchFlyout.FindFirstDescendant(
                        cf => cf.ByAutomationId("RepoDetailBranchSearchBox")),
                    TimeSpan.FromSeconds(5));
                AssertProbe(IsVisible(compactBranchSearch), "Compact branch command opened a flyout without its searchable picker.");
                AutomationElement compactBranchSearchInput =
                    compactBranchSearch.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)) ??
                    compactBranchSearch;
                FocusForKeyboardActivation(window, compactBranchSearchInput);
                Keyboard.Type("release-page-2");
                AutomationElement compactBranchList = WaitForElement(
                    "RepoDetailBranchList from compact command",
                    () => compactBranchFlyout.FindFirstDescendant(
                        cf => cf.ByAutomationId("RepoDetailBranchList")),
                    TimeSpan.FromSeconds(5));
                AutomationElement compactSecondPageBranch = WaitForElement(
                    "second-page branch from compact command",
                    () => compactBranchList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                        .FirstOrDefault(item => GetElementName(item).Contains("release-page-2", StringComparison.OrdinalIgnoreCase)),
                    TimeSpan.FromSeconds(12));
                CaptureWindowWithPopups(
                    window,
                    Path.Combine(options.OutputDirectory, "repository-actions-compact-branches-open.png"));
                InvokeOrClick(compactSecondPageBranch);

                compact = WaitForElement(
                    "RepoDetailCompactCommandsButton after branch selection",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailCompactCommandsButton")) is AutomationElement current &&
                        IsVisible(current)
                            ? current
                            : null,
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(compact);
                AutomationElement compactStar = WaitForElement(
                    "RepoDetailCompactStar",
                    () => FindElementInWindowOrDialog(window, automation, "RepoDetailCompactStar") is AutomationElement starItem &&
                        IsVisible(starItem)
                            ? starItem
                            : null,
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(compactStar);
                compact = WaitForElement(
                    "RepoDetailCompactCommandsButton after star",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailCompactCommandsButton")) is AutomationElement current &&
                        IsVisible(current)
                            ? current
                            : null,
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(compact);
                compactStar = WaitForElement(
                    "RepoDetailCompactStar selected",
                    () => FindElementInWindowOrDialog(window, automation, "RepoDetailCompactStar") is AutomationElement starItem &&
                        IsVisible(starItem)
                            ? starItem
                            : null,
                    TimeSpan.FromSeconds(5));
                AssertProbe(GetElementName(compactStar).Contains("Unstar", StringComparison.OrdinalIgnoreCase), "Compact Star command did not invoke the repository action.");
                InvokeOrClick(compactStar);

                compact = WaitForElement(
                    "RepoDetailCompactCommandsButton before Issues",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailCompactCommandsButton")) is AutomationElement current &&
                        IsVisible(current)
                            ? current
                            : null,
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(compact);
                AutomationElement issues = WaitForElement(
                    "RepoDetailCompactSectionIssues",
                    () => FindElementInWindowOrDialog(window, automation, "RepoDetailCompactSectionIssues") is AutomationElement issuesItem &&
                        IsVisible(issuesItem)
                            ? issuesItem
                            : null,
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(issues);
                _ = WaitForElement(
                    "RepoIssuesAdaptiveWorkspace",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesAdaptiveWorkspace")),
                    TimeSpan.FromSeconds(8));
                compact = WaitForElement(
                    "RepoDetailCompactCommandsButton after Issues",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailCompactCommandsButton")),
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(compact);
                InvokeOrClick(WaitForElement(
                    "RepoDetailCompactSectionCode",
                    () => FindElementInWindowOrDialog(window, automation, "RepoDetailCompactSectionCode"),
                    TimeSpan.FromSeconds(5)));
                _ = WaitForElement(
                    "RepoCodeAdaptiveWorkspace after rapid route",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCodeAdaptiveWorkspace")),
                    TimeSpan.FromSeconds(8));
                ResizeWindow(window, 1180, 800);
            }

            AutomationElement fork = WaitForElement(
                "RepoDetailForkButton",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailForkButton")),
                TimeSpan.FromSeconds(8));
            InvokeOrClick(fork);
            if (expectForkReady)
            {
                WaitForElement(
                    "forked repository identity",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailIdentity")) is AutomationElement identity &&
                        GetElementName(identity).Contains("automation-user", StringComparison.OrdinalIgnoreCase)
                            ? identity
                            : null,
                    TimeSpan.FromSeconds(10));
            }
            else
            {
                AutomationElement retry = WaitForElement(
                    "RepoDetailRetryForkButton",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailRetryForkButton")) is AutomationElement current &&
                        IsVisible(current)
                            ? current
                            : null,
                    TimeSpan.FromSeconds(10));
                AssertProbe(retry.Properties.IsEnabled.ValueOrDefault, $"{scenario}: Fork Retry was visible but disabled.");
            }

            CaptureWindow(window, Path.Combine(options.OutputDirectory, $"repository-actions-{scenario}.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    void ExerciseSharedStarsMutation(Window window, UIA3Automation automation)
    {
        AutomationElement starsNavigation = GetShellNavigationElement(window, "ShellNav_stars");
        InvokeOrClick(starsNavigation);

        AutomationElement starsList = WaitForElement(
            "real Stars workspace after repository Star",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsList")),
            TimeSpan.FromSeconds(12));
        AutomationElement mutatedRepository = WaitForElement(
            $"shared Stars row for {options.RepositoryFullName}",
            () => starsList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(item =>
                    GetElementName(item).Contains(options.RepositoryFullName, StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(12));
        string repositoryAutomationId = GetAutomationId(mutatedRepository);
        AssertProbe(
            repositoryAutomationId.StartsWith("StarsRepository_", StringComparison.Ordinal),
            $"The shared Stars row for {options.RepositoryFullName} did not expose stable repository identity.");

        Rectangle rowBounds = mutatedRepository.BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(
            rowBounds.Left + rowBounds.Width / 2,
            rowBounds.Top + rowBounds.Height / 2));
        AutomationElement unstar = WaitForElement(
            $"shared Stars Unstar for {options.RepositoryFullName}",
            () => mutatedRepository.FindAllDescendants()
                .FirstOrDefault(element =>
                    GetAutomationId(element).StartsWith("StarsHoverUnstar_", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(unstar);
        WaitUntil(
            $"{options.RepositoryFullName} disappears from shared Stars state",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(repositoryAutomationId)) is null,
            TimeSpan.FromSeconds(5));

        AutomationElement undo = WaitForElement(
            "StarsUndoUnstar for repository action mutation",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsUndoUnstar")),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(undo);
        AutomationElement restoredRepository = WaitForElement(
            $"{options.RepositoryFullName} returns to shared Stars state",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(repositoryAutomationId)) is AutomationElement restored &&
                GetElementName(restored).Contains(options.RepositoryFullName, StringComparison.OrdinalIgnoreCase)
                    ? restored
                    : null,
            TimeSpan.FromSeconds(8));
        AssertProbe(
            string.Equals(GetAutomationId(restoredRepository), repositoryAutomationId, StringComparison.Ordinal),
            "Stars Undo returned a different repository identity.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "repository-actions-shared-stars-undo.png"));

        string shellRepositoryId = "ShellRepo_" + new string(options.RepositoryFullName
            .Select(static character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray());
        _ = GetShellNavigationElement(window, "ShellNav_home");
        InvokeOrClick(WaitForElement(
            $"{shellRepositoryId} after shared Stars mutation",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(shellRepositoryId)) is AutomationElement repository &&
                IsVisible(repository)
                    ? repository
                    : null,
            TimeSpan.FromSeconds(8)));
        _ = WaitForElement(
            "repository action workspace after shared Stars mutation",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailIdentity")),
            TimeSpan.FromSeconds(12));
    }

    void CaptureRepositoryActionResponsiveStates(Window window)
    {
        (int Width, int Height)[] sizes =
        [
            (1366, 900),
            (1180, 800),
            (900, 700),
            (760, 650),
            (640, 600),
        ];

        foreach ((int width, int height) in sizes)
        {
            Rectangle resizedBounds = ResizeWindow(window, width, height);
            string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
            WaitUntil(
                $"repository actions at {viewportLabel}",
                () =>
                {
                    bool hasInlineActions =
                        IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailStarButton"))) &&
                        IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailWatchButton"))) &&
                        IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailForkButton")));
                    bool hasCompactActions = IsVisible(
                        window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailCompactCommandsButton")));
                    return hasInlineActions || hasCompactActions;
                },
                TimeSpan.FromSeconds(12));

            CaptureWindow(
                window,
                Path.Combine(
                    options.OutputDirectory,
                    $"repository-actions-responsive-{viewportLabel}.png"));
        }

        ResizeWindow(window, 1366, 900);
    }

    void RunMutationRollback(string scenario, string actionId, string expectedName)
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-code",
            $"--scenario=repository-actions-{scenario}",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, $"repository-actions-{scenario}");
            AutomationElement action = WaitForElement(
                actionId,
                () => window.FindFirstDescendant(cf => cf.ByAutomationId(actionId)),
                TimeSpan.FromSeconds(12));
            string before = GetElementName(action);
            InvokeOrClick(action);
            WaitForElement(
                $"{scenario} rollback status",
                () => IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailActionStatus")))
                    ? action
                    : null,
                TimeSpan.FromSeconds(5));
            AssertProbe(
                GetElementName(action).Contains(expectedName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(before, GetElementName(action), StringComparison.Ordinal),
                $"{scenario}: failed mutation did not restore the exact prior label and count.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, $"repository-actions-{scenario}.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    void RunForkOwnershipRelaunch(string scenario, bool expectReadyAfterRelaunch)
    {
        ResetRepositoryForkOwnershipState();
        KillExistingApplicationInstances(options.AppPath);
        (int status, string createdAt) durableBeforeRelaunch = default;
        try
        {
            using (var firstApp = LaunchApplication(
                options.AppPath,
                "--page=repo-code",
                $"--scenario=repository-actions-{scenario}",
                "--theme=dark",
                $"--repo={options.RepositoryFullName}"))
            using (var firstAutomation = new UIA3Automation())
            {
                Window firstWindow = GetReadyWindow(firstApp, firstAutomation, $"repository-actions-{scenario}-before-relaunch");
                InvokeOrClick(WaitForElement(
                    "RepoDetailForkButton before relaunch",
                    () => firstWindow.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailForkButton")),
                    TimeSpan.FromSeconds(12)));
                _ = WaitForElement(
                    "RepoDetailRetryForkButton before relaunch",
                    () => firstWindow.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailRetryForkButton")) is AutomationElement retry &&
                        IsVisible(retry)
                            ? retry
                            : null,
                    TimeSpan.FromSeconds(10));
                durableBeforeRelaunch = ReadPersistedForkOwnership();
                int expectedStatus = expectReadyAfterRelaunch ? 0 : 1;
                AssertProbe(
                    durableBeforeRelaunch.status == expectedStatus,
                    $"{scenario}: persisted ownership status was {durableBeforeRelaunch.status}, expected {expectedStatus}.");
                TryClose(firstApp);
            }

            KillExistingApplicationInstances(options.AppPath);
            using var relaunchedApp = LaunchApplication(
                options.AppPath,
                "--page=repo-code",
                $"--scenario=repository-actions-{scenario}",
                "--theme=dark",
                $"--repo={options.RepositoryFullName}");
            using var relaunchedAutomation = new UIA3Automation();
            Window relaunchedWindow = GetReadyWindow(
                relaunchedApp,
                relaunchedAutomation,
                $"repository-actions-{scenario}-after-relaunch");
            InvokeOrClick(WaitForElement(
                "RepoDetailForkButton after relaunch",
                () => relaunchedWindow.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailForkButton")),
                TimeSpan.FromSeconds(12)));

            if (expectReadyAfterRelaunch)
            {
                _ = WaitForElement(
                    "reconciled fork identity after relaunch",
                    () => relaunchedWindow.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailIdentity")) is AutomationElement identity &&
                        GetElementName(identity).Contains("automation-user", StringComparison.OrdinalIgnoreCase)
                            ? identity
                            : null,
                    TimeSpan.FromSeconds(10));
            }
            else
            {
                _ = WaitForElement(
                    "RepoDetailRetryForkButton after accepted relaunch",
                    () => relaunchedWindow.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailRetryForkButton")) is AutomationElement retry &&
                        IsVisible(retry)
                            ? retry
                            : null,
                    TimeSpan.FromSeconds(10));
            }

            if (!expectReadyAfterRelaunch)
            {
                (int status, string createdAt) durableAfterRelaunch = ReadPersistedForkOwnership();
                AssertProbe(durableAfterRelaunch.status == 1, $"{scenario}: accepted ownership was not preserved after relaunch.");
                AssertProbe(
                    string.Equals(
                        durableBeforeRelaunch.createdAt,
                        durableAfterRelaunch.createdAt,
                        StringComparison.Ordinal),
                    $"{scenario}: relaunch replaced accepted ownership instead of reconciling it.");
            }
            CaptureWindow(
                relaunchedWindow,
                Path.Combine(options.OutputDirectory, $"repository-actions-{scenario}-relaunch.png"));
            TryClose(relaunchedApp);
        }
        finally
        {
            KillExistingApplicationInstances(options.AppPath);
            ResetRepositoryForkOwnershipState();
        }
    }

    (int Status, string CreatedAt) ReadPersistedForkOwnership()
    {
        string path = Path.Combine(
            GetAutomationDataRoot(),
            "Local",
            "RepositoryActions",
            "v1",
            "repository-fork-ownership.json");
        WaitUntil(
            "persisted repository fork ownership",
            () => File.Exists(path),
            TimeSpan.FromSeconds(3));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement items = document.RootElement.GetProperty("Items");
        AssertProbe(items.GetArrayLength() == 1, "Expected exactly one persisted repository fork ownership record.");
        JsonElement item = items[0];
        return (
            item.GetProperty("Status").GetInt32(),
            item.GetProperty("CreatedAt").GetString() ?? string.Empty);
    }

    void ResetRepositoryForkOwnershipState()
    {
        string directory = Path.Combine(
            GetAutomationDataRoot(),
            "Local",
            "RepositoryActions",
            "v1");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    void RunForkRateLimitUnlock()
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-code",
            "--scenario=repository-actions-rate-limit",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "repository-actions-rate-limit");
            InvokeOrClick(WaitForElement(
                "RepoDetailForkButton",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailForkButton")),
                TimeSpan.FromSeconds(12)));
            AutomationElement retry = WaitForElement(
                "rate-limited retry",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailRetryForkButton")),
                TimeSpan.FromSeconds(8));
            AssertProbe(!retry.Properties.IsEnabled.ValueOrDefault, "Rate-limited fork Retry was enabled before Retry-After elapsed.");
            WaitUntil("rate-limited retry unlock", () => retry.Properties.IsEnabled.ValueOrDefault, TimeSpan.FromSeconds(5));
            InvokeOrClick(retry);
            WaitForElement(
                "rate-limited fork completion",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailIdentity")) is AutomationElement identity &&
                    GetElementName(identity).Contains("automation-user", StringComparison.OrdinalIgnoreCase)
                        ? identity
                        : null,
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    void RunOverlappingRepositoryRoutes()
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(
            options.AppPath,
            "--page=home",
            "--scenario=repository-actions-route-overlap",
            "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "repository-actions-route-overlap");
            AutomationElement list = WaitForElement(
                "ShellRepositoryList",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRepositoryList")),
                TimeSpan.FromSeconds(15));
            AutomationElement[] repositories = list.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Where(IsVisible)
                .Take(2)
                .ToArray();
            AssertProbe(repositories.Length == 2, "Route-overlap probe needs two repository rows.");
            string expected = GetElementName(repositories[1]);
            InvokeOrClick(repositories[0]);
            Thread.Sleep(40);
            InvokeOrClick(repositories[1]);
            AutomationElement identity = WaitForElement(
                "latest overlapping repository route",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailIdentity")) is AutomationElement candidate &&
                    GetElementName(candidate).Contains(expected, StringComparison.OrdinalIgnoreCase)
                        ? candidate
                        : null,
                TimeSpan.FromSeconds(12));
            Thread.Sleep(1200);
            AssertProbe(GetElementName(identity).Contains(expected, StringComparison.OrdinalIgnoreCase), "Earlier repository request overwrote the latest route.");
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    void RunUnavailableRepositoryActions()
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(
            options.AppPath,
            "--page=repo-code",
            "--scenario=repository-actions-disabled",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "repository-actions-disabled");
            AutomationElement star = WaitForElement(
                "disabled Star",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailStarButton")),
                TimeSpan.FromSeconds(12));
            AutomationElement watch = WaitForElement(
                "disabled Watch",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoDetailWatchButton")),
                TimeSpan.FromSeconds(12));
            AssertProbe(!star.Properties.IsEnabled.ValueOrDefault, "Unavailable Star action remained enabled.");
            AssertProbe(!watch.Properties.IsEnabled.ValueOrDefault, "Unavailable Watch action remained enabled.");
            AssertProbe(GetElementName(star).Contains("unavailable", StringComparison.OrdinalIgnoreCase), "Star unavailable state was not explicit.");
            AssertProbe(GetElementName(watch).Contains("unavailable", StringComparison.OrdinalIgnoreCase), "Watch unavailable state was not explicit.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "repository-actions-disabled.png"));
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}

static void RunCommitsResponsiveWorkspaceProbe(CaptureOptions options)
{
    (int Width, int Height)[] sizes =
    [
        (1366, 900),
        (1180, 800),
        (900, 700),
        (760, 650),
        (640, 600)
    ];

    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-commits",
        "--theme=dark",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "commits-responsive-workspace probe");
        foreach ((int width, int height) in sizes)
        {
            Rectangle resizedBounds = ResizeWindow(window, width, height);
            int actualWidth = resizedBounds.Width;
            string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
            try
            {
                WaitForElement(
                    "RepoCommitsAdaptiveWorkspace",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsAdaptiveWorkspace")),
                    TimeSpan.FromSeconds(14));
                EnsureCommitDetailVisible(window);
            }
            catch
            {
                string failurePath = Path.Combine(options.OutputDirectory, $"commits-launch-timeout-{viewportLabel}.png");
                CaptureWindow(window, failurePath);
                PrintVisibleAutomationIds(window, "commits-responsive-workspace");
                Console.WriteLine($"commits-responsive-workspace timeout screenshot={failurePath}");
                throw;
            }

            AssertProbe(
                IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDetailTitle"))),
                $"commits: primary detail was not visible at {viewportLabel}.");
            if (actualWidth >= AutomationResponsiveLayout.ShellRailCollapseWidth)
            {
                AutomationElement? workspace = window.FindFirstDescendant(
                    cf => cf.ByAutomationId("RepoCommitsAdaptiveWorkspace"));
                AutomationElement? inspector = window.FindFirstDescendant(
                    cf => cf.ByAutomationId("RepoCommitsInspectorHost"));
                AutomationElement? inspectorButton = window.FindFirstDescendant(
                    cf => cf.ByAutomationId("RepoCommitsOpenInspectorPaneButton"));
                Console.WriteLine(
                    $"commits-responsive-layout window={viewportLabel}; " +
                    $"workspace={workspace?.BoundingRectangle}; " +
                    $"inspector={inspector?.BoundingRectangle}; " +
                    $"inspector-visible={IsVisible(inspector)}; " +
                    $"opener={inspectorButton?.BoundingRectangle}; " +
                    $"opener-visible={IsVisible(inspectorButton)}");
                AssertProbe(
                    IsVisible(inspector),
                    "commits: inspector was not inline at the normal 1366px wide desktop size.");
                AssertProbe(
                    !IsVisible(inspectorButton),
                    "commits: inspector drawer opener remained visible while the inspector should be inline.");
            }
            bool isDiffViewerVisible = IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffViewer"))) ||
                IsCommitDiffTextVisible(window);
            if (!isDiffViewerVisible)
            {
                string failurePath = Path.Combine(options.OutputDirectory, $"commits-diff-missing-{viewportLabel}.png");
                CaptureWindow(window, failurePath);
                PrintVisibleAutomationIds(window, "commits-diff-missing");
                Console.WriteLine($"commits-responsive-workspace diff missing screenshot={failurePath}");
            }
            AssertProbe(isDiffViewerVisible, $"commits: diff viewer was not visible at {viewportLabel}.");
            AssertCommitDiffViewerContracts(window, $"commits-responsive-workspace {viewportLabel}");
            AssertProbe(
                window.FindFirstDescendant(cf => cf.ByAutomationId("WorkspaceTabs")) is null,
                "Commits exposed workspace tabs.");

            AutomationElement? leadingButton = FindAdaptivePaneButton(window, "RepoCommits", leading: true);
            if (IsVisible(leadingButton))
            {
                ExerciseAdaptiveWorkspaceDrawers(window, "RepoCommits", "RepoCommitsList", "RepoCommitsInspectorHost");
            }
            else
            {
                AssertProbe(
                    IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsList"))),
                    $"commits: leading list was not inline at {viewportLabel}.");
            }

            CaptureWindow(window, Path.Combine(options.OutputDirectory, $"commits-adaptive-{viewportLabel}.png"));
        }

        ResizeWindow(window, 1366, 900);
        Thread.Sleep(350);
        EnsureCommitDetailVisible(window);
        AutomationElement list = WaitForElement(
            "RepoCommitsList",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsList")),
            TimeSpan.FromSeconds(8));
        ExerciseIssueListScrollSelection(window, list, options.OutputDirectory, "commits-page");

        AutomationElement commentsSection = WaitForElement(
            "RepoCommitsSection_Comments",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsSection_Comments")),
            TimeSpan.FromSeconds(8));
        InvokeOrClick(commentsSection);
        try
        {
            AutomationElement commentButton = WaitForElement(
                "RepoCommitsCommentButton",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsCommentButton")),
                TimeSpan.FromSeconds(8));
            AutomationElement commentsViewport = WaitForElement(
                "RepoCommitsCommentsViewport",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsCommentsViewport")),
                TimeSpan.FromSeconds(8));
            for (int attempt = 0; attempt < 6 && !IsVisible(commentButton); attempt++)
            {
                Mouse.MoveTo(CenterPoint(commentsViewport, window));
                Mouse.Scroll(-5);
                Thread.Sleep(180);
            }
            AssertProbe(
                IsVisible(commentButton),
                "Commit comment action could not be reached in the comments viewport.");
        }
        catch
        {
            CaptureWindow(
                window,
                Path.Combine(options.OutputDirectory, "commits-section-comments-missing.png"));
            throw;
        }
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "commits-section-comments.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunCommitsVirtualizedDiffProbe(CaptureOptions options)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-commits",
        "--theme=dark",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "commits-virtualized-diff probe");
        ResizeWindow(window, 1366, 900);
        EnsureCommitDetailVisible(window);
        EnsureCommitDiffVisible(window);
        AssertCommitDiffViewerContracts(window, "commits-virtualized-diff initial");

        AutomationElement filterBox = WaitForElement(
            "RepoCommitsDiffFileFilterBox",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffFileFilterBox")),
            TimeSpan.FromSeconds(8));
        SetTextBoxText(filterBox, "zz-no-file-match");
        WaitUntil(
            "diff file filter no-results row appears",
            () => IsVisible(window.FindFirstDescendant(cf => cf.ByText("No files match the current filter."))),
            TimeSpan.FromSeconds(4));
        SetTextBoxText(filterBox, string.Empty);
        Thread.Sleep(600);
        AssertCommitDiffViewerContracts(window, "commits-virtualized-diff after filter clear");

        AutomationElement searchBox = WaitForElement(
            "RepoCommitsDiffSearchBox",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffSearchBox")),
            TimeSpan.FromSeconds(8));
        SetTextBoxText(searchBox, "a");
        Thread.Sleep(700);
        AutomationElement nextButton = WaitForElement(
            "RepoCommitsNextDiffMatchButton",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsNextDiffMatchButton")),
            TimeSpan.FromSeconds(8));
        if (nextButton.Patterns.Invoke.IsSupported || nextButton.Properties.IsEnabled.ValueOrDefault)
        {
            InvokeOrClick(nextButton);
            Thread.Sleep(350);
        }

        AutomationElement diffRows = WaitForElement(
            "CommitDiffViewerRowsScrollViewer",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("CommitDiffViewerRowsScrollViewer")),
            TimeSpan.FromSeconds(8));
        Mouse.MoveTo(diffRows.BoundingRectangle.Center());
        Mouse.Scroll(-4);
        Thread.Sleep(350);
        AssertCommitDiffMultiRowSelection(window, automation, diffRows, options.OutputDirectory);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "commits-virtualized-diff-wide.png"));

        AutomationElement compareSection = WaitForElement(
            "RepoCommitsSection_Compare",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsSection_Compare")),
            TimeSpan.FromSeconds(8));
        InvokeOrClick(compareSection);
        Thread.Sleep(600);
        AutomationElement compareButton = WaitForElement(
            "RepoCommitsCompareButton",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsCompareButton")),
            TimeSpan.FromSeconds(8));
        InvokeOrClick(compareButton);
        Thread.Sleep(1200);
        AssertProbe(
            IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsCompareDiffSearchBox"))) &&
            IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("CommitDiffViewerRowsScrollViewer"))),
            "Compare diff section did not expose the virtualized wrapped diff surface.");
        AssertCommitDiffViewerContracts(window, "commits-virtualized-diff compare");

        foreach ((int width, int height) in new[] { (900, 700), (640, 600) })
        {
            Rectangle resizedBounds = ResizeWindow(window, width, height);
            string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
            Thread.Sleep(600);
            AssertProbe(
                IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsCompareDiffSearchBox"))) ||
                IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffViewer"))),
                $"Virtualized diff viewer was not visible at {viewportLabel}.");
            AssertCommitDiffViewerContracts(window, $"commits-virtualized-diff {viewportLabel}");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, $"commits-virtualized-diff-{viewportLabel}.png"));
        }
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunCommitsPerformanceProbe(CaptureOptions options)
{
    const double selectionInputBudgetMilliseconds = 50;
    const double firstDiffRowsBudgetMilliseconds = 750;
    const double searchIndexBudgetMilliseconds = 500;
    const double dispatcherStallBudgetMilliseconds = 50;
    const double minimumScrollFramesPerSecond = 30;
    const long workingSetBudgetBytes = 768L * 1024 * 1024;

    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-commits",
        "--theme=dark",
        "--large-commit",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "commits-performance probe");
        ResizeWindow(window, 1366, 900);
        AutomationElement workspace = WaitForElement(
            "RepoCommitsAdaptiveWorkspace performance monitor",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsAdaptiveWorkspace")),
            TimeSpan.FromSeconds(14));
        EnsureCommitDetailVisible(window);
        EnsureCommitDiffVisible(window);

        AutomationElement list = WaitForElement(
            "RepoCommitsList performance fixture",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsList")),
            TimeSpan.FromSeconds(8));
        AutomationElement[] rows = list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .Where(IsVisible)
            .Take(3)
            .ToArray();
        AssertProbe(rows.Length >= 2, "commits-performance: fixture did not expose a neighboring commit.");

        Mouse.MoveTo(CenterPoint(rows[1], window));
        Thread.Sleep(100);
        Stopwatch firstRows = Stopwatch.StartNew();
        Stopwatch input = Stopwatch.StartNew();
        Mouse.Click();
        input.Stop();
        AssertProbe(
            input.Elapsed.TotalMilliseconds <= selectionInputBudgetMilliseconds,
            $"commits-performance: selection input took {input.Elapsed.TotalMilliseconds:F1} ms " +
            $"(budget {selectionInputBudgetMilliseconds:F0} ms).");

        CommitPerformanceSnapshot selectionSnapshot = WaitForCommitPerformanceSnapshot(
            workspace,
            snapshot => snapshot.FirstDiffMilliseconds >= 0 && snapshot.RenderCount > 0,
            TimeSpan.FromSeconds(8));
        AutomationElement performanceDiffRows = WaitForElement(
            "large commit diff viewport",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("CommitDiffViewerRowsScrollViewer")),
            TimeSpan.FromSeconds(8));
        WaitUntil(
            "large commit first realized diff row",
            () => FindVisibleDiffTextElements(performanceDiffRows).Length > 0,
            TimeSpan.FromSeconds(8));
        firstRows.Stop();

        AssertProbe(
            selectionSnapshot.FirstDiffMilliseconds <= firstDiffRowsBudgetMilliseconds &&
            firstRows.Elapsed.TotalMilliseconds <= firstDiffRowsBudgetMilliseconds,
            $"commits-performance: first diff rows took monitor={selectionSnapshot.FirstDiffMilliseconds:F1} ms, " +
            $"visible={firstRows.Elapsed.TotalMilliseconds:F1} ms (budget {firstDiffRowsBudgetMilliseconds:F0} ms).");
        AssertProbe(
            selectionSnapshot.DispatcherMaxGapMilliseconds <= dispatcherStallBudgetMilliseconds,
            $"commits-performance: UI dispatcher stalled for {selectionSnapshot.DispatcherMaxGapMilliseconds:F1} ms " +
            $"(budget {dispatcherStallBudgetMilliseconds:F0} ms).");

        AutomationElement searchBox = WaitForElement(
            "RepoCommitsDiffSearchBox performance fixture",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffSearchBox")),
            TimeSpan.FromSeconds(8));
        SetTextBoxText(searchBox, "PERF_TARGET_35_119");
        CommitPerformanceSnapshot searchSnapshot = WaitForCommitPerformanceSnapshot(
            workspace,
            snapshot => snapshot.SearchMilliseconds >= 0,
            TimeSpan.FromSeconds(5));
        AssertProbe(
            searchSnapshot.SearchMilliseconds <= searchIndexBudgetMilliseconds,
            $"commits-performance: diff search indexing took {searchSnapshot.SearchMilliseconds:F1} ms " +
            $"(budget {searchIndexBudgetMilliseconds:F0} ms).");
        AssertProbe(
            searchSnapshot.DispatcherMaxGapMilliseconds <= dispatcherStallBudgetMilliseconds,
            $"commits-performance: search blocked the dispatcher for {searchSnapshot.DispatcherMaxGapMilliseconds:F1} ms " +
            $"(budget {dispatcherStallBudgetMilliseconds:F0} ms).");
        SetTextBoxText(searchBox, string.Empty);

        AutomationElement diffRows = WaitForElement(
            "CommitDiffViewerRowsScrollViewer performance fixture",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("CommitDiffViewerRowsScrollViewer")),
            TimeSpan.FromSeconds(8));
        Mouse.MoveTo(diffRows.BoundingRectangle.Center());
        CommitPerformanceSnapshot beforeScroll = ReadCommitPerformanceSnapshot(workspace);
        Stopwatch scroll = Stopwatch.StartNew();
        while (scroll.Elapsed < TimeSpan.FromSeconds(1.2))
        {
            Mouse.Scroll(-1);
            Thread.Sleep(20);
        }
        scroll.Stop();
        Thread.Sleep(250);
        CommitPerformanceSnapshot afterScroll = ReadCommitPerformanceSnapshot(workspace);
        double scrollFramesPerSecond = Math.Max(0, afterScroll.RenderCount - beforeScroll.RenderCount) /
            Math.Max(0.001, scroll.Elapsed.TotalSeconds);
        AssertProbe(
            scrollFramesPerSecond >= minimumScrollFramesPerSecond,
            $"commits-performance: scroll rendered {scrollFramesPerSecond:F1} frames/s " +
            $"(budget {minimumScrollFramesPerSecond:F0} frames/s).");
        AssertProbe(
            afterScroll.DispatcherMaxGapMilliseconds <= dispatcherStallBudgetMilliseconds,
            $"commits-performance: scrolling stalled the dispatcher for {afterScroll.DispatcherMaxGapMilliseconds:F1} ms " +
            $"(budget {dispatcherStallBudgetMilliseconds:F0} ms).");

        using Process process = Process.GetProcessById(app.ProcessId);
        process.Refresh();
        long workingSetBytes = process.WorkingSet64;
        AssertProbe(
            workingSetBytes <= workingSetBudgetBytes,
            $"commits-performance: working set {workingSetBytes / (1024 * 1024):N0} MiB exceeded " +
            $"{workingSetBudgetBytes / (1024 * 1024):N0} MiB.");

        CaptureWindow(window, Path.Combine(options.OutputDirectory, "commits-performance-large-fixture.png"));
        Console.WriteLine(
            $"commits-performance probe: input={input.Elapsed.TotalMilliseconds:F1}ms; " +
            $"first-visible={firstRows.Elapsed.TotalMilliseconds:F1}ms; " +
            $"search={searchSnapshot.SearchMilliseconds:F1}ms; " +
            $"dispatcher-max={afterScroll.DispatcherMaxGapMilliseconds:F1}ms; " +
            $"scroll-fps={scrollFramesPerSecond:F1}; working-set={workingSetBytes / (1024 * 1024):N0}MiB; " +
            $"output={options.OutputDirectory}");
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static CommitPerformanceSnapshot WaitForCommitPerformanceSnapshot(
    AutomationElement workspace,
    Func<CommitPerformanceSnapshot, bool> predicate,
    TimeSpan timeout)
{
    CommitPerformanceSnapshot snapshot = default;
    WaitUntil(
        "commit performance counters",
        () => CommitPerformanceSnapshot.TryParse(
                workspace.Properties.ItemStatus.ValueOrDefault,
                out snapshot) &&
            predicate(snapshot),
        timeout);
    return snapshot;
}

static CommitPerformanceSnapshot ReadCommitPerformanceSnapshot(AutomationElement workspace)
{
    string? value = workspace.Properties.ItemStatus.ValueOrDefault;
    AssertProbe(
        CommitPerformanceSnapshot.TryParse(value, out CommitPerformanceSnapshot snapshot),
        $"Commit performance counters were malformed: '{value}'.");
    return snapshot;
}

static void RunProfileResponsiveProbe(CaptureOptions options)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(options.AppPath, "--page=shell", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "profile-responsive probe");
        Rectangle shellBounds = ResizeWindow(window, 760, 650);
        WaitForElement(
            "Dashboard before profile navigation",
            () => FindCurrentVisibleByAutomationId(window, "DashboardPageRoot"),
            TimeSpan.FromSeconds(8));
        AssertProbe(
            shellBounds.Width < 900,
            $"Compact Profile route probe settled at an unexpected native width of {shellBounds.Width}px.");
        AutomationElement profileRoute = WaitForElement(
            "ShellProfileTopButton for compact profile navigation",
            () => FindCurrentVisibleByAutomationId(window, "ShellProfileTopButton"),
            TimeSpan.FromSeconds(8));
        InvokeOrClick(profileRoute);
        WaitForElement(
            "ProfilePageRoot through shell navigation",
            () => FindCurrentVisibleByAutomationId(window, "ProfilePageRoot"),
            TimeSpan.FromSeconds(12));
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
            if (actualWidth >= 900)
            {
                AssertProbe(
                    IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ProfileEditButton"))),
                    $"Wide profile identity rail edit button was not visible at {viewportLabel}.");
            }
            else
            {
                AssertProbe(
                    IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ProfileCompactEditButton"))),
                    $"Compact profile edit button was not visible at {viewportLabel}.");
            }

            if (width == 1366 || width == 640)
            {
                AssertProfileModeSwitchKeepsBoardStable(window, $"profile-responsive {viewportLabel}");
            }

            CaptureWindow(window, Path.Combine(options.OutputDirectory, $"profile-responsive-{viewportLabel}.png"));
        }

        Rectangle editViewport = ResizeWindow(window, 1366, 900);
        AutomationElement graph = WaitForElement(
            "ProfileContributionGraph",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("ProfileContributionGraph")),
            TimeSpan.FromSeconds(8));
        AssertProbe(graph.ControlType == ControlType.Calendar, "Contribution graph did not expose the Calendar control type.");
        AssertProbe(graph.Properties.IsKeyboardFocusable.ValueOrDefault, "Contribution graph was not keyboard focusable.");
        graph.Focus();
        WaitUntil("contribution graph keyboard focus", () => IsElementFocused(graph), TimeSpan.FromSeconds(5));
        string initialContributionName = graph.Name;
        AssertProbe(
            initialContributionName.StartsWith("Contribution calendar. ", StringComparison.Ordinal),
            "Focused contribution graph did not announce the selected date and count.");
        WaitForElement(
            "contribution graph keyboard tooltip",
            () => automation.GetDesktop().FindAllDescendants(cf => cf.ByControlType(ControlType.ToolTip)).FirstOrDefault(IsVisible),
            TimeSpan.FromSeconds(5));
        Keyboard.Press(VirtualKeyShort.LEFT);
        WaitUntil(
            "contribution graph accessible selection changes",
            () => !string.Equals(graph.Name, initialContributionName, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        string previousContributionName = graph.Name;
        Keyboard.Press(VirtualKeyShort.HOME);
        WaitUntil(
            "contribution graph Home navigation",
            () => !string.Equals(graph.Name, previousContributionName, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        previousContributionName = graph.Name;
        Keyboard.Press(VirtualKeyShort.END);
        WaitUntil(
            "contribution graph End navigation",
            () => !string.Equals(graph.Name, previousContributionName, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "profile-contribution-graph-keyboard.png"));

        string editButtonId = editViewport.Width >= 900
            ? "ProfileEditButton"
            : "ProfileCompactEditButton";
        AutomationElement edit = WaitForElement(
            editButtonId,
            () => FindCurrentVisibleByAutomationId(window, editButtonId),
            TimeSpan.FromSeconds(8));
        InvokeOrClick(edit);
        AutomationElement nameBox = WaitForElement(
            "ProfileEditNameBox",
            () => FindElementInWindowOrDialog(window, automation, "ProfileEditNameBox"),
            TimeSpan.FromSeconds(8));
        AssertProbe(IsVisible(nameBox), "Profile edit dialog did not expose the name field.");
        AutomationElement bio = WaitForElement(
            "ProfileEditBioBox",
            () => FindElementInWindowOrDialog(window, automation, "ProfileEditBioBox"),
            TimeSpan.FromSeconds(5));
        AutomationElement hireable = WaitForElement(
            "ProfileEditHireableToggle",
            () => FindElementInWindowOrDialog(window, automation, "ProfileEditHireableToggle"),
            TimeSpan.FromSeconds(5));
        RevealForInteraction(bio, "Profile edit bio field");
        RevealForInteraction(hireable, "Profile edit hireable field");
        AssertProbe(
            IsVisible(hireable),
            "Profile edit dialog did not make the final REST-supported field reachable.");
        CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "profile-auth-edit-open.png"));
        AutomationElement cancel = WaitForElement(
            "Profile edit cancel",
            () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Cancel")),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(cancel);
        WaitUntil(
            "profile edit dialog closes",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, "ProfileEditNameBox")),
            TimeSpan.FromSeconds(5));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunProfileAvatarRoutingProbe(CaptureOptions options)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(
        options.AppPath,
        "--page=repo-issues",
        "--theme=dark",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "profile-avatar-routing probe");
        ResizeWindow(window, 1180, 800);
        AutomationElement avatar = WaitForElement(
            "repeated actionable issue list author avatars",
            () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Where(element =>
                {
                    string id = GetAutomationId(element);
                    return IsVisible(element) &&
                           id.StartsWith("UserProfile_issue_list_author_", StringComparison.Ordinal) &&
                           element.Properties.IsKeyboardFocusable.ValueOrDefault;
                })
                .Skip(1)
                .FirstOrDefault(),
            TimeSpan.FromSeconds(15));
        AutomationElement[] issueAvatars = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .Where(element =>
                IsVisible(element) &&
                GetAutomationId(element).StartsWith("UserProfile_issue_list_author_", StringComparison.Ordinal) &&
                element.Properties.IsKeyboardFocusable.ValueOrDefault)
            .ToArray();
        AssertProbe(
            issueAvatars.Select(GetAutomationId).Distinct(StringComparer.Ordinal).Count() == issueAvatars.Length,
            "Repeated issue author avatars exposed duplicate automation IDs.");
        string selectedIssueIdBeforeAvatar = window
            .FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .Where(element => GetAutomationId(element).StartsWith("RepoIssueRow_", StringComparison.Ordinal))
            .FirstOrDefault(element =>
                element.Patterns.SelectionItem.IsSupported &&
                element.Patterns.SelectionItem.Pattern.IsSelected.Value) is { } selectedBefore
                    ? GetAutomationId(selectedBefore)
                    : string.Empty;

        AssertProbe(
            (avatar.Name ?? string.Empty).StartsWith("Open @", StringComparison.Ordinal) &&
            (avatar.Name ?? string.Empty).EndsWith(" profile", StringComparison.Ordinal),
            "Shared avatar did not expose a user-facing profile action name.");
        AutomationElement commandSearch = WaitForElement(
            "shell command search baseline target",
            () => FindShellSearchTextBox(window),
            TimeSpan.FromSeconds(5));
        MoveMouseToEmptyTitleBar(window, commandSearch);
        WaitForScreenshotRegionToStabilize(
            window,
            avatar.BoundingRectangle,
            TimeSpan.FromSeconds(4));
        string beforePath = Path.Combine(options.OutputDirectory, "profile-avatar-routing-before.png");
        CaptureWindow(window, beforePath);
        string avatarAutomationId = GetAutomationId(avatar);
        Rectangle avatarBounds = avatar.BoundingRectangle;
        Console.WriteLine($"profile-avatar-routing: window={window.BoundingRectangle}; avatar={avatarBounds}; id={avatarAutomationId}");
        Mouse.MoveTo(new Point(avatarBounds.Left + avatarBounds.Width / 2, avatarBounds.Top + avatarBounds.Height / 2));
        Thread.Sleep(500);
        string hoverPath = Path.Combine(options.OutputDirectory, "profile-avatar-routing-hover.png");
        CaptureWindow(window, hoverPath);
        if (ScreenshotRegionPixelsEqual(
                beforePath,
                hoverPath,
                window.BoundingRectangle,
                avatarBounds))
        {
            Console.WriteLine(
                "profile-avatar-routing: the desktop capture backend did not expose composited hover pixels; " +
                "continuing with keyboard focus, invoke, routing, and restoration assertions.");
        }

        TryActivateWindow(window);
        avatar.FocusNative();
        WaitUntil("avatar keyboard focus", () => IsElementFocused(avatar), TimeSpan.FromSeconds(4));
        InvokeOrClick(avatar);
        AutomationElement profileOverviewMode = WaitForElement(
            "Profile Overview mode after issue-list author invocation",
            () => FindCurrentVisibleByAutomationId(window, "ProfileModeOverviewItem"),
            TimeSpan.FromSeconds(20));
        AssertProbe(
            profileOverviewMode.Patterns.SelectionItem.IsSupported &&
            profileOverviewMode.Patterns.SelectionItem.Pattern.IsSelected.Value,
            "Issue-list author did not navigate to the selected Profile Overview mode.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "profile-avatar-routing-opened.png"));

        AutomationElement back = AssertNamedAutomationElement(window, "ShellBackButton", ControlType.Button);
        AssertProbe(back.IsEnabled, "Avatar profile route did not participate in shell history.");
        InvokeOrClick(back);
        AutomationElement restoredAvatar = WaitForElement(
            "same issue-list author avatar after profile route Back",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(avatarAutomationId)),
            TimeSpan.FromSeconds(12));
        if (!string.IsNullOrWhiteSpace(selectedIssueIdBeforeAvatar))
        {
            AutomationElement? selectedAfter = window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Where(element => GetAutomationId(element).StartsWith("RepoIssueRow_", StringComparison.Ordinal))
                .FirstOrDefault(element =>
                    element.Patterns.SelectionItem.IsSupported &&
                    element.Patterns.SelectionItem.Pattern.IsSelected.Value);
            AssertProbe(
                selectedAfter is not null &&
                string.Equals(GetAutomationId(selectedAfter), selectedIssueIdBeforeAvatar, StringComparison.Ordinal),
                "Invoking the nested issue author avatar also invoked or changed the parent issue row.");
        }
        WaitUntil(
            "issue-list author avatar focus restoration",
            () => IsElementFocused(restoredAvatar),
            TimeSpan.FromSeconds(8));
        Console.WriteLine("profile-avatar-routing: issue-list author hover, UIA invocation, Profile route, Back, and focus restoration passed.");

        ResizeWindow(window, 1366, 900);
        ExerciseCanonicalRepositoryAvatarRouteProbe(
            window,
            "Open Active Repo Pull Requests",
            "RepoPullRequestsPageRoot",
            "pull_request_list_author",
            "pull-request-list-author",
            options.OutputDirectory);
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }

}

static void ExerciseCanonicalRepositoryAvatarRouteProbe(
    Window window,
    string repositoryCommandTitle,
    string pageRootAutomationId,
    string navigationSource,
    string artifactName,
    string outputDirectory)
{
    ExecuteShellCommand(window, repositoryCommandTitle);
    WaitForElement(
        pageRootAutomationId,
        () => FindCurrentVisibleByAutomationId(window, pageRootAutomationId),
        TimeSpan.FromSeconds(15));
    ExerciseVisibleRepositoryAvatarRouteProbe(
        window,
        navigationSource,
        artifactName,
        outputDirectory);
}

static void RunDirectRepositoryAvatarRouteProbe(
    CaptureOptions options,
    string pageArgument,
    string pageRootAutomationId,
    string navigationSource,
    string artifactName)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(
        options.AppPath,
        pageArgument,
        "--theme=dark",
        $"--repo={options.RepositoryFullName}");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, $"{artifactName} profile routing probe");
        ResizeWindow(window, 1366, 900);
        try
        {
            if (string.Equals(pageRootAutomationId, "RepoCommitsPageRoot", StringComparison.Ordinal) &&
                FindCurrentVisibleByAutomationId(window, pageRootAutomationId) is null)
            {
                AutomationElement commitsTab = WaitForElement(
                    "repository commits tab",
                    () => FindCurrentVisibleByAutomationId(window, "RepoNavigation_Commits"),
                    TimeSpan.FromSeconds(8));
                if (commitsTab.Patterns.SelectionItem.IsSupported)
                {
                    try
                    {
                        commitsTab.Patterns.SelectionItem.Pattern.Select();
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        InvokeOrClick(commitsTab);
                    }
                }
                else
                {
                    InvokeOrClick(commitsTab);
                }
            }

            WaitForElement(
                pageRootAutomationId,
                () => FindCurrentVisibleByAutomationId(window, pageRootAutomationId),
                TimeSpan.FromSeconds(15));
        }
        catch
        {
            PrintVisibleAutomationIds(window, $"{artifactName}-launch");
            CaptureWindow(
                window,
                Path.Combine(options.OutputDirectory, $"profile-avatar-routing-{artifactName}-launch-failure.png"));
            throw;
        }
        ExerciseVisibleRepositoryAvatarRouteProbe(
            window,
            navigationSource,
            artifactName,
            options.OutputDirectory);
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void ExerciseVisibleRepositoryAvatarRouteProbe(
    Window window,
    string navigationSource,
    string artifactName,
    string outputDirectory)
{
    AutomationElement[] avatars = [];
    WaitUntil(
        $"repeated {artifactName} avatars",
        () =>
        {
            avatars = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .Where(element =>
                IsVisible(element) && IsUserIdentityForSource(GetAutomationId(element), navigationSource))
                .ToArray();
            return avatars.Length >= 2;
        },
        TimeSpan.FromSeconds(15));
    AssertProbe(
        avatars.Select(GetAutomationId).Distinct(StringComparer.Ordinal).Count() == avatars.Length,
        $"Repeated {artifactName} avatars exposed duplicate automation IDs.");
    AutomationElement? avatar = avatars
        .Skip(1)
        .FirstOrDefault(element =>
            element.Properties.IsKeyboardFocusable.ValueOrDefault &&
            (element.Name ?? string.Empty).StartsWith("Open @", StringComparison.Ordinal));
    if (avatar is null)
    {
        AssertProbe(
            avatars.All(element => string.Equals(element.Name, "Profile unavailable", StringComparison.Ordinal)),
            $"Passive {artifactName} identities exposed an unexpected action.");
        CaptureWindow(window, Path.Combine(outputDirectory, $"profile-avatar-routing-{artifactName}-passive.png"));
        Console.WriteLine($"profile-avatar-routing: repeated passive {artifactName} IDs are unique and expose no profile action.");
        return;
    }

    string automationId = GetAutomationId(avatar);
    AssertProbe(
        (avatar.Name ?? string.Empty).StartsWith("Open @", StringComparison.Ordinal),
        $"The {artifactName} avatar did not expose an internal profile action.");

    avatar.FocusNative();
    WaitUntil($"{artifactName} avatar keyboard focus", () => IsElementFocused(avatar), TimeSpan.FromSeconds(4));
    InvokeOrClick(avatar);
    WaitForElement(
        $"Profile page after {artifactName} avatar invocation",
        () => FindCurrentVisibleByAutomationId(window, "ProfilePageRoot"),
        TimeSpan.FromSeconds(20));
    AutomationElement profileOverviewMode = WaitForElement(
        $"Profile Overview after {artifactName} avatar invocation",
        () => FindCurrentVisibleByAutomationId(window, "ProfileModeOverviewItem"),
        TimeSpan.FromSeconds(20));
    AssertProbe(
        profileOverviewMode.Patterns.SelectionItem.IsSupported &&
        profileOverviewMode.Patterns.SelectionItem.Pattern.IsSelected.Value,
        $"The {artifactName} avatar did not route internally to Profile.");

    AutomationElement back = AssertNamedAutomationElement(window, "ShellBackButton", ControlType.Button);
    AssertProbe(back.IsEnabled, $"The {artifactName} profile route was not added to shell history.");
    InvokeOrClick(back);
    _ = WaitForElement(
        $"restored {artifactName} avatar",
        () => FindCurrentVisibleByAutomationId(window, automationId),
        TimeSpan.FromSeconds(12));
    WaitUntil(
        $"{artifactName} avatar focus restoration",
        () => FindCurrentVisibleByAutomationId(window, automationId) is { } currentAvatar &&
              IsElementFocused(currentAvatar),
        TimeSpan.FromSeconds(8));
}

static bool IsUserIdentityForSource(string automationId, string navigationSource) =>
    automationId.StartsWith($"UserProfile_{navigationSource}_", StringComparison.Ordinal) ||
    automationId.StartsWith($"UserProfile_Unavailable_{navigationSource}_", StringComparison.Ordinal);

static void ExecuteShellCommand(Window window, string commandTitle)
{
    TryActivateWindow(window);
    PressCtrlK();
    AutomationElement searchBox = WaitForElement(
        $"shell command search for {commandTitle}",
        () => FindShellSearchTextBox(window),
        TimeSpan.FromSeconds(5));
    TextBox textBox = searchBox.AsTextBox();
    textBox.Text = string.Empty;
    textBox.Enter(commandTitle);

    AutomationElement suggestions = WaitForElement(
        $"shell command suggestions for {commandTitle}",
        () =>
        {
            AutomationElement? list = window.FindFirstDescendant(
                cf => cf.ByAutomationId("ShellSearchSuggestionsList"));
            return IsVisible(list) ? list : null;
        },
        TimeSpan.FromSeconds(10));
    AutomationElement command = WaitForElement(
        commandTitle,
        () => suggestions.FindFirstDescendant(cf => cf.ByText(commandTitle)),
        TimeSpan.FromSeconds(5));
    InvokeOrClick(command);
}

static void RunSettingsResponsiveProbe(CaptureOptions options)
{
    RunSettingsDarkAndLightProbe(options);
    RunSettingsPseudoLongLabelsProbe(options);

    if (NativeMethods.IsHighContrastEnabled())
    {
        RunSettingsHighContrastProbe(options);
    }
    else
    {
        const string reason =
            "Genuine Windows High Contrast is not active in this desktop session. " +
            "The harness did not simulate a palette and skipped this conditional pass.";
        File.WriteAllText(Path.Combine(options.OutputDirectory, "settings-high-contrast-skipped.txt"), reason);
        Console.WriteLine($"settings-responsive: {reason}");
    }
}

static void RunSettingsExportPickerProbe(CaptureOptions options)
{
    bool isAttached = !string.IsNullOrWhiteSpace(options.AttachProcess);
    using var app = isAttached
        ? CreateProbeApplication(options)
        : LaunchApplication(options.AppPath, "--page=settings", "--theme=light");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "settings export picker probe");
        ResizeWindow(window, 1366, 900);
        AssertSettingsExportPicker(window, automation, options.OutputDirectory);
        Console.WriteLine("settings-export-picker probe: picker canceled and settings action gate released.");
    }
    finally
    {
        if (!isAttached)
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}

static void RunVNextPseudoLocalizationProbe(CaptureOptions options)
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

static IReadOnlySet<string> LoadEnglishUiFallbacks(string appPath)
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

static void RunSettingsDarkAndLightProbe(CaptureOptions options)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(options.AppPath, "--page=settings", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "settings-responsive probe");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "settings-responsive-launch.png"));
        WaitForElement(
            "SettingsSectionList",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList")),
            TimeSpan.FromSeconds(10));

        AssertSettingsResponsiveLayoutAtAllWidths(window, automation, options.OutputDirectory, "dark");

        OpenSettingsSection(window, "SettingsSection_data-cache", "SettingsClearQueryCacheButton");
        ResizeWindow(window, 1366, 900);
        (string ActionId, string DialogId, string Name)[] dataConfirmations =
        [
            ("SettingsClearQueryCacheButton", "SettingsConfirmClearQueryCache", "Clear GitHub query cache?"),
            ("SettingsClearStarLibraryButton", "SettingsConfirmClearStarsLibrary", "Clear the Stars library?"),
            ("SettingsClearImageCacheButton", "SettingsConfirmClearImageCache", "Clear avatar and image cache?"),
            ("SettingsClearRepoFileCacheButton", "SettingsConfirmClearRepoFileCache", "Clear repository file cache?"),
            ("SettingsClearAllCacheButton", "SettingsConfirmClearAllCache", "Clear all Phase 0 cache data?")
        ];
        foreach ((string actionId, string dialogId, string name) in dataConfirmations)
        {
            AssertSettingsConfirmation(window, automation, options.OutputDirectory, actionId, dialogId, name);
        }

        OpenSettingsSection(window, "SettingsSection_diagnostics", "SettingsExportDiagnosticsButton");
        AssertSettingsConfirmation(
            window,
            automation,
            options.OutputDirectory,
            "SettingsClearDiagnosticsButton",
            "SettingsConfirmClearDiagnostics",
            "Clear diagnostics?");

        OpenSettingsSection(window, "SettingsSection_general", "SettingsDeveloperModeToggle");
        AssertSettingsSignOutConfirmation(window, automation, options.OutputDirectory);

        OpenSettingsSection(window, "SettingsSection_appearance", "SettingsThemeSystem");
        AssertSettingsThemeCardSemantics(window);
        AssertSettingsResponsiveLayoutAtAllWidths(window, automation, options.OutputDirectory, "light");
        ResizeWindow(window, 1366, 900);
        AssertSettingsExportPicker(window, automation, options.OutputDirectory);
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void AssertSettingsConfirmation(
    Window window,
    UIA3Automation automation,
    string outputDirectory,
    string actionId,
    string dialogId,
    string expectedName)
{
    AutomationElement action = WaitForElement(
        actionId,
        () => window.FindFirstDescendant(cf => cf.ByAutomationId(actionId)),
        TimeSpan.FromSeconds(5));
    RevealForInteraction(action, actionId);
    bool canUseForegroundFocus = !NativeMethods.HasInvisibleForeignForegroundWindow(
        window.Properties.NativeWindowHandle.ValueOrDefault);
    if (canUseForegroundFocus)
    {
        action.Focus();
    }
    string artifactName = dialogId.Replace("SettingsConfirm", "settings-confirm-", StringComparison.Ordinal);
    string baselinePath = Path.Combine(outputDirectory, $"{artifactName}-baseline.png");
    string dialogPath = Path.Combine(outputDirectory, $"{artifactName}-open.png");
    CaptureWindow(window, baselinePath);
    InvokeOrClick(action);
    TryInvokeDisabledSettingsAction(action);
    AutomationElement confirmation = WaitForElement(
        dialogId,
        () => FindElementInWindowOrDialog(window, automation, dialogId),
        TimeSpan.FromSeconds(5));
    AssertProbe(
        string.Equals(confirmation.Name, expectedName, StringComparison.Ordinal),
        $"Settings confirmation {dialogId} exposed the wrong accessible name.");
    Rectangle dialogContentBounds = GetDialogContentEnvelope(confirmation);
    AssertProbe(IsRectangleInsideWindow(dialogContentBounds, window), $"{dialogId} escaped the app window.");
    AssertProbe(IsRectangleHorizontallyCentered(dialogContentBounds, window, 36), $"{dialogId} was not centered in the app window.");
    AssertSingleVisibleSettingsDialog(window, automation, dialogId);
    AssertSettingsActionsDisabled(window);
    if (canUseForegroundFocus)
    {
        AssertDialogFocusContained(automation, confirmation, dialogId);
    }
    CaptureWindowWithPopups(window, dialogPath);
    AssertSettingsDialogScrim(baselinePath, dialogPath, window, dialogContentBounds, dialogId);

    if (canUseForegroundFocus)
    {
        Keyboard.Press(VirtualKeyShort.ESCAPE);
    }
    else
    {
        InvokeOrClick(FindDialogButton(confirmation, automation, "Cancel"));
    }
    WaitUntil(
        $"{dialogId} closes on Escape",
        () => !IsVisible(FindElementInWindowOrDialog(window, automation, dialogId)),
        TimeSpan.FromSeconds(5));
    if (canUseForegroundFocus)
    {
        WaitUntil(
            $"{dialogId} restores opener focus",
            () => IsElementFocused(FindElementInWindowOrDialog(window, automation, actionId)),
            TimeSpan.FromSeconds(5));
    }

    InvokeOrClick(action);
    confirmation = WaitForElement(
        $"{dialogId} cancel cycle",
        () => FindElementInWindowOrDialog(window, automation, dialogId),
        TimeSpan.FromSeconds(5));
    AutomationElement cancel = FindDialogButton(confirmation, automation, "Cancel");
    InvokeOrClick(cancel);
    WaitUntil(
        $"{dialogId} cancel button closes",
        () => !IsVisible(FindElementInWindowOrDialog(window, automation, dialogId)),
        TimeSpan.FromSeconds(5));
    if (canUseForegroundFocus)
    {
        WaitUntil(
            $"{dialogId} cancel restores opener focus",
            () => IsElementFocused(FindElementInWindowOrDialog(window, automation, actionId)),
            TimeSpan.FromSeconds(5));
    }

    InvokeOrClick(action);
    confirmation = WaitForElement(
        $"{dialogId} primary cycle",
        () => FindElementInWindowOrDialog(window, automation, dialogId),
        TimeSpan.FromSeconds(5));
    AutomationElement primary = confirmation.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
        .Where(IsVisible)
        .First(button => !string.Equals(button.Name, "Cancel", StringComparison.Ordinal));
    InvokeOrClick(primary);
    WaitUntil(
        $"{dialogId} primary action completes",
        () => !IsVisible(FindElementInWindowOrDialog(window, automation, dialogId)) &&
            FindElementInWindowOrDialog(window, automation, actionId) is { IsEnabled: true },
        TimeSpan.FromSeconds(15));
    if (canUseForegroundFocus)
    {
        WaitUntil(
            $"{dialogId} primary restores opener focus",
            () => IsElementFocused(FindElementInWindowOrDialog(window, automation, actionId)),
            TimeSpan.FromSeconds(5));
    }
}

static void AssertSettingsResponsiveLayoutAtAllWidths(
    Window window,
    UIA3Automation automation,
    string outputDirectory,
    string theme)
{
    (string Title, string SectionId, string ProofId)[] sections =
    [
        ("Appearance", "SettingsSection_appearance", "SettingsThemeSystem"),
        ("General", "SettingsSection_general", "SettingsDeveloperModeToggle"),
        ("Privacy", "SettingsSection_privacy", "SettingsDiagnosticsToggle"),
        ("Data & Cache", "SettingsSection_data-cache", "SettingsClearQueryCacheButton"),
        ("Diagnostics", "SettingsSection_diagnostics", "SettingsExportDiagnosticsButton"),
        ("About", "SettingsSection_about", "SettingsViewSourceButton")
    ];
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
        bool usesCompactNavigation = actualWidth < 820;
        AutomationElement? rail = window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList"));
        AutomationElement? picker = window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsCompactSectionPicker"));
        if (usesCompactNavigation)
        {
            picker = WaitForElement(
                "SettingsCompactSectionPicker",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsCompactSectionPicker")),
                TimeSpan.FromSeconds(5));
            picker.AsComboBox().Expand();
            foreach ((string title, string sectionId, _) in sections)
            {
                AutomationElement compactItem = WaitForElement(
                    $"compact Settings section {title}",
                    () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId($"{sectionId}_Compact")),
                    TimeSpan.FromSeconds(5));
                AssertProbe(
                    string.Equals(compactItem.Name, title, StringComparison.Ordinal),
                    $"Compact Settings section {title} exposed the wrong accessible name.");
                AssertProbe(IsInsideWindowBounds(compactItem, window), $"Compact Settings section {title} was clipped at {viewportLabel}.");
            }
            picker.AsComboBox().Collapse();
        }
        else
        {
            rail = WaitForElement(
                "SettingsSectionList",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList")),
                TimeSpan.FromSeconds(5));
        }

        AssertProbe(IsVisible(picker) == usesCompactNavigation, $"Settings compact picker visibility was wrong at {viewportLabel}.");
        AssertProbe(IsVisible(rail) != usesCompactNavigation, $"Settings section rail visibility was wrong at {viewportLabel}.");

        foreach ((string title, string sectionId, string proofId) in sections)
        {
            if (usesCompactNavigation)
            {
                picker!.AsComboBox().Select(title);
            }
            else
            {
                AutomationElement section = WaitForElement(
                    $"Settings section {title}",
                    () => rail!.FindFirstDescendant(cf => cf.ByAutomationId(sectionId)),
                    TimeSpan.FromSeconds(5));
                AssertProbe(
                    string.Equals(section.Name, title, StringComparison.Ordinal),
                    $"Settings section {title} exposed the wrong accessible name: '{section.Name}'.");
                SelectAutomationItem(section, $"Settings section {title}");
            }

            AutomationElement proof = WaitForElement(
                proofId,
                () => window.FindFirstDescendant(cf => cf.ByAutomationId(proofId)),
                TimeSpan.FromSeconds(5));
            RevealForInteraction(proof, proofId);
            AssertProbe(IsInsideWindowBounds(proof, window), $"Settings {title} controls escaped the window at {viewportLabel}.");
        }

        AutomationElement contributorLink = WaitForElement(
            "contributor social link",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("ContributorLink_GitHub_httpsgithubcomGet0457")),
            TimeSpan.FromSeconds(5));
        AssertProbe(
            string.Equals(contributorLink.Name, "Open GitHub profile", StringComparison.Ordinal),
            "Contributor social link did not expose a meaningful accessible name.");
        RevealForInteraction(contributorLink, "contributor social link");
        AssertProbe(IsInsideWindowBounds(contributorLink, window), $"Contributor social action was clipped at {viewportLabel}.");

        if (usesCompactNavigation)
        {
            picker!.AsComboBox().Select("Appearance");
        }
        else
        {
            AutomationElement appearance = WaitForElement(
                "Appearance section at capture reset",
                () => rail!.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSection_appearance")),
                TimeSpan.FromSeconds(5));
            SelectAutomationItem(appearance, "Settings Appearance section");
        }
        AutomationElement topControl = WaitForElement(
            "Settings theme card at capture reset",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsThemeSystem")),
            TimeSpan.FromSeconds(5));
        RevealForInteraction(topControl, "Settings theme card at capture reset");
        CaptureWindow(window, Path.Combine(outputDirectory, $"settings-responsive-{theme}-{viewportLabel}.png"));
    }
}

static void OpenSettingsSection(Window window, string sectionAutomationId, string contentAutomationId)
{
    string title = sectionAutomationId switch
    {
        "SettingsSection_appearance" => "Appearance",
        "SettingsSection_general" => "General",
        "SettingsSection_privacy" => "Privacy",
        "SettingsSection_data-cache" => "Data & Cache",
        "SettingsSection_diagnostics" => "Diagnostics",
        "SettingsSection_about" => "About",
        _ => throw new ArgumentOutOfRangeException(nameof(sectionAutomationId), sectionAutomationId, "Unknown Settings section.")
    };
    AutomationElement selector = WaitForElement(
        $"visible {title} Settings selector",
        () =>
        {
            AutomationElement? picker = window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsCompactSectionPicker"));
            if (IsVisible(picker))
            {
                return picker;
            }

            AutomationElement? section = window.FindFirstDescendant(cf => cf.ByAutomationId(sectionAutomationId));
            return IsVisible(section) ? section : null;
        },
        TimeSpan.FromSeconds(5));
    if (selector.ControlType == ControlType.ComboBox)
    {
        selector.AsComboBox().Select(title);
    }
    else
    {
        RevealForInteraction(selector, sectionAutomationId);
        SelectAutomationItem(selector, $"Settings {title} section");
    }
    AutomationElement content = WaitForElement(
        contentAutomationId,
        () => window.FindFirstDescendant(cf => cf.ByAutomationId(contentAutomationId)),
        TimeSpan.FromSeconds(5));
    RevealForInteraction(content, contentAutomationId);
}

static void AssertSettingsThemeCardSemantics(Window window)
{
    AutomationElement system = AssertNamedAutomationElement(window, "SettingsThemeSystem", ControlType.RadioButton);
    AutomationElement light = AssertNamedAutomationElement(window, "SettingsThemeLight", ControlType.RadioButton);
    AutomationElement dark = AssertNamedAutomationElement(window, "SettingsThemeDark", ControlType.RadioButton);
    AssertProbe(system.Patterns.SelectionItem.IsSupported, "System theme card did not expose native selection semantics.");
    AssertProbe(light.Patterns.SelectionItem.IsSupported, "Light theme card did not expose native selection semantics.");
    AssertProbe(dark.Patterns.SelectionItem.IsSupported, "Dark theme card did not expose native selection semantics.");

    if (NativeMethods.HasInvisibleForeignForegroundWindow(window.Properties.NativeWindowHandle.ValueOrDefault))
    {
        light.Patterns.SelectionItem.Pattern.Select();
        WaitUntil("Light theme card native selection", () => light.Patterns.SelectionItem.Pattern.IsSelected.Value, TimeSpan.FromSeconds(5));
        return;
    }

    FocusForKeyboardActivation(window, system);
    WaitUntil("System theme card receives keyboard focus", () => IsElementFocused(system), TimeSpan.FromSeconds(5));
    Keyboard.Press(VirtualKeyShort.RIGHT);
    WaitUntil("theme card Right selects Light", () => light.Patterns.SelectionItem.Pattern.IsSelected.Value, TimeSpan.FromSeconds(5));
    Keyboard.Press(VirtualKeyShort.RIGHT);
    WaitUntil("theme card Right selects Dark", () => dark.Patterns.SelectionItem.Pattern.IsSelected.Value, TimeSpan.FromSeconds(5));
    Keyboard.Press(VirtualKeyShort.LEFT);
    WaitUntil("theme card Left selects Light", () => light.Patterns.SelectionItem.Pattern.IsSelected.Value, TimeSpan.FromSeconds(5));
    Keyboard.Press(VirtualKeyShort.SPACE);
    AssertProbe(light.Patterns.SelectionItem.Pattern.IsSelected.Value, "Space did not preserve native Light theme radio selection.");
}

static void AssertSettingsPaletteCardSemantics(
    Window window,
    IReadOnlyList<(string Id, string Name)> palettes)
{
    foreach ((string id, string name) in palettes)
    {
        string automationId = $"SettingsPalette_{id}";
        AutomationElement card = WaitForElement(
            automationId,
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
            TimeSpan.FromSeconds(8));
        RevealForInteraction(card, $"{name} palette card");
        card = WaitForElement(
            $"visible {automationId}",
            () =>
            {
                AutomationElement? current = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                return IsVisible(current) ? current : null;
            },
            TimeSpan.FromSeconds(5));
        AssertProbe(card.ControlType == ControlType.RadioButton, $"{automationId} exposed {card.ControlType} instead of RadioButton.");
        AssertProbe(
            string.Equals(card.Name, name, StringComparison.Ordinal),
            $"{id} palette card exposed the wrong accessible name: '{card.Name}'.");
        AssertProbe(
            card.Patterns.SelectionItem.IsSupported,
            $"{name} palette card did not expose native selection semantics.");
        AssertProbe(
            !string.IsNullOrWhiteSpace(card.Properties.HelpText.ValueOrDefault),
            $"{name} palette card did not expose its description to assistive technology.");
    }
}

static void RunSettingsPseudoLongLabelsProbe(CaptureOptions options)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(options.AppPath, "--page=settings", "--theme=dark", "--scenario=settings-pseudo-long-labels");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "settings pseudo-long labels probe");
        ResizeWindow(window, 900, 700);
        AutomationElement rail = WaitForElement(
            "pseudo-long Settings rail",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList")),
            TimeSpan.FromSeconds(8));
        foreach (AutomationElement item in rail.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Where(IsVisible))
        {
            AssertProbe(IsInsideWindowBounds(item, window), $"Pseudo-long Settings rail item '{item.Name}' was clipped.");
        }
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "settings-pseudo-long-900x700.png"));

        ResizeWindow(window, 640, 600);
        AutomationElement picker = WaitForElement(
            "pseudo-long Settings compact picker",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsCompactSectionPicker")),
            TimeSpan.FromSeconds(5));
        picker.AsComboBox().Expand();
        string[] expectedLabels = new[] { "Appearance", "General", "Privacy", "Data & Cache", "Diagnostics", "About" }
            .Select(label => string.Join(" ", Enumerable.Repeat(label, 4)))
            .ToArray();
        AssertProbe(
            WaitUntilAvailable(
                () => picker.AsComboBox().Items.Length == expectedLabels.Length,
                TimeSpan.FromSeconds(5)),
            "Pseudo-long compact Settings picker did not realize all six sections.");
        FlaUI.Core.AutomationElements.ComboBoxItem[] items = picker.AsComboBox().Items;
        for (int index = 0; index < expectedLabels.Length; index++)
        {
            FlaUI.Core.AutomationElements.ComboBoxItem item = items[index];
            AssertProbe(
                string.Equals(item.Text, expectedLabels[index], StringComparison.Ordinal),
                $"Pseudo-long compact Settings item {index} exposed '{item.Text}' instead of its full label.");
            AssertProbe(
                IsInsideWindowBounds(item, window),
                $"Pseudo-long compact Settings label '{expectedLabels[index]}' was clipped: " +
                $"item={item.BoundingRectangle}; window={window.BoundingRectangle}.");
        }
        CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "settings-pseudo-long-640x600-open.png"));
        picker.AsComboBox().Collapse();
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunSettingsHighContrastProbe(CaptureOptions options)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(options.AppPath, "--page=settings");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "settings genuine High Contrast probe");
        AssertSettingsPaletteCardSemantics(
            window,
            [
                ("jithub", "JitHub (default)"),
                ("windows-11", "Windows 11"),
                ("visual-studio-code", "Visual Studio Code"),
                ("github", "GitHub"),
                ("solarized", "Solarized")
            ]);
        AssertSettingsResponsiveLayoutAtAllWidths(window, automation, options.OutputDirectory, "high-contrast");
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static string[] GetSettingsSerializedActionIds() =>
[
    "SettingsSignOutButton",
    "SettingsClearQueryCacheButton",
    "SettingsClearStarLibraryButton",
    "SettingsClearImageCacheButton",
    "SettingsClearRepoFileCacheButton",
    "SettingsClearAllCacheButton",
    "SettingsExportDiagnosticsButton",
    "SettingsClearDiagnosticsButton"
];

static void TryInvokeDisabledSettingsAction(AutomationElement action)
{
    WaitUntil("Settings action enters busy state", () => !action.IsEnabled, TimeSpan.FromSeconds(3));
    try
    {
        if (action.Patterns.Invoke.IsSupported)
        {
            action.Patterns.Invoke.Pattern.Invoke();
        }
        else
        {
            action.Click();
        }
    }
    catch (Exception)
    {
        // A disabled provider may reject the second activation. Either result is valid;
        // the dialog-count assertion proves that no concurrent ShowAsync was attempted.
    }
}

static void AssertSettingsActionsDisabled(Window window)
{
    foreach (string automationId in GetSettingsSerializedActionIds())
    {
        AutomationElement? action = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (action is not null)
        {
            AssertProbe(!action.IsEnabled, $"{automationId} remained enabled while a Settings operation was active.");
        }
    }
}

static void AssertSingleVisibleSettingsDialog(
    Window window,
    UIA3Automation automation,
    string dialogId)
{
    AutomationElement[] visiblePeers = automation.GetDesktop()
        .FindAllDescendants(cf => cf.ByAutomationId(dialogId))
        .Where(element => IsVisible(element) && IsInsideWindowBounds(element, window))
        .ToArray();
    int visibleSurfaceCount = visiblePeers
        .Select(element => element.BoundingRectangle)
        .Distinct()
        .Count();
    AssertProbe(
        visibleSurfaceCount == 1,
        $"Rapid Settings activation produced {visibleSurfaceCount} distinct visible {dialogId} dialog surfaces " +
        $"across {visiblePeers.Length} UIA peers.");
}

static void AssertDialogFocusContained(
    UIA3Automation automation,
    AutomationElement dialog,
    string context)
{
    WaitUntil(
        $"{context} receives focus",
        () => IsInsideElementBounds(automation.FocusedElement(), dialog, tolerance: 2),
        TimeSpan.FromSeconds(5));
    for (int index = 0; index < 8; index++)
    {
        Keyboard.Press(VirtualKeyShort.TAB);
        AutomationElement focused = automation.FocusedElement();
        bool contained = WaitUntilAvailable(
            () => IsInsideElementBounds(automation.FocusedElement(), dialog, tolerance: 2),
            TimeSpan.FromMilliseconds(600));
        AssertProbe(
            contained,
            $"{context} focus escaped after Tab {index + 1} to " +
            $"{focused.ControlType} '{GetAutomationId(focused)}'/'{GetElementName(focused)}' at {focused.BoundingRectangle}; " +
            $"dialog bounds are {dialog.BoundingRectangle}.");
    }
}

static AutomationElement FindDialogButton(
    AutomationElement dialog,
    UIA3Automation automation,
    string name)
{
    AutomationElement? button = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
        .FirstOrDefault(candidate => IsVisible(candidate) && string.Equals(candidate.Name, name, StringComparison.Ordinal));
    button ??= automation.GetDesktop()
        .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
        .FirstOrDefault(candidate =>
            IsVisible(candidate) &&
            string.Equals(candidate.Name, name, StringComparison.Ordinal) &&
            IsInsideElementBounds(candidate, dialog, tolerance: 2));
    return button ?? throw new InvalidOperationException($"Could not find the '{name}' button in the Settings dialog.");
}

static void AssertSettingsDialogScrim(
    string baselinePath,
    string dialogPath,
    Window window,
    Rectangle dialogBounds,
    string context)
{
    using var baseline = new Bitmap(baselinePath);
    using var opened = new Bitmap(dialogPath);
    Rectangle windowBounds = window.BoundingRectangle;
    double scaleX = windowBounds.Width > 0 ? (double)baseline.Width / windowBounds.Width : 1;
    double scaleY = windowBounds.Height > 0 ? (double)baseline.Height / windowBounds.Height : 1;
    int left = Math.Clamp(
        (int)Math.Round((dialogBounds.Left - windowBounds.Left) * scaleX),
        0,
        baseline.Width);
    int top = Math.Clamp(
        (int)Math.Round((dialogBounds.Top - windowBounds.Top) * scaleY),
        0,
        baseline.Height);
    int right = Math.Clamp(
        (int)Math.Round((dialogBounds.Right - windowBounds.Left) * scaleX),
        0,
        baseline.Width);
    int bottom = Math.Clamp(
        (int)Math.Round((dialogBounds.Bottom - windowBounds.Top) * scaleY),
        0,
        baseline.Height);
    double baselineLuminance = 0;
    double openedLuminance = 0;
    int samples = 0;

    for (int y = 72; y < Math.Min(baseline.Height, opened.Height) - 16; y += 12)
    {
        for (int x = 16; x < Math.Min(baseline.Width, opened.Width) - 16; x += 12)
        {
            if (x >= left - 12 && x <= right + 12 && y >= top - 12 && y <= bottom + 12)
            {
                continue;
            }

            Color before = baseline.GetPixel(x, y);
            Color after = opened.GetPixel(x, y);
            baselineLuminance += (0.2126 * before.R) + (0.7152 * before.G) + (0.0722 * before.B);
            openedLuminance += (0.2126 * after.R) + (0.7152 * after.G) + (0.0722 * after.B);
            samples++;
        }
    }

    AssertProbe(samples > 20, $"{context} did not leave enough background pixels to verify its scrim.");
    double darkening = (baselineLuminance - openedLuminance) / samples;
    AssertProbe(darkening >= 1.0, $"{context} did not render a visible modal scrim (average darkening {darkening:0.0}).");
}

static Rectangle GetDialogContentEnvelope(AutomationElement dialog)
{
    Rectangle[] contentBounds = dialog
        .FindAllDescendants()
        .Where(element =>
            IsVisible(element) &&
            (element.ControlType == ControlType.Button ||
             element.ControlType == ControlType.Text ||
             element.ControlType == ControlType.CheckBox ||
             element.ControlType == ControlType.Edit ||
             element.ControlType == ControlType.ComboBox))
        .Select(element => element.BoundingRectangle)
        .Where(bounds => bounds.Width > 0 && bounds.Height > 0)
        .ToArray();
    AssertProbe(contentBounds.Length > 0, "The Settings dialog did not expose any visible content bounds.");

    int left = contentBounds.Min(bounds => bounds.Left) - 24;
    int top = contentBounds.Min(bounds => bounds.Top) - 24;
    int right = contentBounds.Max(bounds => bounds.Right) + 24;
    int bottom = contentBounds.Max(bounds => bounds.Bottom) + 24;
    return Rectangle.FromLTRB(left, top, right, bottom);
}

static bool IsRectangleInsideWindow(Rectangle bounds, Window window)
{
    Rectangle windowBounds = window.BoundingRectangle;
    return bounds.Width > 0 &&
        bounds.Height > 0 &&
        bounds.Left >= windowBounds.Left &&
        bounds.Top >= windowBounds.Top &&
        bounds.Right <= windowBounds.Right &&
        bounds.Bottom <= windowBounds.Bottom;
}

static bool IsRectangleHorizontallyCentered(Rectangle bounds, Window window, double tolerance)
{
    Rectangle windowBounds = window.BoundingRectangle;
    double elementCenter = bounds.Left + (bounds.Width / 2d);
    double windowCenter = windowBounds.Left + (windowBounds.Width / 2d);
    return Math.Abs(elementCenter - windowCenter) <= tolerance;
}

static void AssertSettingsSignOutConfirmation(
    Window window,
    UIA3Automation automation,
    string outputDirectory)
{
    AutomationElement action = WaitForElement(
        "Settings sign-out button",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSignOutButton")),
        TimeSpan.FromSeconds(5));
    RevealForInteraction(action, "Settings sign-out button");
    bool canUseForegroundFocus = !NativeMethods.HasInvisibleForeignForegroundWindow(
        window.Properties.NativeWindowHandle.ValueOrDefault);
    if (canUseForegroundFocus)
    {
        action.Focus();
    }
    InvokeOrClick(action);
    TryInvokeDisabledSettingsAction(action);
    AutomationElement dialog = WaitForElement(
        "Settings sign-out confirmation",
        () => FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog"),
        TimeSpan.FromSeconds(5));
    AssertProbe(string.Equals(dialog.Name, "Sign out of JitHub", StringComparison.Ordinal), "Sign-out dialog exposed the wrong accessible name.");
    Rectangle dialogContentBounds = GetDialogContentEnvelope(dialog);
    AssertProbe(IsRectangleInsideWindow(dialogContentBounds, window), "Sign-out dialog escaped the app window.");
    AssertProbe(IsRectangleHorizontallyCentered(dialogContentBounds, window, 36), "Sign-out dialog was not centered in the app window.");
    AssertSingleVisibleSettingsDialog(window, automation, "SignOutConfirmationDialog");
    AssertSettingsActionsDisabled(window);
    if (canUseForegroundFocus)
    {
        AssertDialogFocusContained(automation, dialog, "Settings sign-out confirmation");
    }
    CaptureWindowWithPopups(window, Path.Combine(outputDirectory, "settings-sign-out-confirmation-open.png"));
    AutomationElement cancel = FindDialogButton(dialog, automation, "Cancel");
    InvokeOrClick(cancel);
    WaitUntil(
        "Settings sign-out confirmation closes",
        () => !IsVisible(FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog")),
        TimeSpan.FromSeconds(5));
    if (canUseForegroundFocus)
    {
        WaitUntil("Settings sign-out restores focus", () => IsElementFocused(action), TimeSpan.FromSeconds(5));
    }

    InvokeOrClick(action);
    _ = WaitForElement(
        "Settings sign-out confirmation Escape cycle",
        () => FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog"),
        TimeSpan.FromSeconds(5));
    if (canUseForegroundFocus)
    {
        Keyboard.Press(VirtualKeyShort.ESCAPE);
    }
    else
    {
        AutomationElement escapeDialog = WaitForElement(
            "Settings sign-out confirmation fallback",
            () => FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog"),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(FindDialogButton(escapeDialog, automation, "Cancel"));
    }
    WaitUntil(
        "Settings sign-out Escape closes",
        () => !IsVisible(FindElementInWindowOrDialog(window, automation, "SignOutConfirmationDialog")),
        TimeSpan.FromSeconds(5));
    if (canUseForegroundFocus)
    {
        WaitUntil("Settings sign-out Escape restores focus", () => IsElementFocused(action), TimeSpan.FromSeconds(5));
    }
}

static void AssertSettingsExportPicker(
    Window window,
    UIA3Automation automation,
    string outputDirectory)
{
    OpenSettingsSection(window, "SettingsSection_diagnostics", "SettingsExportDiagnosticsButton");
    AutomationElement action = WaitForElement(
        "Settings diagnostics export",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsExportDiagnosticsButton")),
        TimeSpan.FromSeconds(5));
    RevealForInteraction(action, "Settings diagnostics export");
    bool canUseForegroundFocus = !NativeMethods.HasInvisibleForeignForegroundWindow(
        window.Properties.NativeWindowHandle.ValueOrDefault);
    if (canUseForegroundFocus)
    {
        action.Focus();
    }
    IntPtr appWindowHandle = GetNativeWindowHandle(window);
    InvokeOrClick(action);
    TryInvokeDisabledSettingsAction(action);
    AutomationElement? pickerWindow = null;
    try
    {
        pickerWindow = WaitForElement(
            "Windows diagnostics save picker window",
            () =>
            {
                if (!NativeMethods.TryFindLargestOwnedTopLevelWindow(appWindowHandle, out IntPtr pickerHandle))
                {
                    return null;
                }

                AutomationElement candidate = automation.FromHandle(pickerHandle);
                return IsVisible(candidate) &&
                    candidate.FindFirstDescendant(
                        cf => cf.ByAutomationId("FileNameControlHost")) is not null
                    ? candidate
                    : null;
            },
            TimeSpan.FromSeconds(8));
        AutomationElement pickerSave = WaitForElement(
            "Windows diagnostics save picker primary command",
            () => pickerWindow.FindFirstDescendant(
                cf => cf.ByAutomationId("1").And(cf.ByControlType(ControlType.Button))),
            TimeSpan.FromSeconds(3));
        AssertSettingsActionsDisabled(window);
        IntPtr pickerHandle = new(pickerWindow.Properties.NativeWindowHandle.ValueOrDefault);
        File.WriteAllText(
            Path.Combine(outputDirectory, "settings-export-picker.txt"),
            $"Picker HWND 0x{pickerHandle.ToInt64():X} in process " +
            $"{pickerWindow.Properties.ProcessId.ValueOrDefault} exposed its standard primary command while " +
            "Settings actions were disabled, then closed through its standard cancel command.");
    }
    finally
    {
        if (pickerWindow is not null)
        {
            AutomationElement? cancel = pickerWindow.FindFirstDescendant(
                cf => cf.ByAutomationId("2").And(cf.ByControlType(ControlType.Button)));
            if (cancel is not null)
            {
                InvokeOrClick(cancel);
            }
            else
            {
                pickerWindow.Focus();
                Keyboard.Press(VirtualKeyShort.ESCAPE);
            }
        }
        else
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
        }
        if (pickerWindow is not null)
        {
            WaitUntil(
                "diagnostics save picker closes",
                () => !IsVisible(pickerWindow),
                TimeSpan.FromSeconds(8));
        }
    }
    if (canUseForegroundFocus)
    {
        WaitUntil("diagnostics export restores focus", () => IsElementFocused(action), TimeSpan.FromSeconds(5));
    }
    WaitUntil(
        "diagnostics export action gate releases",
        () => action.IsEnabled,
        TimeSpan.FromSeconds(5));
}

static void RunStarsLibraryProbe(CaptureOptions options, bool includeCategoryPersistence = true)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(options.AppPath, "--page=stars", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "stars-library probe");
        ResizeWindow(window, 1366, 900);
        AutomationElement starsRoot = AssertNamedAutomationElement(window, "StarsSearch", ControlType.Edit);
        _ = AssertNamedAutomationElement(window, "StarsSort", ControlType.ComboBox);
        _ = AssertNamedAutomationElement(window, "StarsFilter", ControlType.Button);
        _ = AssertNamedAutomationElement(window, "StarsCategoryNavigation", ControlType.List);
        AutomationElement starsList = AssertNamedAutomationElement(window, "StarsList", ControlType.List);
        AutomationElement firstStarRow = WaitForElement(
            "first Stars row",
            () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
            TimeSpan.FromSeconds(12));
        AssertProbe(
            !string.IsNullOrWhiteSpace(firstStarRow.Name) &&
            !firstStarRow.Name.Contains("StarRepositoryViewItem", StringComparison.Ordinal),
            "Stars rows must expose repository details instead of a CLR model name.");
        Thread.Sleep(900);

        AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNavigation"))), "Wide Stars did not show the category pane.");
        AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsOpenCategories"))), "Wide Stars exposed the compact category drawer button.");

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
            AutomationElement? categoryButton = window.FindFirstDescendant(cf => cf.ByAutomationId("StarsOpenCategories"));
            bool compact = IsVisible(categoryButton);
            if (actualWidth <= 760)
            {
                AssertProbe(compact, $"Stars did not expose its category drawer at {viewportLabel}.");
                AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCloseCategories"))), $"Stars category drawer started open at {viewportLabel}.");
            }

            CaptureWindow(window, Path.Combine(options.OutputDirectory, $"stars-responsive-{viewportLabel}.png"));
            if (compact)
            {
                int toggleCount = actualWidth <= 640 ? 3 : 1;
                for (int toggleIndex = 0; toggleIndex < toggleCount; toggleIndex++)
                {
                    AutomationElement opener = WaitForElement(
                        "StarsOpenCategories",
                        () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsOpenCategories")),
                        TimeSpan.FromSeconds(5));
                    AssertProbe(IsVisible(opener), $"Stars category drawer opener disappeared at {viewportLabel}.");
                    InvokeOrClick(opener);
                    AutomationElement close = WaitForElement(
                        "StarsCloseCategories",
                        () =>
                        {
                            AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCloseCategories"));
                            return IsVisible(candidate) ? candidate : null;
                        },
                        TimeSpan.FromSeconds(5));
                    Thread.Sleep(250);
                    if (toggleIndex == 0)
                    {
                        CaptureWindow(window, Path.Combine(options.OutputDirectory, $"stars-responsive-{viewportLabel}-drawer.png"));
                    }

                    InvokeOrClick(close);
                    WaitUntil(
                        "Stars category drawer close",
                        () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCloseCategories"))),
                        TimeSpan.FromSeconds(5));
                }
            }
        }

        ResizeWindow(window, 1366, 900);
        Thread.Sleep(700);
        AutomationElement search = WaitForElement("StarsSearch", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSearch")), TimeSpan.FromSeconds(5));
        InvokeOrClick(search);
        Keyboard.Type("WinUI");
        Thread.Sleep(500);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-search-filtered.png"));
        search.AsTextBox().Text = string.Empty;
        WaitUntil(
            "Stars search cleared",
            () => string.IsNullOrEmpty(GetTextBoxText(search)),
            TimeSpan.FromSeconds(3));
        Thread.Sleep(400);

        AutomationElement sort = WaitForElement("StarsSort", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSort")), TimeSpan.FromSeconds(5));
        ComboBox sortCombo = sort.AsComboBox();
        sortCombo.Expand();
        WaitUntil("Stars sort options realized", () => sortCombo.Items.Length > 2, TimeSpan.FromSeconds(3));
        sortCombo.Select(2);
        Thread.Sleep(650);
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-sort-most-stars.png"));

        AutomationElement filter = WaitForElement("StarsFilter", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsFilter")), TimeSpan.FromSeconds(5));
        InvokeOrClick(filter);
        AutomationElement languageFilter = WaitForElement(
            "StarsFilterLanguage",
            () => FindElementInWindowOrDialog(window, automation, "StarsFilterLanguage"),
            TimeSpan.FromSeconds(5));
        foreach (string filterId in new[]
        {
            "StarsFilterOwner",
            "StarsFilterTopic",
            "StarsFilterVisibility",
            "StarsFilterKind",
            "StarsFilterActivity",
            "StarsFilterCategoryState"
        })
        {
            AssertProbe(IsVisible(FindElementInWindowOrDialog(window, automation, filterId)), $"Filter flyout omitted {filterId}.");
        }
        CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "stars-filter-flyout.png"));
        (string FilterId, string ChipId, string OptionName)[] filterCases =
        [
            ("StarsFilterLanguage", "language", "C#"),
            ("StarsFilterOwner", "owner", "JitHubApp"),
            ("StarsFilterTopic", "topic", "developer-tools"),
            ("StarsFilterVisibility", "visibility", "Public"),
            ("StarsFilterKind", "kind", "Sources"),
            ("StarsFilterActivity", "activity", "Active"),
            ("StarsFilterCategoryState", "category", "Categorized")
        ];
        for (int filterIndex = 0; filterIndex < filterCases.Length; filterIndex++)
        {
            (string filterId, string chipId, string optionName) = filterCases[filterIndex];
            AutomationElement filterControl;
            if (filterIndex == 0)
            {
                filterControl = languageFilter;
            }
            else
            {
                filterControl = WaitForElement(
                    filterId,
                    () => FindElementInWindowOrDialog(window, automation, filterId),
                    TimeSpan.FromSeconds(5));
            }

            filterControl.AsComboBox().Select(optionName);
            WaitForElement(
                $"{chipId} filter chip",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId($"StarsFilterChip_{chipId}")),
                TimeSpan.FromSeconds(5));
            if (filterIndex == 0)
            {
                CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "stars-filter-chip.png"));
            }

            AutomationElement? clearFilters = Retry.WhileNull(
                () =>
                {
                    AutomationElement? candidate = FindElementInWindowOrDialog(window, automation, "StarsClearFilters");
                    return IsVisible(candidate) ? candidate : null;
                },
                timeout: TimeSpan.FromSeconds(1),
                interval: TimeSpan.FromMilliseconds(100),
                ignoreException: true).Result;
            if (!IsVisible(clearFilters))
            {
                Keyboard.Press(VirtualKeyShort.ESCAPE);
                filter = WaitForElement(
                    "StarsFilter for clear",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsFilter")),
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(filter);
                clearFilters = WaitForElement(
                    "StarsClearFilters after reopening filter flyout",
                    () =>
                    {
                        AutomationElement? candidate = FindElementInWindowOrDialog(window, automation, "StarsClearFilters");
                        return IsVisible(candidate) ? candidate : null;
                    },
                    TimeSpan.FromSeconds(5));
            }

            InvokeOrClick(clearFilters!);
            Thread.Sleep(250);
        }
        Keyboard.Press(VirtualKeyShort.ESCAPE);

        AutomationElement selection = WaitForElement("StarsSelectionMode", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSelectionMode")), TimeSpan.FromSeconds(5));
        InvokeOrClick(selection);
        AutomationElement firstRow = WaitForElement("first Stars row", () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(5));
        AssertStarsRepositoryAutomationIdentity(firstRow);
        AutomationElement firstRowCheckBox = WaitForElement(
            "first Stars row checkbox",
            () => firstRow.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox)),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(firstRowCheckBox);
        _ = WaitForElement(
            "Stars selection contextual commands",
            () =>
            {
                AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory"));
                return IsVisible(candidate) ? candidate : null;
            },
            TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-selection-mode.png"));

        // Toggling selection mode off with an active selection used to clear an already-invalidated
        // WinRT vector and crash with E_UNEXPECTED. Exercise the toolbar toggle itself first.
        InvokeOrClick(selection);
        WaitUntil(
            "Stars selection toolbar toggle dismissal",
            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory"))),
            TimeSpan.FromSeconds(5));
        AssertProbe(
            IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSearch"))) &&
            IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsList"))),
            "Stars interactive workspace disappeared after toggling selection mode off.");

        // Exercise the explicit cancel path independently as it shares the selection-mode transition.
        InvokeOrClick(selection);
        firstRow = WaitForElement("first Stars row for cancel selection", () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(5));
        firstRowCheckBox = WaitForElement(
            "first Stars row checkbox for cancel selection",
            () => firstRow.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox)),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(firstRowCheckBox);
        InvokeOrClick(WaitForElement("StarsCancelSelection", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCancelSelection")), TimeSpan.FromSeconds(5)));
        WaitUntil(
            "Stars cancel selection dismissal",
            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory"))),
            TimeSpan.FromSeconds(5));

        firstRow = WaitForElement("first Stars row after selection", () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(5));
        var firstRowBounds = firstRow.BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(
            firstRowBounds.Left + firstRowBounds.Width / 2,
            firstRowBounds.Top + firstRowBounds.Height / 2));
        Thread.Sleep(350);
        AutomationElement hoverUnstar = WaitForElement(
            "Stars hover Unstar action",
            () => firstRow.FindFirstDescendant(cf => cf.ByName("Unstar repository")),
            TimeSpan.FromSeconds(5));
        AssertProbe(IsVisible(hoverUnstar), "Hovering a Stars row did not expose the Unstar action.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-row-hover.png"));

        var contextPoint = new System.Drawing.Point(
            firstRowBounds.Left + Math.Min(120, firstRowBounds.Width / 3),
            firstRowBounds.Top + 4);
        if (NativeMethods.HasInvisibleForeignForegroundWindow(window.Properties.NativeWindowHandle.ValueOrDefault))
        {
            AutomationElement hoverMenu = WaitForElement(
                "Stars hover repository actions",
                () => firstRow.FindFirstDescendant(cf => cf.ByName("Repository actions")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(hoverMenu);
        }
        else
        {
            Mouse.MoveTo(contextPoint);
            Mouse.RightClick();
        }
        WaitForElement("Stars row context menu", () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("StarsContextOpenrepository")), TimeSpan.FromSeconds(5));
        AssertProbe(IsVisible(automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("StarsContextAddtocategory"))), "Stars row context menu omitted category assignment.");
        Thread.Sleep(300);
        CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "stars-row-top-context-menu.png"));
        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Mouse.MoveTo(new System.Drawing.Point(
            firstRowBounds.Left + firstRowBounds.Width / 2,
            firstRowBounds.Top + firstRowBounds.Height / 2));
        Thread.Sleep(250);
        hoverUnstar = WaitForElement(
            "Stars hover Unstar action after context menu",
            () => firstRow.FindFirstDescendant(cf => cf.ByName("Unstar repository")),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(hoverUnstar);
        AutomationElement undo = WaitForElement(
            "StarsUndoUnstar",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsUndoUnstar")),
            TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-unstar-undo.png"));
        InvokeOrClick(undo);
        WaitForElement(
            "restored Stars row",
            () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
            TimeSpan.FromSeconds(5));

        AssertProbe(starsRoot.BoundingRectangle.Width > 0 && starsRoot.BoundingRectangle.Height > 0, "Stars workspace lost its stable root bounds.");
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }

    if (includeCategoryPersistence)
    {
        RunStarsCategoryPersistenceProbe(options);
    }
}

static void RunGistsWorkspaceProbe(CaptureOptions options, bool skipResponsiveMatrix = false)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(options.AppPath, "--page=gists", "--theme=dark");
    using var automation = new UIA3Automation();
    Exception? probeFailure = null;
    try
    {
        Window window = GetReadyWindow(app, automation, "gists-workspace probe");
        ResizeWindow(window, 1366, 900);
        AutomationElement gistList = AssertNamedAutomationElement(window, "GistsList", ControlType.List);
        _ = AssertNamedAutomationElement(window, "GistsSearch", ControlType.Edit);
        _ = AssertNamedAutomationElement(window, "GistsVisibilityFilter", ControlType.ComboBox);
        _ = AssertNamedAutomationElement(window, "GistsSort", ControlType.ComboBox);
        _ = AssertNamedAutomationElement(window, "GistsNew", ControlType.Button);
        AutomationElement firstRow = WaitForElement(
            "first Gist row",
            () => gistList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
            TimeSpan.FromSeconds(15));
        AssertProbe(!string.IsNullOrWhiteSpace(firstRow.Name), "Gist rows must expose a useful accessible name.");
        AssertProbe(
            !firstRow.Name.Contains("GistViewItem", StringComparison.Ordinal),
            "Gist rows must not expose a CLR model name.");
        AssertProbe(
            !firstRow.Name.Contains("Updated Updated", StringComparison.OrdinalIgnoreCase),
            "Gist row repeated the relative-time label in its accessible name.");
        InvokeOrClick(firstRow);
        WaitForElement(
            "Gist detail title",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsDetailTitle")),
            TimeSpan.FromSeconds(8));

        (int Width, int Height)[] sizes = skipResponsiveMatrix
            ? []
            :
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
            Thread.Sleep(350);
            AutomationElement? listButton = window.FindFirstDescendant(cf => cf.ByAutomationId("GistsLeadingPaneButton"));
            bool libraryDrawerOpened = false;
            if (actualWidth <= 640)
            {
                AssertProbe(IsVisible(listButton), $"Compact Gists omitted its library drawer button at {viewportLabel}.");
                AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("GistsDetailTitle"))), "Compact Gists did not preserve detail as the primary pane.");
                InvokeOrClick(listButton!);
                libraryDrawerOpened = true;
                WaitUntil(
                    "Gists compact library drawer",
                    () => IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("GistsSearch"))),
                    TimeSpan.FromSeconds(5));
            }

            CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, $"gists-responsive-{viewportLabel}.png"));
            AutomationElement? responsiveNewGist = window.FindFirstDescendant(cf => cf.ByAutomationId("GistsNew"));
            if (!IsVisible(responsiveNewGist))
            {
                AssertProbe(IsVisible(listButton), $"Gists library and its drawer command were both unavailable at {viewportLabel}.");
                InvokeOrClick(listButton!);
                libraryDrawerOpened = true;
                responsiveNewGist = WaitForElement(
                    $"GistsNew in library drawer at {viewportLabel}",
                    () =>
                    {
                        AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId("GistsNew"));
                        return IsVisible(candidate) ? candidate : null;
                    },
                    TimeSpan.FromSeconds(5));
            }

            AssertProbe(IsVisible(responsiveNewGist), $"New Gist was not reachable at {viewportLabel}.");
            responsiveNewGist = WaitForElement(
                $"live GistsNew at {viewportLabel}",
                () =>
                {
                    AutomationElement? candidate = FindCurrentVisibleByAutomationId(window, "GistsNew");
                    return candidate is { IsEnabled: true } ? candidate : null;
                },
                TimeSpan.FromSeconds(5));
            InvokeOrClick(responsiveNewGist);
            AutomationElement responsiveVisibility = WaitForElement(
                $"GistEditorVisibility at {viewportLabel}",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorVisibility"),
                TimeSpan.FromSeconds(5));
            AutomationElement responsiveContentLabel = WaitForElement(
                $"GistEditorContentLabel at {viewportLabel}",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorContentLabel"),
                TimeSpan.FromSeconds(5));
            AssertProbe(IsVisible(responsiveVisibility), $"Gist visibility was not visible at {viewportLabel}.");
            AssertProbe(responsiveVisibility.Properties.IsEnabled.ValueOrDefault, $"Gist visibility was not clickable at {viewportLabel}.");
            AssertProbe(IsInsideWindowBounds(responsiveVisibility, window), $"Gist visibility was clipped at {viewportLabel}.");
            AssertProbe(IsVisible(responsiveContentLabel), $"The persistent File content label was not visible at {viewportLabel}.");
            AssertProbe(IsInsideWindowBounds(responsiveContentLabel, window), $"The File content label was clipped at {viewportLabel}.");
            InvokeOrClick(responsiveVisibility);
            responsiveVisibility = WaitForElement(
                $"live GistEditorVisibility after toggle at {viewportLabel}",
                () => FindElementInWindowOrDialog(window, automation, "GistEditorVisibility") is { } candidate && IsVisible(candidate)
                    ? candidate
                    : null,
                TimeSpan.FromSeconds(5));
            InvokeOrClick(responsiveVisibility);
            CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, $"gists-editor-{viewportLabel}.png"), includePopups: true);
            AutomationElement cancelEditor = WaitForElement(
                $"Cancel Gist editor at {viewportLabel}",
                () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Cancel").And(cf.ByControlType(ControlType.Button))),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(cancelEditor);
            WaitUntil(
                $"Gist editor closes at {viewportLabel}",
                () => !IsVisible(FindElementInWindowOrDialog(window, automation, "GistEditorDialog")),
                TimeSpan.FromSeconds(8));
            if (libraryDrawerOpened)
            {
                Keyboard.Press(VirtualKeyShort.ESCAPE);
                Thread.Sleep(250);
            }
        }

        ResizeWindow(window, 1366, 900);
        Thread.Sleep(500);
        gistList = WaitForElement(
            "live GistsList after responsive reparenting",
            () => FindCurrentVisibleByAutomationId(window, "GistsList"),
            TimeSpan.FromSeconds(5));
        AutomationElement search = WaitForElement(
            "GistsSearch",
            () => FindCurrentVisibleByAutomationId(window, "GistsSearch"),
            TimeSpan.FromSeconds(5));
        search.AsTextBox().Text = "workflow note 121";
        WaitUntil(
            "filtered Gist row",
            () => gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 1,
            TimeSpan.FromSeconds(5));
        CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, "gists-search-filtered.png"));
        search.AsTextBox().Text = string.Empty;
        Thread.Sleep(400);

        AutomationElement visibility = WaitForElement(
            "GistsVisibilityFilter",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsVisibilityFilter")),
            TimeSpan.FromSeconds(5));
        visibility.AsComboBox().Select(2);
        Thread.Sleep(400);
        firstRow = WaitForElement(
            "secret Gist row",
            () => gistList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
            TimeSpan.FromSeconds(5));
        AssertProbe(firstRow.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase), "Secret filter returned a row without a Secret accessible state.");

        AutomationElement sort = WaitForElement(
            "GistsSort",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsSort")),
            TimeSpan.FromSeconds(5));
        sort.AsComboBox().Select(3);
        Thread.Sleep(350);
        CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, "gists-filter-sort.png"));
        visibility = WaitForElement(
            "current GistsVisibilityFilter",
            () => FindCurrentVisibleByAutomationId(window, "GistsVisibilityFilter"),
            TimeSpan.FromSeconds(5));
        visibility.AsComboBox().Select(0);

        AutomationElement newGist = WaitForElement(
            "GistsNew",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsNew")),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(newGist);
        const string firstFilename = "native.cs";
        const string firstFileContent = "public static bool Ready => true;";
        const string secondFilename = "second.txt";
        const string secondFileContent = "second file persisted across create and reopen";
        const string renamedSecondFilename = "renamed-second.txt";
        const string updatedSecondFileContent = "second file updated and preserved in full";
        AutomationElement description = WaitForElement(
            "GistEditorDescription",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorDescription"),
            TimeSpan.FromSeconds(5));
        description.AsTextBox().Text = "Automation native gist";
        AutomationElement filename = WaitForElement(
            "GistEditorFilename",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorFilename"),
            TimeSpan.FromSeconds(5));
        filename.AsTextBox().Text = firstFilename;
        AutomationElement content = WaitForElement(
            "GistEditorContent",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorContent"),
            TimeSpan.FromSeconds(5));
        content.AsTextBox().Text = firstFileContent;
        AutomationElement editorFiles = WaitForElement(
            "GistEditorFiles",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorFiles"),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(WaitForElement(
            "GistEditorAddFile",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorAddFile"),
            TimeSpan.FromSeconds(5)));
        WaitUntil(
            "second Gist editor file",
            () => editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 2,
            TimeSpan.FromSeconds(5));
        AutomationElement secondCreatedRow = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .First(item => !item.Name.Contains(firstFilename, StringComparison.OrdinalIgnoreCase));
        AssertProbe(
            secondCreatedRow.Patterns.SelectionItem.IsSupported && secondCreatedRow.Patterns.SelectionItem.Pattern.IsSelected.Value,
            "Adding a second Gist file did not select it for immediate editing.");
        filename.AsTextBox().Text = secondFilename;
        content.AsTextBox().Text = secondFileContent;

        secondCreatedRow = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .First(item =>
                item.Patterns.SelectionItem.IsSupported &&
                item.Patterns.SelectionItem.Pattern.IsSelected.Value);
        secondCreatedRow.FocusNative();
        WaitUntil(
            "second Gist editor row keyboard focus",
            () => IsElementFocused(secondCreatedRow),
            TimeSpan.FromSeconds(4));
        PressKeyForWindow(window, VirtualKeyShort.UP);
        AutomationElement firstCreatedRow = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .First(item => !string.Equals(GetAutomationId(item), GetAutomationId(secondCreatedRow), StringComparison.Ordinal));
        ApplySelectionFallbackForInvisibleForeignForeground(
            window,
            () => string.Equals(filename.AsTextBox().Text, firstFilename, StringComparison.Ordinal),
            firstCreatedRow,
            "first Gist editor file");
        WaitUntil(
            "keyboard selects first Gist file",
            () => string.Equals(filename.AsTextBox().Text, firstFilename, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        AssertProbe(
            string.Equals(content.AsTextBox().Text, firstFileContent, StringComparison.Ordinal),
            "Keyboard file traversal did not preserve the first file content.");
        PressKeyForWindow(window, VirtualKeyShort.DOWN);
        ApplySelectionFallbackForInvisibleForeignForeground(
            window,
            () => string.Equals(filename.AsTextBox().Text, secondFilename, StringComparison.Ordinal),
            secondCreatedRow,
            "second Gist editor file");
        WaitUntil(
            "keyboard selects second Gist file",
            () => string.Equals(filename.AsTextBox().Text, secondFilename, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        AssertProbe(
            string.Equals(content.AsTextBox().Text, secondFileContent, StringComparison.Ordinal),
            "Keyboard file traversal did not restore the second file content.");
        CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, "gists-new-editor.png"), includePopups: true);
        AutomationElement save = WaitForElement(
            "Save gist",
            () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Save").And(cf.ByControlType(ControlType.Button))),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(save);
        WaitUntil(
            "Gist editor closes after create",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, "GistEditorDescription")),
            TimeSpan.FromSeconds(7));

        search = WaitForElement("GistsSearch after create", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsSearch")), TimeSpan.FromSeconds(5));
        search.AsTextBox().Text = "Automation native gist";
        firstRow = WaitForElement(
            "created Gist row",
            () => gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(item => item.Name.Contains("Automation native gist", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(5));
        AssertProbe(firstRow.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase), "Default Gist creation did not produce a Secret Gist row.");
        InvokeOrClick(firstRow);
        const string gistClipboardSentinel = "__jithub_gist_copy_pending__";
        NativeMethods.SetClipboardText(gistClipboardSentinel);
        InvokeOrClick(WaitForElement("GistsCopyLink", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsCopyLink")), TimeSpan.FromSeconds(5)));
        string copiedGistLink = string.Empty;
        WaitUntil(
            "Gist link copied to clipboard",
            () =>
            {
                copiedGistLink = NativeMethods.GetClipboardText();
                return Uri.TryCreate(copiedGistLink, UriKind.Absolute, out Uri? copiedUri) &&
                    copiedUri.Scheme == Uri.UriSchemeHttps &&
                    copiedUri.Host.Equals("gist.github.com", StringComparison.OrdinalIgnoreCase);
            },
            TimeSpan.FromSeconds(5));
        AssertProbe(
            Uri.TryCreate(copiedGistLink, UriKind.Absolute, out Uri? copiedGistUri) &&
            copiedGistUri.Scheme == Uri.UriSchemeHttps &&
            copiedGistUri.Host.Equals("gist.github.com", StringComparison.OrdinalIgnoreCase),
            "Copy Gist link did not place a trusted gist.github.com URL on the clipboard.");

        InvokeOrClick(WaitForElement("GistsShare", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsShare")), TimeSpan.FromSeconds(5)));
        Thread.Sleep(600);
        AssertProbe(!app.HasExited, "Invoking Windows Share terminated the Gists workspace.");
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        TryActivateWindow(window);
        Thread.Sleep(250);
        CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, "gists-share-returned.png"));

        firstRow = WaitForElement(
            "live created Gist row after Windows Share",
            () => gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(item =>
                    IsVisible(item) &&
                    item.Name.Contains("Automation native gist", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(5));
        firstRow.FocusNative();
        WaitUntil(
            "created Gist row keyboard focus after Windows Share",
            () => IsElementFocused(firstRow),
            TimeSpan.FromSeconds(4));
        string contextRowAutomationId = GetAutomationId(firstRow);
        using (Keyboard.Pressing(VirtualKeyShort.LSHIFT))
        {
            Keyboard.Press(VirtualKeyShort.F10);
        }
        AutomationElement? contextEdit = null;
        bool keyboardContextOpened = WaitUntilAvailable(
            () =>
            {
                contextEdit = automation.GetDesktop().FindAllDescendants(cf => cf.ByAutomationId("GistsContextEdit"))
                    .FirstOrDefault(IsVisible);
                return contextEdit is not null;
            },
            TimeSpan.FromSeconds(2));
        if (!keyboardContextOpened && NativeMethods.HasInvisibleForeignForegroundWindow(
                window.Properties.NativeWindowHandle.ValueOrDefault))
        {
            firstRow.Click();
            Thread.Sleep(150);
            firstRow = WaitForElement(
                "live created Gist row after activation click",
                () => FindCurrentVisibleByAutomationId(window, contextRowAutomationId),
                TimeSpan.FromSeconds(5));
            firstRow.RightClick();
            keyboardContextOpened = WaitUntilAvailable(
                () =>
                {
                    contextEdit = automation.GetDesktop().FindAllDescendants(cf => cf.ByAutomationId("GistsContextEdit"))
                        .FirstOrDefault(IsVisible);
                    return contextEdit is not null;
                },
                TimeSpan.FromSeconds(2));
        }

        if (keyboardContextOpened)
        {
            contextEdit = WaitForElement(
                "Gist row keyboard context Edit",
                () => automation.GetDesktop().FindAllDescendants(cf => cf.ByAutomationId("GistsContextEdit"))
                    .FirstOrDefault(IsVisible),
                TimeSpan.FromSeconds(5));
            CaptureGistsWindowWithRetry(
                window,
                Path.Combine(options.OutputDirectory, "gists-keyboard-context-menu.png"),
                includePopups: true);
            InvokeOrClick(contextEdit);
        }
        else
        {
            AssertProbe(
                NativeMethods.HasInvisibleForeignForegroundWindow(window.Properties.NativeWindowHandle.ValueOrDefault),
                "The Gist row keyboard context menu did not open on a normal foreground desktop.");
            Console.WriteLine("gists-workspace: used the visible Edit action because an invisible foreign window blocked system context gestures.");
            InvokeOrClick(WaitForElement(
                "GistsEdit fallback",
                () => FindCurrentVisibleByAutomationId(window, "GistsEdit"),
                TimeSpan.FromSeconds(5)));
        }
        description = WaitForElement(
            "GistEditorDescription edit",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorDescription"),
            TimeSpan.FromSeconds(5));
        description.AsTextBox().Text = "Automation edited gist";
        editorFiles = WaitForElement(
            "GistEditorFiles edit",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorFiles"),
            TimeSpan.FromSeconds(5));
        WaitUntil(
            "two persisted Gist files before edit",
            () => editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 2,
            TimeSpan.FromSeconds(5));
        AutomationElement secondRow = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .First(item => item.Name.Contains(secondFilename, StringComparison.OrdinalIgnoreCase));
        AssertProbe(secondRow.Patterns.SelectionItem.IsSupported, "The second Gist editor row omitted native selection semantics.");
        secondRow.Patterns.SelectionItem.Pattern.Select();
        filename = WaitForElement("GistEditorFilename edit", () => FindElementInWindowOrDialog(window, automation, "GistEditorFilename"), TimeSpan.FromSeconds(5));
        content = WaitForElement("GistEditorContent edit", () => FindElementInWindowOrDialog(window, automation, "GistEditorContent"), TimeSpan.FromSeconds(5));
        WaitUntil(
            "second Gist file selected for edit",
            () => string.Equals(filename.AsTextBox().Text, secondFilename, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        AssertProbe(
            string.Equals(content.AsTextBox().Text, secondFileContent, StringComparison.Ordinal),
            "The second Gist file did not preserve its full content after create.");
        filename.AsTextBox().Text = renamedSecondFilename;
        content.AsTextBox().Text = updatedSecondFileContent;

        InvokeOrClick(WaitForElement(
            "GistEditorAddFile edit",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorAddFile"),
            TimeSpan.FromSeconds(5)));
        WaitUntil(
            "temporary third Gist file",
            () => editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 3,
            TimeSpan.FromSeconds(5));
        filename.AsTextBox().Text = "remove-me.txt";
        content.AsTextBox().Text = "this temporary file must not survive";
        InvokeOrClick(WaitForElement(
            "GistEditorRemoveFile",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorRemoveFile"),
            TimeSpan.FromSeconds(5)));
        WaitUntil(
            "temporary Gist file removed",
            () => editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 2,
            TimeSpan.FromSeconds(5));
        AssertProbe(
            editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .All(item => !item.Name.Contains("remove-me.txt", StringComparison.OrdinalIgnoreCase)),
            "Removed Gist editor file remained in the editor list.");

        secondRow = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .First(item =>
                item.Patterns.SelectionItem.IsSupported &&
                item.Patterns.SelectionItem.Pattern.IsSelected.Value);
        secondRow.FocusNative();
        WaitUntil(
            "edited Gist file row keyboard focus",
            () => IsElementFocused(secondRow),
            TimeSpan.FromSeconds(4));
        PressKeyForWindow(window, VirtualKeyShort.HOME);
        AutomationElement firstEditRow = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .First(item => item.Name.Contains(firstFilename, StringComparison.OrdinalIgnoreCase));
        ApplySelectionFallbackForInvisibleForeignForeground(
            window,
            () => string.Equals(filename.AsTextBox().Text, firstFilename, StringComparison.Ordinal),
            firstEditRow,
            "first edited Gist file");
        WaitUntil(
            "keyboard returns to first Gist file during edit",
            () => string.Equals(filename.AsTextBox().Text, firstFilename, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        AssertProbe(
            string.Equals(content.AsTextBox().Text, firstFileContent, StringComparison.Ordinal),
            "Editing another file changed the first Gist file content.");
        CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, "gists-edit-editor.png"), includePopups: true);
        save = WaitForElement(
            "Save edited gist",
            () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Save").And(cf.ByControlType(ControlType.Button))),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(save);
        WaitUntil(
            "Gist editor closes after edit",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, "GistEditorDescription")),
            TimeSpan.FromSeconds(7));

        search = WaitForElement("GistsSearch after edit", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsSearch")), TimeSpan.FromSeconds(5));
        search.AsTextBox().Text = "Automation edited gist";
        firstRow = WaitForElement(
            "edited Gist row",
            () => gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(item => item.Name.Contains("Automation edited gist", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(firstRow);

        InvokeOrClick(WaitForElement("GistsEdit reopen", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsEdit")), TimeSpan.FromSeconds(5)));
        editorFiles = WaitForElement(
            "GistEditorFiles reopen",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorFiles"),
            TimeSpan.FromSeconds(5));
        WaitUntil(
            "two Gist files after reopen",
            () => editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Length == 2,
            TimeSpan.FromSeconds(5));
        AutomationElement[] reopenedRows = editorFiles.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
        AssertProbe(reopenedRows.Any(item => item.Name.Contains(firstFilename, StringComparison.OrdinalIgnoreCase)), "The first Gist file was missing after reopen.");
        AssertProbe(reopenedRows.Any(item => item.Name.Contains(renamedSecondFilename, StringComparison.OrdinalIgnoreCase)), "The renamed second Gist file was missing after reopen.");
        AssertProbe(reopenedRows.All(item => !item.Name.Contains("remove-me.txt", StringComparison.OrdinalIgnoreCase)), "The removed Gist file reappeared after reopen.");
        filename = WaitForElement("GistEditorFilename reopen", () => FindElementInWindowOrDialog(window, automation, "GistEditorFilename"), TimeSpan.FromSeconds(5));
        content = WaitForElement("GistEditorContent reopen", () => FindElementInWindowOrDialog(window, automation, "GistEditorContent"), TimeSpan.FromSeconds(5));
        reopenedRows.First(item => item.Name.Contains(firstFilename, StringComparison.OrdinalIgnoreCase))
            .Patterns.SelectionItem.Pattern.Select();
        WaitUntil("first file after reopen", () => string.Equals(filename.AsTextBox().Text, firstFilename, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        AssertProbe(string.Equals(content.AsTextBox().Text, firstFileContent, StringComparison.Ordinal), "The first file content was not fully preserved after reopen.");
        reopenedRows.First(item => item.Name.Contains(renamedSecondFilename, StringComparison.OrdinalIgnoreCase))
            .Patterns.SelectionItem.Pattern.Select();
        WaitUntil("renamed second file after reopen", () => string.Equals(filename.AsTextBox().Text, renamedSecondFilename, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        AssertProbe(string.Equals(content.AsTextBox().Text, updatedSecondFileContent, StringComparison.Ordinal), "The renamed file content was not fully preserved after reopen.");
        save = WaitForElement(
            "Save reopened gist",
            () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Save").And(cf.ByControlType(ControlType.Button))),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(save);
        WaitUntil(
            "Gist editor closes after preservation check",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, "GistEditorDescription")),
            TimeSpan.FromSeconds(7));

        InvokeOrClick(WaitForElement("GistsDelete", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsDelete")), TimeSpan.FromSeconds(5)));
        CaptureGistsWindowWithRetry(window, Path.Combine(options.OutputDirectory, "gists-delete-confirmation.png"), includePopups: true);
        AutomationElement confirmDelete = WaitForElement(
            "Confirm gist deletion",
            () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Delete").And(cf.ByControlType(ControlType.Button))),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(confirmDelete);
        WaitUntil(
            "created Secret Gist deleted",
            () => !gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Any(item => item.Name.Contains("Automation edited gist", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(7));

        search = WaitForElement("GistsSearch after delete", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsSearch")), TimeSpan.FromSeconds(5));
        search.AsTextBox().Text = string.Empty;
        InvokeOrClick(newGist);
        description = WaitForElement(
            "GistEditorDescription public",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorDescription"),
            TimeSpan.FromSeconds(5));
        description.AsTextBox().Text = "Automation public gist";
        filename = WaitForElement(
            "GistEditorFilename public",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorFilename"),
            TimeSpan.FromSeconds(5));
        filename.AsTextBox().Text = "public.txt";
        content = WaitForElement(
            "GistEditorContent public",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorContent"),
            TimeSpan.FromSeconds(5));
        content.AsTextBox().Text = "public gist content";
        AutomationElement publicToggle = WaitForElement(
            "GistEditorVisibility",
            () => FindElementInWindowOrDialog(window, automation, "GistEditorVisibility"),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(publicToggle);
        save = WaitForElement(
            "Save public gist",
            () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Save").And(cf.ByControlType(ControlType.Button))),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(save);
        WaitUntil(
            "public Gist editor closes",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, "GistEditorDescription")),
            TimeSpan.FromSeconds(7));
        search = WaitForElement("GistsSearch public", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsSearch")), TimeSpan.FromSeconds(5));
        search.AsTextBox().Text = "Automation public gist";
        firstRow = WaitForElement(
            "created Public Gist row",
            () => gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(item => item.Name.Contains("Automation public gist", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(5));
        AssertProbe(firstRow.Name.Contains("Public", StringComparison.OrdinalIgnoreCase), "Public Gist creation did not expose a Public row state.");
        InvokeOrClick(firstRow);
        InvokeOrClick(WaitForElement("GistsDelete public", () => window.FindFirstDescendant(cf => cf.ByAutomationId("GistsDelete")), TimeSpan.FromSeconds(5)));
        confirmDelete = WaitForElement(
            "Confirm public gist deletion",
            () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByText("Delete").And(cf.ByControlType(ControlType.Button))),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(confirmDelete);
        WaitUntil(
            "created Public Gist deleted",
            () => !gistList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Any(item => item.Name.Contains("Automation public gist", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(7));
        Console.WriteLine($"gists-workspace probe: library, detail, filters, two-file CRUD/persistence, keyboard/context paths, actions, and {sizes.Length} responsive/editor captures passed; output={options.OutputDirectory}");
    }
    catch (Exception exception)
    {
        probeFailure = exception;
        throw;
    }
    finally
    {
        try
        {
            TryClose(app);
        }
        catch (Exception closeException) when (probeFailure is not null)
        {
            Console.Error.WriteLine($"Gists cleanup also failed after the primary probe failure: {closeException.Message}");
        }
        finally
        {
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}

static void CaptureGistsWindowWithRetry(Window window, string path, bool includePopups = false)
{
    Exception? lastError = null;
    for (int attempt = 0; attempt < 4; attempt++)
    {
        try
        {
            if (!includePopups)
            {
                NativeMethods.ActivateForKeyboard(new IntPtr(window.Properties.NativeWindowHandle.ValueOrDefault));
                Thread.Sleep(100);
            }

            if (includePopups)
            {
                CaptureWindowWithPopups(window, path);
            }
            else
            {
                CaptureWindow(window, path);
            }

            return;
        }
        catch (COMException ex) when (attempt < 3)
        {
            lastError = ex;
            Thread.Sleep(250 * (attempt + 1));
        }
        catch (InvalidOperationException ex) when (
            attempt < 3
            && ex.Message.StartsWith("Refusing popup capture because the foreground window belongs to process", StringComparison.Ordinal))
        {
            lastError = ex;
            Thread.Sleep(250 * (attempt + 1));
        }
    }

    throw new InvalidOperationException("The Gists window did not stabilize for screenshot capture.", lastError);
}

static void RunStarsSelectionModeProbe(CaptureOptions options)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(options.AppPath, "--page=stars", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "stars-selection-mode probe");
        ResizeWindow(window, 1366, 900);
        AutomationElement starsRoot = WaitForElement(
            "StarsSearch",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSearch")),
            TimeSpan.FromSeconds(10));
        AutomationElement starsList = WaitForElement(
            "StarsList",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsList")),
            TimeSpan.FromSeconds(10));
        AutomationElement selection = WaitForElement(
            "StarsSelectionMode",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSelectionMode")),
            TimeSpan.FromSeconds(10));

        WaitForElement(
            "first Stars row",
            () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
            TimeSpan.FromSeconds(12));

        for (int pass = 0; pass < 3; pass++)
        {
            InvokeOrClick(selection);
            AutomationElement row = WaitForElement(
                $"first Stars row for selection pass {pass + 1}",
                () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                TimeSpan.FromSeconds(5));
            AssertStarsRepositoryAutomationIdentity(row);
            AutomationElement checkBox = WaitForElement(
                $"Stars row checkbox for pass {pass + 1}",
                () => row.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox)),
                TimeSpan.FromSeconds(5));
            AssertProbe(IsVisible(checkBox), $"Stars did not expose a native row checkbox in selection pass {pass + 1}.");
            InvokeOrClick(checkBox);
            WaitForElement(
                $"Stars bulk toolbar for pass {pass + 1}",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory")),
                TimeSpan.FromSeconds(5));
            if (pass == 0)
            {
                CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-selection-checkbox.png"));
            }

            InvokeOrClick(selection);
            WaitUntil(
                $"Stars selection mode dismissal pass {pass + 1}",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory"))),
                TimeSpan.FromSeconds(5));
            AssertProbe(IsVisible(starsRoot), $"Stars closed during selection mode pass {pass + 1}.");
        }

        ResizeWindow(window, 640, 600);
        selection = WaitForElement(
            "StarsSelectionMode at compact width",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSelectionMode")),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(selection);
        AutomationElement compactRow = WaitForElement(
            "first Stars row for compact selection",
            () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
            TimeSpan.FromSeconds(5));
        AutomationElement compactCheckBox = WaitForElement(
            "Stars row checkbox at compact width",
            () => compactRow.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox)),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(compactCheckBox);
        WaitForElement(
            "Stars compact selection toolbar",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory")),
            TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-selection-checkbox-compact.png"));
        InvokeOrClick(WaitForElement(
            "StarsCancelSelection",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCancelSelection")),
            TimeSpan.FromSeconds(5)));
        WaitUntil(
            "Stars cancel selection dismissal",
            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory"))),
            TimeSpan.FromSeconds(5));
        AssertProbe(IsVisible(starsRoot), "Stars closed while canceling selection mode.");
        CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-selection-mode-regression.png"));
    }
    finally
    {
        TryClose(app);
        KillExistingApplicationInstances(options.AppPath);
    }
}

static void RunStarsCategoryPersistenceProbe(CaptureOptions options)
{
    string suffix = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
    string firstName = $"Automation Alpha {suffix}";
    string secondName = $"Automation Beta {suffix}";
    string renamedName = $"Automation Priority {suffix}";

    KillExistingApplicationInstances(options.AppPath);
    using (var app = LaunchApplication(options.AppPath, "--page=stars", "--theme=dark"))
    using (var automation = new UIA3Automation())
    {
        bool setupCompleted = false;
        try
        {
            Window window = GetReadyWindow(app, automation, "stars category persistence setup");
            ResizeWindow(window, 1180, 800);
            WaitForElement("StarsSearch", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSearch")), TimeSpan.FromSeconds(10));
            WaitForElement("Stars category setup row", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsList"))?.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(10));
            PurgeAutomationStarsCategories(window, automation);

            CreateStarsCategory(window, automation, firstName);
            CreateStarsCategory(window, automation, secondName);
            RenameStarsCategory(window, automation, secondName, renamedName);
            MoveStarsCategoryToTop(window, automation, renamedName);

            SelectStarsCategory(window, "All stars");
            AutomationElement list = WaitForElement("StarsList", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsList")), TimeSpan.FromSeconds(5));
            InvokeOrClick(WaitForElement("StarsSelectionMode", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSelectionMode")), TimeSpan.FromSeconds(5)));
            AutomationElement row = WaitForElement("Stars drag source", () => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(5));
            AssertStarsRepositoryAutomationIdentity(row);
            InvokeOrClick(WaitForElement(
                "Stars drag source checkbox",
                () => row.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox)),
                TimeSpan.FromSeconds(5)));
            WaitUntil(
                "Stars selected repository count",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSelectionCount")) is AutomationElement count
                    && string.Equals(GetElementName(count), "1 selected", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            WaitForElement("StarsCancelSelection", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCancelSelection")), TimeSpan.FromSeconds(5));
            row = WaitForElement("selected Stars drag source", () => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(5));
            AutomationElement dragSurface = WaitForElement(
                "Stars repository drag handle",
                () => row.FindAllDescendants()
                    .FirstOrDefault(element => GetAutomationId(element).StartsWith("StarsDragHandle_", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5));
            AutomationElement target = WaitForElement(renamedName, () => FindVisibleStarsCategory(window, renamedName), TimeSpan.FromSeconds(5));
            if (NativeMethods.HasInvisibleForeignForegroundWindow(window.Properties.NativeWindowHandle.ValueOrDefault))
            {
                InvokeOrClick(WaitForElement(
                    "StarsBulkCategory",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory")),
                    TimeSpan.FromSeconds(5)));
                AutomationElement picker = WaitForElement(
                    "StarsCategoryPickerList",
                    () => FindElementInWindowOrDialog(window, automation, "StarsCategoryPickerList"),
                    TimeSpan.FromSeconds(5));
                AutomationElement pickerItem = WaitForElement(
                    renamedName,
                    () => picker.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                        .FirstOrDefault(item => string.Equals(item.Name, renamedName, StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(5));
                AssertProbe(pickerItem.Patterns.SelectionItem.IsSupported, "The Stars category picker omitted native selection semantics.");
                pickerItem.Patterns.SelectionItem.Pattern.Select();
                InvokeOrClick(WaitForElement(
                    "Add selected repositories",
                    () => automation.GetDesktop().FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                        .FirstOrDefault(element => IsVisible(element) && string.Equals(element.Name, "Add", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(5)));
                Console.WriteLine("stars-categories: used the visible bulk category action because an invisible foreign window owned the system pointer queue.");
            }
            else
            {
                DragBetween(dragSurface, target, window);
                try
                {
                    WaitUntil(
                        "Stars category drop completion",
                        () => string.Equals(list.Properties.ItemStatus.ValueOrDefault, "drop-completed", StringComparison.Ordinal),
                        TimeSpan.FromSeconds(8));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Stars category drag stopped at '{list.Properties.ItemStatus.ValueOrDefault ?? "no-stage"}'.",
                        ex);
                }
            }
            WaitForElement(
                "Stars category assignment status",
                () => window.FindAllDescendants()
                    .FirstOrDefault(element => IsVisible(element)
                        && GetElementName(element).Contains("Added", StringComparison.Ordinal)
                        && GetElementName(element).Contains(renamedName, StringComparison.Ordinal)),
                TimeSpan.FromSeconds(8));
            AutomationElement? cancelSelection = window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCancelSelection"));
            if (IsVisible(cancelSelection))
            {
                InvokeOrClick(cancelSelection!);
            }
            SelectStarsCategory(window, renamedName);
            WaitForElement("assigned Stars row", () => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-category-drag-assignment.png"));
            setupCompleted = true;
        }
        finally
        {
            if (!setupCompleted)
            {
                TryPurgeAutomationStarsCategories(app, automation);
            }

            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    using (var relaunched = LaunchApplication(options.AppPath, "--page=stars", "--theme=dark"))
    using (var automation = new UIA3Automation())
    {
        try
        {
            Window window = GetReadyWindow(relaunched, automation, "stars category persistence verification");
            ResizeWindow(window, 1180, 800);
            AutomationElement persisted = WaitForElement(
                "persisted Stars category",
                () => FindVisibleText(window, renamedName),
                TimeSpan.FromSeconds(10));
            AssertProbe(IsVisible(persisted), "Stars category did not persist across app relaunch.");
            AutomationElement navigation = WaitForElement(
                "StarsCategoryNavigation after relaunch",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNavigation")),
                TimeSpan.FromSeconds(5));
            string[] customCategoryNames = navigation.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Where(IsVisible)
                .Select(GetElementName)
                .Where(name => name.StartsWith("Automation ", StringComparison.Ordinal))
                .ToArray();
            AssertProbe(
                customCategoryNames.Length >= 2 && string.Equals(customCategoryNames[0], renamedName, StringComparison.Ordinal),
                "Reordered Stars category position did not persist across app relaunch.");
            SelectStarsCategory(window, renamedName);
            AutomationElement persistedList = WaitForElement(
                "StarsList after category relaunch",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsList")),
                TimeSpan.FromSeconds(5));
            AutomationElement persistedRow = WaitForElement(
                "persisted category membership row",
                () => persistedList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                TimeSpan.FromSeconds(5));
            AssertStarsRepositoryAutomationIdentity(persistedRow);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-category-relaunch-persistence.png"));

            PurgeAutomationStarsCategories(window, automation);
        }
        finally
        {
            TryPurgeAutomationStarsCategories(relaunched, automation);
            TryClose(relaunched);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}

static void RunNotificationsWorkspaceProbe(CaptureOptions options)
{
    KillExistingApplicationInstances(options.AppPath);
    using var app = LaunchApplication(options.AppPath, "--page=notifications", "--theme=dark");
    using var automation = new UIA3Automation();
    try
    {
        Window window = GetReadyWindow(app, automation, "notifications-workspace probe");
        ResizeWindow(window, 1366, 900);

        AutomationElement root;
        try
        {
            root = WaitForElement(
                "NotificationsPageRoot",
                () => FindNotificationsWorkspaceRoot(automation, window),
                TimeSpan.FromSeconds(10));
        }
        catch
        {
            PrintVisibleAutomationIds(window, "notifications-workspace launch failure");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "notifications-launch-failure.png"));
            throw;
        }
        AssertProbe(IsVisible(root), "The Notifications workspace root was not visible.");
        AssertProbe(IsInsideWindowBounds(root, window), "The Notifications workspace root escaped the app window.");

        AutomationElement search = AssertNamedAutomationElement(window, "NotificationsSearch", ControlType.Edit);
        AssertProbe(
            string.Equals(GetElementName(search), "Search notifications", StringComparison.Ordinal),
            "NotificationsSearch did not expose its intended accessible name.");

        AutomationElement filter = WaitForElement(
            "NotificationsFilter",
            () => FindNotificationsFilterRoot(automation, window),
            TimeSpan.FromSeconds(8));
        AssertProbe(IsVisible(filter), "NotificationsFilter was not visible in the live UIA tree.");

        AutomationElement unreadFilter = AssertNotificationFilterItem(window, "NotificationsFilter_Unread", "Unread");
        AutomationElement allFilter = AssertNotificationFilterItem(window, "NotificationsFilter_All", "All");
        AutomationElement participatingFilter = AssertNotificationFilterItem(window, "NotificationsFilter_Participating", "Participating");

        AutomationElement list = AssertNamedAutomationElement(window, "NotificationsList", ControlType.List);
        AssertProbe(
            string.Equals(GetElementName(list), "GitHub notifications", StringComparison.Ordinal),
            "NotificationsList did not expose its intended accessible name.");

        AutomationElement markAllRead = AssertNamedAutomationElement(window, "NotificationsMarkAllRead", ControlType.Button);
        AssertProbe(
            string.Equals(GetElementName(markAllRead), "Mark all notifications as read", StringComparison.Ordinal),
            "NotificationsMarkAllRead did not expose its intended accessible name.");

        WaitForElement(
            "unread preview notification",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-issue")),
            TimeSpan.FromSeconds(10));
        AssertProbe(
            !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-release"))),
            "The Unread filter included the read preview notification.");

        search.AsTextBox().Text = "Polish";
        WaitForElement(
            "filtered pull request notification",
            () =>
            {
                AutomationElement? row = window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-pr"));
                return IsVisible(row) ? row : null;
            },
            TimeSpan.FromSeconds(5));
        WaitUntil(
            "non-matching notification to leave the filtered list",
            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-issue"))),
            TimeSpan.FromSeconds(5));
        search.AsTextBox().Text = string.Empty;
        WaitForElement(
            "unread preview notification after clearing search",
            () =>
            {
                AutomationElement? row = window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-issue"));
                return IsVisible(row) ? row : null;
            },
            TimeSpan.FromSeconds(5));

        SelectAutomationItem(allFilter, "Notifications All filter");
        AutomationElement releaseRow = WaitForElement(
            "All filter read preview notification",
            () =>
            {
                AutomationElement? row = window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-release"));
                return IsVisible(row) ? row : null;
            },
            TimeSpan.FromSeconds(8));
        AssertProbe(IsVisible(releaseRow), "The All filter did not include the read preview notification.");

        AutomationElement readAction = WaitForNotificationActionName(
            window,
            "NotificationRead_preview_issue",
            "Mark as read");
        InvokeOrClick(readAction);
        WaitUntil(
            "mark-read action to leave the read notification",
            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRead_preview_issue"))),
            TimeSpan.FromSeconds(5));

        AutomationElement subscriptionAction = WaitForNotificationActionNames(
            window,
            "NotificationSubscription_preview_issue",
            "Manage following",
            "Follow thread");
        InvokeOrClick(subscriptionAction);
        subscriptionAction = WaitForNotificationActionName(
            window,
            "NotificationSubscription_preview_issue",
            "Unsubscribe from thread");
        InvokeOrClick(subscriptionAction);
        _ = WaitForNotificationActionName(
            window,
            "NotificationSubscription_preview_issue",
            "Follow thread");

        AutomationElement muteAction = WaitForNotificationActionNames(
            window,
            "NotificationMute_preview_issue",
            "Manage muting",
            "Mute thread");
        InvokeOrClick(muteAction);
        muteAction = WaitForNotificationActionName(
            window,
            "NotificationMute_preview_issue",
            "Unmute thread");
        InvokeOrClick(muteAction);
        _ = WaitForNotificationActionName(
            window,
            "NotificationMute_preview_issue",
            "Mute thread");

        SelectAutomationItem(participatingFilter, "Notifications Participating filter");
        WaitForElement(
            "Participating filter preview notification",
            () =>
            {
                AutomationElement? row = window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-pr"));
                return IsVisible(row) ? row : null;
            },
            TimeSpan.FromSeconds(8));
        SelectAutomationItem(unreadFilter, "Notifications Unread filter");
        WaitUntil(
            "Unread filter to hide the read preview notification",
            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-release"))),
            TimeSpan.FromSeconds(8));

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
            string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
            root = WaitForElement(
                "NotificationsPageRoot after resize",
                () => FindNotificationsWorkspaceRoot(automation, window),
                TimeSpan.FromSeconds(5));
            search = WaitForElement(
                "NotificationsSearch after resize",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsSearch")),
                TimeSpan.FromSeconds(5));
            filter = WaitForElement(
                "NotificationsFilter after resize",
                () => FindNotificationsFilterRoot(automation, window),
                TimeSpan.FromSeconds(5));
            list = WaitForElement(
                "NotificationsList after resize",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsList")),
                TimeSpan.FromSeconds(5));
            markAllRead = WaitForElement(
                "NotificationsMarkAllRead after resize",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsMarkAllRead")),
                TimeSpan.FromSeconds(5));

            foreach ((AutomationElement element, string name) in new[]
            {
                (root, "workspace root"),
                (search, "search"),
                (filter, "filter"),
                (list, "notification list"),
                (markAllRead, "mark-all-read action")
            })
            {
                AssertProbe(IsVisible(element), $"Notifications {name} was offscreen at {viewportLabel}.");
                AssertProbe(IsInsideWindowBounds(element, window), $"Notifications {name} was clipped at {viewportLabel}.");
                AssertProbe(IsInsideElementBounds(element, root, 1.5), $"Notifications {name} escaped the page at {viewportLabel}.");
            }

            CaptureWindow(
                window,
                Path.Combine(options.OutputDirectory, $"notifications-responsive-{viewportLabel}.png"));
        }

        ResizeWindow(window, 1366, 900);
        AutomationElement issueRow = WaitForElement(
            "preview issue row for routing",
            () =>
            {
                AutomationElement? row = window.FindFirstDescendant(cf => cf.ByAutomationId("NotificationRow_preview-issue"));
                return IsVisible(row) ? row : null;
            },
            TimeSpan.FromSeconds(8));
        InvokeOrClick(issueRow);
        try
        {
            WaitForElement(
                "repository Issues destination",
                () =>
                {
                    AutomationElement? destination = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesDetailTitle"));
                    return IsVisible(destination) ? destination : null;
                },
                TimeSpan.FromSeconds(20));
        }
        catch
        {
            PrintVisibleAutomationIds(window, "notifications-workspace routing failure");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "notifications-routing-failure.png"));
            throw;
        }

        Console.WriteLine(
            $"notifications-workspace probe: UIA, filters, search, row actions, routing, and {sizes.Length} responsive captures passed; output={options.OutputDirectory}");
    }
    finally
    {
        TryClose(app);
    }
}

static AutomationElement AssertNotificationFilterItem(Window window, string automationId, string expectedName)
{
    AutomationElement item = WaitForElement(
        automationId,
        () => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
        TimeSpan.FromSeconds(8));
    AssertProbe(IsVisible(item), $"{automationId} was not visible in the live UIA tree.");
    AssertProbe(
        string.Equals(GetElementName(item), expectedName, StringComparison.Ordinal),
        $"{automationId} did not expose the accessible name '{expectedName}'.");
    return item;
}

static AutomationElement WaitForNotificationActionName(
    Window window,
    string automationId,
    string expectedName)
{
    return WaitForElement(
        $"{automationId} to become {expectedName}",
        () =>
        {
            AutomationElement? action = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            return IsVisible(action)
                && action!.Properties.IsEnabled.ValueOrDefault
                && string.Equals(GetElementName(action), expectedName, StringComparison.Ordinal)
                ? action
                : null;
        },
        TimeSpan.FromSeconds(8));
}

static AutomationElement WaitForNotificationActionNames(
    Window window,
    string automationId,
    params string[] expectedNames)
{
    return WaitForElement(
        $"{automationId} to become one of: {string.Join(", ", expectedNames)}",
        () =>
        {
            AutomationElement? action = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            return IsVisible(action)
                && action!.Properties.IsEnabled.ValueOrDefault
                && expectedNames.Contains(GetElementName(action), StringComparer.Ordinal)
                ? action
                : null;
        },
        TimeSpan.FromSeconds(8));
}

static AutomationElement? FindRawDescendantByAutomationId(
    UIA3Automation automation,
    AutomationElement root,
    string automationId)
{
    ITreeWalker walker = automation.TreeWalkerFactory.GetRawViewWalker();
    var pending = new Queue<AutomationElement>();
    AutomationElement? firstChild = walker.GetFirstChild(root);
    if (firstChild is not null)
    {
        pending.Enqueue(firstChild);
    }

    int visited = 0;
    while (pending.Count > 0 && visited++ < 5000)
    {
        AutomationElement current = pending.Dequeue();
        if (string.Equals(GetAutomationId(current), automationId, StringComparison.Ordinal))
        {
            return current;
        }

        AutomationElement? child = walker.GetFirstChild(current);
        if (child is not null)
        {
            pending.Enqueue(child);
        }

        AutomationElement? sibling = walker.GetNextSibling(current);
        if (sibling is not null)
        {
            pending.Enqueue(sibling);
        }
    }

    return null;
}

static AutomationElement? FindNotificationsWorkspaceRoot(UIA3Automation automation, Window window)
{
    AutomationElement? exactRoot = FindRawDescendantByAutomationId(automation, window, "NotificationsPageRoot");
    if (exactRoot is not null)
    {
        return exactRoot;
    }

    AutomationElement? candidate = window.FindFirstDescendant(
        cf => cf.ByAutomationId("NotificationsMarkAllRead"));
    if (candidate is null)
    {
        return null;
    }

    ITreeWalker walker = automation.TreeWalkerFactory.GetControlViewWalker();
    for (int depth = 0; candidate is not null && depth < 16; depth++)
    {
        bool ownsSearch = candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsSearch")) is not null;
        bool ownsFilter = candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsFilter")) is not null
            || candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsFilter_Unread")) is not null;
        bool ownsList = candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsList")) is not null;
        if (ownsSearch && ownsFilter && ownsList)
        {
            Console.WriteLine(
                "NotificationsPageRoot has no WinUI runtime provider; verified the smallest UIA workspace ancestor containing its unique page contract.");
            return candidate;
        }

        candidate = walker.GetParent(candidate);
    }

    return null;
}

static AutomationElement? FindNotificationsFilterRoot(UIA3Automation automation, Window window)
{
    AutomationElement? exactFilter = FindRawDescendantByAutomationId(automation, window, "NotificationsFilter");
    if (exactFilter is not null)
    {
        return exactFilter;
    }

    AutomationElement? candidate = window.FindFirstDescendant(
        cf => cf.ByAutomationId("NotificationsFilter_Unread"));
    if (candidate is null)
    {
        return null;
    }

    ITreeWalker walker = automation.TreeWalkerFactory.GetControlViewWalker();
    for (int depth = 0; candidate is not null && depth < 8; depth++)
    {
        bool ownsUnread = string.Equals(GetAutomationId(candidate), "NotificationsFilter_Unread", StringComparison.Ordinal)
            || candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsFilter_Unread")) is not null;
        bool ownsAll = candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsFilter_All")) is not null;
        bool ownsParticipating = candidate.FindFirstDescendant(cf => cf.ByAutomationId("NotificationsFilter_Participating")) is not null;
        if (ownsUnread && ownsAll && ownsParticipating)
        {
            Console.WriteLine(
                "NotificationsFilter has no WinUI runtime provider; verified the smallest UIA group containing all three filter items.");
            return candidate;
        }

        candidate = walker.GetParent(candidate);
    }

    return null;
}

static void TryPurgeAutomationStarsCategories(Application application, UIA3Automation automation)
{
    try
    {
        Window? window = application.GetMainWindow(automation);
        if (window is not null)
        {
            PurgeAutomationStarsCategories(window, automation);
        }
    }
    catch
    {
        // Best effort after a failed probe. The next run purges test-owned rows before setup.
    }
}

static void PurgeAutomationStarsCategories(Window window, UIA3Automation automation)
{
    const string automationPrefix = "Automation ";
    for (int attempt = 0; attempt < 100; attempt++)
    {
        AutomationElement navigation = WaitForElement(
            "StarsCategoryNavigation",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNavigation")),
            TimeSpan.FromSeconds(5));
        string? categoryName = navigation.FindAllDescendants()
            .Where(IsVisible)
            .Select(element => element.Name)
            .FirstOrDefault(name => name.StartsWith(automationPrefix, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return;
        }

        DeleteStarsCategory(window, automation, categoryName);
        WaitUntil(
            $"test category deletion: {categoryName}",
            () => !IsVisible(FindVisibleStarsCategory(window, categoryName)),
            TimeSpan.FromSeconds(10));
    }

    throw new InvalidOperationException("Unable to purge all automation-owned Stars categories.");
}

static void CreateStarsCategory(Window window, UIA3Automation automation, string name)
{
    InvokeOrClick(WaitForElement("StarsNewCategory", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsNewCategory")), TimeSpan.FromSeconds(5)));
    AutomationElement dialog = WaitForElement(
        "StarsCreateCategoryDialog",
        () => FindElementInWindowOrDialog(window, automation, "StarsCreateCategoryDialog"),
        TimeSpan.FromSeconds(5));
    AutomationElement nameBox = WaitForElement(
        "StarsCategoryNameBox",
        () => dialog.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNameBox")),
        TimeSpan.FromSeconds(5));
    InvokeOrClick(nameBox);
    nameBox.AsTextBox().Text = name;
    AssertProbe(string.Equals(nameBox.AsTextBox().Text, name, StringComparison.Ordinal), "Stars category name was not entered completely.");
    InvokeOrClick(WaitForElement(
        "Create category",
        () => dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(element => IsVisible(element) && string.Equals(element.Name, "Create", StringComparison.Ordinal)),
        TimeSpan.FromSeconds(5)));
    WaitUntil(
        "category dialog dismissal",
        () => !IsVisible(FindElementInWindowOrDialog(window, automation, "StarsCreateCategoryDialog")),
        TimeSpan.FromSeconds(5));
    try
    {
        WaitForElement(
            $"created category {name}",
            () => FindVisibleStarsCategory(window, name),
            TimeSpan.FromSeconds(15));
    }
    catch
    {
        AutomationElement? navigation = window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNavigation"));
        string peers = navigation is null
            ? "<navigation unavailable>"
            : string.Join(" | ", navigation.FindAllDescendants()
                .Where(IsVisible)
                .Select(element => $"{element.ControlType}:{GetElementName(element)} [{GetAutomationId(element)}]"));
        string status = string.Join(" | ", window.FindAllDescendants()
            .Where(element => IsVisible(element) && GetElementName(element).Contains("category", StringComparison.OrdinalIgnoreCase))
            .Select(element => $"{element.ControlType}:{GetElementName(element)}"));
        Console.Error.WriteLine($"Stars category navigation peers: {peers}");
        Console.Error.WriteLine($"Stars category status peers: {status}");
        throw;
    }
    SelectStarsCategory(window, name);
}

static void RenameStarsCategory(Window window, UIA3Automation automation, string oldName, string newName)
{
    SelectStarsCategory(window, oldName);
    InvokeOrClick(OpenStarsCategoryMenuItem(window, automation, "StarsCategoryActionRename"));
    AutomationElement dialog = WaitForElement(
        "StarsEditCategoryDialog",
        () => FindElementInWindowOrDialog(window, automation, "StarsEditCategoryDialog"),
        TimeSpan.FromSeconds(5));
    AutomationElement nameBox = WaitForElement(
        "StarsCategoryNameBox",
        () => dialog.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNameBox")),
        TimeSpan.FromSeconds(5));
    InvokeOrClick(nameBox);
    nameBox.AsTextBox().Text = newName;
    AssertProbe(string.Equals(nameBox.AsTextBox().Text, newName, StringComparison.Ordinal), "Stars renamed category value was not entered completely.");
    InvokeOrClick(WaitForElement(
        "Save category",
        () => dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(element => IsVisible(element) && string.Equals(element.Name, "Save", StringComparison.Ordinal)),
        TimeSpan.FromSeconds(5)));
    WaitUntil(
        "category rename dialog dismissal",
        () => !IsVisible(FindElementInWindowOrDialog(window, automation, "StarsEditCategoryDialog")),
        TimeSpan.FromSeconds(5));
    WaitForElement(
        $"renamed category {newName}",
        () => FindVisibleText(window, newName),
        TimeSpan.FromSeconds(5));
    SelectStarsCategory(window, newName);
}

static void MoveStarsCategoryToTop(Window window, UIA3Automation automation, string name)
{
    SelectStarsCategory(window, name);
    for (int attempt = 0; attempt < 100; attempt++)
    {
        AutomationElement moveUp = OpenStarsCategoryMenuItem(window, automation, "StarsCategoryActionMoveUp");
        if (!moveUp.Properties.IsEnabled.ValueOrDefault)
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            return;
        }

        InvokeOrClick(moveUp);
        Thread.Sleep(140);
    }

    throw new InvalidOperationException($"Category '{name}' did not reach the first custom-category position.");
}

static void DeleteStarsCategory(Window window, UIA3Automation automation, string name)
{
    SelectStarsCategory(window, name);
    InvokeOrClick(OpenStarsCategoryMenuItem(window, automation, "StarsCategoryActionDelete"));
    AutomationElement dialog = WaitForElement(
        "StarsDeleteCategoryDialog",
        () => FindElementInWindowOrDialog(window, automation, "StarsDeleteCategoryDialog"),
        TimeSpan.FromSeconds(5));
    AutomationElement delete = WaitForElement(
        "Delete category primary action",
        () => dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(element =>
                IsVisible(element) &&
                string.Equals(GetAutomationId(element), "PrimaryButton", StringComparison.Ordinal)),
        TimeSpan.FromSeconds(5));
    AssertProbe(delete.Properties.IsEnabled.ValueOrDefault, "Delete category primary action was disabled.");
    InvokeOrClick(delete);
    WaitUntil(
        "delete category dialog dismissal",
        () => !IsVisible(FindElementInWindowOrDialog(window, automation, "StarsDeleteCategoryDialog")),
        TimeSpan.FromSeconds(10));
}

static AutomationElement OpenStarsCategoryMenuItem(
    Window window,
    UIA3Automation automation,
    string automationId)
{
    AutomationElement menuButton = WaitForElement(
        "StarsCategoryMenu",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryMenu")),
        TimeSpan.FromSeconds(5));
    for (int attempt = 0; attempt < 3; attempt++)
    {
        InvokeOrClick(menuButton);
        Thread.Sleep(250);
        AutomationElement? item = automation.GetDesktop()
            .FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (IsVisible(item))
        {
            return item!;
        }

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(150);
    }

    throw new InvalidOperationException($"Timed out waiting for {automationId} after reopening the category menu.");
}

static void SelectStarsCategory(Window window, string name)
{
    AutomationElement? category = FindVisibleStarsCategory(window, name);
    if (!IsVisible(category))
    {
        AutomationElement navigationAnchor = WaitForElement(
            "StarsNewCategory",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsNewCategory")),
            TimeSpan.FromSeconds(5));
        System.Drawing.Point panePoint = CenterPoint(navigationAnchor, window);
        panePoint.Y = window.BoundingRectangle.Center().Y;
        Mouse.MoveTo(panePoint);
        Mouse.Scroll(30);
        Thread.Sleep(250);
        for (int attempt = 0; attempt < 25 && !IsVisible(category); attempt++)
        {
            category = FindVisibleStarsCategory(window, name);
            if (IsVisible(category))
            {
                break;
            }

            Mouse.Scroll(-4);
            Thread.Sleep(120);
        }
    }

    category = WaitForElement(name, () => IsVisible(category) ? category : FindVisibleStarsCategory(window, name), TimeSpan.FromSeconds(12));
    if (category.Patterns.SelectionItem.IsSupported)
    {
        category.Patterns.SelectionItem.Pattern.Select();
    }
    else
    {
        InvokeOrClick(category);
    }
    Thread.Sleep(450);
}

static AutomationElement? FindVisibleStarsCategory(Window window, string name)
{
    AutomationElement? navigation = window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNavigation"));
    if (navigation is null)
    {
        return null;
    }

    return navigation.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
               .FirstOrDefault(element => IsVisible(element) && GetElementName(element).Contains(name, StringComparison.Ordinal))
           ?? navigation.FindAllDescendants(cf => cf.ByText(name)).FirstOrDefault(IsVisible);
}

static AutomationElement? FindVisibleText(AutomationElement root, string text) =>
    root.FindAllDescendants(cf => cf.ByText(text)).FirstOrDefault(IsVisible);

static AutomationElement? FindCurrentVisibleByAutomationId(AutomationElement root, string automationId) =>
    root.FindAllDescendants(cf => cf.ByAutomationId(automationId)).FirstOrDefault(IsVisible);

static void ExerciseMyWorkItemMarkdownCopy(
    Window window,
    UIA3Automation automation,
    string hostAutomationId,
    string selectionPhrase,
    string context)
{
    AutomationElement host = WaitForElement(
        $"{context} Markdown host",
        () => FindCurrentVisibleByAutomationId(window, hostAutomationId),
        TimeSpan.FromSeconds(10));
    var textPattern = host.Patterns.Text.PatternOrDefault
        ?? throw new InvalidOperationException($"{context}: Markdown host did not expose TextPattern.");
    string documentText = textPattern.DocumentRange.GetText(-1);
    string[] preferredWords = selectionPhrase
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(word => word.Trim('.', ',', ':', ';', '!', '?', '`', '\'', '"'))
        .Where(word => word.Length >= 5)
        .ToArray();
    string[] documentWords = documentText
        .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(word => word.Trim('.', ',', ':', ';', '!', '?', '`', '\'', '"', '-', '*', '#'))
        .Where(word => word.Length >= 5)
        .Take(40)
        .ToArray();
    string effectivePhrase = preferredWords
        .Concat(documentWords)
        .FirstOrDefault(candidate =>
            textPattern.DocumentRange.FindText(candidate, backward: false, ignoreCase: false) is not null)
        ?? throw new InvalidOperationException($"{context}: Markdown host exposed no selectable text range.");
    var range = textPattern.DocumentRange.FindText(
        effectivePhrase,
        backward: false,
        ignoreCase: false)
        ?? throw new InvalidOperationException($"{context}: selectable text range disappeared before interaction.");
    Rectangle selectionRect = FindVisibleMarkdownRangeRect(host, range, $"{context} selection phrase");
    Point start = new(selectionRect.Left + 2, selectionRect.Top + selectionRect.Height / 2);
    Point end = new(selectionRect.Right - 2, selectionRect.Top + selectionRect.Height / 2);
    Point selectionPoint = new((start.X + end.X) / 2, start.Y);

    TryActivateWindow(window);
    host.Focus();
    Thread.Sleep(100);
    SendMouseInput(start, MouseEventFlags.MOUSEEVENTF_MOVE);
    SendMouseInput(start, MouseEventFlags.MOUSEEVENTF_LEFTDOWN);
    for (int step = 1; step <= 12; step++)
    {
        double ratio = step / 12d;
        SendMouseInput(new Point(
            (int)Math.Round(start.X + ((end.X - start.X) * ratio)),
            start.Y), MouseEventFlags.MOUSEEVENTF_MOVE | MouseEventFlags.MOUSEEVENTF_MOVE_NOCOALESCE);
        Thread.Sleep(25);
    }
    SendMouseInput(end, MouseEventFlags.MOUSEEVENTF_LEFTUP);
    Thread.Sleep(200);
    string selectedText = string.Concat(textPattern.GetSelection().Select(item => item.GetText(-1)));
    AssertProbe(selectedText.Contains(effectivePhrase, StringComparison.Ordinal),
        $"{context}: pointer selection was not preserved by the Markdown host.");

    NativeMethods.SetClipboardText("__jithub_my_work_item_ctrl_c_pending__");
    Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
    WaitUntil(
        $"{context} Ctrl+C",
        () => NativeMethods.GetClipboardText().Contains(effectivePhrase, StringComparison.Ordinal),
        TimeSpan.FromSeconds(4));

    NativeMethods.SetClipboardText("__jithub_my_work_item_context_copy_pending__");
    Mouse.MoveTo(selectionPoint);
    Thread.Sleep(100);
    Mouse.RightClick();
    Thread.Sleep(400);
    AutomationElement copy = WaitForElement(
        $"{context} context Copy",
        () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                .FirstOrDefault(item =>
                    string.Equals(GetElementName(item), "Copy", StringComparison.OrdinalIgnoreCase) &&
                    !item.Properties.IsOffscreen.ValueOrDefault)
            ?? automation.GetDesktop()
                .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                .FirstOrDefault(item =>
                    string.Equals(GetElementName(item), "Copy", StringComparison.OrdinalIgnoreCase) &&
                    !item.Properties.IsOffscreen.ValueOrDefault),
        TimeSpan.FromSeconds(5));
    InvokeOrClick(copy);
    WaitUntil(
        $"{context} context Copy clipboard",
        () => NativeMethods.GetClipboardText().Contains(effectivePhrase, StringComparison.Ordinal),
        TimeSpan.FromSeconds(4));
}

static void AssertStarsRepositoryAutomationIdentity(AutomationElement row)
{
    AssertProbe(
        row.ControlType == ControlType.ListItem,
        "Stars repository automation target is not the native ListViewItem container.");
    AssertProbe(
        GetAutomationId(row).StartsWith("StarsRepository_", StringComparison.Ordinal),
        "Stars repository row did not expose its stable automation ID on the ListViewItem container.");
    AssertProbe(
        !string.IsNullOrWhiteSpace(GetElementName(row)) && GetElementName(row).Contains('/', StringComparison.Ordinal),
        "Stars repository row did not expose a meaningful owner/name automation label.");
}

static void DragBetween(AutomationElement source, AutomationElement target, Window window)
{
    System.Drawing.Point start = CenterPoint(source, window);
    System.Drawing.Point end = CenterPoint(target, window);
    SendMouseInput(start, MouseEventFlags.MOUSEEVENTF_MOVE);
    SendMouseInput(start, MouseEventFlags.MOUSEEVENTF_LEFTDOWN);
    Thread.Sleep(350);
    for (int step = 1; step <= 24; step++)
    {
        double ratio = step / 24d;
        SendMouseInput(new System.Drawing.Point(
            (int)Math.Round(start.X + ((end.X - start.X) * ratio)),
            (int)Math.Round(start.Y + ((end.Y - start.Y) * ratio))),
            MouseEventFlags.MOUSEEVENTF_MOVE | MouseEventFlags.MOUSEEVENTF_MOVE_NOCOALESCE);
        Thread.Sleep(step == 3 ? 800 : 65);
    }
    Thread.Sleep(250);
    SendMouseInput(end, MouseEventFlags.MOUSEEVENTF_LEFTUP);
    Thread.Sleep(450);
}

static void SendMouseXButton(Window window, bool forward)
{
    System.Drawing.Point point = CenterPoint(window, window);
    Mouse.Click(point, forward ? MouseButton.XButton2 : MouseButton.XButton1);
}

static void SendMarkdownPointerDrag(System.Drawing.Point start, System.Drawing.Point end)
{
    SendMouseInput(start, MouseEventFlags.MOUSEEVENTF_MOVE);
    Thread.Sleep(100);
    SendMouseInput(start, MouseEventFlags.MOUSEEVENTF_LEFTDOWN);
    try
    {
        for (int step = 1; step <= 10; step++)
        {
            double ratio = step / 10d;
            SendMouseInput(new System.Drawing.Point(
                (int)Math.Round(start.X + ((end.X - start.X) * ratio)),
                (int)Math.Round(start.Y + ((end.Y - start.Y) * ratio))),
                MouseEventFlags.MOUSEEVENTF_MOVE | MouseEventFlags.MOUSEEVENTF_MOVE_NOCOALESCE);
            Thread.Sleep(45);
        }
    }
    finally
    {
        SendMouseInput(end, MouseEventFlags.MOUSEEVENTF_LEFTUP);
    }
}

static void SendMouseInput(System.Drawing.Point point, MouseEventFlags flags)
{
    int virtualLeft = User32.GetSystemMetrics(SystemMetric.SM_XVIRTUALSCREEN);
    int virtualTop = User32.GetSystemMetrics(SystemMetric.SM_YVIRTUALSCREEN);
    int virtualWidth = Math.Max(2, User32.GetSystemMetrics(SystemMetric.SM_CXVIRTUALSCREEN));
    int virtualHeight = Math.Max(2, User32.GetSystemMetrics(SystemMetric.SM_CYVIRTUALSCREEN));
    int normalizedX = (int)Math.Round((point.X - virtualLeft) * 65535d / (virtualWidth - 1));
    int normalizedY = (int)Math.Round((point.Y - virtualTop) * 65535d / (virtualHeight - 1));
    var input = new INPUT
    {
        type = InputType.INPUT_MOUSE,
        u = new INPUTUNION
        {
            mi = new MOUSEINPUT
            {
                dx = normalizedX,
                dy = normalizedY,
                dwFlags = flags
                    | MouseEventFlags.MOUSEEVENTF_ABSOLUTE
                    | MouseEventFlags.MOUSEEVENTF_VIRTUALDESK
            }
        }
    };
    uint sent = User32.SendInput(1, [input], Marshal.SizeOf<INPUT>());
    if (sent != 1)
    {
        throw new InvalidOperationException($"SendInput failed for mouse flags {flags}.");
    }
}

static void AssertProfileModeSwitchKeepsBoardStable(Window window, string context)
{
    AutomationElement viewport = WaitForElement(
        "ProfileOverviewScrollViewer",
        () => FindCurrentVisibleByAutomationId(window, "ProfileOverviewScrollViewer"),
        TimeSpan.FromSeconds(8));
    var initialBounds = viewport.BoundingRectangle;
    (string ModeId, string ViewportId)[] modes =
    [
        ("ProfileModeRepositoriesItem", "ProfileRepositoriesList"),
        ("ProfileModeActivityItem", "ProfileActivityList"),
        ("ProfileModeReadmeItem", "ProfileReadmeScrollViewer"),
        ("ProfileModeOverviewItem", "ProfileOverviewScrollViewer")
    ];

    foreach ((string modeId, string viewportId) in modes)
    {
        AutomationElement mode = WaitForElement(
            modeId,
            () => FindCurrentVisibleByAutomationId(window, modeId),
            TimeSpan.FromSeconds(8));
        TryActivateWindow(window);
        RevealForInteraction(mode, modeId);
        mode.FocusNative();
        WaitUntil($"{modeId} keyboard focus", () => IsElementFocused(mode), TimeSpan.FromSeconds(4));
        Keyboard.Press(VirtualKeyShort.SPACE);
        AutomationElement currentViewport = WaitForElement(
            $"{viewportId} after mode switch",
            () => FindCurrentVisibleByAutomationId(window, viewportId),
            TimeSpan.FromSeconds(8));
        AutomationElement selectedMode = WaitForElement(
            $"{modeId} selected instance",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(modeId)),
            TimeSpan.FromSeconds(5));
        AssertProbe(
            selectedMode.Patterns.SelectionItem.IsSupported &&
            selectedMode.Patterns.SelectionItem.Pattern.IsSelected.Value,
            $"{modeId} did not expose its selected state after keyboard activation.");
        Thread.Sleep(250);
        var currentBounds = currentViewport.BoundingRectangle;
        double xDelta = Math.Abs(currentBounds.X - initialBounds.X);
        double widthDelta = Math.Abs(currentBounds.Width - initialBounds.Width);
        AssertProbe(
            xDelta <= 2 && widthDelta <= 2,
            $"{context}: active Profile viewport moved/resized after selecting {modeId}. X delta={xDelta:0.0}, width delta={widthDelta:0.0}.");
    }
}

static void EnsureCommitDiffVisible(Window window)
{
    if (IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffViewer"))))
    {
        return;
    }

    AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsList"));
    if (IsVisible(list))
    {
        AutomationElement? row = list?.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem));
        if (row is not null)
        {
            InvokeOrClick(row);
            Thread.Sleep(900);
        }
    }

        WaitForElement(
            "CommitDiffViewerRowsScrollViewer",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("CommitDiffViewerRowsScrollViewer")),
            TimeSpan.FromSeconds(14));
}

static void AssertCommitDiffViewerContracts(Window window, string context)
{
    AssertProbe(
        IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("CommitDiffViewerRowsScrollViewer"))),
        $"{context}: virtualized diff row ScrollViewer was not visible.");
    AssertProbe(
        !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("RepoCommitsDiffModeComboBox"))),
        $"{context}: split/unified mode selector is still visible.");
    AssertProbe(
        window.FindFirstDescendant(cf => cf.ByText("Show more files")) is null &&
        window.FindFirstDescendant(cf => cf.ByText("Show more lines")) is null,
        $"{context}: legacy show-more diff controls are still present.");
}

static void AssertCommitDiffMultiRowSelection(Window window, UIA3Automation automation, AutomationElement diffRows, string outputDirectory)
{
    AutomationElement[] textElements = FindVisibleDiffTextElements(diffRows);
    AssertProbe(textElements.Length >= 2, "Commit diff did not expose at least two visible text rows for selection.");

    var startBounds = textElements[0].BoundingRectangle;
    var endBounds = textElements[Math.Min(textElements.Length - 1, 4)].BoundingRectangle;
    Point start = new(
        (int)Math.Round(startBounds.X + Math.Min(24, Math.Max(6, startBounds.Width / 4d))),
        (int)Math.Round(startBounds.Y + Math.Max(4, startBounds.Height / 2d)));
    Point end = new(
        (int)Math.Round(endBounds.X + Math.Min(endBounds.Width - 4, Math.Max(28, endBounds.Width * 0.7))),
        (int)Math.Round(endBounds.Y + Math.Max(4, endBounds.Height / 2d)));

    AutomationElement diffSearch = WaitForElement(
        "commit diff search focus anchor",
        () => FindCurrentVisibleByAutomationId(window, "RepoCommitsDiffSearchBox"),
        TimeSpan.FromSeconds(5));
    FocusForKeyboardActivation(window, diffSearch);
    Thread.Sleep(150);
    Mouse.MoveTo(start);
    Thread.Sleep(100);
    Mouse.Down(MouseButton.Left);
    for (int step = 1; step <= 8; step++)
    {
        double ratio = step / 8d;
        Point intermediate = new(
            (int)Math.Round(start.X + ((end.X - start.X) * ratio)),
            (int)Math.Round(start.Y + ((end.Y - start.Y) * ratio)));
        Mouse.MoveTo(intermediate);
        Thread.Sleep(90);
    }

    Mouse.Up(MouseButton.Left);
    Thread.Sleep(300);
    CaptureWindow(window, Path.Combine(outputDirectory, "commits-virtualized-diff-selection-debug.png"));

    NativeMethods.SetClipboardText("__jithub_context_menu_copy_pending__");
    Mouse.RightClick(new System.Drawing.Point(start.X + 4, start.Y));
    int appProcessId = window.Properties.ProcessId.ValueOrDefault;
    AutomationElement contextCopy = WaitForElement(
        "Commit diff copy context menu item",
        () => automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
            .FirstOrDefault(item =>
                item.Properties.ProcessId.ValueOrDefault == appProcessId &&
                IsVisible(item) &&
                string.Equals(GetElementName(item), "Copy selected diff text", StringComparison.Ordinal)),
        TimeSpan.FromSeconds(3));
    InvokeOrClick(contextCopy);
    string contextClipboard = string.Empty;
    Stopwatch stopwatch = Stopwatch.StartNew();
    do
    {
        contextClipboard = NativeMethods.GetClipboardText();
        if (contextClipboard.Trim().Length > 0 &&
            contextClipboard.Contains('\n', StringComparison.Ordinal))
        {
            break;
        }

        Thread.Sleep(100);
    }
    while (stopwatch.Elapsed < TimeSpan.FromSeconds(3));

    int contextCopiedLineCount = contextClipboard.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    Console.WriteLine(
        $"commit-diff-context-copy copiedLength={contextClipboard.Length} lines={contextCopiedLineCount} preview='{TruncateForLog(contextClipboard.ReplaceLineEndings("\\n"), 160)}'");
    CaptureWindow(window, Path.Combine(outputDirectory, "commits-virtualized-diff-context-copy.png"));
    AssertProbe(
        contextCopiedLineCount >= 2,
        "Right-clicking a selected diff range cleared the selection before the context menu copy command.");
    AssertProbe(
        !contextClipboard.Contains("Show more", StringComparison.OrdinalIgnoreCase),
        "Commit diff selection copied legacy show-more UI text instead of diff text.");

    Mouse.Click(start);
    Thread.Sleep(250);
    const string scrollbarSentinel = "__jithub_scrollbar_drag_should_not_select__";
    NativeMethods.SetClipboardText(scrollbarSentinel);
    var diffBounds = diffRows.BoundingRectangle;
    Point scrollStart = new(
        (int)Math.Round((double)diffBounds.Right - 8),
        (int)Math.Round((double)diffBounds.Top + Math.Min(120d, (double)diffBounds.Height * 0.25)));
    Point scrollEnd = new(
        scrollStart.X,
        (int)Math.Round(Math.Min((double)diffBounds.Bottom - 24, scrollStart.Y + Math.Max(80d, (double)diffBounds.Height * 0.28))));
    Mouse.MoveTo(scrollStart);
    Thread.Sleep(100);
    Mouse.Down(MouseButton.Left);
    for (int step = 1; step <= 6; step++)
    {
        double ratio = step / 6d;
        Mouse.MoveTo(new System.Drawing.Point(
            scrollStart.X,
            (int)Math.Round(scrollStart.Y + ((scrollEnd.Y - scrollStart.Y) * ratio))));
        Thread.Sleep(80);
    }

    Mouse.Up(MouseButton.Left);
    Thread.Sleep(250);
    AutomationElement scrollbarCopyRow = FindVisibleDiffTextElements(diffRows).First();
    var scrollbarCopyBounds = scrollbarCopyRow.BoundingRectangle;
    NativeMethods.SetClipboardText(scrollbarSentinel);
    Mouse.RightClick(new System.Drawing.Point(
        (int)Math.Round(scrollbarCopyBounds.X + Math.Min(24, Math.Max(6, scrollbarCopyBounds.Width / 4d))),
        (int)Math.Round(scrollbarCopyBounds.Y + Math.Max(4, scrollbarCopyBounds.Height / 2d))));
    AutomationElement scrollbarContextCopy = WaitForElement(
        "Commit diff scrollbar isolation copy menu item",
        () => automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
            .FirstOrDefault(item =>
                item.Properties.ProcessId.ValueOrDefault == appProcessId &&
                IsVisible(item) &&
                string.Equals(GetElementName(item), "Copy selected diff text", StringComparison.Ordinal)),
        TimeSpan.FromSeconds(3));
    InvokeOrClick(scrollbarContextCopy);
    string afterScrollbarDrag = string.Empty;
    stopwatch.Restart();
    do
    {
        afterScrollbarDrag = NativeMethods.GetClipboardText();
        if (!string.Equals(afterScrollbarDrag, scrollbarSentinel, StringComparison.Ordinal))
        {
            break;
        }

        Thread.Sleep(100);
    }
    while (stopwatch.Elapsed < TimeSpan.FromSeconds(3));

    AssertProbe(
        afterScrollbarDrag.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 1,
        "Dragging the commit diff scrollbar created a multi-row diff text selection.");
}

static AutomationElement[] FindVisibleDiffTextElements(AutomationElement diffRows)
{
    var rowBounds = diffRows.BoundingRectangle;
    return diffRows
        .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
        .Where(IsVisible)
        .Where(element =>
        {
            var bounds = element.BoundingRectangle;
            return bounds.Height > 8 &&
                bounds.Width > 24 &&
                bounds.X >= rowBounds.X &&
                bounds.Y >= rowBounds.Y &&
                bounds.X < rowBounds.X + rowBounds.Width &&
                bounds.Y < rowBounds.Y + rowBounds.Height;
        })
        .Where(element =>
        {
            string name = GetElementName(element);
            return !string.IsNullOrWhiteSpace(name) &&
                name.Trim().Length > 3 &&
                !name.Trim().All(char.IsDigit) &&
                !string.Equals(name.Trim(), "+", StringComparison.Ordinal) &&
                !string.Equals(name.Trim(), "-", StringComparison.Ordinal) &&
                !string.Equals(name.Trim(), "@", StringComparison.Ordinal) &&
                !name.Contains("No files match", StringComparison.OrdinalIgnoreCase);
        })
        .OrderBy(element => element.BoundingRectangle.Y)
        .ThenBy(element => element.BoundingRectangle.X)
        .Take(8)
        .ToArray();
}

static string TruncateForLog(string value, int maxLength)
{
    if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
    {
        return value ?? string.Empty;
    }

    return value[..maxLength] + "...";
}

static void SetTextBoxText(AutomationElement element, string text)
{
    TextBox textBox = element.AsTextBox();
    textBox.Text = string.Empty;
    if (!string.IsNullOrEmpty(text))
    {
        textBox.Text = text;
    }
}

static void EnsureCommitDetailVisible(Window window)
{
    AutomationElement? detail = FindCurrentVisibleByAutomationId(window, "RepoCommitsDetailTitle");
    if (IsVisible(detail) ||
        IsVisible(FindCurrentVisibleByAutomationId(window, "RepoCommitsDetailShyHeader")) ||
        IsVisible(FindCurrentVisibleByAutomationId(window, "RepoCommitsDiffViewer")))
    {
        return;
    }

    AutomationElement? list = FindCurrentVisibleByAutomationId(window, "RepoCommitsList");
    if (!IsVisible(list))
    {
        AutomationElement? leadingButton = FindAdaptivePaneButton(window, "RepoCommits", leading: true);
        if (IsVisible(leadingButton))
        {
            InvokeOrClick(leadingButton!);
            Thread.Sleep(450);
            list = FindCurrentVisibleByAutomationId(window, "RepoCommitsList");
        }
    }

    AssertProbe(IsVisible(list), "RepoCommitsList was not available for commit selection.");
    AutomationElement firstRow = WaitForElement(
        "first visible commit row",
        () => list!.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).FirstOrDefault(IsVisible),
        TimeSpan.FromSeconds(12));
    SelectAutomationItem(firstRow, "first commit row");
    Thread.Sleep(650);

    WaitForElement(
        "RepoCommitsDetailTitle",
        () => FindCurrentVisibleByAutomationId(window, "RepoCommitsDetailTitle"),
        TimeSpan.FromSeconds(12));
}

static void ExerciseAdaptiveWorkspaceDrawers(
    Window window,
    string prefix,
    string listId,
    string inspectorId,
    bool exerciseLeading = true,
    bool exerciseTrailing = true,
    bool requireAlignedCloseControls = true)
{
    if (exerciseLeading)
    {
        AutomationElement leadingButton = WaitForElement(
            $"{prefix}LeadingPaneButton",
            () => FindAdaptivePaneButton(window, prefix, leading: true),
            TimeSpan.FromSeconds(8));
        AssertProbe(IsVisible(leadingButton), $"{prefix}: leading pane button was not visible.");
        var leadingButtonBounds = leadingButton.BoundingRectangle;
        FocusForKeyboardActivation(window, leadingButton);
        leadingButton.Click();
        WaitForElement($"{prefix}LeftDrawer", () =>
        {
            AutomationElement? drawer = window.FindFirstDescendant(cf => cf.ByAutomationId($"{prefix}LeftDrawer"));
            return IsVisible(drawer) ? drawer : null;
        }, TimeSpan.FromSeconds(8));
        WaitForElement(listId, () =>
        {
            AutomationElement? list = window.FindFirstDescendant(cf => cf.ByAutomationId(listId));
            return IsVisible(list) ? list : null;
        }, TimeSpan.FromSeconds(8));
        Thread.Sleep(320);
        AssertDrawerInsideWorkspace(window, prefix, leading: true);
        AssertDrawerKeyboardFocusContained(window, $"{prefix}LeftDrawer", $"{prefix} leading drawer");
        AutomationElement? leadingCloseButton = FindAdaptivePaneCloseButton(window, prefix, leading: true);
        if (requireAlignedCloseControls)
        {
            leadingCloseButton = WaitForElement(
                $"{prefix} leading pane close button",
                () => FindAdaptivePaneCloseButton(window, prefix, leading: true),
                TimeSpan.FromSeconds(5));
            AssertProbe(IsVisible(leadingCloseButton), $"{prefix}: leading drawer did not expose its in-panel close control.");
            AssertElementBoundsAligned(leadingButtonBounds, leadingCloseButton, $"{prefix} leading pane toggle");
            leadingCloseButton.Click();
        }
        else
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
        }

        WaitUntil(
            $"{prefix} leading drawer closes",
            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId($"{prefix}LeftDrawer"))),
            TimeSpan.FromSeconds(5));
        AssertPaneFocusReturned(window, prefix, leading: true, $"{prefix} leading close button");

        ExerciseDrawerDismissal(window, prefix, listId, leading: true, useEscape: true);
        ExerciseDrawerDismissal(window, prefix, listId, leading: true, useEscape: false);
    }

    if (exerciseTrailing)
    {
        AutomationElement trailingButton = WaitForElement(
            $"{prefix}TrailingPaneButton",
            () => FindAdaptivePaneButton(window, prefix, leading: false),
            TimeSpan.FromSeconds(8));
        AssertProbe(IsVisible(trailingButton), $"{prefix}: inspector pane button was not visible.");
        var trailingButtonBounds = trailingButton.BoundingRectangle;
        FocusForKeyboardActivation(window, trailingButton);
        trailingButton.Click();
        WaitForElement($"{prefix}RightDrawer", () =>
        {
            AutomationElement? drawer = window.FindFirstDescendant(cf => cf.ByAutomationId($"{prefix}RightDrawer"));
            return IsVisible(drawer) ? drawer : null;
        }, TimeSpan.FromSeconds(8));
        Thread.Sleep(320);
        AssertDrawerInsideWorkspace(window, prefix, leading: false);
        AssertDrawerKeyboardFocusContained(window, $"{prefix}RightDrawer", $"{prefix} inspector drawer");
        AutomationElement? trailingCloseButton = FindAdaptivePaneCloseButton(window, prefix, leading: false);
        if (requireAlignedCloseControls)
        {
            trailingCloseButton = WaitForElement(
                $"{prefix} inspector pane close button",
                () => FindAdaptivePaneCloseButton(window, prefix, leading: false),
                TimeSpan.FromSeconds(5));
            AssertProbe(IsVisible(trailingCloseButton), $"{prefix}: inspector drawer did not expose its in-panel close control.");
            AssertElementBoundsAligned(trailingButtonBounds, trailingCloseButton, $"{prefix} inspector pane toggle");
            trailingCloseButton.Click();
        }
        else
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
        }

        WaitUntil(
            $"{prefix} inspector drawer closes",
            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId($"{prefix}RightDrawer"))),
            TimeSpan.FromSeconds(5));
        AssertPaneFocusReturned(window, prefix, leading: false, $"{prefix} inspector close button");

        ExerciseDrawerDismissal(window, prefix, inspectorId, leading: false, useEscape: true);
        ExerciseDrawerDismissal(window, prefix, inspectorId, leading: false, useEscape: false);
    }
}

static void ExerciseDrawerDismissal(
    Window window,
    string prefix,
    string paneContentId,
    bool leading,
    bool useEscape)
{
    AutomationElement opener = WaitForElement(
        $"{prefix} {(leading ? "leading" : "inspector")} pane opener",
        () => FindAdaptivePaneButton(window, prefix, leading),
        TimeSpan.FromSeconds(8));
    FocusForKeyboardActivation(window, opener);
    opener.Click();
    string drawerId = $"{prefix}{(leading ? "Left" : "Right")}Drawer";
    AutomationElement drawer = WaitForElement(
        drawerId,
        () =>
        {
            AutomationElement? candidate = FindCurrentVisibleByAutomationId(window, drawerId);
            return IsVisible(candidate) ? candidate : null;
        },
        TimeSpan.FromSeconds(8));
    try
    {
        WaitForElement(
            paneContentId,
            () => FindCurrentVisibleByAutomationId(window, paneContentId) ??
                (!leading ? FindAdaptivePaneCloseButton(window, prefix, leading: false) : null),
            TimeSpan.FromSeconds(8));
    }
    catch
    {
        PrintVisibleAutomationIds(
            window,
            $"{prefix}-{(leading ? "leading" : "trailing")}-drawer-content-missing");
        throw;
    }
    Thread.Sleep(240);
    WaitForDrawerSettled(window, prefix, leading);

    if (useEscape)
    {
        Keyboard.Press(VirtualKeyShort.ESCAPE);
    }
    else
    {
        AutomationElement workspace = WaitForElement(
            $"{prefix}AdaptiveWorkspace",
            () => FindCurrentVisibleByAutomationId(window, $"{prefix}AdaptiveWorkspace"),
            TimeSpan.FromSeconds(5));
        Rectangle bounds = workspace.BoundingRectangle;
        Rectangle drawerBounds = FindCurrentVisibleByAutomationId(window, drawerId)!.BoundingRectangle;
        Rectangle visibleWorkspace = Rectangle.Intersect(bounds, window.BoundingRectangle);
        int outsideStart = leading
            ? Math.Max(drawerBounds.Right, visibleWorkspace.Left)
            : visibleWorkspace.Left;
        int outsideEnd = leading
            ? visibleWorkspace.Right
            : Math.Min(drawerBounds.Left, visibleWorkspace.Right);
        AssertProbe(
            outsideEnd - outsideStart >= 24,
            $"{prefix} did not leave a visible light-dismiss target outside {drawerId}. workspace={visibleWorkspace}; drawer={drawerBounds}.");
        int x = outsideStart + ((outsideEnd - outsideStart) / 2);
        int y = visibleWorkspace.Top + (visibleWorkspace.Height / 2);
        Mouse.Click(new System.Drawing.Point(x, y));
    }

    WaitUntil(
        $"{prefix} drawer closes by {(useEscape ? "Escape" : "light dismiss")}",
        () => !IsVisible(FindCurrentVisibleByAutomationId(window, drawerId)),
        TimeSpan.FromSeconds(5));
    AssertPaneFocusReturned(
        window,
        prefix,
        leading,
        $"{prefix} {(leading ? "leading" : "inspector")} {(useEscape ? "Escape" : "light dismiss")}");
}

static void WaitForDrawerSettled(Window window, string prefix, bool leading)
{
    string workspaceId = $"{prefix}AdaptiveWorkspace";
    string drawerId = $"{prefix}{(leading ? "Left" : "Right")}Drawer";
    bool settled = WaitUntilAvailable(
        () =>
        {
            AutomationElement? workspace = FindCurrentVisibleByAutomationId(window, workspaceId);
            AutomationElement? drawer = FindCurrentVisibleByAutomationId(window, drawerId);
            if (workspace is null || drawer is null)
            {
                return false;
            }

            Rectangle workspaceBounds = workspace.BoundingRectangle;
            Rectangle drawerBounds = drawer.BoundingRectangle;
            return leading
                ? Math.Abs(drawerBounds.Left - workspaceBounds.Left) <= 2
                : Math.Abs(drawerBounds.Right - workspaceBounds.Right) <= 2;
        },
        TimeSpan.FromSeconds(5));
    AssertProbe(settled, $"{prefix} {(leading ? "leading" : "inspector")} drawer did not settle at its workspace edge.");
}

static void AssertPaneFocusReturned(Window window, string prefix, bool leading, string context)
{
    AutomationElement opener = WaitForElement(
        $"{prefix} pane opener after dismiss",
        () => FindAdaptivePaneButton(window, prefix, leading),
        TimeSpan.FromSeconds(5));
    bool returned = WaitUntilAvailable(
        () =>
        {
            AutomationElement focused = window.Automation.FocusedElement();
            string focusedId = GetAutomationId(focused);
            string openerId = GetAutomationId(opener);
            return !string.IsNullOrWhiteSpace(openerId) && string.Equals(focusedId, openerId, StringComparison.Ordinal);
        },
        TimeSpan.FromSeconds(3));
    AssertProbe(returned, $"{context} did not restore keyboard focus to the pane opener.");
}

static string WaitForRepoCodeRoutePath(Window window, TimeSpan timeout)
{
    string? routePath = null;
    bool found = WaitUntilAvailable(
        () => !string.IsNullOrEmpty(routePath = GetRepoCodeRoutePath(window)),
        timeout);
    AssertProbe(found, "repo-code: current breadcrumb route did not become available.");
    return routePath!;
}

static string? GetRepoCodeRoutePath(Window window)
    => window.FindAllDescendants()
        .Where(element =>
            IsVisible(element) &&
            element.ControlType == ControlType.Button &&
            GetAutomationId(element).StartsWith("RepoCodeBreadcrumbSegment_", StringComparison.Ordinal))
        .OrderBy(element => element.BoundingRectangle.Left)
        .Select(element => element.Properties.ItemStatus.ValueOrDefault)
        .LastOrDefault(path => !string.IsNullOrEmpty(path));

static void AssertDrawerKeyboardFocusContained(Window window, string drawerAutomationId, string context)
{
    IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
    AutomationElement? lastContainedFocus = null;
    AutomationElement? CurrentDrawer() => FindCurrentVisibleByAutomationId(window, drawerAutomationId);

    bool FocusIsInsideCurrentDrawer()
    {
        try
        {
            AutomationElement? currentDrawer = CurrentDrawer();
            return currentDrawer is not null &&
                IsInsideElementBounds(window.Automation.FocusedElement(), currentDrawer, tolerance: 2);
        }
        catch (COMException)
        {
            return false;
        }
    }

    void RecoverFromExternalForegroundLoss()
    {
        if (NativeMethods.IsForegroundWindow(windowHandle))
        {
            return;
        }

        NativeMethods.ActivateForKeyboard(windowHandle);
        if (lastContainedFocus is not null)
        {
            try
            {
                lastContainedFocus.FocusNative();
            }
            catch (COMException)
            {
                lastContainedFocus = null;
            }
        }

        _ = WaitUntilAvailable(
            () => NativeMethods.IsForegroundWindow(windowHandle) && FocusIsInsideCurrentDrawer(),
            TimeSpan.FromMilliseconds(750));
    }

    string Describe(AutomationElement element)
    {
        try
        {
            return $"{element.ControlType}:'{GetAutomationId(element)}'/{GetElementName(element)}" +
                $"@{element.BoundingRectangle}; focused={element.Properties.HasKeyboardFocus.ValueOrDefault}; " +
                $"focusable={element.Properties.IsKeyboardFocusable.ValueOrDefault}";
        }
        catch (COMException)
        {
            return "stale UIA provider";
        }
    }

    _ = WaitForElement(
        drawerAutomationId,
        CurrentDrawer,
        TimeSpan.FromSeconds(5));
    bool receivedFocus = WaitUntilAvailable(FocusIsInsideCurrentDrawer, TimeSpan.FromSeconds(3));
    if (!receivedFocus)
    {
        AutomationElement focused = window.Automation.FocusedElement();
        AutomationElement? currentDrawer = CurrentDrawer();
        string drawerControls = currentDrawer is null
            ? "drawer provider unavailable"
            : string.Join(
                "; ",
                currentDrawer.FindAllDescendants()
                    .Where(element =>
                        element.ControlType == ControlType.Button ||
                        element.ControlType == ControlType.Edit ||
                        element.ControlType == ControlType.Tree ||
                        element.ControlType == ControlType.TreeItem)
                    .Take(12)
                    .Select(Describe));
        AssertProbe(
            false,
            $"{context} did not receive keyboard focus; focus remained on {Describe(focused)}; " +
            $"drawer={((currentDrawer is null) ? "unavailable" : Describe(currentDrawer))}; controls=[{drawerControls}].");
    }

    lastContainedFocus = window.Automation.FocusedElement();

    List<string> forwardFocusTrace = [];
    for (int index = 0; index < 10; index++)
    {
        RecoverFromExternalForegroundLoss();
        AutomationElement focused = window.Automation.FocusedElement();
        if (!FocusIsInsideCurrentDrawer())
        {
            _ = WaitUntilAvailable(FocusIsInsideCurrentDrawer, TimeSpan.FromMilliseconds(500));
            focused = window.Automation.FocusedElement();
        }
        forwardFocusTrace.Add($"{index + 1}:{Describe(focused)}");
        AssertProbe(
            FocusIsInsideCurrentDrawer(),
            $"{context}: Tab transition {index + 1} escaped the open drawer to {Describe(focused)}. " +
            $"Focus trace: {string.Join(" -> ", forwardFocusTrace)}");
        lastContainedFocus = focused;
        Keyboard.Press(VirtualKeyShort.TAB);
        Thread.Sleep(70);
    }

    for (int index = 0; index < 6; index++)
    {
        RecoverFromExternalForegroundLoss();
        AutomationElement focused = window.Automation.FocusedElement();
        if (!FocusIsInsideCurrentDrawer())
        {
            _ = WaitUntilAvailable(FocusIsInsideCurrentDrawer, TimeSpan.FromMilliseconds(500));
            focused = window.Automation.FocusedElement();
        }
        AssertProbe(
            FocusIsInsideCurrentDrawer(),
            $"{context}: reverse Tab transition {index + 1} escaped the open drawer to {Describe(focused)}.");
        lastContainedFocus = focused;
        using (Keyboard.Pressing(VirtualKeyShort.LSHIFT))
        {
            Keyboard.Press(VirtualKeyShort.TAB);
        }
        Thread.Sleep(70);
    }
}

static AutomationElement? FindAdaptivePaneCloseButton(Window window, string prefix, bool leading)
{
    string[] ids = prefix switch
    {
        "MyIssues" => leading
            ? ["MyIssuesCloseListPaneButton"]
            : ["MyIssuesCloseInspectorPaneButton"],
        "MyPullRequests" => leading
            ? ["MyPullRequestsCloseListPaneButton"]
            : ["MyPullRequestsCloseInspectorPaneButton"],
        "RepoIssues" => leading
            ? ["RepoIssuesCloseListPaneButton"]
            : ["RepoIssuesCloseInspectorPaneButton"],
        "RepoPullRequests" => leading
            ? ["RepoPullRequestsCloseListPaneButton"]
            : ["RepoPullRequestsCloseInspectorPaneButton"],
        "RepoCommits" => leading
            ? ["RepoCommitsCloseListPaneButton"]
            : ["RepoCommitsCloseInspectorPaneButton"],
        "RepoCode" => leading
            ? ["RepoCodeCloseFileTreeButton"]
            : [],
        _ => []
    };

    foreach (string id in ids)
    {
        AutomationElement? element = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
        if (IsVisible(element))
        {
            return element;
        }
    }

    return null;
}

static void AssertDrawerInsideWorkspace(Window window, string prefix, bool leading)
{
    string workspaceId = $"{prefix}AdaptiveWorkspace";
    string drawerId = leading ? $"{prefix}LeftDrawer" : $"{prefix}RightDrawer";
    AutomationElement? workspace = window.FindFirstDescendant(cf => cf.ByAutomationId(workspaceId));
    AutomationElement? drawer = window.FindFirstDescendant(cf => cf.ByAutomationId(drawerId));
    AssertProbe(IsVisible(workspace), $"{prefix}: adaptive workspace was not visible.");
    AssertProbe(IsVisible(drawer), $"{prefix}: expected {drawerId} to be visible after opening the pane.");

    AssertProbe(
        IsInsideElementBounds(drawer!, workspace!, tolerance: 1.5),
        $"{prefix}: {(leading ? "leading" : "inspector")} drawer rendered outside the workspace bounds.");
}

static void ExerciseRepoIssueFiltersAndCommentEditor(Window window, string outputDirectory)
{
    AutomationElement launcher = WaitForElement(
        "RepoIssuesOpenCommentButton",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesOpenCommentButton")),
        TimeSpan.FromSeconds(8));
    if (!launcher.IsEnabled)
    {
        CaptureWindow(window, Path.Combine(outputDirectory, "repo-issues-page-comment-read-only.png"));
    }
    else
    {
        InvokeOrClick(launcher);
        AutomationElement editor = WaitForElement(
            "RepoIssuesCommentBox_Editor",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesCommentBox_Editor")),
            TimeSpan.FromSeconds(8));
        AutomationElement mode = WaitForElement(
            "RepoIssuesCommentBox_Mode",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesCommentBox_Mode")),
            TimeSpan.FromSeconds(5));
        AutomationElement preview = WaitForElement(
            "Repo Issues comment Preview",
            () => mode.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesCommentBox_Mode_Preview")),
            TimeSpan.FromSeconds(5));
        AutomationElement commentButton = WaitForElement(
            "RepoIssuesCommentButton",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesCommentButton")),
            TimeSpan.FromSeconds(5));
        AssertProbe(IsVisible(commentButton), "Issue comment command was not visible with the comment editor.");
        AssertProbe(editor.IsEnabled, "Enabled issue comment launcher opened a disabled editor.");
        SetTextBoxText(editor, "**automation comment preview**");
        InvokeOrClick(preview);
        WaitForElement(
            "RepoIssuesCommentBox_Preview",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesCommentBox_Preview")),
            TimeSpan.FromSeconds(5));
        CaptureWindow(window, Path.Combine(outputDirectory, "repo-issues-page-comment-preview.png"));
    }

    AutomationElement search = WaitForElement(
        "RepoIssuesSearchBox",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesSearchBox")),
        TimeSpan.FromSeconds(5));
    SetTextBoxText(search, "test");
    Thread.Sleep(450);
    SetTextBoxText(search, string.Empty);

    AutomationElement scope = WaitForElement(
        "RepoIssuesScopeComboBox",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesScopeComboBox")),
        TimeSpan.FromSeconds(5));
    AutomationElement sort = WaitForElement(
        "RepoIssuesSortComboBox",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesSortComboBox")),
        TimeSpan.FromSeconds(5));
    AutomationElement direction = WaitForElement(
        "RepoIssuesDirectionComboBox",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesDirectionComboBox")),
        TimeSpan.FromSeconds(5));
    scope.AsComboBox().Select("Mine");
    sort.AsComboBox().Select("Created");
    direction.AsComboBox().Select("Oldest first");
    Thread.Sleep(500);
    scope.AsComboBox().Select("All");
    sort.AsComboBox().Select("Updated");
    direction.AsComboBox().Select("Newest first");

    AutomationElement closed = WaitForElement(
        "RepoIssuesState_Closed",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesState_Closed")),
        TimeSpan.FromSeconds(5));
    AutomationElement open = WaitForElement(
        "RepoIssuesState_Open",
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("RepoIssuesState_Open")),
        TimeSpan.FromSeconds(5));
    InvokeOrClick(closed);
    Thread.Sleep(350);
    InvokeOrClick(open);
    Thread.Sleep(450);
    CaptureWindow(window, Path.Combine(outputDirectory, "repo-issues-page-filters.png"));
}

static void AssertElementBoundsAligned(System.Drawing.Rectangle expectedBounds, AutomationElement actualElement, string context)
{
    var actualBounds = actualElement.BoundingRectangle;
    const double tolerance = 1.5;
    double leftDelta = Math.Abs(actualBounds.X - expectedBounds.X);
    double topDelta = Math.Abs(actualBounds.Y - expectedBounds.Y);
    double widthDelta = Math.Abs(actualBounds.Width - expectedBounds.Width);
    double heightDelta = Math.Abs(actualBounds.Height - expectedBounds.Height);
    AssertProbe(
        leftDelta <= tolerance &&
        topDelta <= tolerance &&
        widthDelta <= tolerance &&
        heightDelta <= tolerance,
        $"{context} bounds were not aligned. expected=({expectedBounds.X:0.0},{expectedBounds.Y:0.0},{expectedBounds.Width:0.0},{expectedBounds.Height:0.0}) actual=({actualBounds.X:0.0},{actualBounds.Y:0.0},{actualBounds.Width:0.0},{actualBounds.Height:0.0}) Δleft={leftDelta:0.0}, Δtop={topDelta:0.0}, Δwidth={widthDelta:0.0}, Δheight={heightDelta:0.0}.");
}

static AutomationElement? FindAdaptivePaneButton(Window window, string prefix, bool leading)
{
    string[] ids = prefix switch
    {
        "MyIssues" => leading
            ? ["MyIssuesOpenListPaneButton", "MyIssuesLeadingPaneButton"]
            : ["MyIssuesOpenInspectorPaneButton", "MyIssuesTrailingPaneButton"],
        "MyPullRequests" => leading
            ? ["MyPullRequestsOpenListPaneButton", "MyPullRequestsLeadingPaneButton"]
            : ["MyPullRequestsOpenInspectorPaneButton", "MyPullRequestsTrailingPaneButton"],
        "RepoIssues" => leading
            ? ["RepoIssuesOpenListPaneButton", "RepoDetailOpenIssueListPaneButton", "RepoIssuesLeadingPaneButton"]
            : ["RepoIssuesOpenInspectorPaneButton", "RepoIssuesCompactOpenInspectorPaneButton", "RepoDetailOpenIssueInspectorPaneButton", "RepoIssuesTrailingPaneButton"],
        "RepoPullRequests" => leading
            ? ["RepoPullRequestsOpenListPaneButton", "RepoPullRequestsLeadingPaneButton"]
            : ["RepoPullRequestsOpenInspectorPaneButton", "RepoPullRequestsTrailingPaneButton"],
        "RepoCommits" => leading
            ? ["RepoCommitsOpenListPaneButton", "RepoCommitsLeadingPaneButton"]
            : ["RepoCommitsOpenInspectorPaneButton", "RepoCommitsTrailingPaneButton"],
        "RepoCode" => leading
            ? ["RepoCodeOpenFileTreeButton", "RepoCodeLeadingPaneButton"]
            : [],
        _ => leading
            ? [$"{prefix}LeadingPaneButton"]
            : [$"{prefix}TrailingPaneButton"]
    };

    foreach (string id in ids)
    {
        AutomationElement? element = FindCurrentVisibleByAutomationId(window, id);
        if (IsVisible(element))
        {
            return element;
        }

        AutomationElement? currentProvider = window
            .FindAllDescendants(cf => cf.ByAutomationId(id))
            .LastOrDefault();
        if (currentProvider is null)
        {
            continue;
        }

        try
        {
            RevealForInteraction(currentProvider, id);
            element = FindCurrentVisibleByAutomationId(window, id);
            if (IsVisible(element))
            {
                return element;
            }
        }
        catch (COMException)
        {
            // A responsive reparent can invalidate the old provider; retry the next stable ID.
        }
    }

    return null;
}

static void AssertMarkdownTextVisible(Window window, params string[] expectedFragments)
{
    string[] visibleTexts = window
        .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
        .Where(IsVisible)
        .Select(GetElementName)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .ToArray();

    foreach (string expectedFragment in expectedFragments)
    {
        AssertProbe(
            visibleTexts.Any(text => text.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase)),
            $"Expected markdown/comment text fragment '{expectedFragment}' was not visible.");
    }
}

static bool IsCommitDiffTextVisible(Window window)
{
    return window
        .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
        .Where(IsVisible)
        .Select(GetElementName)
        .Any(text =>
            text.Contains("@@", StringComparison.Ordinal) ||
            text.Contains("texture_mtl.mm", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("_texture", StringComparison.OrdinalIgnoreCase));
}

static void ExerciseIssueListScrollSelection(Window window, AutomationElement list, string outputDirectory, string prefix)
{
    string listAutomationId = GetAutomationId(list);
    ScrollElementToBottom(list);
    Thread.Sleep(450);
    AutomationElement target = FindLastVisibleListItem(list)
        ?? throw new InvalidOperationException($"{prefix}: no visible issue item was available after scrolling.");
    string signature = GetListItemSignature(target);
    string targetAutomationId = GetAutomationId(target);
    AssertProbe(!string.IsNullOrWhiteSpace(signature), $"{prefix}: issue item did not expose enough text for scroll-jump validation.");
    var beforeBounds = target.BoundingRectangle;
    CaptureWindow(window, Path.Combine(outputDirectory, $"{prefix}-scroll-click-before.png"));

    ClickListItemSurface(target);
    Thread.Sleep(1500);

    AutomationElement? currentList = FindCurrentVisibleByAutomationId(window, listAutomationId) ?? list;
    AutomationElement? afterTarget = !string.IsNullOrWhiteSpace(targetAutomationId)
        ? FindCurrentVisibleByAutomationId(window, targetAutomationId)
        : null;
    afterTarget = IsVisible(afterTarget)
        ? afterTarget
        : FindVisibleListItemBySignature(currentList, signature);
    CaptureWindow(window, Path.Combine(outputDirectory, $"{prefix}-scroll-click-after.png"));
    if (afterTarget is null)
    {
        string visibleRows = string.Join(
            "; ",
            currentList.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Where(IsVisible)
                .Select(row => $"{GetAutomationId(row)}:{GetListItemSignature(row)}@{row.BoundingRectangle}"));
        Console.WriteLine($"{prefix}: post-click visible rows: {visibleRows}");
    }
    AssertProbe(afterTarget is not null, $"{prefix}: selected list row disappeared after click.");
    var afterBounds = afterTarget!.BoundingRectangle;
    double movement = Math.Abs(afterBounds.Y - beforeBounds.Y);
    AssertProbe(movement <= 28, $"{prefix}: selected issue row moved {movement:0.0}px after click.");
}

static void ClickListItemSurface(AutomationElement item)
{
    Rectangle bounds = item.BoundingRectangle;
    AssertProbe(bounds.Width >= 20 && bounds.Height >= 20, $"List item did not expose a usable click surface: {bounds}.");
    Mouse.Click(new System.Drawing.Point(bounds.Left + 8, bounds.Top + 8));
}

static void ScrollElementToBottom(AutomationElement element)
{
    try
    {
        if (element.Patterns.Scroll.IsSupported)
        {
            var scroll = element.Patterns.Scroll.Pattern;
            for (int index = 0; index < 8; index++)
            {
                scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                Thread.Sleep(60);
            }

            return;
        }
    }
    catch
    {
    }

    try
    {
        element.Focus();
    }
    catch
    {
    }

    for (int index = 0; index < 8; index++)
    {
        Keyboard.Press((VirtualKeyShort)0x22);
        Thread.Sleep(60);
    }
}

static AutomationElement? FindLastVisibleListItem(AutomationElement list)
{
    return list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
        .Where(IsVisible)
        .OrderBy(item => item.BoundingRectangle.Y)
        .LastOrDefault();
}

static AutomationElement? FindVisibleListItemBySignature(AutomationElement list, string signature)
{
    return list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
        .Where(IsVisible)
        .FirstOrDefault(item => string.Equals(GetListItemSignature(item), signature, StringComparison.Ordinal));
}

static string GetListItemSignature(AutomationElement item)
{
    string[] names = item.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
        .Select(element => element.Name)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Take(4)
        .ToArray();
    if (names.Length > 0)
    {
        return string.Join("|", names);
    }

    return item.Name ?? string.Empty;
}

static Window GetReadyWindow(Application app, UIA3Automation automation, string context)
{
    int expectedProcessId = TryGetApplicationProcessId(app);
    Application windowProvider = app;
    if (AutomationApplicationPathRegistry.TryGet(
            app,
            out AutomationApplicationRegistration? registration) &&
        registration is not null)
    {
        expectedProcessId = registration.ProcessId;
    }

    if (expectedProcessId <= 0)
    {
        throw new InvalidOperationException($"Unable to resolve the JitHub process for {context}.");
    }

    var windowRetry = Retry.WhileNull(
        () => FindExpectedJitHubWindow(windowProvider, automation, expectedProcessId),
        timeout: TimeSpan.FromSeconds(20),
        interval: TimeSpan.FromMilliseconds(250),
        ignoreException: true);
    if (!windowRetry.Success || windowRetry.Result is null)
    {
        throw new InvalidOperationException(
            $"Unable to find the verified JitHub main window for {context} (expected process {expectedProcessId}).");
    }

    Window window = windowRetry.Result;
    if (window.Patterns.Transform.IsSupported)
    {
        window.Patterns.Transform.Pattern.Resize(1600, 1000);
    }

    window.Move(80, 80);
    TryActivateWindow(window);
    Thread.Sleep(1200);
    var currentWindowRetry = Retry.WhileNull(
        () =>
        {
            Window? candidate = FindExpectedJitHubWindow(windowProvider, automation, expectedProcessId);
            return candidate is not null && IsVisible(candidate) ? candidate : null;
        },
        timeout: TimeSpan.FromSeconds(10),
        interval: TimeSpan.FromMilliseconds(150),
        ignoreException: true);
    if (!currentWindowRetry.Success || currentWindowRetry.Result is null)
    {
        throw new InvalidOperationException(
            $"The current visible JitHub window provider was unavailable for {context} (expected process {expectedProcessId}).");
    }

    Window currentWindow = currentWindowRetry.Result;
    AssertJitHubWindow(currentWindow, expectedProcessId, context);
    return currentWindow;
}

static Window? FindExpectedJitHubWindow(
    Application windowProvider,
    UIA3Automation automation,
    int expectedProcessId)
{
    try
    {
        Window? candidate = windowProvider.GetMainWindow(automation);
        if (IsExpectedJitHubWindow(candidate, expectedProcessId))
        {
            return candidate;
        }
    }
    catch (COMException)
    {
    }
    catch (InvalidOperationException)
    {
    }

    try
    {
        using Process process = Process.GetProcessById(expectedProcessId);
        process.Refresh();
        IntPtr mainWindowHandle = process.MainWindowHandle;
        if (mainWindowHandle == IntPtr.Zero)
        {
            return null;
        }

        Window candidate = automation.FromHandle(mainWindowHandle).AsWindow();
        return IsExpectedJitHubWindow(candidate, expectedProcessId) ? candidate : null;
    }
    catch (ArgumentException)
    {
        return null;
    }
    catch (InvalidOperationException)
    {
        return null;
    }
    catch (COMException)
    {
        return null;
    }
}

static double GetVerticalScrollPercent(AutomationElement element)
{
    AssertProbe(element.Patterns.Scroll.IsSupported, $"{GetAutomationId(element)} does not expose the Scroll pattern.");
    var scroll = element.Patterns.Scroll.Pattern;
    AssertProbe(scroll.VerticalScrollPercent.IsSupported, $"{GetAutomationId(element)} does not expose vertical scroll state.");
    return scroll.VerticalScrollPercent.ValueOrDefault;
}

static Window GetReadyWindowByHandle(
    UIA3Automation automation,
    IntPtr windowHandle,
    int expectedProcessId,
    string context)
{
    var retry = Retry.WhileNull(
        () =>
        {
            try
            {
                Window candidate = automation.FromHandle(windowHandle).AsWindow();
                return IsExpectedJitHubWindow(candidate, expectedProcessId) ? candidate : null;
            }
            catch
            {
                return null;
            }
        },
        timeout: TimeSpan.FromSeconds(10),
        interval: TimeSpan.FromMilliseconds(200));
    if (!retry.Success || retry.Result is null)
    {
        throw new InvalidOperationException($"Could not reacquire the original JitHub window during {context}.");
    }

    AssertJitHubWindow(retry.Result, expectedProcessId, context);
    return retry.Result;
}

static bool IsExpectedJitHubWindow(Window? window, int expectedProcessId)
{
    if (window is null)
    {
        return false;
    }

    try
    {
        return window.Properties.ProcessId.ValueOrDefault == expectedProcessId
            && string.Equals(window.Title, "JitHub", StringComparison.Ordinal)
            && window.FindFirstDescendant(cf => cf.ByAutomationId("JitHubMainWindowRoot")) is not null;
    }
    catch (COMException)
    {
        return false;
    }
}

static void AssertJitHubWindow(Window window, int? expectedProcessId = null, string? context = null)
{
    int actualProcessId;
    bool hasRoot;
    try
    {
        actualProcessId = window.Properties.ProcessId.ValueOrDefault;
        hasRoot = window.FindFirstDescendant(cf => cf.ByAutomationId("JitHubMainWindowRoot")) is not null;
    }
    catch (COMException exception)
    {
        throw new InvalidOperationException($"The JitHub window became unavailable before {context ?? "capture"}.", exception);
    }

    if (!string.Equals(window.Title, "JitHub", StringComparison.Ordinal)
        || !hasRoot
        || (expectedProcessId.HasValue && actualProcessId != expectedProcessId.Value))
    {
        throw new InvalidOperationException(
            $"Refusing to automate or capture an unverified window during {context ?? "capture"}. " +
            $"Title='{window.Title}', process={actualProcessId}, expectedProcess={expectedProcessId?.ToString() ?? "n/a"}, hasJitHubRoot={hasRoot}.");
    }
}

static Rectangle ResizeWindow(Window window, int width, int height, bool reactivate = true)
{
    Rectangle workArea = NativeMethods.GetWorkArea();
    int targetWidth = workArea.Width > 0 ? Math.Min(width, workArea.Width) : width;
    int targetHeight = workArea.Height > 0 ? Math.Min(height, workArea.Height) : height;
    if (targetWidth != width || targetHeight != height)
    {
        Console.WriteLine(
            $"Requested {width}x{height}; using {targetWidth}x{targetHeight} to fit the current Windows work area.");
    }

    IntPtr windowHandle = GetNativeWindowHandle(window);
    Rectangle initialBounds = NativeMethods.GetWindowBounds(windowHandle);
    bool alreadySized =
        Math.Abs(initialBounds.Width - targetWidth) <= 4 &&
        Math.Abs(initialBounds.Height - targetHeight) <= 4;
    if (!alreadySized)
    {
        NativeMethods.ResizeWindow(windowHandle, targetWidth, targetHeight);
    }
    if (reactivate)
    {
        TryActivateWindow(window);
    }
    try
    {
        WaitUntil(
            $"window to resize to {targetWidth}x{targetHeight}",
            () =>
            {
                Rectangle bounds = NativeMethods.GetWindowBounds(windowHandle);
                bool reachedTarget =
                    Math.Abs(bounds.Width - targetWidth) <= 4 &&
                    Math.Abs(bounds.Height - targetHeight) <= 4;
                if (!reachedTarget)
                {
                    // MainWindow applies its persisted/default launch size after
                    // the readiness signal. Reapply the requested test viewport
                    // until that one-time startup sizing has settled.
                    NativeMethods.ResizeWindow(windowHandle, targetWidth, targetHeight);
                }

                return reachedTarget;
            },
            TimeSpan.FromSeconds(5));
    }
    catch (InvalidOperationException exception)
    {
        Rectangle actual = NativeMethods.GetWindowBounds(windowHandle);
        throw new InvalidOperationException(
            $"Timed out resizing the JitHub window to {targetWidth}x{targetHeight}; actual native bounds were {actual}.",
            exception);
    }
    Thread.Sleep(600);
    Rectangle settledBounds = NativeMethods.GetWindowBounds(windowHandle);
    Console.WriteLine(
        $"Viewport requested={width}x{height}; actual={settledBounds.Width}x{settledBounds.Height}.");
    return settledBounds;
}

static Rectangle ResizeLogicalWindow(Window window, int logicalWidth, int logicalHeight)
{
    uint dpi = NativeMethods.GetWindowDpi(GetNativeWindowHandle(window));
    double scale = dpi / 96d;
    int physicalWidth = Math.Max(1, (int)Math.Round(logicalWidth * scale));
    int physicalHeight = Math.Max(1, (int)Math.Round(logicalHeight * scale));
    Rectangle physicalBounds = ResizeWindow(window, physicalWidth, physicalHeight);
    int actualLogicalWidth = (int)Math.Round(physicalBounds.Width / scale);
    int actualLogicalHeight = (int)Math.Round(physicalBounds.Height / scale);
    Console.WriteLine(
        $"Logical viewport requested={logicalWidth}x{logicalHeight}; " +
        $"actual={actualLogicalWidth}x{actualLogicalHeight}; dpi={dpi}.");
    return physicalBounds;
}

static string GetResponsiveViewportLabel(int requestedWidth, int requestedHeight, Rectangle actualBounds)
{
    string actual = $"{actualBounds.Width}x{actualBounds.Height}";
    bool constrained =
        Math.Abs(actualBounds.Width - requestedWidth) > 4 ||
        Math.Abs(actualBounds.Height - requestedHeight) > 4;
    return constrained
        ? $"{actual}-requested-{requestedWidth}x{requestedHeight}"
        : actual;
}

static IntPtr GetNativeWindowHandle(Window window)
{
    if (AutomationWindowHandleCache.TryGet(window, out IntPtr cachedHandle))
    {
        return cachedHandle;
    }

    COMException? lastError = null;
    for (int attempt = 0; attempt < 8; attempt++)
    {
        try
        {
            IntPtr handle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
            if (handle != IntPtr.Zero)
            {
                AutomationWindowHandleCache.Store(window, handle);
                return handle;
            }
        }
        catch (COMException ex)
        {
            lastError = ex;
        }

        Thread.Sleep(75);
    }

    throw new InvalidOperationException("Could not reacquire the application window handle after a UI Automation transition.", lastError);
}

static Window ReacquireJitHubWindow(
    Application application,
    UIA3Automation automation,
    int processId,
    string context,
    IntPtr preferredWindowHandle = default)
{
    var retry = Retry.WhileNull(
        () => FindExpectedJitHubWindowFromHandle(automation, preferredWindowHandle, processId)
            ?? FindExpectedJitHubWindow(application, automation, processId),
        timeout: TimeSpan.FromSeconds(12),
        interval: TimeSpan.FromMilliseconds(200),
        ignoreException: true);
    if (!retry.Success || retry.Result is null)
    {
        throw new InvalidOperationException(
            $"Could not reacquire the verified JitHub window after {context}. " +
            DescribeWindowReacquisitionFailure(automation, processId, preferredWindowHandle));
    }

    Window refreshed = retry.Result;
    AutomationWindowHandleCache.Store(refreshed, GetNativeWindowHandle(refreshed));
    return refreshed;
}

static string DescribeWindowReacquisitionFailure(
    UIA3Automation automation,
    int expectedProcessId,
    IntPtr preferredWindowHandle)
{
    var details = new List<string>();
    try
    {
        using Process process = Process.GetProcessById(expectedProcessId);
        process.Refresh();
        details.Add($"processExited={process.HasExited}");
        if (!process.HasExited)
        {
            details.Add($"processMainHwnd=0x{process.MainWindowHandle.ToInt64():X}");
        }
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
    {
        details.Add($"processLookup={exception.GetType().Name}");
    }

    details.Add($"preferredHwnd=0x{preferredWindowHandle.ToInt64():X}");
    if (preferredWindowHandle != IntPtr.Zero)
    {
        try
        {
            AutomationElement element = automation.FromHandle(preferredWindowHandle);
            details.Add($"uiaProcess={element.Properties.ProcessId.ValueOrDefault}");
            details.Add($"uiaName='{element.Name}'");
            details.Add($"uiaId='{element.AutomationId}'");
            Window candidate = element.AsWindow();
            details.Add($"uiaTitle='{candidate.Title}'");
            details.Add($"uiaRoot={candidate.FindFirstDescendant(cf => cf.ByAutomationId("JitHubMainWindowRoot")) is not null}");
        }
        catch (Exception exception)
        {
            details.Add($"uiaLookup={exception.GetType().Name}:0x{exception.HResult:X8}");
        }
    }

    return string.Join(", ", details);
}

static Window? FindExpectedJitHubWindowFromHandle(
    UIA3Automation automation,
    IntPtr windowHandle,
    int expectedProcessId)
{
    if (windowHandle == IntPtr.Zero)
    {
        return null;
    }

    try
    {
        Window candidate = automation.FromHandle(windowHandle).AsWindow();
        return IsExpectedJitHubWindow(candidate, expectedProcessId) ? candidate : null;
    }
    catch (COMException)
    {
        return null;
    }
    catch (InvalidOperationException)
    {
        return null;
    }
}

static void TryActivateWindow(Window window)
{
    try
    {
        window.SetForeground();
    }
    catch (COMException)
    {
    }

    try
    {
        window.FocusNative();
    }
    catch (COMException)
    {
    }
}

static void PressKeyForWindow(Window window, VirtualKeyShort key)
{
    IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
    if (NativeMethods.IsForegroundWindow(windowHandle))
    {
        Keyboard.Press(key);
        return;
    }

    NativeMethods.SendKey(windowHandle, key);
}

static void ApplySelectionFallbackForInvisibleForeignForeground(
    Window window,
    Func<bool> selectionApplied,
    AutomationElement target,
    string description)
{
    if (WaitUntilAvailable(selectionApplied, TimeSpan.FromSeconds(1)) ||
        !NativeMethods.HasInvisibleForeignForegroundWindow(
            window.Properties.NativeWindowHandle.ValueOrDefault))
    {
        return;
    }

    AssertProbe(
        target.Patterns.SelectionItem.IsSupported,
        $"The {description} did not expose native SelectionItem semantics for the invisible-foreground fallback.");
    target.Patterns.SelectionItem.Pattern.Select();
    Console.WriteLine($"gists-workspace: selected {description} through UIA because an invisible foreign window owned the system foreground queue.");
}

static void FocusForKeyboardActivation(Window window, AutomationElement element)
{
    IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
    NativeMethods.ActivateForKeyboard(windowHandle);
    Thread.Sleep(100);
    try
    {
        element.FocusNative();
    }
    catch (COMException)
    {
        element.Focus();
    }

    WaitUntil(
        $"keyboard focus for {GetAutomationId(element)}",
        () => IsElementFocused(element) && NativeMethods.IsForegroundWindow(windowHandle),
        TimeSpan.FromSeconds(3));
    Thread.Sleep(100);
}

static void PrintVisibleAutomationIds(Window window, string context)
{
    Console.WriteLine($"{context}: visible automation ids");
    foreach (AutomationElement element in window.FindAllDescendants().Take(220))
    {
        try
        {
            string automationId = element.AutomationId;
            string name = element.Name;
            if (string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!IsVisible(element))
            {
                continue;
            }

            Console.WriteLine($"  {element.ControlType}: id='{automationId}' name='{name}'");
        }
        catch (Exception)
        {
        }
    }
}

static void CaptureWindow(Window window, string path)
{
    AssertJitHubWindow(window);
    (_, IntPtr windowHandle) = ReadCaptureWindowIdentity(window);
    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
    using Bitmap bitmap = NativeMethods.CaptureWindowSurface(windowHandle);
    ValidateCapturePixels(bitmap);
    bitmap.Save(path, ImageFormat.Png);
}

static void WaitForScreenshotRegionToStabilize(
    Window window,
    Rectangle screenRegion,
    TimeSpan timeout)
{
    string firstPath = Path.Combine(Path.GetTempPath(), $"jithub-avatar-normal-{Guid.NewGuid():N}-1.png");
    string secondPath = Path.Combine(Path.GetTempPath(), $"jithub-avatar-normal-{Guid.NewGuid():N}-2.png");
    try
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            CaptureWindow(window, firstPath);
            Thread.Sleep(200);
            CaptureWindow(window, secondPath);
            if (ScreenshotRegionPixelsEqual(
                    firstPath,
                    secondPath,
                    NativeMethods.GetPhysicalWindowBounds(GetNativeWindowHandle(window)),
                    screenRegion))
                return;

            Thread.Sleep(100);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new InvalidOperationException("The comment avatar did not settle into its normal visual state before hover.");
    }
    finally
    {
        TryDeleteFile(firstPath);
        TryDeleteFile(secondPath);
    }
}

static bool ScreenshotRegionPixelsEqual(
    string firstPath,
    string secondPath,
    Rectangle captureBounds,
    Rectangle screenRegion)
{
    using var first = new Bitmap(firstPath);
    using var second = new Bitmap(secondPath);
    if (first.Width != second.Width || first.Height != second.Height)
        return false;

    int left = Math.Max(0, screenRegion.Left - captureBounds.Left - 2);
    int top = Math.Max(0, screenRegion.Top - captureBounds.Top - 2);
    int right = Math.Min(first.Width, screenRegion.Right - captureBounds.Left + 2);
    int bottom = Math.Min(first.Height, screenRegion.Bottom - captureBounds.Top + 2);
    if (right <= left || bottom <= top)
        return false;

    for (int y = top; y < bottom; y++)
    {
        for (int x = left; x < right; x++)
        {
            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb())
                return false;
        }
    }

    return true;
}

static void TryDeleteFile(string path)
{
    try
    {
        if (File.Exists(path))
            File.Delete(path);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static void CaptureElement(Window window, AutomationElement element, string path)
{
    CaptureVerifiedScreenRegion(window, element.BoundingRectangle, path);
}

static void CaptureWindowWithPopups(Window window, string path)
{
    CaptureVerifiedScreenRegion(
        window,
        NativeMethods.GetPhysicalWindowBounds(GetNativeWindowHandle(window)),
        path,
        preservePopupForeground: true);
}

static void CaptureMarkdownHostWindow(
    Window window,
    MarkdownLifecycleTarget target,
    string path)
{
    if (string.IsNullOrWhiteSpace(target.LauncherControlAutomationId))
    {
        CaptureWindow(window, path);
    }
    else
    {
        CaptureWindowWithPopups(window, path);
    }
}

static void CaptureVerifiedScreenRegion(
    Window window,
    Rectangle bounds,
    string path,
    bool preservePopupForeground = false)
{
    AssertJitHubWindow(window);
    (int processId, IntPtr windowHandle) = ReadCaptureWindowIdentity(window);
    // Resizing, opening a picker, or the test runner itself can briefly take foreground
    // ownership. Re-establish the app as the capture target immediately before the
    // native ownership check; that check still refuses the capture if activation fails.
    try
    {
        if (!preservePopupForeground)
        {
            window.SetForeground();
        }
    }
    catch (COMException)
    {
    }
    Thread.Sleep(100);
    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);

    var captureBounds = new Rectangle(
        bounds.X,
        bounds.Y,
        Math.Max(1, bounds.Width),
        Math.Max(1, bounds.Height));
    IReadOnlyList<IntPtr>? hiddenCaptureOverlays = null;
    InvalidOperationException? lastOwnershipError = null;
    for (int attempt = 0; attempt < 5 && hiddenCaptureOverlays is null; attempt++)
    {
        if (!preservePopupForeground)
        {
            NativeMethods.ActivateForKeyboard(windowHandle);
            TryActivateWindow(window);
        }
        Thread.Sleep(100 + (attempt * 50));
        try
        {
            hiddenCaptureOverlays = NativeMethods.PrepareExclusiveScreenCapture(
                windowHandle,
                processId,
                captureBounds);
        }
        catch (InvalidOperationException ex)
        {
            lastOwnershipError = ex;
        }
    }

    if (hiddenCaptureOverlays is null)
    {
        throw new InvalidOperationException(
            "Could not establish exclusive JitHub foreground ownership for screenshot capture.",
            lastOwnershipError);
    }

    try
    {
        using var bitmap = new Bitmap(captureBounds.Width, captureBounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(captureBounds.Location, Point.Empty, captureBounds.Size, CopyPixelOperation.SourceCopy);
        }

        ValidateCapturePixels(bitmap);
        bitmap.Save(path, ImageFormat.Png);
    }
    finally
    {
        NativeMethods.RestoreCaptureOverlays(hiddenCaptureOverlays);
    }
}

static (int ProcessId, IntPtr WindowHandle) ReadCaptureWindowIdentity(Window window)
{
    COMException? lastProviderError = null;
    for (int attempt = 0; attempt < 6; attempt++)
    {
        try
        {
            int processId = window.Properties.ProcessId.ValueOrDefault;
            IntPtr windowHandle = new(window.Properties.NativeWindowHandle.ValueOrDefault);
            if (processId > 0 && windowHandle != IntPtr.Zero)
            {
                return (processId, windowHandle);
            }
        }
        catch (COMException ex)
        {
            lastProviderError = ex;
        }

        Thread.Sleep(75);
    }

    throw new InvalidOperationException(
        "The current JitHub window provider did not stabilize before screenshot capture.",
        lastProviderError);
}

static void ValidateCapturePixels(Bitmap bitmap)
{
    int stepX = Math.Max(1, bitmap.Width / 24);
    int stepY = Math.Max(1, bitmap.Height / 18);
    int minimumChannel = byte.MaxValue;
    int maximumChannel = byte.MinValue;
    HashSet<int> sampledColors = [];
    for (int y = 0; y < bitmap.Height; y += stepY)
    {
        for (int x = 0; x < bitmap.Width; x += stepX)
        {
            Color color = bitmap.GetPixel(x, y);
            sampledColors.Add(color.ToArgb());
            minimumChannel = Math.Min(minimumChannel, Math.Min(color.R, Math.Min(color.G, color.B)));
            maximumChannel = Math.Max(maximumChannel, Math.Max(color.R, Math.Max(color.G, color.B)));
        }
    }

    if (sampledColors.Count < 8 || maximumChannel - minimumChannel < 12)
    {
        throw new InvalidOperationException(
            $"Refusing to save an invalid popup capture: colors={sampledColors.Count}, channelRange={maximumChannel - minimumChannel}.");
    }
}

static System.Drawing.Point CenterPoint(AutomationElement element, Window window)
{
    _ = window;
    var bounds = element.BoundingRectangle;
    return new System.Drawing.Point(
        (int)Math.Round(bounds.X + bounds.Width / 2d),
        (int)Math.Round(bounds.Y + bounds.Height / 2d));
}

static string SanitizeFileName(string value)
{
    char[] invalid = Path.GetInvalidFileNameChars();
    var builder = new StringBuilder(value.Length);
    foreach (char ch in value)
    {
        builder.Append(invalid.Contains(ch) ? '_' : ch);
    }

    return builder.ToString();
}

static bool IsVisible(AutomationElement? element)
{
    if (element is null)
    {
        return false;
    }

    try
    {
        var bounds = element.BoundingRectangle;
        return !element.Properties.IsOffscreen.ValueOrDefault && bounds.Width > 0.5 && bounds.Height > 0.5;
    }
    catch
    {
        return false;
    }
}

static AutomationElement? FindElementInWindowOrDialog(Window window, UIA3Automation automation, string automationId)
{
    AutomationElement? inWindow = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
    if (IsVisible(inWindow))
    {
        return inWindow;
    }

    try
    {
        foreach (Window modalWindow in window.ModalWindows)
        {
            AutomationElement? inModal = modalWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (IsVisible(inModal))
            {
                return inModal;
            }
        }
    }
    catch
    {
    }

    try
    {
        AutomationElement[] matches = automation.GetDesktop().FindAllDescendants(cf => cf.ByAutomationId(automationId));
        foreach (AutomationElement match in matches)
        {
            if (IsVisible(match) && IsInsideWindowBounds(match, window))
            {
                return match;
            }
        }
    }
    catch
    {
    }

    return inWindow;
}

static bool IsInsideWindowBounds(AutomationElement element, Window window)
{
    try
    {
        var elementBounds = element.BoundingRectangle;
        var windowBounds = window.BoundingRectangle;
        if (elementBounds.Width <= 0 || elementBounds.Height <= 0)
        {
            return false;
        }

        return elementBounds.X >= windowBounds.X
            && elementBounds.Y >= windowBounds.Y
            && elementBounds.X + elementBounds.Width <= windowBounds.X + windowBounds.Width
            && elementBounds.Y + elementBounds.Height <= windowBounds.Y + windowBounds.Height;
    }
    catch
    {
        return false;
    }
}

static bool IsInsideElementBounds(AutomationElement element, AutomationElement container, double tolerance = 0)
{
    try
    {
        var elementBounds = element.BoundingRectangle;
        var containerBounds = container.BoundingRectangle;
        if (elementBounds.Width <= 0 || elementBounds.Height <= 0 || containerBounds.Width <= 0 || containerBounds.Height <= 0)
        {
            return false;
        }

        return elementBounds.X >= containerBounds.X - tolerance
            && elementBounds.Y >= containerBounds.Y - tolerance
            && elementBounds.X + elementBounds.Width <= containerBounds.X + containerBounds.Width + tolerance
            && elementBounds.Y + elementBounds.Height <= containerBounds.Y + containerBounds.Height + tolerance;
    }
    catch
    {
        return false;
    }
}

static bool IsHorizontallyCentered(AutomationElement element, Window window, double tolerance)
{
    try
    {
        var elementBounds = element.BoundingRectangle;
        var windowBounds = window.BoundingRectangle;
        double elementCenter = elementBounds.X + elementBounds.Width / 2d;
        double windowCenter = windowBounds.X + windowBounds.Width / 2d;
        return Math.Abs(elementCenter - windowCenter) <= tolerance;
    }
    catch
    {
        return false;
    }
}

static AutomationElement? FindShellSearchTextBox(Window window)
{
    AutomationElement? byId = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchTextBox"));
    if (IsVisible(byId))
    {
        return byId;
    }

    return window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
        .Where(IsVisible)
        .OrderByDescending(element => element.BoundingRectangle.Width)
        .FirstOrDefault();
}

static string GetElementName(AutomationElement element)
{
    try
    {
        return element.Name;
    }
    catch
    {
        return string.Empty;
    }
}

static string GetAutomationId(AutomationElement element)
{
    try
    {
        return element.Properties.AutomationId.ValueOrDefault ?? string.Empty;
    }
    catch
    {
        return string.Empty;
    }
}

static string GetFocusedAutomationId(UIA3Automation automation)
{
    try
    {
        return GetAutomationId(automation.FocusedElement());
    }
    catch
    {
        return string.Empty;
    }
}

static string GetTextBoxText(AutomationElement searchBox)
{
    try
    {
        return searchBox.AsTextBox().Text ?? string.Empty;
    }
    catch
    {
        return string.Empty;
    }
}

static bool IsElementFocused(AutomationElement? element)
{
    if (element is null)
    {
        return false;
    }

    try
    {
        return element.Properties.HasKeyboardFocus.ValueOrDefault;
    }
    catch
    {
        return false;
    }
}

static bool IsMarkdownHostOrDescendantFocused(Window window, AutomationElement host)
    => IsElementOrDescendantFocused(window, host);

static bool IsElementOrDescendantFocused(Window window, AutomationElement element)
{
    if (IsElementFocused(element))
    {
        return true;
    }

    try
    {
        return IsInsideElementBounds(window.Automation.FocusedElement(), element, tolerance: 1);
    }
    catch
    {
        return false;
    }
}

static void ActivateWindowForMarkdownHost(Window window, MarkdownLifecycleTarget target)
{
    if (string.IsNullOrWhiteSpace(target.LauncherControlAutomationId))
    {
        IntPtr windowHandle = GetNativeWindowHandle(window);
        NativeMethods.ActivateForKeyboard(windowHandle);
        WaitUntil(
            "Markdown pointer target window foreground activation",
            () => NativeMethods.IsForegroundWindow(windowHandle),
            TimeSpan.FromSeconds(3));
        Thread.Sleep(80);
    }
}

static void FocusMarkdownHostIfInline(AutomationElement host, MarkdownLifecycleTarget target)
{
    if (string.IsNullOrWhiteSpace(target.LauncherControlAutomationId))
    {
        host.FocusNative();
    }
}

static bool IsSearchActuallyFocused(UIA3Automation automation, AutomationElement searchBox)
{
    if (IsElementFocused(searchBox))
    {
        return true;
    }

    try
    {
        AutomationElement focused = automation.FocusedElement();
        return string.Equals(GetAutomationId(focused), "ShellSearchTextBox", StringComparison.Ordinal);
    }
    catch
    {
        return false;
    }
}

static string GetFocusedAutomationDescription(UIA3Automation automation)
{
    try
    {
        AutomationElement focused = automation.FocusedElement();
        return $"id='{GetAutomationId(focused)}', name='{focused.Properties.Name.ValueOrDefault}', " +
            $"type='{focused.ControlType}', bounds='{focused.BoundingRectangle}'";
    }
    catch (Exception exception)
    {
        return $"unavailable ({exception.GetType().Name})";
    }
}

static bool IsFocusedElementWithin(UIA3Automation automation, AutomationElement ancestor)
{
    try
    {
        AutomationElement? candidate = automation.FocusedElement();
        ITreeWalker walker = automation.TreeWalkerFactory.GetRawViewWalker();
        for (int depth = 0; candidate is not null && depth < 128; depth++)
        {
            if (candidate.Equals(ancestor))
            {
                return true;
            }

            candidate = walker.GetParent(candidate);
        }

        return false;
    }
    catch
    {
        return false;
    }
}

static AutomationElement GetShellNavigationElement(Window window, string automationId)
{
    AutomationElement? navigation = null;
    AutomationElement? drawerButton = null;
    WaitUntil(
        $"stable shell navigation chrome for {automationId}",
        () =>
        {
            navigation = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (IsVisible(navigation) && IsInsideWindowBounds(navigation!, window))
            {
                return true;
            }

            drawerButton = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellRailDrawerButton"));
            return IsVisible(drawerButton) && IsInsideWindowBounds(drawerButton!, window);
        },
        TimeSpan.FromSeconds(8));

    if (IsVisible(navigation) && IsInsideWindowBounds(navigation!, window))
    {
        return navigation!;
    }

    InvokeOrClick(drawerButton!);
    AutomationElement result = WaitForElement(
        automationId,
        () =>
        {
            AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            return IsVisible(candidate) && IsInsideWindowBounds(candidate!, window) ? candidate : null;
        },
        TimeSpan.FromSeconds(8));
    Thread.Sleep(350);
    return window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) ?? result;
}

static bool WaitUntilAvailable(Func<bool> predicate, TimeSpan timeout)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    do
    {
        try
        {
            if (predicate())
            {
                return true;
            }
        }
        catch
        {
        }

        Thread.Sleep(100);
    }
    while (stopwatch.Elapsed < timeout);

    return false;
}

static AutomationElement WaitForElement(string name, Func<AutomationElement?> findElement, TimeSpan timeout)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    AutomationElement? element;
    do
    {
        try
        {
            element = findElement();
        }
        catch
        {
            element = null;
        }

        if (element is not null)
        {
            return element;
        }

        Thread.Sleep(100);
    }
    while (stopwatch.Elapsed < timeout);

    throw new InvalidOperationException($"Timed out waiting for {name}.");
}

static void WaitUntil(string name, Func<bool> predicate, TimeSpan timeout)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    do
    {
        try
        {
            if (predicate())
            {
                return;
            }
        }
        catch (COMException) when (stopwatch.Elapsed < timeout)
        {
            // WinUI can briefly invalidate an automation peer while a dialog or flyout closes.
        }

        Thread.Sleep(100);
    }
    while (stopwatch.Elapsed < timeout);

    throw new InvalidOperationException($"Timed out waiting for {name}.");
}

static void AssertProbe(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void OpenRepoCodeFileTreeDrawer(Window window, AutomationElement opener)
{
    InvokeOrClick(opener);
    bool opened = WaitUntilAvailable(
        () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer")).Any(IsVisible),
        TimeSpan.FromSeconds(1));
    if (!opened)
    {
        FocusForKeyboardActivation(window, opener);
        Keyboard.Press(VirtualKeyShort.SPACE);
        opened = WaitUntilAvailable(
            () => window.FindAllDescendants(cf => cf.ByAutomationId("RepoCodeLeftDrawer")).Any(IsVisible),
            TimeSpan.FromSeconds(1));
    }
    if (!opened)
    {
        Keyboard.Press(VirtualKeyShort.ENTER);
    }
}

static void InvokeOrClick(AutomationElement element)
{
    if (element.Patterns.Invoke.IsSupported)
    {
        try
        {
            element.Patterns.Invoke.Pattern.Invoke();
            return;
        }
        catch (Exception)
        {
            // Some reparented WinUI controls briefly advertise a stale Invoke provider.
            // Coordinate click remains a valid UIA interaction once the drawer settles.
        }
    }

    if (element.Patterns.Toggle.IsSupported)
    {
        try
        {
            element.Patterns.Toggle.Pattern.Toggle();
            return;
        }
        catch (Exception)
        {
            // Fall through to physical input if a recycled toggle provider is stale.
        }
    }

    element.Click();
}

static void SelectAutomationItem(AutomationElement element, string description)
{
    if (element.Patterns.SelectionItem.IsSupported)
    {
        element.Patterns.SelectionItem.Pattern.Select();
        return;
    }

    if (element.Patterns.Invoke.IsSupported)
    {
        element.Patterns.Invoke.Pattern.Invoke();
        return;
    }

    if (element.Patterns.Toggle.IsSupported)
    {
        element.Patterns.Toggle.Pattern.Toggle();
        return;
    }

    if (element.Patterns.LegacyIAccessible.IsSupported)
    {
        element.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
        return;
    }

    throw new InvalidOperationException($"{description} omitted a semantic UI Automation activation pattern.");
}

static AutomationElement AssertNamedAutomationElement(Window window, string automationId, ControlType controlType)
{
    AutomationElement element = WaitForElement(
        automationId,
        () => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
        TimeSpan.FromSeconds(8));
    AssertProbe(IsVisible(element), $"{automationId} was not visible in the live UIA tree.");
    AssertProbe(element.ControlType == controlType, $"{automationId} exposed {element.ControlType} instead of {controlType}.");
    AssertProbe(!string.IsNullOrWhiteSpace(element.Name), $"{automationId} did not expose a meaningful accessible name.");
    return element;
}

static void RevealForInteraction(AutomationElement element, string description)
{
    if (IsVisible(element))
    {
        return;
    }

    if (element.Patterns.ScrollItem.IsSupported)
    {
        element.Patterns.ScrollItem.Pattern.ScrollIntoView();
    }
    else
    {
        element.FocusNative();
    }

    WaitUntil($"{description} to become visible", () => IsVisible(element), TimeSpan.FromSeconds(5));
}

static void MoveMouseToEmptyTitleBar(Window window, AutomationElement searchBox)
{
    var windowBounds = window.BoundingRectangle;
    var searchBounds = searchBox.BoundingRectangle;
    double logicalX = Math.Min(windowBounds.X + windowBounds.Width - 340, searchBounds.X + searchBounds.Width + 160);
    logicalX = Math.Max(logicalX, searchBounds.X + searchBounds.Width + 80);
    logicalX = Math.Min(logicalX, windowBounds.X + windowBounds.Width - 260);
    double logicalY = windowBounds.Y + 30;

    Mouse.MoveTo(new System.Drawing.Point(
        (int)Math.Round(logicalX),
        (int)Math.Round(logicalY)));
}

static void PressCtrlK()
{
    using (Keyboard.Pressing(VirtualKeyShort.LCONTROL))
    {
        Thread.Sleep(100);
        Keyboard.Press(VirtualKeyShort.KEY_K);
        Thread.Sleep(100);
    }
}

static bool HasCtrlKTooltip(UIA3Automation automation)
{
    AutomationElement[] tooltips = automation.GetDesktop().FindAllDescendants(cf => cf.ByControlType(ControlType.ToolTip));
    foreach (AutomationElement tooltip in tooltips)
    {
        var text = new StringBuilder(GetElementName(tooltip));
        foreach (AutomationElement childText in tooltip.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
        {
            text.Append(' ');
            text.Append(GetElementName(childText));
        }

        string tooltipText = text.ToString();
        if (tooltipText.Contains("Ctrl", StringComparison.OrdinalIgnoreCase)
            && tooltipText.Contains("K", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

static Application CreateProbeApplication(CaptureOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.AttachProcess))
    {
        string attachValue = options.AttachProcess.Trim();
        if (int.TryParse(attachValue, out int processId))
        {
            return Application.Attach(processId);
        }

        Process[] matches = Process.GetProcessesByName(attachValue);
        try
        {
            Process[] visibleMatches = matches
                .Where(static process => process.MainWindowHandle != IntPtr.Zero)
                .ToArray();
            if (visibleMatches.Length == 0)
            {
                throw new InvalidOperationException($"Could not find a visible process '{attachValue}' to attach automation.");
            }

            if (visibleMatches.Length != 1)
            {
                string processIds = string.Join(", ", visibleMatches.Select(static process => process.Id));
                throw new InvalidOperationException(
                    $"Process name '{attachValue}' is ambiguous ({processIds}). Pass --attach-process=<pid> instead.");
            }

            return Application.Attach(visibleMatches[0].Id);
        }
        finally
        {
            foreach (Process match in matches)
            {
                match.Dispose();
            }
        }
    }

    return LaunchApplication(options.AppPath);
}

static Application LaunchApplication(string appPath, params string[] arguments) =>
    LaunchApplicationWithDataRoot(appPath, GetAutomationDataRoot(), killExisting: true, arguments);

static Application LaunchApplicationWithDataRoot(
    string appPath,
    string dataRoot,
    bool killExisting,
    params string[] arguments)
{
    if (killExisting)
    {
        KillExistingApplicationInstances(appPath);
    }

    Directory.CreateDirectory(dataRoot);
    DateTime launchStartedUtc = DateTime.UtcNow;
    HashSet<int> excludedProcessIds = GetApplicationProcessIds(appPath);
    var processStartInfo = new ProcessStartInfo(appPath)
    {
        WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory,
        UseShellExecute = false
    };

    foreach (string argument in arguments)
    {
        processStartInfo.ArgumentList.Add(argument);
    }

    AddPreviewEnvironment(processStartInfo, arguments, dataRoot);
    Application launched = Application.Launch(processStartInfo);
    int launchedProcessId = TryGetApplicationProcessId(launched);
    AutomationLifecycleLog.Write(
        "launch-started",
        $"pid={launchedProcessId}; path={Path.GetFullPath(appPath)}; args={string.Join(' ', arguments)}");
    var launchedRegistration = new AutomationApplicationRegistration(
        Path.GetFullPath(appPath),
        launchedProcessId,
        launchStartedUtc,
        excludedProcessIds);
    AutomationApplicationPathRegistry.Register(
        launched,
        appPath,
        launchedProcessId,
        launchStartedUtc,
        excludedProcessIds);

    int stableProcessId;
    try
    {
        stableProcessId = WaitForVisibleApplicationProcess(
            appPath,
            launchedProcessId,
            launchStartedUtc,
            excludedProcessIds,
            TimeSpan.FromSeconds(15));
    }
    catch
    {
        bool terminated = TryTerminateOwnedProcess(launchedRegistration);
        AutomationLifecycleLog.Write(
            "launch-failed-cleanup",
            $"pid={launchedProcessId}; terminated={terminated}; path={Path.GetFullPath(appPath)}");
        AutomationApplicationPathRegistry.Forget(launched);
        launched.Dispose();
        throw;
    }

    if (stableProcessId == launchedProcessId)
    {
        return launched;
    }

    AutomationApplicationPathRegistry.Forget(launched);
    try
    {
        launched.Dispose();
    }
    catch
    {
    }

    Application attached = Application.Attach(stableProcessId);
    AutomationApplicationPathRegistry.Register(
        attached,
        appPath,
        stableProcessId,
        launchStartedUtc,
        excludedProcessIds);
    AutomationLifecycleLog.Write(
        "launch-handoff",
        $"launchedPid={launchedProcessId}; windowPid={stableProcessId}; path={Path.GetFullPath(appPath)}");
    return attached;
}

static int TryGetApplicationProcessId(Application application)
{
    try
    {
        return application.ProcessId;
    }
    catch (InvalidOperationException)
    {
        return 0;
    }
}

static int WaitForVisibleApplicationProcess(
    string appPath,
    int preferredProcessId,
    DateTime launchStartedUtc,
    IReadOnlySet<int> excludedProcessIds,
    TimeSpan timeout)
{
    string processName = Path.GetFileNameWithoutExtension(appPath);
    string normalizedAppPath = Path.GetFullPath(appPath);
    Stopwatch stopwatch = Stopwatch.StartNew();
    do
    {
        bool preferredIsAlive = false;
        if (TryGetOwnedProcess(preferredProcessId, normalizedAppPath, launchStartedUtc, out Process? preferred) &&
            preferred is not null)
        {
            using (preferred)
            {
                preferredIsAlive = true;
                if (preferred.MainWindowHandle != IntPtr.Zero)
                {
                    return preferredProcessId;
                }
            }
        }

        if (preferredIsAlive)
        {
            Thread.Sleep(100);
            continue;
        }

        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (excludedProcessIds.Contains(process.Id) ||
                    !IsProcessForApplication(process, normalizedAppPath) ||
                    process.HasExited ||
                    process.StartTime.ToUniversalTime() < launchStartedUtc.AddSeconds(-1))
                {
                    continue;
                }

                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return process.Id;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        Thread.Sleep(100);
    }
    while (stopwatch.Elapsed < timeout);

    throw new TimeoutException(
        $"Timed out after {timeout.TotalSeconds:0.#} seconds waiting for a visible JitHub window " +
        $"from owned process {preferredProcessId} at '{normalizedAppPath}'.");
}

static void AddPreviewEnvironment(
    ProcessStartInfo processStartInfo,
    IEnumerable<string> arguments,
    string? dataRoot = null)
{
    processStartInfo.Environment["JITHUB_AUTOMATION_DATA_ROOT"] = dataRoot ?? GetAutomationDataRoot();
    foreach (string argument in arguments)
    {
        if (argument.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_PAGE"] = argument[7..];
        }
        else if (argument.StartsWith("--scenario=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_SCENARIO"] = argument[11..];
        }
        else if (argument.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_THEME"] = argument[8..];
        }
        else if (argument.StartsWith("--palette=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_PALETTE"] = argument[10..];
        }
        else if (argument.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_REPOSITORY"] = argument[7..];
        }
        else if (argument.StartsWith("--repository=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_REPOSITORY"] = argument[13..];
        }
        else if (argument.StartsWith("--branch=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_PREVIEW_BRANCH"] = argument[9..];
        }
        else if (string.Equals(argument, "--high-contrast", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_AUTOMATION_HIGH_CONTRAST"] = "1";
        }
        else if (string.Equals(argument, "--large-commit", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_AUTOMATION_LARGE_COMMIT"] = "1";
        }
        else if (string.Equals(argument, "--network-disabled", StringComparison.OrdinalIgnoreCase))
        {
            const string blockedProxy = "http://127.0.0.1:9";
            processStartInfo.Environment["JITHUB_AUTOMATION_NETWORK_DISABLED"] = "1";
            processStartInfo.Environment["HTTP_PROXY"] = blockedProxy;
            processStartInfo.Environment["HTTPS_PROXY"] = blockedProxy;
            processStartInfo.Environment["ALL_PROXY"] = blockedProxy;
            processStartInfo.Environment["NO_PROXY"] = string.Empty;
        }
        else if (string.Equals(argument, "--markdown-lifecycle-fixture", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_MARKDOWN_LIFECYCLE_FIXTURE"] = "1";
            processStartInfo.Environment["JITHUB_AUTOMATION_TEXT_SCALE_FACTOR"] = "1.5";
        }
        else if (argument.StartsWith("--markdown-lifecycle-host=", StringComparison.OrdinalIgnoreCase))
        {
            processStartInfo.Environment["JITHUB_MARKDOWN_LIFECYCLE_HOST"] = argument[26..];
        }
    }
}

static string[] BuildLaunchArguments(CaptureTarget target, string theme, string repoFullName)
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

static void PrepareTargetForCapture(Window window, CaptureTarget target)
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

static bool ClickReadmeByCoordinates(Window window)
{
    var windowBounds = window.BoundingRectangle;
    Mouse.DoubleClick(new System.Drawing.Point(
        windowBounds.X + 220,
        windowBounds.Y + 430));
    return true;
}

static int GetSettleDelay(CaptureTarget target)
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

static bool IsPullRequestTarget(CaptureTarget target) =>
    string.Equals(target.Page, "repo-pulls", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(target.Page, "repo-pull-requests", StringComparison.OrdinalIgnoreCase);

static bool IsRepoCodeTarget(CaptureTarget target) =>
    string.Equals(target.Page, "repo-code", StringComparison.OrdinalIgnoreCase);

static bool IsAppPreviewTarget(CaptureTarget target) =>
    target.Page.StartsWith("repo", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(target.Page, "home", StringComparison.OrdinalIgnoreCase);

static void TryClose(Application app)
{
    if (!AutomationApplicationPathRegistry.TryGet(app, out AutomationApplicationRegistration? registration) ||
        registration is null)
    {
        int unknownProcessId = TryGetApplicationProcessId(app);
        AutomationLifecycleLog.Write(
            "close-incomplete",
            $"pid={unknownProcessId}; result=ownership-not-proven");
        throw new InvalidOperationException(
            $"Refusing to report a successful close for unregistered process {unknownProcessId}.");
    }

    int processId = registration.ProcessId;
    if (!TryGetOwnedProcess(
            registration.ProcessId,
            registration.AppPath,
            registration.LaunchStartedUtc,
            out Process? ownedProcess) ||
        ownedProcess is null)
    {
        try
        {
            using Process observedProcess = Process.GetProcessById(processId);
            if (observedProcess.HasExited)
            {
                AutomationLifecycleLog.Write(
                    "close-confirmed",
                    $"pid={processId}; result=already-exited");
                AutomationApplicationPathRegistry.Forget(app);
                return;
            }
        }
        catch (ArgumentException)
        {
            AutomationLifecycleLog.Write(
                "close-confirmed",
                $"pid={processId}; result=already-exited");
            AutomationApplicationPathRegistry.Forget(app);
            return;
        }

        AutomationLifecycleLog.Write(
            "close-incomplete",
            $"pid={processId}; result=owned-process-not-provable");
        throw new InvalidOperationException(
            $"Could not prove the exact owned process {processId} before requesting close.");
    }

    IntPtr processExitHandle = IntPtr.Zero;
    using (ownedProcess)
    try
    {
        processExitHandle = NativeMethods.OpenProcessExitHandle(processId);
        if (!NativeMethods.TryRequestGracefulClose(processId, out IntPtr windowHandle))
        {
            AutomationLifecycleLog.Write(
                "close-request-failed",
                $"pid={processId}; result=owned-top-level-window-not-found-or-close-rejected");
            throw new InvalidOperationException(
                $"Could not request a graceful close for the top-level window owned by process {processId}.");
        }

        AutomationLifecycleLog.Write(
            "close-requested",
            $"pid={processId}; hwnd=0x{windowHandle.ToInt64():X}; transport=WM_CLOSE");

        bool exited = ownedProcess.HasExited || ownedProcess.WaitForExit(TimeSpan.FromSeconds(12));
        if (!exited)
        {
            AutomationLifecycleLog.Write(
                "close-incomplete",
                $"pid={processId}; result=graceful-timeout");
            throw new InvalidOperationException(
                $"Owned process {processId} did not exit after its graceful close request.");
        }

        int exitCode = unchecked((int)NativeMethods.GetProcessExitCode(processExitHandle));
        if (exitCode != 0)
        {
            AutomationLifecycleLog.Write(
                "close-failed",
                $"pid={processId}; result=abnormal-exit; exitCode={exitCode}");
            throw new InvalidOperationException(
                $"Owned process {processId} exited abnormally with code {exitCode}.");
        }

        AutomationLifecycleLog.Write(
            "close-completed",
            $"pid={processId}; result=graceful; exitCode={exitCode}");
        AutomationApplicationPathRegistry.Forget(app);
    }
    catch (Exception exception)
    {
        if (exception is InvalidOperationException &&
            exception.Message.StartsWith("Owned process", StringComparison.Ordinal))
        {
            throw;
        }

        AutomationLifecycleLog.Write(
            "close-request-failed",
            $"pid={processId}; exception={exception.GetType().Name}; message={exception.Message}");
        throw new InvalidOperationException(
            $"Graceful close failed for owned process {processId}.",
            exception);
    }
    finally
    {
        NativeMethods.CloseProcessExitHandle(processExitHandle);
    }
}

static void KillExistingApplicationInstances(string appPath)
{
    List<string> cleanupFailures = [];
    foreach (AutomationApplicationRegistration registration in
        AutomationApplicationPathRegistry.GetOwnedRegistrations(appPath))
    {
        if (WaitForProcessExit(registration.ProcessId, TimeSpan.FromSeconds(12)))
        {
            AutomationApplicationPathRegistry.Forget(registration.ProcessId);
            continue;
        }

        if (TryTerminateOwnedProcess(registration))
        {
            AutomationLifecycleLog.Write(
                "owned-process-cleanup-failed",
                $"pid={registration.ProcessId}; path={registration.AppPath}; result=forced-termination");
            cleanupFailures.Add($"forced termination of owned process {registration.ProcessId}");
        }
        else
        {
            AutomationLifecycleLog.Write(
                "owned-process-cleanup-failed",
                $"pid={registration.ProcessId}; path={registration.AppPath}; result=process-not-provable-or-termination-failed");
            cleanupFailures.Add($"unprovable cleanup of owned process {registration.ProcessId}");
        }

        AutomationApplicationPathRegistry.Forget(registration.ProcessId);
    }

    if (cleanupFailures.Count > 0)
    {
        throw new InvalidOperationException(
            "Automation required failed process cleanup: " + string.Join(", ", cleanupFailures));
    }
}

static HashSet<int> GetApplicationProcessIds(string appPath)
{
    string processName = Path.GetFileNameWithoutExtension(appPath);
    if (string.IsNullOrWhiteSpace(processName))
    {
        return [];
    }

    HashSet<int> processIds = [];
    foreach (Process process in Process.GetProcessesByName(processName))
    {
        try
        {
            if (IsProcessForApplication(process, appPath) && !process.HasExited)
            {
                processIds.Add(process.Id);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    return processIds;
}

static bool TryGetOwnedProcess(
    int processId,
    string appPath,
    DateTime launchStartedUtc,
    out Process? process)
{
    process = null;
    if (processId <= 0)
    {
        return false;
    }

    try
    {
        Process candidate = Process.GetProcessById(processId);
        candidate.Refresh();
        if (candidate.HasExited ||
            !IsProcessForApplication(candidate, appPath) ||
            candidate.StartTime.ToUniversalTime() < launchStartedUtc.AddSeconds(-1))
        {
            candidate.Dispose();
            return false;
        }

        process = candidate;
        return true;
    }
    catch
    {
        return false;
    }
}

static bool WaitForProcessExit(int processId, TimeSpan timeout)
{
    if (processId <= 0)
    {
        return false;
    }

    try
    {
        using Process process = Process.GetProcessById(processId);
        return process.HasExited || process.WaitForExit((int)Math.Ceiling(timeout.TotalMilliseconds));
    }
    catch (ArgumentException)
    {
        // Process.GetProcessById throws when the owned process has already exited.
        // Absence is the successful state this cleanup wait is proving.
        return true;
    }
    catch (InvalidOperationException)
    {
        return true;
    }
}

static bool TryTerminateOwnedProcess(AutomationApplicationRegistration registration)
{
    if (!TryGetOwnedProcess(
            registration.ProcessId,
            registration.AppPath,
            registration.LaunchStartedUtc,
            out Process? process) ||
        process is null)
    {
        return false;
    }

    using (process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(5000);
        }
        catch
        {
            return false;
        }
    }
}

static bool IsProcessForApplication(Process process, string appPath)
{
    try
    {
        string? processPath = process.MainModule?.FileName;
        return !string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(
                Path.GetFullPath(processPath),
                Path.GetFullPath(appPath),
                StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}

static void WriteManifest(string outputDirectory, IReadOnlyList<CaptureResult> captures)
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

static string GetAutomationDataRoot() => Path.Combine(
    Path.GetTempPath(),
    "JitHub.WinUI.Automation",
    $"run-{Environment.ProcessId}");

static void PrepareAutomationDataRoot()
{
    string root = GetAutomationDataRoot();
    bool preserveDataRoot = string.Equals(
        Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_PRESERVE_DATA_ROOT"),
        "1",
        StringComparison.Ordinal);
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }

    Directory.CreateDirectory(root);
    if (preserveDataRoot)
    {
        return;
    }

    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
        }
    };
}

internal sealed record CaptureResult(string Theme, string Name, string FileName);

internal sealed record DiagnosticProbeEvent(
    int LineIndex,
    string Name,
    IReadOnlyDictionary<string, string> Properties);

internal sealed record CaptureTarget(string Name, string Page, string? Scenario, string? AutomationId);

internal static partial class NativeMethods
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;

    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint GwOwner = 4;
    private const uint GwHwndPrev = 3;
    private const uint GaRoot = 2;
    private const uint WmClose = 0x0010;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int SwRestore = 9;
    private const uint SpiGetWorkArea = 0x0030;
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x00000001;
    private const uint KeyEventKeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeHighContrast
    {
        internal uint Size;
        internal uint Flags;
        internal IntPtr DefaultScheme;
    }

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    internal static void MoveCursorPhysical(Point point)
    {
        _ = SetCursorPos(point.X, point.Y);
    }

    internal static Point GetCursorPositionPhysical() =>
        GetCursorPos(out NativePoint point)
            ? new Point(point.X, point.Y)
            : Point.Empty;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref NativeHighContrast highContrast,
        uint updateFlags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoForRect(
        uint action,
        uint parameter,
        out NativeRect value,
        uint updateFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        out NativeRect attributeValue,
        int attributeSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr processHandle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    internal static bool TryRequestGracefulClose(int processId, out IntPtr windowHandle)
    {
        windowHandle = IntPtr.Zero;
        if (processId <= 0)
        {
            return false;
        }

        IntPtr selectedWindow = IntPtr.Zero;
        long selectedArea = -1;
        bool enumerationCompleted = EnumWindows(
            (candidate, parameter) =>
            {
                _ = parameter;
                _ = GetWindowThreadProcessId(candidate, out uint candidateProcessId);
                if (candidateProcessId != (uint)processId ||
                    !IsWindowVisible(candidate) ||
                    GetWindow(candidate, GwOwner) != IntPtr.Zero)
                {
                    return true;
                }

                long area = 0;
                if (GetWindowRect(candidate, out NativeRect bounds))
                {
                    long width = Math.Max(0L, (long)bounds.Right - bounds.Left);
                    long height = Math.Max(0L, (long)bounds.Bottom - bounds.Top);
                    area = width * height;
                }

                if (selectedWindow == IntPtr.Zero || area > selectedArea)
                {
                    selectedWindow = candidate;
                    selectedArea = area;
                }

                return true;
            },
            IntPtr.Zero);

        if (!enumerationCompleted || selectedWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(selectedWindow, out uint selectedProcessId);
        if (selectedProcessId != (uint)processId || !IsWindowVisible(selectedWindow))
        {
            return false;
        }

        windowHandle = selectedWindow;
        return PostMessage(selectedWindow, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    internal static bool IsOwnedTopLevelWindow(IntPtr candidateWindow, IntPtr ownerWindow)
    {
        if (candidateWindow == IntPtr.Zero || ownerWindow == IntPtr.Zero)
        {
            return false;
        }

        IntPtr candidateRoot = GetAncestor(candidateWindow, GaRoot);
        IntPtr ownerRoot = GetAncestor(ownerWindow, GaRoot);
        return candidateRoot == candidateWindow &&
            ownerRoot != IntPtr.Zero &&
            GetWindow(candidateWindow, GwOwner) == ownerRoot;
    }

    internal static bool TryFindLargestOwnedTopLevelWindow(
        IntPtr ownerWindow,
        out IntPtr ownedWindow)
    {
        ownedWindow = IntPtr.Zero;
        if (ownerWindow == IntPtr.Zero)
        {
            return false;
        }

        IntPtr selectedWindow = IntPtr.Zero;
        long selectedArea = -1;
        bool enumerationCompleted = EnumWindows(
            (candidate, parameter) =>
            {
                _ = parameter;
                if (!IsWindowVisible(candidate) ||
                    !IsOwnedTopLevelWindow(candidate, ownerWindow))
                {
                    return true;
                }

                long area = 0;
                if (GetWindowRect(candidate, out NativeRect bounds))
                {
                    long width = Math.Max(0L, (long)bounds.Right - bounds.Left);
                    long height = Math.Max(0L, (long)bounds.Bottom - bounds.Top);
                    area = width * height;
                }

                if (selectedWindow == IntPtr.Zero || area > selectedArea)
                {
                    selectedWindow = candidate;
                    selectedArea = area;
                }

                return true;
            },
            IntPtr.Zero);

        ownedWindow = selectedWindow;
        return enumerationCompleted && selectedWindow != IntPtr.Zero;
    }

    internal static bool IsHighContrastEnabled()
    {
        NativeHighContrast highContrast = new()
        {
            Size = (uint)Marshal.SizeOf<NativeHighContrast>()
        };
        return SystemParametersInfo(SpiGetHighContrast, highContrast.Size, ref highContrast, 0) &&
            (highContrast.Flags & HcfHighContrastOn) != 0;
    }

    internal static void SendKey(IntPtr windowHandle, VirtualKeyShort key)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("JitHub did not expose a native window handle for directed keyboard input.");
        }

        IntPtr foregroundWindow = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(windowHandle, out _);
        uint foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        bool attachedTarget = targetThread != 0 && targetThread != currentThread &&
            AttachThreadInput(currentThread, targetThread, attach: true);
        bool attachedForeground = foregroundThread != 0 &&
            foregroundThread != currentThread &&
            foregroundThread != targetThread &&
            AttachThreadInput(currentThread, foregroundThread, attach: true);
        try
        {
            _ = ShowWindow(windowHandle, SwRestore);
            _ = BringWindowToTop(windowHandle);
            _ = SetForegroundWindow(windowHandle);
            _ = SetFocus(windowHandle);
            byte virtualKey = unchecked((byte)(ushort)key);
            keybd_event(virtualKey, 0, 0, 0);
            keybd_event(virtualKey, 0, KeyEventKeyUp, 0);
        }
        finally
        {
            if (attachedForeground)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, attach: false);
            }

            if (attachedTarget)
            {
                _ = AttachThreadInput(currentThread, targetThread, attach: false);
            }
        }
    }


    internal static void ActivateForKeyboard(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("JitHub did not expose a native window handle for keyboard activation.");
        }

        IntPtr foregroundWindow = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(windowHandle, out _);
        uint foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        bool attachedTarget = targetThread != 0 && targetThread != currentThread &&
            AttachThreadInput(currentThread, targetThread, attach: true);
        bool attachedForeground = foregroundThread != 0 &&
            foregroundThread != currentThread &&
            foregroundThread != targetThread &&
            AttachThreadInput(currentThread, foregroundThread, attach: true);
        try
        {
            _ = ShowWindow(windowHandle, SwRestore);
            _ = BringWindowToTop(windowHandle);
            _ = SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (attachedForeground)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, attach: false);
            }

            if (attachedTarget)
            {
                _ = AttachThreadInput(currentThread, targetThread, attach: false);
            }
        }
    }

    internal static bool IsForegroundWindow(IntPtr windowHandle)
    {
        IntPtr foregroundRoot = GetAncestor(GetForegroundWindow(), GaRoot);
        IntPtr expectedRoot = GetAncestor(windowHandle, GaRoot);
        return foregroundRoot != IntPtr.Zero && foregroundRoot == expectedRoot;
    }

    internal static bool HasInvisibleForeignForegroundWindow(IntPtr windowHandle)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || IsWindowVisible(foreground))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
        _ = GetWindowThreadProcessId(windowHandle, out uint targetProcessId);
        return foregroundProcessId != 0 && foregroundProcessId != targetProcessId;
    }

    internal static Rectangle GetWorkArea()
    {
        return SystemParametersInfoForRect(SpiGetWorkArea, 0, out NativeRect workArea, 0)
            ? Rectangle.FromLTRB(workArea.Left, workArea.Top, workArea.Right, workArea.Bottom)
            : Rectangle.Empty;
    }

    internal static uint GetWindowDpi(IntPtr windowHandle)
    {
        uint dpi = windowHandle == IntPtr.Zero ? 0 : GetDpiForWindow(windowHandle);
        if (dpi == 0)
        {
            throw new InvalidOperationException("Could not read the JitHub window DPI for website capture.");
        }

        return dpi;
    }

    internal static void EnablePerMonitorV2DpiAwareness()
    {
        IntPtr perMonitorV2 = new(-4);
        if (!SetProcessDpiAwarenessContext(perMonitorV2))
        {
            int error = Marshal.GetLastWin32Error();
            const int errorAccessDenied = 5;
            if (error != errorAccessDenied)
            {
                throw new System.ComponentModel.Win32Exception(
                    error,
                    "The automation harness could not enable per-monitor-v2 DPI awareness.");
            }
        }

        int awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        const int perMonitorAware = 2;
        if (awareness != perMonitorAware)
        {
            throw new InvalidOperationException(
                $"The automation harness requires per-monitor DPI awareness; active awareness={awareness}.");
        }
    }

    internal static IntPtr OpenProcessExitHandle(int processId)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, inheritHandle: false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not retain a native process handle for JitHub process {processId}.");
        }

        return handle;
    }

    internal static uint GetProcessExitCode(IntPtr processHandle)
    {
        if (!GetExitCodeProcess(processHandle, out uint exitCode))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read the native JitHub process exit code.");
        }

        return exitCode;
    }

    internal static void CloseProcessExitHandle(IntPtr processHandle)
    {
        if (processHandle != IntPtr.Zero)
            _ = CloseHandle(processHandle);
    }

    internal static void ResizeWindow(IntPtr windowHandle, int width, int height)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("JitHub did not expose a native window handle for resizing.");
        }

        // A prior interrupted automation run can leave Windows restoring the
        // next top-level window as maximized. MoveWindow reports success in
        // that state but the visible bounds remain maximized, so normalize the
        // native state before applying deterministic responsive dimensions.
        _ = ShowWindow(windowHandle, SwRestore);
        if (!MoveWindow(windowHandle, 0, 0, width, height, repaint: true))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not resize the JitHub window.");
        }
    }

    internal static Rectangle GetWindowBounds(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !GetWindowRect(windowHandle, out NativeRect rect))
        {
            return Rectangle.Empty;
        }

        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    internal static Rectangle GetPhysicalWindowBounds(IntPtr windowHandle)
    {
        const int DwmwaExtendedFrameBounds = 9;
        if (windowHandle != IntPtr.Zero &&
            DwmGetWindowAttribute(
                windowHandle,
                DwmwaExtendedFrameBounds,
                out NativeRect rect,
                Marshal.SizeOf<NativeRect>()) == 0)
        {
            return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        Rectangle fallback = GetWindowBounds(windowHandle);
        if (fallback.IsEmpty)
        {
            throw new InvalidOperationException("Could not read the native JitHub window bounds for screenshot capture.");
        }

        return fallback;
    }

    internal static Bitmap CaptureWindowSurface(IntPtr windowHandle)
    {
        const uint PwRenderFullContent = 0x00000002;
        Rectangle windowBounds = GetWindowBounds(windowHandle);
        Rectangle visibleBounds = GetPhysicalWindowBounds(windowHandle);
        if (windowBounds.IsEmpty || visibleBounds.IsEmpty)
        {
            throw new InvalidOperationException("Could not read JitHub window bounds for window-only capture.");
        }

        using var fullWindow = new Bitmap(
            Math.Max(1, windowBounds.Width),
            Math.Max(1, windowBounds.Height),
            PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(fullWindow))
        {
            IntPtr deviceContext = graphics.GetHdc();
            try
            {
                if (!PrintWindow(windowHandle, deviceContext, PwRenderFullContent))
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not render the JitHub surface for screenshot capture.");
                }
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
        }

        Rectangle crop = Rectangle.Intersect(
            new Rectangle(
                visibleBounds.Left - windowBounds.Left,
                visibleBounds.Top - windowBounds.Top,
                visibleBounds.Width,
                visibleBounds.Height),
            new Rectangle(Point.Empty, fullWindow.Size));
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            throw new InvalidOperationException("The DWM window surface does not intersect the rendered JitHub window.");
        }

        return fullWindow.Clone(crop, PixelFormat.Format32bppArgb);
    }

    internal static IReadOnlyList<IntPtr> PrepareExclusiveScreenCapture(
        IntPtr mainWindowHandle,
        int expectedProcessId,
        Rectangle captureBounds)
    {
        if (mainWindowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("JitHub did not expose a native window handle for popup capture.");
        }

        IntPtr rootWindowHandle = GetAncestor(mainWindowHandle, GaRoot);
        if (rootWindowHandle != IntPtr.Zero)
        {
            mainWindowHandle = rootWindowHandle;
        }

        IntPtr blockingSystemDialog = FindBlockingSystemDialog(captureBounds);
        if (blockingSystemDialog != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(blockingSystemDialog, out uint blockingProcessId);
            throw new InvalidOperationException(
                $"Refusing screenshot capture because a Windows system dialog from process {blockingProcessId} overlaps JitHub.");
        }

        IntPtr foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
        if (foreground == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Refusing popup capture because Windows did not expose a foreground window.");
        }

        if (foregroundProcessId != (uint)expectedProcessId && IsWindowVisible(foreground))
        {
            throw new InvalidOperationException(
                $"Refusing popup capture because the foreground window belongs to process {foregroundProcessId}, not JitHub process {expectedProcessId}.");
        }

        List<IntPtr> hiddenOverlays = [];
        for (IntPtr candidate = GetWindow(mainWindowHandle, GwHwndPrev);
             candidate != IntPtr.Zero;
             candidate = GetWindow(candidate, GwHwndPrev))
        {
            if (!IsWindowVisible(candidate) || !GetWindowRect(candidate, out NativeRect rect))
            {
                continue;
            }

            Rectangle candidateBounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (!captureBounds.IntersectsWith(candidateBounds))
            {
                continue;
            }

            _ = GetWindowThreadProcessId(candidate, out uint candidateProcessId);
            if (candidateProcessId != (uint)expectedProcessId)
            {
                if (IsWindowsShellSurface((int)candidateProcessId, candidate))
                {
                    continue;
                }

                if (IsKnownCaptureOverlay((int)candidateProcessId, candidate))
                {
                    _ = ShowWindow(candidate, SwHide);
                    hiddenOverlays.Add(candidate);
                    continue;
                }

                RestoreCaptureOverlays(hiddenOverlays);
                throw new InvalidOperationException(
                    $"Refusing popup capture because process {candidateProcessId} ({GetWindowClassName(candidate)}) overlaps JitHub above the app window.");
            }
        }

        if (hiddenOverlays.Count > 0)
        {
            Thread.Sleep(60);
        }

        return hiddenOverlays;
    }

    private static IntPtr FindBlockingSystemDialog(Rectangle captureBounds)
    {
        IntPtr blockingWindow = IntPtr.Zero;
        _ = EnumWindows(
            (candidate, parameter) =>
            {
                _ = parameter;
                if (GetWindowClassName(candidate) is not (
                        "Shell_SystemDialog" or
                        "Shell_SystemDialogProxy" or
                        "Shell_SystemDim"))
                {
                    return true;
                }

                if (!GetWindowRect(candidate, out NativeRect rect))
                {
                    blockingWindow = candidate;
                    return false;
                }

                Rectangle bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (!bounds.IsEmpty && !captureBounds.IntersectsWith(bounds))
                {
                    return true;
                }

                blockingWindow = candidate;
                return false;
            },
            IntPtr.Zero);
        return blockingWindow;
    }

    internal static void RestoreCaptureOverlays(IReadOnlyList<IntPtr> windowHandles)
    {
        foreach (IntPtr windowHandle in windowHandles)
        {
            _ = ShowWindow(windowHandle, SwShowNoActivate);
        }
    }

    private static bool IsKnownCaptureOverlay(int processId, IntPtr windowHandle)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (string.Equals(process.ProcessName, "codex-computer-use", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(process.MainWindowTitle, "Codex Computer Use Cursor Overlay", StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(process.ProcessName, "DDPM.Subagent.User", StringComparison.OrdinalIgnoreCase) &&
                GetWindowClassName(windowHandle).StartsWith(
                    "HwndWrapper[DDPM.Subagent.User;",
                    StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWindowsShellSurface(int processId, IntPtr windowHandle)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return GetWindowClassName(windowHandle) is
                "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or
                "ThumbnailDeviceHelperWnd" or "ProxyModalWindow";
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowClassName(IntPtr windowHandle)
    {
        var className = new StringBuilder(128);
        _ = GetClassName(windowHandle, className, className.Capacity);
        return className.ToString();
    }

    internal static void SetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return;
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            _ = EmptyClipboard();
            byte[] bytes = Encoding.Unicode.GetBytes((text ?? string.Empty) + '\0');
            handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            IntPtr locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
            {
                return;
            }

            try
            {
                Marshal.Copy(bytes, 0, locked, bytes.Length);
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }

            if (SetClipboardData(CfUnicodeText, handle) != IntPtr.Zero)
            {
                handle = IntPtr.Zero;
            }
        }
        finally
        {
            _ = CloseClipboard();
            if (handle != IntPtr.Zero)
            {
                _ = GlobalFree(handle);
            }
        }
    }

    internal static string GetClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return string.Empty;
        }

        try
        {
            if (!IsClipboardFormatAvailable(CfUnicodeText))
            {
                return string.Empty;
            }

            IntPtr handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                return string.Empty;
            }

            IntPtr locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringUni(locked) ?? string.Empty;
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }
}

internal static class AutomationWindowHandleCache
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, HandleHolder> Handles = new();

    internal static bool TryGet(Window window, out IntPtr handle)
    {
        if (Handles.TryGetValue(window, out HandleHolder? holder) && holder.Value != IntPtr.Zero)
        {
            handle = holder.Value;
            return true;
        }

        handle = IntPtr.Zero;
        return false;
    }

    internal static void Store(Window window, IntPtr handle)
    {
        Handles.Remove(window);
        Handles.Add(window, new HandleHolder(handle));
    }

    private sealed record HandleHolder(IntPtr Value);
}

internal sealed record MarkdownLifecycleTarget(
    string Name,
    string Page,
    string HostAutomationId,
    bool PrefixMatch,
    string? SectionControlAutomationId = null,
    string? LauncherControlAutomationId = null,
    bool RequiresCompactWidth = false,
    string? RequiredViewportName = null,
    string? RealizationContainerAutomationId = null,
    string? CompactSectionPickerAutomationId = null,
    string? CompactSectionControlAutomationId = null,
    bool RealizationStartsAtTop = false)
{
    public bool AppliesTo(MarkdownLifecycleViewport viewport, double textScale) =>
        string.IsNullOrWhiteSpace(RequiredViewportName) ||
        string.Equals(RequiredViewportName, viewport.Name, StringComparison.OrdinalIgnoreCase);
}

internal sealed record MarkdownLifecycleViewport(string Name, int Width, int Height);

internal sealed record MarkdownRelayoutMetrics(
    int Cycles,
    long BaselinePrivateBytes,
    long RetainedPrivateBytes,
    long RetainedGrowthBytes,
    long RetainedGrowthBudgetBytes);

internal sealed record MarkdownLifecycleHostAcquisition(
    Window Window,
    AutomationElement Host);

internal sealed record MarkdownLifecycleRunPaths(
    string Directory,
    string AppReadyPath,
    string HostReadyPath,
    string RuntimeSettingsPath,
    string ResourceMapEvidencePath,
    string LinkEvidencePath,
    string ImageEvidencePath)
{
    public IEnumerable<string> SignalPaths =>
        [AppReadyPath, HostReadyPath, RuntimeSettingsPath, ResourceMapEvidencePath, LinkEvidencePath, ImageEvidencePath];
}

internal sealed class MarkdownLifecycleApplication : IDisposable
{
    private readonly object _processExitHandleGate = new();
    private IntPtr _processExitHandle;
    private int _disposed;

    public MarkdownLifecycleApplication(
        Application application,
        Process process,
        Process launcher,
        IntPtr processExitHandle)
    {
        Application = application;
        Process = process;
        Launcher = launcher;
        _processExitHandle = processExitHandle;
    }

    public Application Application { get; }

    public Process Process { get; }

    public Process Launcher { get; }

    public uint GetExitCode()
    {
        lock (_processExitHandleGate)
        {
            if (_processExitHandle == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(MarkdownLifecycleApplication));
            }

            return NativeMethods.GetProcessExitCode(_processExitHandle);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? firstException = null;
        DisposeResource(Application, ref firstException);
        DisposeResource(Process, ref firstException);
        if (!ReferenceEquals(Process, Launcher))
        {
            DisposeResource(Launcher, ref firstException);
        }

        try
        {
            lock (_processExitHandleGate)
            {
                IntPtr handle = Interlocked.Exchange(ref _processExitHandle, IntPtr.Zero);
                if (handle != IntPtr.Zero)
                {
                    NativeMethods.CloseProcessExitHandle(handle);
                }
            }
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        if (firstException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstException).Throw();
        }
    }

    private static void DisposeResource(IDisposable resource, ref Exception? firstException)
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }
    }
}

internal sealed class MarkdownLifecycleManifest
{
    public int Version { get; set; }
    public string RunScope { get; set; } = string.Empty;
    public string? RequestedTarget { get; set; }
    public bool RequiresSupplementalCases { get; set; }
    public string Configuration { get; set; } = string.Empty;
    public string AppPath { get; set; } = string.Empty;
    public string AppSha256 { get; set; } = string.Empty;
    public DateTime AppLastWriteUtc { get; set; }
    public string AppAssemblyPath { get; set; } = string.Empty;
    public string AppAssemblySha256 { get; set; } = string.Empty;
    public DateTime AppAssemblyLastWriteUtc { get; set; }
    public string AutomationAssemblyPath { get; set; } = string.Empty;
    public string AutomationAssemblySha256 { get; set; } = string.Empty;
    public DateTime AutomationAssemblyLastWriteUtc { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int ExpectedHostCount { get; set; }
    public int ExpectedCaseCount { get; set; }
    public string[] Hosts { get; set; } = [];
    public string[] Themes { get; set; } = [];
    public int[] TextScalePercents { get; set; } = [];
    public string[] Viewports { get; set; } = [];
    public bool Completed { get; set; }
    public List<MarkdownLifecycleCaseResult> Cases { get; set; } = [];
    public MarkdownLifecycleCaseResult? ResourceMapAbsentCase { get; set; }
    public MarkdownLifecycleCaseResult? SecurityPolicyCase { get; set; }
    public List<string> Failures { get; set; } = [];
}

internal readonly record struct CommitPerformanceSnapshot(
    double ElapsedMilliseconds,
    double FirstDiffMilliseconds,
    double SearchMilliseconds,
    double DispatcherMaxGapMilliseconds,
    double RenderMaxGapMilliseconds,
    int RenderCount)
{
    public static bool TryParse(string? value, out CommitPerformanceSnapshot snapshot)
    {
        snapshot = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Dictionary<string, string> values = value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);

        return TryReadDouble(values, "elapsed_ms", out double elapsed) &&
            TryReadDouble(values, "first_diff_ms", out double firstDiff) &&
            TryReadDouble(values, "search_ms", out double search) &&
            TryReadDouble(values, "dispatcher_max_gap_ms", out double dispatcherGap) &&
            TryReadDouble(values, "render_max_gap_ms", out double renderGap) &&
            values.TryGetValue("render_count", out string? renderCountText) &&
            int.TryParse(renderCountText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int renderCount) &&
            SetSnapshot(
                elapsed,
                firstDiff,
                search,
                dispatcherGap,
                renderGap,
                renderCount,
                out snapshot);
    }

    private static bool TryReadDouble(
        IReadOnlyDictionary<string, string> values,
        string key,
        out double result)
    {
        result = default;
        return values.TryGetValue(key, out string? text) &&
            double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);
    }

    private static bool SetSnapshot(
        double elapsed,
        double firstDiff,
        double search,
        double dispatcherGap,
        double renderGap,
        int renderCount,
        out CommitPerformanceSnapshot snapshot)
    {
        snapshot = new CommitPerformanceSnapshot(
            elapsed,
            firstDiff,
            search,
            dispatcherGap,
            renderGap,
            renderCount);
        return true;
    }
}

internal sealed class MarkdownLifecycleCaseResult
{
    public string CaseId { get; set; } = string.Empty;
    public string Configuration { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Theme { get; set; } = string.Empty;
    public int TextScalePercent { get; set; }
    public string Viewport { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool HostReady { get; set; }
    public bool Selection { get; set; }
    public bool PointerDragSelection { get; set; }
    public bool CtrlC { get; set; }
    public bool ContextCopy { get; set; }
    public bool KeyboardLinkFocus { get; set; }
    public bool InternalRepositoryRoute { get; set; }
    public bool InternalUserRoute { get; set; }
    public bool ExternalBrowserRoute { get; set; }
    public bool InlineSvg { get; set; }
    public bool RemoteImageNotice { get; set; }
    public bool Relayout { get; set; }
    public bool RepeatedRelayout { get; set; }
    public int RelayoutCycles { get; set; }
    public bool Scroll { get; set; }
    public bool MemoryBudget { get; set; }
    public bool RetainedMemoryBudget { get; set; }
    public bool RealHostComposition { get; set; }
    public bool HostUnloadOnClose { get; set; }
    public bool HostileSvgBudget { get; set; }
    public bool OversizedSvgBudget { get; set; }
    public bool RedirectPolicyFixture { get; set; }
    public bool RemoteImagePolicy { get; set; }
    public bool CleanClose { get; set; }
    public int? ExitCode { get; set; }
    public int UnhandledLogCount { get; set; }
    public double MeasuredLineHeight { get; set; }
    public double MeasuredFontSize { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public long MemoryBudgetBytes { get; set; }
    public long RelayoutBaselinePrivateBytes { get; set; }
    public long RelayoutRetainedPrivateBytes { get; set; }
    public long RetainedMemoryGrowthBytes { get; set; }
    public long RetainedMemoryBudgetBytes { get; set; }
    public string? Screenshot { get; set; }
    public string? Error { get; set; }

    public static MarkdownLifecycleCaseResult Passed(
        string configuration,
        string host,
        string theme,
        double textScale,
        MarkdownLifecycleViewport viewport,
        string caseId,
        string screenshot,
        double measuredLineHeight,
        double measuredFontSize) => new()
        {
            CaseId = caseId,
            Configuration = configuration,
            Host = host,
            Theme = theme,
            TextScalePercent = (int)Math.Round(textScale * 100),
            Viewport = viewport.Name,
            Width = viewport.Width,
            Height = viewport.Height,
            Status = "passed",
            HostReady = true,
            Selection = true,
            PointerDragSelection = true,
            CtrlC = true,
            ContextCopy = true,
            KeyboardLinkFocus = true,
            InternalRepositoryRoute = true,
            InternalUserRoute = true,
            ExternalBrowserRoute = true,
            InlineSvg = true,
            RemoteImageNotice = true,
            Relayout = true,
            RealHostComposition = true,
            Scroll = true,
            MeasuredLineHeight = measuredLineHeight,
            MeasuredFontSize = measuredFontSize,
            Screenshot = screenshot,
        };

    public static MarkdownLifecycleCaseResult Failed(
        string configuration,
        string host,
        string theme,
        double textScale,
        MarkdownLifecycleViewport viewport,
        string caseId,
        string error) => new()
        {
            CaseId = caseId,
            Configuration = configuration,
            Host = host,
            Theme = theme,
            TextScalePercent = (int)Math.Round(textScale * 100),
            Viewport = viewport.Name,
            Width = viewport.Width,
            Height = viewport.Height,
            Status = "failed",
            Error = error,
        };
}

internal sealed record AutomationApplicationRegistration(
    string AppPath,
    int ProcessId,
    DateTime LaunchStartedUtc,
    IReadOnlySet<int> ExcludedProcessIds);

internal static class AutomationApplicationPathRegistry
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Application, AutomationApplicationRegistration> Paths = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, AutomationApplicationRegistration> OwnedProcesses = new();

    public static void Register(
        Application application,
        string appPath,
        int processId,
        DateTime launchStartedUtc,
        IReadOnlySet<int> excludedProcessIds)
    {
        Paths.Remove(application);
        var registration = new AutomationApplicationRegistration(
            Path.GetFullPath(appPath),
            processId,
            launchStartedUtc,
            new HashSet<int>(excludedProcessIds));
        Paths.Add(application, registration);
        if (processId > 0)
        {
            OwnedProcesses[processId] = registration;
        }
    }

    public static bool TryGet(
        Application application,
        out AutomationApplicationRegistration? registration)
    {
        if (Paths.TryGetValue(application, out AutomationApplicationRegistration? current))
        {
            registration = current;
            return true;
        }

        registration = null;
        return false;
    }

    public static IReadOnlyList<AutomationApplicationRegistration> GetOwnedRegistrations(string appPath)
    {
        string normalizedPath = Path.GetFullPath(appPath);
        return OwnedProcesses.Values
            .Where(registration => string.Equals(
                registration.AppPath,
                normalizedPath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static void Forget(Application application)
    {
        if (Paths.TryGetValue(application, out AutomationApplicationRegistration? registration))
        {
            OwnedProcesses.TryRemove(registration.ProcessId, out _);
            Paths.Remove(application);
        }
    }

    public static void Forget(int processId)
    {
        if (processId > 0)
        {
            OwnedProcesses.TryRemove(processId, out _);
        }
    }
}

internal static class AutomationLifecycleLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Configure(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        lock (Gate)
        {
            _path = fullPath;
            File.WriteAllText(fullPath, string.Empty);
        }
    }

    public static void Write(string eventName, string detail)
    {
        lock (Gate)
        {
            if (_path is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(
                    _path,
                    $"{DateTimeOffset.Now:O}\t{eventName}\t{detail}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }
}

internal sealed class CaptureOptions
{
    public required string AppPath { get; init; }
    public required string OutputDirectory { get; init; }
    public required IReadOnlyList<string> Themes { get; init; }
    public required IReadOnlyList<CaptureTarget> Targets { get; init; }
    public required string RepositoryFullName { get; init; }
    public string? Probe { get; init; }
    public string? AttachProcess { get; init; }
    public string? Configuration { get; init; }
    public IReadOnlyList<string> ShowcaseIds { get; init; } = [];

    public static CaptureOptions Parse(string[] args)
    {
        string outputDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "screenshots", "winui"));
        string? appPath = null;
        string? probe = null;
        string? attachProcess = null;
        string? configuration = null;
        string repoFullName = "JitHubApp/JitHubV2";
        string[] themes = ["light", "dark"];
        string[] targetNames = ["buttons", "inputs", "segments", "navigation", "settings", "repo", "conversation", "pr-timeline", "empty", "login", "settings-page"];
        string[] showcaseIds = [];

        foreach (string arg in args)
        {
            if (arg.StartsWith("--app=", StringComparison.OrdinalIgnoreCase))
            {
                appPath = Path.GetFullPath(arg[6..]);
            }
            else if (arg.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
            {
                outputDirectory = Path.GetFullPath(arg[6..]);
            }
            else if (arg.StartsWith("--themes=", StringComparison.OrdinalIgnoreCase))
            {
                themes = arg[9..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else if (arg.StartsWith("--targets=", StringComparison.OrdinalIgnoreCase))
            {
                targetNames = arg[10..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else if (arg.StartsWith("--probe=", StringComparison.OrdinalIgnoreCase))
            {
                probe = arg[8..].Trim();
            }
            else if (arg.StartsWith("--showcase-ids=", StringComparison.OrdinalIgnoreCase))
            {
                showcaseIds = arg[15..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else if (arg.StartsWith("--attach-process=", StringComparison.OrdinalIgnoreCase))
            {
                attachProcess = arg[17..].Trim();
            }
            else if (arg.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
            {
                repoFullName = arg[7..].Trim();
            }
            else if (arg.StartsWith("--configuration=", StringComparison.OrdinalIgnoreCase))
            {
                configuration = arg[16..].Trim();
            }
        }

        appPath ??= GuessAppPath();
        if (!File.Exists(appPath))
        {
            throw new FileNotFoundException($"Could not find JitHub.WinUI app executable at '{appPath}'. Pass --app=<path>.");
        }

        EnsureAppBinaryIsFresh(appPath);

        Dictionary<string, CaptureTarget> allTargets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["buttons"] = new CaptureTarget("buttons", "design-lab", "buttons", "ScenarioButtons"),
            ["inputs"] = new CaptureTarget("inputs", "design-lab", "inputs", "ScenarioInputs"),
            ["segments"] = new CaptureTarget("segments", "design-lab", "segments", "ScenarioSegments"),
            ["segmented"] = new CaptureTarget("segments", "design-lab", "segments", "ScenarioSegments"),
            ["navigation"] = new CaptureTarget("navigation", "design-lab", "navigation", "ScenarioNavigation"),
            ["settings"] = new CaptureTarget("settings", "design-lab", "settings", "ScenarioSettings"),
            ["repo"] = new CaptureTarget("repo", "design-lab", "repo", "ScenarioRepoReference"),
            ["activities"] = new CaptureTarget("activities", "design-lab", "activities", "ScenarioActivities"),
            ["activity"] = new CaptureTarget("activities", "design-lab", "activities", "ScenarioActivities"),
            ["conversation"] = new CaptureTarget("conversation", "design-lab", "conversation", "ScenarioConversation"),
            ["pr-timeline"] = new CaptureTarget("pr-timeline", "design-lab", "pr-timeline", "ScenarioPullRequestTimeline"),
            ["pull-request-timeline"] = new CaptureTarget("pr-timeline", "design-lab", "pr-timeline", "ScenarioPullRequestTimeline"),
            ["timeline"] = new CaptureTarget("pr-timeline", "design-lab", "pr-timeline", "ScenarioPullRequestTimeline"),
            ["home"] = new CaptureTarget("home", "home", null, null),
            ["shell"] = new CaptureTarget("shell", "shell", null, null),
            ["profile"] = new CaptureTarget("profile", "profile", null, null),
            ["repo-code"] = new CaptureTarget("repo-code", "repo-code", null, null),
            ["real-repo"] = new CaptureTarget("repo-code", "repo-code", null, null),
            ["repo-issues"] = new CaptureTarget("repo-issues", "repo-issues", null, null),
            ["repo-pulls"] = new CaptureTarget("repo-pulls", "repo-pulls", null, null),
            ["repo-pull-requests"] = new CaptureTarget("repo-pulls", "repo-pull-requests", null, null),
            ["repo-commits"] = new CaptureTarget("repo-commits", "repo-commits", null, null),
            ["empty"] = new CaptureTarget("empty", "design-lab", "empty", "ScenarioEmptyState"),
            ["login"] = new CaptureTarget("login", "login", null, null),
            ["settings-page"] = new CaptureTarget("settings-page", "settings", null, null)
        };

        return new CaptureOptions
        {
            AppPath = appPath,
            OutputDirectory = outputDirectory,
            Themes = themes,
            Targets = targetNames.Select(name => allTargets[name]).ToList(),
            RepositoryFullName = string.IsNullOrWhiteSpace(repoFullName) ? "JitHubApp/JitHubV2" : repoFullName,
            Probe = probe,
            AttachProcess = attachProcess,
            Configuration = configuration,
            ShowcaseIds = showcaseIds
        };
    }

    private static string GuessAppPath()
    {
        string baseDirectory = FindRepositoryRoot();
        string[] candidates =
        [
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "publish", "JitHub.WinUI.exe"),
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "JitHub.WinUI.exe"),
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "AppX", "JitHub.WinUI.exe"),
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "JitHub.WinUI.exe"),
            Path.Combine(baseDirectory, "JitHub.WinUI", "bin", "x64", "Release", "net10.0-windows10.0.26100.0", "win-x64", "publish", "JitHub.WinUI.exe")
        ];

        DateTime newestSourceWrite = GetNewestSourceWriteTimeUtc(baseDirectory);
        string? freshCandidate = candidates.FirstOrDefault(candidate =>
        {
            if (!File.Exists(candidate))
            {
                return false;
            }

            string? freshnessArtifact = GetFreshnessArtifact(candidate);
            return freshnessArtifact is not null &&
                File.GetLastWriteTimeUtc(freshnessArtifact) >= newestSourceWrite;
        });

        if (freshCandidate is not null)
        {
            return freshCandidate;
        }

        throw new InvalidOperationException(
            "No fresh JitHub executable was found for UI automation. " +
            "Run 'dotnet publish JitHub.WinUI\\JitHub.WinUI.csproj -c Debug -p:Platform=x64' " +
            "(add '-p:EnablePseudoLocalization=true' for the vnext-pseudo-localization probe) " +
            "or pass an intentional executable with --app=<path>.");
    }

    private static void EnsureAppBinaryIsFresh(string appPath)
    {
        string? freshnessArtifact = GetFreshnessArtifact(appPath);
        if (freshnessArtifact is null)
        {
            throw new InvalidOperationException(
                $"The automation app '{appPath}' is neither a managed JitHub build with an adjacent assembly " +
                "nor a native PE image without a CLR header.");
        }

        string repositoryRoot = FindRepositoryRoot();
        DateTime newestSourceWrite = GetNewestSourceWriteTimeUtc(repositoryRoot);
        DateTime artifactWrite = File.GetLastWriteTimeUtc(freshnessArtifact);
        if (artifactWrite < newestSourceWrite)
        {
            throw new InvalidOperationException(
                $"Refusing stale JitHub automation binary '{appPath}'. " +
                $"Artifact timestamp {artifactWrite:O} predates source timestamp {newestSourceWrite:O}. " +
                "Rebuild the app and pass the rebuilt executable.");
        }
    }

    private static string? GetFreshnessArtifact(string appPath)
    {
        string adjacentAssembly = Path.Combine(Path.GetDirectoryName(appPath)!, "JitHub.WinUI.dll");
        if (File.Exists(adjacentAssembly))
        {
            return adjacentAssembly;
        }

        return IsNativeAotExecutable(appPath) ? appPath : null;
    }

    private static bool IsNativeAotExecutable(string appPath)
    {
        try
        {
            using FileStream stream = File.Open(appPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using PEReader reader = new(stream);
            return reader.PEHeaders.PEHeader is not null &&
                reader.PEHeaders.CorHeader is null;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "JitHub.WinUI")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private static DateTime GetNewestSourceWriteTimeUtc(string baseDirectory)
    {
        string[] sourceRoots =
        [
            Path.Combine(baseDirectory, "JitHub.WinUI"),
            Path.Combine(baseDirectory, "MarkdownRenderer", "MarkdownRenderer"),
            Path.Combine(baseDirectory, "MarkdownRenderer", "MarkdownRenderer.Gfm")
        ];
        string[] sourceExtensions = [".cs", ".xaml", ".csproj", ".props", ".targets"];
        DateTime newest = DateTime.MinValue;

        foreach (string sourceRoot in sourceRoots.Where(Directory.Exists))
        {
            foreach (string sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                string firstSegment = relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    2,
                    StringSplitOptions.RemoveEmptyEntries)[0];
                if (firstSegment.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                    firstSegment.StartsWith("obj", StringComparison.OrdinalIgnoreCase) ||
                    firstSegment.Equals("artifacts", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!sourceExtensions.Contains(Path.GetExtension(sourcePath), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                DateTime writeTime = File.GetLastWriteTimeUtc(sourcePath);
                if (writeTime > newest)
                {
                    newest = writeTime;
                }
            }
        }

        return newest;
    }
}

internal static class AutomationResponsiveLayout
{
    // Mirrors ShellResponsiveLayout.RailCollapseWidth. Keeping the real-app
    // probes on the production collapse order avoids false wide-layout
    // assertions when the Windows work area clamps a requested viewport.
    public const int ShellRailCollapseWidth = 1298;
}
