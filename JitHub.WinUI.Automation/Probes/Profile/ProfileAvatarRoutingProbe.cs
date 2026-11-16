using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Probes.Profile.DirectRepositoryAvatarRouteProbe;
using static JitHub.WinUI.Automation.Screenshots.ScreenshotStability;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Shell.ShellSearch;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowActivation;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Profile;

/// <summary>
/// Drives the profile-avatar-routing probe.
/// </summary>
internal static class ProfileAvatarRoutingProbe
{
    // This method stays whole because the issue-row selection and avatar AutomationId recorded before invoking the
    // author avatar are what it checks after the Profile route and shell Back, to prove the parent row was not invoked
    // and focus returned to that same avatar, before the pull-request avatar route runs.
    internal static void RunProfileAvatarRoutingProbe(CaptureOptions options)
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

    private static void ExerciseCanonicalRepositoryAvatarRouteProbe(
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

    private static void ExecuteShellCommand(Window window, string commandTitle)
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
}
