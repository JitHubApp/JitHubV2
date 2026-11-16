using System.Diagnostics;
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
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Probes.RepoCode.RepoCodeNavigation;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.RepoCode;

/// <summary>
/// Drives the repo-code-performance probe, including find, outline and traversal.
/// </summary>
internal static class RepoCodePerformanceProbe
{
    internal static void RunRepoCodePerformanceProbe(CaptureOptions options)
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

    // This method stays whole because it is one ordered pass over editor commands: find, the outline flyout, copy line
    // link and F6 tree focus each run only after Escape dismisses the previous flyout, so the editor focus state
    // carries from one command to the next.
    private static void ExerciseRepoCodeFindOutlineAndTraversal(
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

    private static string WaitForChangingItemStatus(
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
}
