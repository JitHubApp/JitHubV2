using System.Collections.Frozen;
using JitHub.WinUI.Automation.Options;
using JitHub.WinUI.Automation.Probes.Authentication;
using JitHub.WinUI.Automation.Probes.Commits;
using JitHub.WinUI.Automation.Probes.DesignLab;
using JitHub.WinUI.Automation.Probes.Diagnostics;
using JitHub.WinUI.Automation.Probes.Dialogs;
using JitHub.WinUI.Automation.Probes.Gists;
using JitHub.WinUI.Automation.Probes.HighContrast;
using JitHub.WinUI.Automation.Probes.Home;
using JitHub.WinUI.Automation.Probes.Issues;
using JitHub.WinUI.Automation.Probes.KeyboardAccessibility;
using JitHub.WinUI.Automation.Probes.Localization;
using JitHub.WinUI.Automation.Probes.Notifications;
using JitHub.WinUI.Automation.Probes.Profile;
using JitHub.WinUI.Automation.Probes.PullRequests;
using JitHub.WinUI.Automation.Probes.RepoCode;
using JitHub.WinUI.Automation.Probes.Repositories;
using JitHub.WinUI.Automation.Probes.Search;
using JitHub.WinUI.Automation.Probes.Settings;
using JitHub.WinUI.Automation.Probes.Shell;
using JitHub.WinUI.Automation.Probes.Stars;
using JitHub.WinUI.Automation.Probes.TargetCapture;
using JitHub.WinUI.Automation.Probes.Theming;
using JitHub.WinUI.Automation.Probes.Website;

namespace JitHub.WinUI.Automation.Probes;

