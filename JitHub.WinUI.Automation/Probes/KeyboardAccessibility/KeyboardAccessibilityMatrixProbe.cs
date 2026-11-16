using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Dialogs.DialogAssertions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Interaction.KeyboardInput;
using static JitHub.WinUI.Automation.Probes.KeyboardAccessibility.KeyboardCommitDiffSearchMatrix;
using static JitHub.WinUI.Automation.Probes.Settings.SettingsPageLayout;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;
using static JitHub.WinUI.Automation.Workspaces.AdaptivePaneButtons;
using static JitHub.WinUI.Automation.Workspaces.AdaptiveWorkspaceDrawers;

namespace JitHub.WinUI.Automation.Probes.KeyboardAccessibility;

/// <summary>
/// Drives the keyboard-accessibility-matrix probe: list traversal, mode selectors, adaptive drawers, context menus and dialogs.
/// </summary>
internal static class KeyboardAccessibilityMatrixProbe
{
    internal static void RunKeyboardAccessibilityMatrixProbe(CaptureOptions options)
    {
        // Keep each keyboard surface in its own deterministic preview process. A route or
        // popup failure cannot leave focus in an unknown window for the following pass.
        RunKeyboardListTraversalMatrix(options);
        RunKeyboardModeSelectorMatrix(options);
        RunKeyboardAdaptiveDrawerMatrix(options);
        RunKeyboardContextMenuMatrix(options);
        RunKeyboardDialogMatrix(options);
        RunKeyboardCommitDiffSearchMatrix(options);
    }

    private static void RunKeyboardListTraversalMatrix(CaptureOptions options)
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

    private static void RunKeyboardModeSelectorMatrix(CaptureOptions options)
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

    private static void RunKeyboardAdaptiveDrawerMatrix(CaptureOptions options)
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

    private static void ExerciseAdaptivePaneWithKeyboard(
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

    private static void RunKeyboardContextMenuMatrix(CaptureOptions options)
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

    private static void RunKeyboardDialogMatrix(CaptureOptions options)
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
}
