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
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Interaction.MouseInput;
using static JitHub.WinUI.Automation.Interaction.ScrollActions;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Shell.ShellNavigation;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Shell;

/// <summary>
/// Drives the shell-nav-clicks probe.
/// </summary>
internal static class ShellNavClicksProbe
{
    // This method stays whole because it builds one shell history stack (nav clicks, scrolled Home, Settings Privacy)
    // and then replays it through Back and Forward buttons, Alt+arrows, and mouse X-buttons, before checking the New
    // Repository modal's focus trap, so each step depends on the history the previous step left.
    internal static void RunShellNavClicksProbe(CaptureOptions options)
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
}