/// <summary>
/// Maps each <c>--probe</c> name to the probe it runs. A run without a known probe name captures the
/// configured targets instead.
/// </summary>
internal static class ProbeCatalog
{
    private static readonly FrozenDictionary<string, Action<CaptureOptions>> ProbesByName =
        new Dictionary<string, Action<CaptureOptions>>(StringComparer.OrdinalIgnoreCase)
        {
            { "search-context", SearchContextProbe.RunSearchContextProbe },
            { "theme-switch", ThemeSwitchProbe.RunThemeSwitchProbe },
            { "theme-palettes", ThemePaletteProbe.RunThemePaletteProbe },
            { "login-auth-ui", LoginAuthUiProbe.RunLoginAuthUiProbe },
            { "auth-lifecycle", AuthLifecycleProbe.RunAuthLifecycleProbe },
            { "high-contrast-live", HighContrastLiveProbe.Run },
            { CompactDialogMatrixProbe.ProbeName, CompactDialogMatrixProbe.Run },
            { "combo-open", ComboOpenProbe.RunComboOpenProbe },
            { "search-select-dismiss", SearchSelectDismissProbe.RunSearchSelectDismissProbe },
            { "search-focus-contract", SearchFocusContractProbe.RunSearchFocusContractProbe },
            { "emoji-panel", EmojiPanelProbe.RunEmojiPanelProbe },
            { "segments-hover", SegmentsHoverProbe.RunSegmentsHoverProbe },
            { "activity-link-hover", ActivityLinkHoverProbe.RunActivityLinkHoverProbe },
            { "pr-timeline-link-hover", PullRequestTimelineLinkHoverProbe.RunPullRequestTimelineLinkHoverProbe },
            { "shell-responsive", ShellResponsiveProbe.RunShellResponsiveProbe },
            { "shell-nav-clicks", ShellNavClicksProbe.RunShellNavClicksProbe },
            { "shell-hover-states", ShellHoverStatesProbe.RunShellHoverStatesProbe },
            { "shell-search-states", ShellSearchStatesProbe.RunShellSearchStatesProbe },
            { "shell-repo-click", ShellRepoClickProbe.RunShellRepoClickProbe },
            { "home-widget-board", HomeWidgetBoardProbe.RunHomeWidgetBoardProbe },
            { "home-customize", HomeCustomizeProbe.RunHomeCustomizeProbe },
            { "home-view-all", HomeViewAllProbe.RunHomeViewAllProbe },
            { "command-search", CommandSearchProbe.RunCommandSearchProbe },
            { "repo-search-responsive", RepositorySearchResponsiveProbe.RunRepositorySearchResponsiveProbe },
            { "keyboard-accessibility-matrix", KeyboardAccessibilityMatrixProbe.RunKeyboardAccessibilityMatrixProbe },
            { "keyboard-commit-diff-search", KeyboardCommitDiffSearchMatrix.RunKeyboardCommitDiffSearchMatrix },
            { "my-issues-page", MyIssuesPageProbe.RunMyIssuesPageProbe },
            { "my-pull-requests-page", MyPullRequestsPageProbe.RunMyPullRequestsPageProbe },
            { "my-pull-requests-pseudo-long-labels", MyPullRequestsPseudoLongLabelsProbe.RunMyPullRequestsPseudoLongLabelsProbe },
            { "my-issues-pseudo-long-labels", MyIssuesPseudoLongLabelsProbe.RunMyIssuesPseudoLongLabelsProbe },
            { "repo-issues-page", RepoIssuesPageProbe.RunRepoIssuesPageProbe },
            { "issues-responsive-workspace", IssuesResponsiveWorkspaceProbe.RunIssuesResponsiveWorkspaceProbe },
            { "pull-requests-responsive-workspace", PullRequestsResponsiveWorkspaceProbe.RunPullRequestsResponsiveWorkspaceProbe },
            { "pull-request-reply-identities", PullRequestReplyIdentityProbe.RunPullRequestReplyIdentityProbe },
            { "commits-responsive-workspace", CommitsResponsiveWorkspaceProbe.RunCommitsResponsiveWorkspaceProbe },
            { "repo-code-responsive-workspace", RepoCodeResponsiveWorkspaceProbe.RunRepoCodeResponsiveWorkspaceProbe },
            { "repo-code-content-surfaces", RepoCodeContentSurfacesProbe.RunRepoCodeContentSurfacesProbe },
            { "repo-code-performance", RepoCodePerformanceProbe.RunRepoCodePerformanceProbe },
            { "repo-code-high-contrast", RepoCodeHighContrastProbe.RunRepoCodeHighContrastProbe },
            { "repository-actions", RepositoryActionsProbe.RunRepositoryActionsProbe },
            { "repository-library", RepositoryLibraryProbe.Run },
            { "commits-virtualized-diff", CommitsVirtualizedDiffProbe.RunCommitsVirtualizedDiffProbe },
            { "commits-performance", CommitsPerformanceProbe.RunCommitsPerformanceProbe },
            { "profile-responsive", ProfileResponsiveProbe.RunProfileResponsiveProbe },
            { "profile-avatar-routing", ProfileAvatarRoutingProbe.RunProfileAvatarRoutingProbe },
            { "profile-avatar-routing-commits", options => DirectRepositoryAvatarRouteProbe.RunDirectRepositoryAvatarRouteProbe(
            options,
            "--page=repo-commits",
            "RepoCommitsPageRoot",
            "commit_list_author",
            "commit-list-author") },
            { "settings-responsive", SettingsResponsiveProbe.RunSettingsResponsiveProbe },
            { "settings-export-picker", SettingsExportPickerProbe.RunSettingsExportPickerProbe },
            { "settings-pseudo-long", SettingsPseudoLongLabelsProbe.RunSettingsPseudoLongLabelsProbe },
            { "vnext-pseudo-localization", VNextPseudoLocalizationProbe.RunVNextPseudoLocalizationProbe },
            { "stars-library", options => StarsLibraryProbe.RunStarsLibraryProbe(options, includeCategoryPersistence: false) },
            { "stars-selection-mode", StarsSelectionModeProbe.RunStarsSelectionModeProbe },
            { "stars-categories", StarsCategoryPersistenceProbe.RunStarsCategoryPersistenceProbe },
            { "gists-workspace", options => GistsWorkspaceProbe.RunGistsWorkspaceProbe(options) },
            { "gists-crud", options => GistsWorkspaceProbe.RunGistsWorkspaceProbe(options, skipResponsiveMatrix: true) },
            { "notifications-workspace", NotificationsWorkspaceProbe.RunNotificationsWorkspaceProbe },
            { "diagnostics-launch-close", DiagnosticsLaunchCloseProbe.RunDiagnosticsLaunchCloseProbe },
            { "website-showcase", WebsiteShowcaseProbe.RunWebsiteShowcaseProbe },
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the probe named by <see cref="CaptureOptions.Probe"/>, or captures the configured targets when
    /// no probe name matches.
    /// </summary>
    public static void Run(CaptureOptions options)
    {
        if (options.Probe is not null && ProbesByName.TryGetValue(options.Probe, out Action<CaptureOptions>? probe))
        {
            probe(options);
            return;
        }

        TargetCaptureProbe.RunTargetCaptureProbe(options);
    }
}
