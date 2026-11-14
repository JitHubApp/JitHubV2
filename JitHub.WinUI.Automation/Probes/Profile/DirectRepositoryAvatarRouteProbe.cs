using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Profile;

/// <summary>
/// Drives the profile-avatar-routing-commits probe and the repository avatar route check the avatar routing probe shares.
/// </summary>
internal static class DirectRepositoryAvatarRouteProbe
{
    internal static void RunDirectRepositoryAvatarRouteProbe(
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

    internal static void ExerciseVisibleRepositoryAvatarRouteProbe(
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

    private static bool IsUserIdentityForSource(string automationId, string navigationSource) =>
        automationId.StartsWith($"UserProfile_{navigationSource}_", StringComparison.Ordinal) ||
        automationId.StartsWith($"UserProfile_Unavailable_{navigationSource}_", StringComparison.Ordinal);
}
