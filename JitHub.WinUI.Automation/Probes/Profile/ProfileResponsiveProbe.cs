using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
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
using static JitHub.WinUI.Automation.Windows.WindowActivation;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Profile;

/// <summary>
/// Drives the profile-responsive probe.
/// </summary>
internal static class ProfileResponsiveProbe
{
    // This method stays whole because it is one ordered script on a single app: route to Profile from the compact
    // shell, check the edit button and mode-switch stability at five sizes, then drive contribution-graph keyboard
    // focus and Left/Home/End, then open and cancel the edit dialog chosen by the final viewport width.
    internal static void RunProfileResponsiveProbe(CaptureOptions options)
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

    private static void AssertProfileModeSwitchKeepsBoardStable(Window window, string context)
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
}
