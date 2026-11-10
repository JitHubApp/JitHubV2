using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using MarkdownRenderer.Sample.Automation.Applications;
using MarkdownRenderer.Sample.Automation.Probes;
using MarkdownRenderer.Sample.Automation.Uia;

namespace MarkdownRenderer.Sample.Automation.Runs;

/// <summary>
/// The default driver pass: launches the sample once and runs every probe
/// against it in a fixed order, ending with the window-close lifecycle probe.
/// </summary>
internal static class SampleAutomationSuite
{
    internal static int Run(SampleAppLauncher launcher)
    {
        Console.WriteLine($"[automation] launching {launcher.AppPath}");
        launcher.KillExistingInstances();
        var runner = new ProbeRunner(Console.Out, Console.Error);
        using var app = launcher.Launch();
        using var automation = new UIA3Automation();
        try
        {
            var window = SampleAppLauncher.WaitForMainWindow(app, automation);
            SampleAppLauncher.WaitForSampleContent(window, app);
            SampleWindowQueries.TryFocus(window, "main window");
            Thread.Sleep(750);

            RunProbes(runner, window, app, launcher.DisposalEvidencePath);
        }
        finally
        {
            // The lifecycle probe already closed the window; Kill() is a safety
            // net only. Do not call app.Close() again here — the using statement
            // will Dispose the wrapper and a second Close() can throw
            // ObjectDisposedException.
            try { if (!app.HasExited) app.Kill(); }
            catch
            {
                // The process exited between the check and Kill(); nothing is left to stop.
            }
        }

        return runner.WriteSummary();
    }

    /// <summary>
    /// The probe order matters: later probes rely on page state the earlier
    /// ones leave behind, and the lifecycle probe closes the window, so it
    /// stays last.
    /// </summary>
    private static void RunProbes(ProbeRunner runner, Window window, Application app, string disposalEvidencePath)
    {
        runner.Run("automation-tree-shape", () => ShellProbes.ProbeAutomationTreeShape(window));
        runner.Run("rtl-toggle-flips-flow",  () => ShellProbes.ProbeRtlToggle(window));
        runner.Run("sample-navigation-discoverable", () => ShellProbes.ProbeSampleNavigation(window));
        runner.Run("navigation-resets-document-position", () => ShellProbes.ProbeNavigationResetsDocumentPosition(window));
        runner.Run("safe-html-native-and-inert", () => ContentProbes.ProbeSafeHtmlSample(window));
        runner.Run("math-native-clean-page", () => ContentProbes.ProbeMathSample(window));
        runner.Run("audit-matrix-hostile-content", () => AuditMatrixProbes.ProbeAuditMatrixHostileContent(window));
        runner.Run("host-link-scheme-allowlist", () => ContentProbes.ProbeHostLinkSchemeAllowlist(window));
        runner.Run("plain-horizontal-overflow-keyboard", () => AuditMatrixProbes.ProbePlainHorizontalOverflowKeyboard(window));
        runner.Run("text-scale-reflows", () => ShellProbes.ProbeTextScale(window));
        runner.Run("accessibility-lab-text-pattern", () => AccessibilityLabTextProbes.ProbeAccessibilityLabTextPattern(window));
        runner.Run("accessibility-lab-semantic-roles", () => AccessibilityLabTextProbes.ProbeAccessibilityLabSemanticRoles(window));
        runner.Run("accessibility-lab-text-attributes", () => AccessibilityLabTextProbes.ProbeAccessibilityLabTextAttributes(window));
        runner.Run("accessibility-lab-forced-high-contrast", () => AccessibilityLabTextProbes.ProbeAccessibilityLabForcedHighContrast(window));
        runner.Run("accessibility-lab-keyboard-order", () => AccessibilityLabKeyboardProbes.ProbeAccessibilityLabKeyboardOrder(window));
        runner.Run("mermaid-vector-scene-hit-and-uia-invoke", () => MermaidProbes.ProbeMermaidVectorScene(window));
        runner.Run("accessibility-lab-pointer-resume", () => AccessibilityLabKeyboardProbes.ProbeAccessibilityLabPointerResume(window));
        runner.Run("virtualization-bounded-realization", () => ContentProbes.ProbeVirtualization(window));
        runner.Run("images-sample-loads", () => ContentProbes.ProbeImagesSample(window));
        runner.Run("lazy-images-sample-loads", () => ContentProbes.ProbeLazyImagesSample(window));
        runner.Run("scroll-anchor-sample-loads", () => ContentProbes.ProbeScrollAnchorSample(window));
        runner.Run("footnotes-sample-loads", () => ContentProbes.ProbeFootnotesSample(window));
        runner.Run("keyboard-nav-tab-traversal", () => SelectionProbes.ProbeKeyboardNav(window));
        runner.Run("click-dismisses-focus-ring", () => SelectionProbes.ProbeClickDismissesFocus(window));
        runner.Run("selection-dismisses-on-external-pointer", () => SelectionProbes.ProbeSelectionDismissesOnExternalPointer(window));
        runner.Run("selection-dismisses-on-hosted-control-pointer", () => SelectionProbes.ProbeSelectionDismissesOnHostedControlPointer(window));
        runner.Run("selection-persists-after-pointer-release", () => SelectionProbes.ProbeSelectionPersistsAfterPointerRelease(window));
        runner.Run("ctrl-c-copies-pointer-selection", () => SelectionProbes.ProbeCtrlCCopiesPointerSelection(window));
        runner.Run("table-selection-row-border-is-stable", () => SelectionProbes.ProbeTableSelectionRowBorderIsStable(window));
        runner.Run("double-click-selects-word",  () => SelectionProbes.ProbeDoubleClickSelectsWord(window));
        runner.Run("triple-click-selects-line",  () => SelectionProbes.ProbeTripleClickSelectsLine(window));
        runner.Run("context-copy-copies-selection", () => ContextMenuProbes.ProbeContextMenuCopy(window));
        runner.Run("target-aware-context-commands", () => ContextMenuProbes.ProbeTargetAwareContextCommands(window));
        runner.Run("window-close-unloads-renderer", () => LifecycleProbes.ProbeWindowCloseLifecycle(window, app, disposalEvidencePath));
    }
}
