using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Probes.Commits.CommitDiffNavigation;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.KeyboardAccessibility;

/// <summary>
/// Drives the keyboard-commit-diff-search probe, which is also the last group of the keyboard accessibility matrix.
/// </summary>
internal static class KeyboardCommitDiffSearchMatrix
{
    // This method stays whole because it is one keyboard script on the commit diff search: after a query is typed, Tab
    // and Shift+Tab move between search and match buttons, and Enter on Next and Space on Previous are checked against
    // the match count captured beforehand, with Invoke fallback diagnostics.
    internal static void RunKeyboardCommitDiffSearchMatrix(CaptureOptions options)
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
}
