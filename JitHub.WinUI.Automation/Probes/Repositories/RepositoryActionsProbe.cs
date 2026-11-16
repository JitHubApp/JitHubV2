using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.AutomationDataRoot;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Interaction.KeyboardInput;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Shell.ShellNavigation;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Repositories;

/// <summary>
/// Drives the repository-actions probe.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it holds RunRepositoryActionsProbe, a single 674-line probe whose
/// local functions run the repository action scenarios in a fixed order and share its captured options; the comment
/// on that method states why it stays whole.
/// </remarks>
internal static class RepositoryActionsProbe
{
    // This method stays whole because it is the repository-actions entry point that runs the success, timeout, failure,
    // rollback, fork-relaunch, rate-limit, route-overlap and disabled scenarios in a fixed order, and its local
    // functions share the captured options and the persisted fork-ownership reset and read helpers.
    internal static void RunRepositoryActionsProbe(CaptureOptions options)
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
}
