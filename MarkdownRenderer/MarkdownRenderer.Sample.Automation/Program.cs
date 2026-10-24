using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace MarkdownRenderer.Sample.Automation;

internal static class Program
{
    private const string DiagnosticsEnvironmentVariable = "MARKDOWN_RENDERER_DIAGNOSTICS";
    private static readonly string DisposalEvidencePath = Environment.GetEnvironmentVariable("MARKDOWN_RENDERER_DISPOSAL_EVIDENCE") ?? Path.Combine(
        Path.GetTempPath(), $"MarkdownRenderer-disposal-{Guid.NewGuid():N}.json");

    private static readonly List<string> Failures = new();
    private static readonly List<string> Passes = new();

    private static int Main(string[] args)
    {
        int attachIndex = Array.IndexOf(args, "--attach-lifecycle-pid");
        if (attachIndex >= 0)
            return RunAttachedLifecycleProbe(int.Parse(args[attachIndex + 1]));

        string appPath = ParseAppPath(args)
            ?? FindDefaultAppPath()
            ?? throw new InvalidOperationException(
                "Cannot locate MarkdownRenderer.Sample.exe. Pass --app-path <exe>.");

        if (args.Contains("--narrator-smoke", StringComparer.OrdinalIgnoreCase))
            return RunNarratorSmoke(appPath);

        string? requestedProbe = ParseRequestedProbe(args);

        Console.WriteLine($"[automation] launching {appPath}");
        KillExistingApplicationInstances(appPath);
        using var app = LaunchSample(appPath, enableDiagnostics: true);
        using var automation = new UIA3Automation();
        try
        {
            var window = Retry.WhileNull(() =>
                {
                    try { return app.GetMainWindow(automation); }
                    catch { return null; }
                },
                timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(250)).Result
                ?? throw new InvalidOperationException("Main window did not appear.");
            WaitForSampleContent(window, app);
            TryFocus(window, "main window");
            Thread.Sleep(750);

            if (requestedProbe is not null)
            {
                RunRequestedProbe(requestedProbe, window);
            }
            else
            {
                RunProbe("automation-tree-shape", () => ProbeAutomationTreeShape(window));
                RunProbe("rtl-toggle-flips-flow",  () => ProbeRtlToggle(window));
                RunProbe("sample-navigation-discoverable", () => ProbeSampleNavigation(window));
                RunProbe("navigation-resets-document-position", () => ProbeNavigationResetsDocumentPosition(window));
                RunProbe("safe-html-native-and-inert", () => ProbeSafeHtmlSample(window));
                RunProbe("math-native-clean-page", () => ProbeMathSample(window));
                RunProbe("audit-matrix-hostile-content", () => ProbeAuditMatrixHostileContent(window));
                RunProbe("host-link-scheme-allowlist", () => ProbeHostLinkSchemeAllowlist(window));
                RunProbe("plain-horizontal-overflow-keyboard", () => ProbePlainHorizontalOverflowKeyboard(window));
                RunProbe("text-scale-reflows", () => ProbeTextScale(window));
                RunProbe("accessibility-lab-text-pattern", () => ProbeAccessibilityLabTextPattern(window));
                RunProbe("accessibility-lab-semantic-roles", () => ProbeAccessibilityLabSemanticRoles(window));
                RunProbe("accessibility-lab-text-attributes", () => ProbeAccessibilityLabTextAttributes(window));
                RunProbe("accessibility-lab-forced-high-contrast", () => ProbeAccessibilityLabForcedHighContrast(window));
                RunProbe("accessibility-lab-keyboard-order", () => ProbeAccessibilityLabKeyboardOrder(window));
                RunProbe("mermaid-vector-scene-hit-and-uia-invoke", () => ProbeMermaidVectorScene(window));
                RunProbe("accessibility-lab-pointer-resume", () => ProbeAccessibilityLabPointerResume(window));
                RunProbe("virtualization-bounded-realization", () => ProbeVirtualization(window));
                RunProbe("images-sample-loads", () => ProbeImagesSample(window));
                RunProbe("lazy-images-sample-loads", () => ProbeLazyImagesSample(window));
                RunProbe("scroll-anchor-sample-loads", () => ProbeScrollAnchorSample(window));
                RunProbe("footnotes-sample-loads", () => ProbeFootnotesSample(window));
                RunProbe("keyboard-nav-tab-traversal", () => ProbeKeyboardNav(window));
                RunProbe("click-dismisses-focus-ring", () => ProbeClickDismissesFocus(window));
                RunProbe("selection-dismisses-on-external-pointer", () => ProbeSelectionDismissesOnExternalPointer(window));
                RunProbe("selection-dismisses-on-hosted-control-pointer", () => ProbeSelectionDismissesOnHostedControlPointer(window));
                RunProbe("selection-persists-after-pointer-release", () => ProbeSelectionPersistsAfterPointerRelease(window));
                RunProbe("ctrl-c-copies-pointer-selection", () => ProbeCtrlCCopiesPointerSelection(window));
                RunProbe("table-selection-row-border-is-stable", () => ProbeTableSelectionRowBorderIsStable(window));
                RunProbe("double-click-selects-word",  () => ProbeDoubleClickSelectsWord(window));
                RunProbe("triple-click-selects-line",  () => ProbeTripleClickSelectsLine(window));
                RunProbe("context-copy-copies-selection", () => ProbeContextMenuCopy(window));
                RunProbe("target-aware-context-commands", () => ProbeTargetAwareContextCommands(window));
                RunProbe("hover-does-not-shake",       () => ProbeHoverDoesNotShake(window));
                RunProbe("embeds-selection-does-not-shake", () => ProbeEmbedsSelectionDoesNotShake(window));
                RunProbe("window-close-unloads-renderer", () => ProbeWindowCloseLifecycle(window, app));
            }
        }
        finally
        {
            // app.Close() already called above; Kill() as safety net only.
            // Do not call app.Close() again here — the using statement will
            // Dispose the wrapper and a second Close() can throw ObjectDisposedException.
            try { if (!app.HasExited) app.Kill(); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("──────────────── results ────────────────");
        foreach (var p in Passes)   Console.WriteLine($"  PASS {p}");
        foreach (var f in Failures) Console.WriteLine($"  FAIL {f}");
        Console.WriteLine($"{Passes.Count} passed, {Failures.Count} failed");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void ProbeWindowCloseLifecycle(Window window, FlaUI.Core.Application app)
    {
        // Retain the native handle before closing: GetProcessById after exit
        // cannot recover a reliable exit code and PID reuse is not evidence.
        using var process = Process.GetProcessById(app.ProcessId);
        _ = process.Handle;
        window.Close();
        bool exited = process.WaitForExit(10_000);
        bool disposalObserved = false;
        if (File.Exists(DisposalEvidencePath))
        {
            using var evidence = System.Text.Json.JsonDocument.Parse(File.ReadAllText(DisposalEvidencePath));
            disposalObserved = evidence.RootElement.GetProperty("processId").GetInt32() == process.Id &&
                evidence.RootElement.GetProperty("disposalCompleted").GetBoolean();
        }
        string? failure = ProcessExitValidation.GetFailure(exited, exited ? process.ExitCode : null, disposalObserved);
        Assert(failure is null, failure ?? string.Empty);
    }

    private static int RunAttachedLifecycleProbe(int processId)
    {
        // The caller launches through project-mode winapp run. Never terminate
        // unrelated sample instances or substitute UIA selection for mouse input.
        using var app = Application.Attach(processId);
        using var automation = new UIA3Automation();
        var window = app.GetMainWindow(automation) ?? throw new InvalidOperationException("Probe window missing.");
        var status = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId("LifecycleStatus")),
            TimeSpan.FromSeconds(15)).Result ?? throw new InvalidOperationException("Lifecycle status missing.");
        RunProbe("both-viewports-three-native-reloads", () =>
        {
            Retry.WhileTrue(() => status.Name.StartsWith("Running", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert(status.Name.StartsWith("PASS:", StringComparison.Ordinal), status.Name);
        });
        TryFocus(window, "lifecycle window");
        foreach (string id in new[] { "OwnedRenderer", "AncestorRenderer" })
        {
            RunProbe(id + "-mouse-selection-after-reload", () =>
            {
                var renderer = window.FindFirstDescendant(cf => cf.ByAutomationId(id))
                    ?? throw new InvalidOperationException(id + " missing.");
                var range = renderer.Patterns.Text.Pattern.DocumentRange.FindText("quick brown fox", false, false)
                    ?? throw new InvalidOperationException("Text range missing.");
                var rects = range.GetBoundingRectangles();
                Assert(rects.Length > 0, "Text has no rendered bounds after reload.");
                var rect = rects[0];
                var start = new System.Drawing.Point((int)rect.Left + 1, (int)(rect.Top + rect.Height / 2));
                Mouse.Position = start;
                Thread.Sleep(100);
                Assert(Mouse.Position == start, $"Desktop pointer did not reach text: requested={start}, actual={Mouse.Position}, bounds={rect}");
                Console.WriteLine($"[automation] {id} pointer start={start}, bounds={rect}");
                Mouse.Down(MouseButton.Left);
                try
                {
                    for (int step = 1; step <= 12; step++)
                    {
                        var next = new System.Drawing.Point((int)(rect.Left + rect.Width * step / 12), (int)(rect.Top + rect.Height / 2));
                        Mouse.Position = next;
                        Assert(Mouse.Position == next, $"Desktop pointer left drag path: requested={next}, actual={Mouse.Position}");
                        Thread.Sleep(30);
                    }
                }
                finally { Mouse.Up(MouseButton.Left); }
                Thread.Sleep(500);
                string selected = GetRendererSelectionText(renderer);
                Assert(selected.Contains("brown", StringComparison.Ordinal), "Mouse selection was lost: " + selected);
            });
        }
        RunProbe("clean-exit-and-all-view-disposal", () => ProbeWindowCloseLifecycle(window, app));
        Console.WriteLine($"{Passes.Count} passed, {Failures.Count} failed");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static string? ParseRequestedProbe(string[] args)
    {
        int index = Array.IndexOf(args, "--probe");
        if (index < 0)
            return null;
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException("--probe requires a probe name.");
        return args[index + 1];
    }

    private static void RunRequestedProbe(string name, Window window)
    {
        if (name.Equals("accessibility-residuals", StringComparison.OrdinalIgnoreCase))
        {
            RunProbe("accessibility-lab-text-pattern", () => ProbeAccessibilityLabTextPattern(window));
            RunProbe("accessibility-lab-semantic-roles", () => ProbeAccessibilityLabSemanticRoles(window));
            RunProbe("accessibility-lab-text-attributes", () => ProbeAccessibilityLabTextAttributes(window));
            RunProbe("mermaid-vector-scene-hit-and-uia-invoke", () => ProbeMermaidVectorScene(window));
            return;
        }

        if (name.Equals("embeds-selection-does-not-shake", StringComparison.OrdinalIgnoreCase))
        {
            RunProbe("embeds-selection-does-not-shake", () => ProbeEmbedsSelectionDoesNotShake(window));
            return;
        }

        if (name.Equals("hover-does-not-shake", StringComparison.OrdinalIgnoreCase))
        {
            RunProbe("hover-does-not-shake", () => ProbeHoverDoesNotShake(window));
            return;
        }

        if (name.Equals("keyboard-input-residuals", StringComparison.OrdinalIgnoreCase))
        {
            RunProbe("plain-horizontal-overflow-keyboard", () => ProbePlainHorizontalOverflowKeyboard(window));
            return;
        }

        if (name.Equals("virtualization-bounded-realization", StringComparison.OrdinalIgnoreCase))
        {
            RunProbe("virtualization-bounded-realization", () => ProbeVirtualization(window));
            return;
        }

        throw new ArgumentException(
            $"Unknown focused probe '{name}'. Supported probes: accessibility-residuals, embeds-selection-does-not-shake, hover-does-not-shake, keyboard-input-residuals, virtualization-bounded-realization.");
    }

    private static int RunNarratorSmoke(string appPath)
    {
        Console.WriteLine($"[narrator-smoke] launching {appPath}");
        KillExistingApplicationInstances(appPath);
        bool narratorWasRunning = Process.GetProcessesByName("Narrator").Any();
        using var app = LaunchSample(appPath, enableDiagnostics: false);
        using var automation = new UIA3Automation();
        Process? narrator = null;

        try
        {
            var window = Retry.WhileNull(() =>
                {
                    try { return app.GetMainWindow(automation); }
                    catch { return null; }
                },
                timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(250)).Result
                ?? throw new InvalidOperationException("Main window did not appear.");
            WaitForSampleContent(window, app);

            SelectSample(window, "AccessibilityLab");
            Thread.Sleep(1500);

            var renderer = FindRenderer(window);
            renderer.Focus();
            Thread.Sleep(500);

            var textPattern = renderer.Patterns.Text.PatternOrDefault
                              ?? throw new InvalidOperationException("Renderer does not expose TextPattern");
            var quick = textPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                        ?? throw new InvalidOperationException("Could not find 'quick' via TextPattern");
            quick.ExpandToEnclosingUnit(TextUnit.Word);
            var quickRects = quick.GetBoundingRectangles();
            Console.WriteLine($"[narrator-smoke] renderer name: {renderer.Name}");
            Console.WriteLine($"[narrator-smoke] quick rects: {string.Join("; ", quickRects.Select(FormatRect))}");
            Console.WriteLine($"[narrator-smoke] renderer bounds: {FormatRect(renderer.BoundingRectangle)}");

            if (!narratorWasRunning)
            {
                var narratorPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Narrator.exe");
                narrator = Process.Start(new ProcessStartInfo(narratorPath) { UseShellExecute = true });
                Thread.Sleep(3500);
            }
            MinimizeNarratorHome(automation);

            string artifactDir = Path.Combine(Path.GetFullPath("."), "MarkdownRenderer", "artifacts");
            Directory.CreateDirectory(artifactDir);

            renderer.Focus();
            Thread.Sleep(700);

            string beforePath = Path.Combine(artifactDir, "narrator-before-read.png");
            using (var image = FlaUI.Core.Capturing.Capture.ScreensWithElement(renderer, new FlaUI.Core.Capturing.CaptureSettings()))
                image.ToFile(beforePath);
            Console.WriteLine($"[narrator-smoke] before screenshot: {beforePath}");

            Keyboard.TypeSimultaneously(VirtualKeyShort.INSERT, VirtualKeyShort.DOWN);
            Thread.Sleep(1700);

            string afterPath = Path.Combine(artifactDir, "narrator-after-read.png");
            using (var image = FlaUI.Core.Capturing.Capture.ScreensWithElement(renderer, new FlaUI.Core.Capturing.CaptureSettings()))
                image.ToFile(afterPath);
            Console.WriteLine($"[narrator-smoke] after screenshot: {afterPath}");

            Thread.Sleep(1200);
            string laterPath = Path.Combine(artifactDir, "narrator-later-read.png");
            using (var image = FlaUI.Core.Capturing.Capture.ScreensWithElement(renderer, new FlaUI.Core.Capturing.CaptureSettings()))
                image.ToFile(laterPath);
            Console.WriteLine($"[narrator-smoke] later screenshot: {laterPath}");

            var tabFocus = TabIntoRendererFromSampleList(window, renderer);
            Console.WriteLine($"[narrator-smoke] tab-entry focus event: {DescribeFocus(tabFocus)}");
            Thread.Sleep(1200);
            string tabEntryPath = Path.Combine(artifactDir, "narrator-tab-entry.png");
            using (var image = FlaUI.Core.Capturing.Capture.ScreensWithElement(renderer, new FlaUI.Core.Capturing.CaptureSettings()))
                image.ToFile(tabEntryPath);
            Console.WriteLine($"[narrator-smoke] tab-entry screenshot: {tabEntryPath}");

            try { window.Close(); } catch { }
            return 0;
        }
        finally
        {
            if (!narratorWasRunning)
            {
                foreach (var process in Process.GetProcessesByName("Narrator"))
                {
                    try { process.Kill(); } catch { }
                }
            }

            try { if (!app.HasExited) app.Kill(); } catch { }
        }
    }

    private static void MinimizeNarratorHome(UIA3Automation automation)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                var desktop = automation.GetDesktop();
                var narratorWindow = desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window))
                    .FirstOrDefault(w => (w.Name ?? string.Empty).Contains("Narrator", StringComparison.OrdinalIgnoreCase));
                if (narratorWindow is null)
                {
                    Thread.Sleep(250);
                    continue;
                }

                var minimizeButton = narratorWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b =>
                    {
                        var name = b.Name ?? string.Empty;
                        return name.Equals("Minimise", StringComparison.OrdinalIgnoreCase) ||
                               name.Equals("Minimize", StringComparison.OrdinalIgnoreCase);
                    });
                if (minimizeButton is not null)
                {
                    minimizeButton.AsButton().Invoke();
                    Thread.Sleep(600);
                    return;
                }

                var windowPattern = narratorWindow.Patterns.Window.PatternOrDefault;
                if (windowPattern is not null)
                {
                    windowPattern.SetWindowVisualState(WindowVisualState.Minimized);
                    Thread.Sleep(600);
                    return;
                }
            }
            catch
            {
            }

            Thread.Sleep(250);
        }
    }

    private static void ProbeAutomationTreeShape(Window window)
    {
        var renderer = FindRenderer(window);
        var name = renderer.Name ?? string.Empty;
        Assert(!string.IsNullOrWhiteSpace(name), "renderer.Name must expose a short document label");
        Assert(name.Length <= 120, $"renderer.Name must stay short so Narrator reads content through TextPattern, got length {name.Length}");
        try
        {
            var landmarkType = renderer.Properties.LandmarkType.ValueOrDefault;
            Assert(!string.Equals(landmarkType.ToString(), "Custom", StringComparison.OrdinalIgnoreCase),
                $"renderer must not expose itself as a custom landmark, got LandmarkType={landmarkType}");
        }
        catch (NotSupportedException)
        {
            // UIA providers may omit LandmarkType entirely; that is acceptable
            // and preferable to exposing this document as a custom landmark.
        }
        Assert(renderer.Properties.IsControlElement.ValueOrDefault,
            "renderer must be present in the UIA control view");
        Assert(renderer.Properties.IsContentElement.ValueOrDefault,
            "renderer must be present in the UIA content view");
        var descendants = renderer.FindAllDescendants();
        Assert(descendants.Length > 0, "renderer must expose block peers as descendants");
    }

    private static void ProbeRtlToggle(Window window)
    {
        var rtl = window.FindFirstDescendant(cf => cf.ByAutomationId("RtlToggle"))?.AsToggleButton()
                  ?? throw new InvalidOperationException("RtlToggle not found");
        Assert(rtl.ToggleState == ToggleState.Off, "RTL must start OFF");
        Assert(ReadFlowDirection(window) == "ltr", "renderer must start LTR");

        rtl.Toggle();
        Thread.Sleep(350);
        Assert(rtl.ToggleState == ToggleState.On, "RTL must report ON after toggling");
        Assert(ReadFlowDirection(window) == "rtl", "renderer FlowDirection mirror must be 'rtl' after toggle");

        rtl.Toggle();
        Thread.Sleep(350);
        Assert(rtl.ToggleState == ToggleState.Off, "RTL must return to OFF after re-toggling");
        Assert(ReadFlowDirection(window) == "ltr", "renderer FlowDirection mirror must be 'ltr' after re-toggle");
    }

    private static string ReadFlowDirection(Window window)
    {
        var el = FindFirstRawDescendantByAutomationId(window, "FlowDirectionStatus");
        string? text = el?.Name ?? el?.Properties.Name.ValueOrDefault;
        if (string.IsNullOrEmpty(text)) return string.Empty;
        const string prefix = "flow:";
        int idx = text.IndexOf(prefix, StringComparison.Ordinal);
        return idx < 0 ? string.Empty : text.Substring(idx + prefix.Length).Trim();
    }

    private static void ProbeSampleNavigation(Window window)
    {
        Assert(
            window.FindFirstDescendant(cf => cf.ByAutomationId("SampleNavigation")) is not null,
            "SampleNavigation not found in automation tree");

        AutomationElement pageTitle = window.FindFirstDescendant(
            cf => cf.ByAutomationId("SamplePageTitle"))
            ?? throw new InvalidOperationException("SamplePageTitle not found in automation tree");
        Assert(pageTitle.Properties.HeadingLevel.ValueOrDefault == HeadingLevel.Level1,
            "SamplePageTitle must expose heading level 1");

        foreach (string statusId in new[]
                 {
                     "RealizedEmbedCount", "FlowDirectionStatus", "HighContrastStatus",
                     "TextScaleStatus", "LinkActivationStatus", "ThemeStatus", "CurrentSamplePage",
                 })
        {
            AutomationElement status = FindFirstRawDescendantByAutomationId(window, statusId)
                ?? throw new InvalidOperationException($"{statusId} raw automation status not found");
            Assert(!status.Properties.IsControlElement.ValueOrDefault &&
                   !status.Properties.IsContentElement.ValueOrDefault,
                $"{statusId} must remain outside the assistive-technology control/content views");
        }

        string[] expected =
        {
            "FullDemo", "Typography", "Lists", "Tables", "Code", "Images",
            "GitHubAlerts", "Footnotes", "MarkdownExtra", "Html", "Math",
            "Mermaid", "Diagrams", "Embeds", "Selection", "KeyboardNav",
            "Rtl", "LazyImages", "ScrollAnchor", "Virtualization", "Stress",
            "AccessibilityLab", "AuditMatrix",
        };
        foreach (string key in expected)
        {
            var item = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleNav_" + key))
                ?? throw new InvalidOperationException($"SampleNav_{key} not found in automation tree");
            Assert(item.Patterns.SelectionItem.IsSupported,
                $"SampleNav_{key} must expose SelectionItem semantics");
        }
    }

    private static void ProbeNavigationResetsDocumentPosition(Window window)
    {
        SelectSample(window, "FullDemo");
        AutomationElement renderer = FindRenderer(window);
        var scroll = renderer.Patterns.Scroll.PatternOrDefault
                     ?? throw new InvalidOperationException("Markdown renderer must expose ScrollPattern");
        Assert(scroll.VerticallyScrollable.ValueOrDefault,
            "Full demo must be vertically scrollable for the navigation reset probe");

        scroll.SetScrollPercent(-1, 100);
        bool reachedBottom = Retry.WhileFalse(
            () => scroll.VerticalScrollPercent.ValueOrDefault >= 95,
            timeout: TimeSpan.FromSeconds(3),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(reachedBottom, "Could not move the Full demo preview to the bottom");

        SelectSample(window, "Mermaid");
        bool rendered = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains("Native Mermaid", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(rendered, "Mermaid page did not render after navigation");

        bool reset = Retry.WhileFalse(
            () =>
            {
                double percent = scroll.VerticalScrollPercent.ValueOrDefault;
                return percent < 0 || percent <= 0.5;
            },
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(reset, "Navigation carried the previous document's scroll position into Mermaid");
    }

    private static void ProbeSafeHtmlSample(Window window)
    {
        SelectSample(window, "Html");
        var renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains(
                "Native safe HTML",
                StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(ready, "Safe HTML sample did not render");

        string documentText = GetRendererDocumentText(renderer);
        Assert(documentText.Contains("HTML disclosure body", StringComparison.Ordinal),
            "expanded HTML details content is missing from TextPattern");
        Assert(documentText.Contains("<custom-card>", StringComparison.Ordinal),
            "unknown HTML element was not retained as inert literal source");
        Assert(documentText.Contains(
                "<script>unsafe-script-sentinel</script>",
                StringComparison.Ordinal),
            "tag-filtered script did not remain visible as inert literal source");
        Assert(!documentText.Contains("unsafe-frame-sentinel", StringComparison.Ordinal) &&
               !documentText.Contains("unsafe-form-sentinel", StringComparison.Ordinal),
            "interactive HTML leaked into the rendered document");

        AutomationElement[] descendants = renderer.FindAllDescendants();
        AutomationElement disclosure = descendants.FirstOrDefault(element =>
            ClassNameOrEmpty(element) == "MarkdownDisclosure")
            ?? throw new InvalidOperationException("HTML details summary did not expose MarkdownDisclosure");
        Assert(disclosure.Patterns.ExpandCollapse.IsSupported,
            "HTML details summary must expose ExpandCollapsePattern");
        Assert(descendants.Any(element => ClassNameOrEmpty(element) == "MarkdownTable"),
            "HTML table did not retain native table semantics");
        Assert(descendants.Any(element =>
                ClassNameOrEmpty(element) == "MarkdownLink" &&
                NameOrEmpty(element).Contains("safe HTML example", StringComparison.Ordinal)),
            "safe HTML link did not retain native hyperlink semantics");

        disclosure.Patterns.ExpandCollapse.Pattern.Collapse();
        bool collapsed = Retry.WhileFalse(
            () => !GetRendererDocumentText(renderer).Contains(
                "HTML disclosure body",
                StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(collapsed, "collapsing HTML details did not update the document text surface");

        disclosure = renderer.FindAllDescendants().First(element =>
            ClassNameOrEmpty(element) == "MarkdownDisclosure");
        disclosure.Patterns.ExpandCollapse.Pattern.Expand();
        bool expanded = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains(
                "HTML disclosure body",
                StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(expanded, "expanding HTML details did not restore its document text");
    }

    private static void ProbeMathSample(Window window)
    {
        const int ExpectedFormulaCount = 27;
        SelectSample(window, "Math");
        AutomationElement renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () =>
            {
                string documentText = GetRendererDocumentText(renderer);
                int nativeFormulaCount = renderer.FindAllDescendants()
                    .Count(element => ClassNameOrEmpty(element) == "MarkdownMath");
                return documentText.Contains("Native mathematics", StringComparison.Ordinal) &&
                       documentText.Contains("Matrix multiplication", StringComparison.Ordinal) &&
                       documentText.Contains("Aligned derivation", StringComparison.Ordinal) &&
                       nativeFormulaCount == ExpectedFormulaCount;
            },
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(ready, $"Math page must expose {ExpectedFormulaCount} native formulas");

        string renderedText = GetRendererDocumentText(renderer);
        Assert(!renderedText.Contains("\\notacommand{sample}", StringComparison.Ordinal),
            "the primary Math page must not contain the invalid-formula diagnostics fixture");
        AutomationElement? diagnostics = window.FindFirstDescendant(
            cf => cf.ByAutomationId("SampleDiagnosticsMessage"));
        Assert(diagnostics is null ||
               diagnostics.IsOffscreen ||
               string.IsNullOrEmpty(GetAutomationElementText(diagnostics)),
            "the primary Math page must commit without renderer diagnostics");
    }

    private static void ProbeAuditMatrixHostileContent(Window window)
    {
        var stopwatch = Stopwatch.StartNew();
        SelectSample(window, "AuditMatrix");

        var renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains(
                "Document remains responsive after hostile content",
                StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(8),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        stopwatch.Stop();
        Assert(ready, "audit matrix did not finish rendering its final marker");
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(8),
            $"audit matrix first render exceeded the interaction budget: {stopwatch.Elapsed.TotalMilliseconds:0} ms");

        string documentText = GetRendererDocumentText(renderer);
        Assert(documentText.Contains("Completed audit task", StringComparison.Ordinal),
            "audit matrix task-list content is missing from TextPattern");
        Assert(documentText.Contains("Expected behavior", StringComparison.Ordinal),
            "audit matrix table content is missing from TextPattern");
        Assert(documentText.Contains("public static string Audit", StringComparison.Ordinal),
            "audit matrix code content is missing from TextPattern");
        Assert(documentText.Contains("\\notacommand{sample}", StringComparison.Ordinal),
            "audit matrix must preserve the invalid formula as exact fallback source");
        Assert(documentText.Contains("layout: elk", StringComparison.Ordinal),
            "audit matrix must preserve the unsupported ELK request as exact fallback source");

        var descendants = renderer.FindAllDescendants();
        Assert(descendants.All(e => e.ControlType != ControlType.CheckBox),
            "read-only task state must not be exposed as an interactive checkbox");
        Assert(descendants.Any(e => e.ControlType == ControlType.Table),
            "audit matrix must expose native table semantics");
        Assert(descendants.Any(e => e.ControlType == ControlType.Image &&
                                    (e.Name ?? string.Empty).Contains("Safe blue audit square", StringComparison.Ordinal)),
            "audit matrix safe SVG must expose image semantics");

        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        var finalMarker = textPattern.DocumentRange.FindText(
            "Document remains responsive after hostile content", backward: false, ignoreCase: false)
            ?? throw new InvalidOperationException("audit matrix final marker range not found");
        finalMarker.ScrollIntoView(alignToTop: false);
        Thread.Sleep(500);
        var finalRects = finalMarker.GetBoundingRectangles();
        Assert(finalRects.Length > 0 && finalRects.All(r => r.Width <= renderer.BoundingRectangle.Width + 2),
            "hostile SVG/HTML caused text geometry to escape the renderer viewport");

        CaptureAuditScreenshot(renderer, "audit-matrix-hostile-content.png");

        AutomationElement diagnostics = WaitForDiagnostics(window, "MATH100");
        string diagnosticText = GetAutomationElementText(diagnostics);
        Assert(diagnosticText.Contains("Error MATH100", StringComparison.Ordinal),
            $"Math diagnostic must expose severity and code, got '{diagnosticText}'");
        string? mermaidDiagnosticLine = diagnosticText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(static line => line.Contains("MMR0002", StringComparison.Ordinal));
        Assert(mermaidDiagnosticLine is not null &&
               mermaidDiagnosticLine.Contains("Error MMR0002", StringComparison.Ordinal) &&
               mermaidDiagnosticLine.Contains("[", StringComparison.Ordinal) &&
               mermaidDiagnosticLine.Contains("..", StringComparison.Ordinal) &&
               mermaidDiagnosticLine.Contains("): ", StringComparison.Ordinal),
            $"Mermaid diagnostic must expose severity, code, and a half-open UTF-16 source span, got '{diagnosticText}'");
        Assert(diagnosticText.Contains("[", StringComparison.Ordinal) &&
               diagnosticText.Contains("..", StringComparison.Ordinal) &&
               diagnosticText.Contains("): ", StringComparison.Ordinal),
            $"Math diagnostic must expose a half-open UTF-16 source span, got '{diagnosticText}'");
        Assert(diagnostics.Patterns.Text.IsSupported,
            "diagnostic details must expose selectable TextPattern content");

        AutomationElement diagnosticsBar = window.FindFirstDescendant(
            cf => cf.ByAutomationId("SampleDiagnostics"))
            ?? throw new InvalidOperationException("open diagnostics InfoBar not found");
        Assert(NameOrEmpty(diagnosticsBar).Contains("MATH100", StringComparison.Ordinal),
            "diagnostics live-region summary must include the actionable diagnostic code");

        using var diagnosticsResolved = new ManualResetEventSlim(false);
        string resolutionNotification = string.Empty;
        var notificationHandler = diagnosticsBar.RegisterNotificationEvent(
            TreeScope.Element,
            (_, kind, processing, displayString, activityId) =>
            {
                if (kind == NotificationKind.ActionCompleted &&
                    processing == NotificationProcessing.MostRecent &&
                    string.Equals(activityId, "RendererDiagnostics", StringComparison.Ordinal))
                {
                    resolutionNotification = displayString;
                    diagnosticsResolved.Set();
                }
            });
        try
        {
            SelectSample(window, "Typography");
            Assert(diagnosticsResolved.Wait(TimeSpan.FromSeconds(5)),
                "clearing committed diagnostics did not raise an accessibility resolution notification");
            Assert(resolutionNotification.Contains("No renderer diagnostics", StringComparison.Ordinal),
                $"diagnostics resolution notification was not actionable, got '{resolutionNotification}'");

            bool cleared = Retry.WhileFalse(
                () =>
                {
                    AutomationElement? current = window.FindFirstDescendant(
                        cf => cf.ByAutomationId("SampleDiagnosticsMessage"));
                    return current is null ||
                           current.IsOffscreen ||
                           string.IsNullOrEmpty(GetAutomationElementText(current));
                },
                timeout: TimeSpan.FromSeconds(5),
                interval: TimeSpan.FromMilliseconds(100)).Result;
            Assert(cleared, "audit diagnostics persisted after a valid document committed");
        }
        finally
        {
            diagnosticsBar.FrameworkAutomationElement.UnregisterNotificationEventHandler(notificationHandler);
        }
    }

    private static void ProbeHostLinkSchemeAllowlist(Window window)
    {
        SelectSample(window, "Typography");
        var editor = window.FindFirstDescendant(cf => cf.ByAutomationId("MarkdownEditor"))?.AsTextBox()
            ?? throw new InvalidOperationException("Markdown source editor not found");
        editor.Text = "# Host link policy\n\n[Blocked custom URI](jithub-unsafe://example/path)";

        AutomationElement renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains("Blocked custom URI", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(ready, "custom-scheme link did not render for the host policy probe");

        AutomationElement blockedLink = renderer.FindAllDescendants().FirstOrDefault(element =>
            ClassNameOrEmpty(element) == "MarkdownLink" &&
            NameOrEmpty(element).Contains("Blocked custom URI", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("custom-scheme hyperlink peer not found");
        blockedLink.Patterns.Invoke.Pattern.Invoke();

        bool blocked = Retry.WhileFalse(
            () => (FindFirstRawDescendantByAutomationId(window, "LinkActivationStatus")?.Name ?? string.Empty)
                .Contains("jithub-unsafe://example/path::blocked", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(3),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(blocked, "sample host did not reject a non-allowlisted URI scheme");

        SelectSample(window, "Lists");
    }

    private static void ProbePlainHorizontalOverflowKeyboard(Window window)
    {
        SelectSample(window, "AuditMatrix");
        AutomationElement renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains(
                "Document remains responsive after hostile content",
                StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(8),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(ready, "audit matrix did not finish rendering before the overflow keyboard probe");

        AutomationElement overflow = FindPlainHorizontalOverflow(renderer);
        var scroll = overflow.Patterns.Scroll.PatternOrDefault
                     ?? throw new InvalidOperationException("plain overflow table must expose ScrollPattern");
        scroll.SetScrollPercent(0, -1);
        bool atStart = Retry.WhileFalse(
            () => ReadPlainHorizontalScrollPercent(window) <= 0.5,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(40)).Result;
        Assert(atStart,
            $"overflow table did not reset to the start, percent={ReadPlainHorizontalScrollPercent(window):0.##}");

        overflow.Focus();
        bool focused = Retry.WhileFalse(
            () => overflow.Properties.HasKeyboardFocus.ValueOrDefault,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(40)).Result;
        Assert(focused, "plain overflow peer did not take keyboard focus");

        Keyboard.Press(VirtualKeyShort.RIGHT);
        bool movedRight = Retry.WhileFalse(
            () => ReadPlainHorizontalScrollPercent(window) > 0.5,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(40)).Result;
        Assert(movedRight,
            $"Right must scroll a focused plain overflow before spatial navigation, percent={ReadPlainHorizontalScrollPercent(window):0.##}");
        Assert(ReadPlainHorizontalOverflowFocus(window),
            "Right moved focus away from the plain overflow surface");

        Keyboard.Press(VirtualKeyShort.END);
        bool atEnd = Retry.WhileFalse(
            () => ReadPlainHorizontalScrollPercent(window) >= 99.5,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(40)).Result;
        Assert(atEnd,
            $"End must move plain overflow to its trailing edge, percent={ReadPlainHorizontalScrollPercent(window):0.##}");

        Keyboard.Press(VirtualKeyShort.LEFT);
        bool movedLeft = Retry.WhileFalse(
            () => ReadPlainHorizontalScrollPercent(window) < 99.5,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(40)).Result;
        Assert(movedLeft,
            $"Left must scroll a focused plain overflow before spatial navigation, percent={ReadPlainHorizontalScrollPercent(window):0.##}");
        Assert(ReadPlainHorizontalOverflowFocus(window),
            "Left moved focus away from the plain overflow surface");

        Keyboard.Press(VirtualKeyShort.HOME);
        bool returnedHome = Retry.WhileFalse(
            () => ReadPlainHorizontalScrollPercent(window) <= 0.5,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(40)).Result;
        Assert(returnedHome,
            $"Home must move plain overflow to its leading edge, percent={ReadPlainHorizontalScrollPercent(window):0.##}");
    }

    private static AutomationElement FindPlainHorizontalOverflow(AutomationElement renderer) =>
        renderer.FindAllDescendants(cf => cf.ByControlType(ControlType.Table))
            .FirstOrDefault(element =>
                element.Patterns.Scroll.PatternOrDefault?.HorizontallyScrollable.ValueOrDefault == true)
        ?? throw new InvalidOperationException(
            "audit matrix must expose a link-free horizontally scrollable table");

    private static double ReadPlainHorizontalScrollPercent(Window window)
    {
        try
        {
            return FindPlainHorizontalOverflow(FindRenderer(window))
                .Patterns.Scroll.PatternOrDefault?
                .HorizontalScrollPercent.ValueOrDefault ?? double.NaN;
        }
        catch (Exception ex) when (
            ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return double.NaN;
        }
    }

    private static bool ReadPlainHorizontalOverflowFocus(Window window)
    {
        try
        {
            return FindPlainHorizontalOverflow(FindRenderer(window))
                .Properties.HasKeyboardFocus.ValueOrDefault;
        }
        catch (Exception ex) when (
            ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void ProbeTextScale(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1000);

        var renderer = FindRenderer(window);
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        var attributes = renderer.Automation.TextAttributeLibrary;
        var range = textPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                    ?? throw new InvalidOperationException("text-scale marker range not found");
        Assert(TryAttributeNumber(range.GetAttributeValue(attributes.FontSize), out double normalSize),
            "normal text FontSize attribute is unavailable");

        var toggle = window.FindFirstDescendant(cf => cf.ByAutomationId("TextScaleToggle"))?.AsToggleButton()
                     ?? throw new InvalidOperationException("TextScaleToggle not found");
        try
        {
            if (toggle.ToggleState != ToggleState.On)
                toggle.Toggle();

            bool scaled = Retry.WhileFalse(() =>
                {
                    var currentRenderer = FindRenderer(window);
                    var currentPattern = currentRenderer.Patterns.Text.PatternOrDefault;
                    var currentRange = currentPattern?.DocumentRange.FindText("quick", backward: false, ignoreCase: true);
                    return currentRange is not null &&
                           TryAttributeNumber(currentRange.GetAttributeValue(currentRenderer.Automation.TextAttributeLibrary.FontSize), out double size) &&
                           size >= normalSize * 1.8;
                },
                timeout: TimeSpan.FromSeconds(8),
                interval: TimeSpan.FromMilliseconds(150)).Result;
            Assert(scaled, "200% text scale did not reflow the rendered document");

            var status = FindFirstRawDescendantByAutomationId(window, "TextScaleStatus");
            Assert((status?.Name ?? string.Empty).Contains("text-scale:2", StringComparison.Ordinal),
                "text scale status did not report the active 200% scale");
            CaptureAuditScreenshot(FindRenderer(window), "accessibility-text-scale-200.png");
        }
        finally
        {
            if (toggle.ToggleState == ToggleState.On)
            {
                toggle.Toggle();
                Thread.Sleep(600);
            }
        }
    }

    private static void ProbeMermaidVectorScene(Window window)
    {
        const int ExpectedDiagramCount = 17;
        SelectSample(window, "Mermaid");
        var renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () => renderer.FindAllDescendants().Any(element =>
                element.ControlType == ControlType.Hyperlink &&
                (element.Name ?? string.Empty).Contains("Invokable Mermaid node", StringComparison.Ordinal)),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(
            ready,
            "native Mermaid hyperlink did not appear in the UIA tree; document=" +
            GetRendererDocumentText(renderer) +
            "; descendants=" +
            string.Join(" | ", renderer.FindAllDescendants().Select(static element =>
                $"{element.ControlType}/{NameOrEmpty(element)}/id={AutomationIdOrEmpty(element)}/class={ClassNameOrEmpty(element)}")));

        AutomationElement? diagnostics = window.FindFirstDescendant(
            cf => cf.ByAutomationId("SampleDiagnosticsMessage"));
        Assert(diagnostics is null ||
               diagnostics.IsOffscreen ||
               string.IsNullOrEmpty(GetAutomationElementText(diagnostics)),
            "the primary Mermaid page must commit without renderer diagnostics");

        AutomationElement[] pageDescendants = renderer.FindAllDescendants();
        int diagramCount = pageDescendants.Count(element =>
            ClassNameOrEmpty(element) == "MarkdownDiagram");
        Assert(
            diagramCount == ExpectedDiagramCount,
            $"Mermaid page must expose {ExpectedDiagramCount} native diagrams, got {diagramCount}");
        string initialDocumentText = GetRendererDocumentText(renderer);
        Assert(
            initialDocumentText.Contains("Class diagram", StringComparison.Ordinal) &&
            initialDocumentText.Contains("XY chart", StringComparison.Ordinal) &&
            initialDocumentText.Contains("Kanban", StringComparison.Ordinal),
            "Mermaid page is missing representative standard or beta grammar families");

        AutomationElement link = pageDescendants
            .First(element => element.ControlType == ControlType.Hyperlink &&
                (element.Name ?? string.Empty).Contains("Invokable Mermaid node", StringComparison.Ordinal));
        Assert(link.Patterns.Invoke.IsSupported, "Mermaid hyperlink must expose InvokePattern");

        var linkBounds = link.BoundingRectangle;
        Assert(linkBounds.Width > 1 && linkBounds.Height > 1,
            $"Mermaid hyperlink must expose non-empty hit-test bounds, got {FormatRect(linkBounds)}");
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        var attributes = renderer.Automation.TextAttributeLibrary;
        var linkRange = textPattern.RangeFromChild(link);
        Assert(
            string.Equals(linkRange.GetText(-1).Trim(), "Invokable Mermaid node", StringComparison.Ordinal),
            $"Mermaid RangeFromChild must be child-granular, got '{linkRange.GetText(-1)}'");
        Assert(
            (linkRange.GetAttributeValue(attributes.StyleName)?.ToString() ?? string.Empty)
                .Contains("Diagram", StringComparison.Ordinal),
            "Mermaid child text must expose the block-vector Diagram style");
        Assert(
            TryAttributeNumber(linkRange.GetAttributeValue(attributes.FontSize), out double normalDiagramFontSize),
            "Mermaid child text must expose a numeric FontSize");
        Assert(
            (linkRange.GetAttributeValue(attributes.FontName)?.ToString() ?? string.Empty)
                .Contains("Cascadia Mono", StringComparison.OrdinalIgnoreCase),
            "Diagram.FontFamily resource customization must reach Mermaid vector text");
        var formatRange = linkRange.Clone();
        formatRange.ExpandToEnclosingUnit(TextUnit.Format);
        Assert(
            formatRange.GetText(-1).Contains("Invokable Mermaid node", StringComparison.Ordinal) &&
            !formatRange.GetText(-1).Contains("Native scene", StringComparison.Ordinal),
            "TextUnit.Format must stop at the semantic vector-child boundary");
        var pointRange = textPattern.RangeFromPoint(new System.Drawing.Point(
            linkBounds.Left + linkBounds.Width / 3,
            linkBounds.Top + linkBounds.Height / 2));
        pointRange.ExpandToEnclosingUnit(TextUnit.Format);
        Assert(
            pointRange.GetText(-1).Contains("Invokable Mermaid node", StringComparison.Ordinal),
            "TextPattern.RangeFromPoint must resolve linked-only Mermaid semantics");

        var textScaleToggle = window.FindFirstDescendant(cf => cf.ByAutomationId("TextScaleToggle"))?.AsToggleButton()
                              ?? throw new InvalidOperationException("TextScaleToggle not found");
        string committedThemeStatus =
            FindFirstRawDescendantByAutomationId(window, "ThemeStatus")?.Name ?? string.Empty;
        Assert(
            committedThemeStatus.StartsWith("theme:", StringComparison.Ordinal) &&
            !string.Equals(committedThemeStatus, "theme:pending", StringComparison.Ordinal),
            $"Mermaid theme was not committed before the scale probe: '{committedThemeStatus}'");
        try
        {
            if (textScaleToggle.ToggleState != ToggleState.On)
                textScaleToggle.Toggle();

            bool vectorScaled = Retry.WhileFalse(() =>
                {
                    AutomationElement? currentLink = FindMermaidLink(renderer);
                    if (currentLink is null)
                        return false;
                    var currentPattern = renderer.Patterns.Text.PatternOrDefault;
                    var currentRange = currentPattern?.RangeFromChild(currentLink);
                    return currentRange is not null &&
                           TryAttributeNumber(currentRange.GetAttributeValue(attributes.FontSize), out double scaledFontSize) &&
                           scaledFontSize >= normalDiagramFontSize * 1.8d &&
                           currentLink.BoundingRectangle.Height >= linkBounds.Height * 1.8;
                },
                timeout: TimeSpan.FromSeconds(8),
                interval: TimeSpan.FromMilliseconds(150)).Result;
            Assert(vectorScaled, "200% text scale did not resize the Mermaid block and semantic link bounds");
        }
        finally
        {
            if (textScaleToggle.ToggleState == ToggleState.On)
            {
                textScaleToggle.Toggle();
                bool scaleResetCommitted = Retry.WhileFalse(
                    () => string.Equals(
                        FindFirstRawDescendantByAutomationId(window, "ThemeStatus")?.Name,
                        committedThemeStatus,
                        StringComparison.Ordinal),
                    timeout: TimeSpan.FromSeconds(8),
                    interval: TimeSpan.FromMilliseconds(100)).Result;
                Assert(
                    scaleResetCommitted,
                    "text-scale reset did not commit before pointer bounds were reacquired");
            }
        }

        link = FindMermaidLink(renderer)
               ?? throw new InvalidOperationException("Mermaid hyperlink disappeared after text-scale rebuild");
        linkBounds = link.BoundingRectangle;
        var linkPoint = new System.Drawing.Point(
            // Stay inside the first text half. The vector text hit-test changes
            // its insertion position at the exact horizontal midpoint, so a
            // one-pixel press/release rounding difference there can look like a
            // drag selection and correctly suppress link activation.
            linkBounds.Left + linkBounds.Width / 3,
            linkBounds.Top + linkBounds.Height / 2);
        window.SetForeground();
        window.FocusNative();
        PositionPointerOnWritableInputDesktop(linkPoint);
        Mouse.Click(linkPoint, FlaUI.Core.Input.MouseButton.Left);
        bool pointerActivated = Retry.WhileFalse(
            () => (FindFirstRawDescendantByAutomationId(window, "LinkActivationStatus")?.Name ?? string.Empty)
                .Contains("link:Mouse:https://example.invalid/mermaid-node", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(
            pointerActivated,
            "pointer hit testing did not raise the renderer's host LinkClick event; " +
            $"bounds={linkBounds}, point={linkPoint}, status='{FindFirstRawDescendantByAutomationId(window, "LinkActivationStatus")?.Name}'");

        link.Patterns.Invoke.Pattern.Invoke();

        bool activated = Retry.WhileFalse(
            () => (FindFirstRawDescendantByAutomationId(window, "LinkActivationStatus")?.Name ?? string.Empty)
                .Contains("link:Automation:https://example.invalid/mermaid-node", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(activated, "UIA Invoke did not raise the renderer's host LinkClick event");

        link.Focus();
        bool focused = Retry.WhileFalse(
            () => link.Properties.HasKeyboardFocus.ValueOrDefault,
            timeout: TimeSpan.FromSeconds(3),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(focused, "UIA SetFocus did not report keyboard focus on the Mermaid semantic hyperlink");
        Keyboard.Press(VirtualKeyShort.RETURN);
        bool keyboardActivated = Retry.WhileFalse(
            () => (FindFirstRawDescendantByAutomationId(window, "LinkActivationStatus")?.Name ?? string.Empty)
                .Contains("link:Keyboard:https://example.invalid/mermaid-node", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(50)).Result;
        Assert(keyboardActivated, "keyboard activation did not use the same Mermaid link path as pointer/UIA");

        string documentText = GetRendererDocumentText(renderer);
        Assert(documentText.Contains("Invokable Mermaid node", StringComparison.Ordinal),
            "Mermaid semantic labels must contribute to TextPattern");
        Assert(!documentText.Contains("layout: elk", StringComparison.Ordinal),
            "the primary Mermaid page must not contain the unsupported-layout diagnostics fixture");
        Assert(renderer.FindAllDescendants().Any(element => element.ControlType == ControlType.Image),
            "Mermaid diagram root must expose an image/diagram UIA peer");

        ProbeMermaidHighContrastPixelsAndTextAttributes(window);
    }

    private static void ProbeMermaidHighContrastPixelsAndTextAttributes(Window window)
    {
        var toggle = window.FindFirstDescendant(cf => cf.ByAutomationId("ForcedHighContrastToggle"))?.AsToggleButton()
                     ?? throw new InvalidOperationException("ForcedHighContrastToggle not found");

        try
        {
            if (toggle.ToggleState != ToggleState.On)
            {
                toggle.Toggle();
                Thread.Sleep(1200);
            }

            AutomationElement renderer = FindRenderer(window);
            bool ready = Retry.WhileFalse(
                () => FindMermaidLink(renderer) is not null &&
                      FindMermaidSemantic(renderer, "Native scene") is not null,
                timeout: TimeSpan.FromSeconds(8),
                interval: TimeSpan.FromMilliseconds(100)).Result;
            Assert(ready, "High Contrast Mermaid semantic nodes did not become available");

            // The keyboard-invoke portion of this probe leaves the linked
            // semantic focused. Move real focus outside the renderer so its
            // LosingFocus path clears the logical vector focus before pixel
            // capture; focusing an already-focused renderer can be a UIA no-op.
            // This keeps the Hotlight assertion on glyph paint rather than a
            // cyan focus rectangle.
            toggle.Focus();
            bool vectorFocusCleared = Retry.WhileFalse(
                () => FindMermaidLink(renderer)?.Properties.HasKeyboardFocus.ValueOrDefault != true,
                timeout: TimeSpan.FromSeconds(3),
                interval: TimeSpan.FromMilliseconds(50)).Result;
            Assert(vectorFocusCleared, "Mermaid vector focus remained active before High Contrast capture");

            AutomationElement linked = FindMermaidLink(renderer)
                                       ?? throw new InvalidOperationException("High Contrast Mermaid link not found");
            AutomationElement plain = FindMermaidSemantic(renderer, "Native scene")
                                      ?? throw new InvalidOperationException("High Contrast Mermaid non-link node not found");
            var textPattern = renderer.Patterns.Text.PatternOrDefault
                              ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
            var attributes = renderer.Automation.TextAttributeLibrary;
            var linkedRange = textPattern.RangeFromChild(linked);
            var plainRange = textPattern.RangeFromChild(plain);

            Assert(
                (linkedRange.GetAttributeValue(attributes.StyleName)?.ToString() ?? string.Empty)
                    .Contains("Diagram", StringComparison.Ordinal),
                "linked vector text must retain its Diagram semantic style context");
            Assert(
                (plainRange.GetAttributeValue(attributes.StyleName)?.ToString() ?? string.Empty)
                    .Contains("Diagram", StringComparison.Ordinal),
                "non-linked vector text must retain its Diagram semantic style context");
            Assert(
                ToColorRefValue(linkedRange.GetAttributeValue(attributes.ForegroundColor)) ==
                ColorRef(0x00, 0xFF, 0xFF),
                "linked High Contrast vector text must expose the draw-time Hotlight foreground");
            Assert(
                ToColorRefValue(plainRange.GetAttributeValue(attributes.ForegroundColor)) ==
                ColorRef(0xFF, 0xFF, 0xFF),
                "non-linked High Contrast vector text must expose the draw-time WindowText foreground");
            Assert(
                ToColorRefValue(linkedRange.GetAttributeValue(attributes.BackgroundColor)) ==
                ColorRef(0x00, 0x00, 0x00) &&
                ToColorRefValue(plainRange.GetAttributeValue(attributes.BackgroundColor)) ==
                ColorRef(0x00, 0x00, 0x00),
                "High Contrast vector TextPattern backgrounds must expose the Window surface");

            AssertHighContrastVectorPixels(
                linked,
                expectHotlight: true,
                "mermaid-high-contrast-linked-node.png");
            AssertHighContrastVectorPixels(
                plain,
                expectHotlight: false,
                "mermaid-high-contrast-plain-node.png");
        }
        finally
        {
            if (toggle.ToggleState == ToggleState.On)
            {
                toggle.Toggle();
                Thread.Sleep(800);
            }
        }
    }

    private static AutomationElement? FindMermaidSemantic(
        AutomationElement renderer,
        string accessibleName) =>
        renderer.FindAllDescendants().FirstOrDefault(element =>
            string.Equals(NameOrEmpty(element).Trim(), accessibleName, StringComparison.Ordinal));

    private static void AssertHighContrastVectorPixels(
        AutomationElement semantic,
        bool expectHotlight,
        string fileName)
    {
        string artifactDir = Path.Combine(
            Path.GetFullPath("."), "artifacts", "screenshots", "markdown-audit");
        Directory.CreateDirectory(artifactDir);
        string path = Path.Combine(artifactDir, fileName);
        using var capture = FlaUI.Core.Capturing.Capture.Element(
            semantic,
            new FlaUI.Core.Capturing.CaptureSettings());
        capture.ToFile(path);

        System.Drawing.Bitmap bitmap = capture.Bitmap;
        Assert(bitmap.Width > 2 && bitmap.Height > 2,
            $"High Contrast vector capture is empty: {bitmap.Width}x{bitmap.Height}");

        // Ignore the outer focus-border band even after focus has been cleared.
        // Requiring Hotlight/WindowText pixels in this interior makes a cyan
        // perimeter alone insufficient to pass the rendered-paint gate.
        int interiorInset = Math.Min(
            4,
            Math.Max(1, (Math.Min(bitmap.Width, bitmap.Height) - 1) / 4));
        int interiorWidth = bitmap.Width - (interiorInset * 2);
        int interiorHeight = bitmap.Height - (interiorInset * 2);
        Assert(interiorWidth > 0 && interiorHeight > 0,
            $"High Contrast vector capture has no testable interior: {bitmap.Width}x{bitmap.Height}");

        int surfacePixels = 0;
        int semanticPixels = 0;
        long surfaceRed = 0;
        long surfaceGreen = 0;
        long surfaceBlue = 0;
        long semanticRed = 0;
        long semanticGreen = 0;
        long semanticBlue = 0;
        for (int y = interiorInset; y < bitmap.Height - interiorInset; y++)
        {
            for (int x = interiorInset; x < bitmap.Width - interiorInset; x++)
            {
                System.Drawing.Color pixel = bitmap.GetPixel(x, y);
                if (pixel.R <= 48 && pixel.G <= 48 && pixel.B <= 48)
                {
                    surfacePixels++;
                    surfaceRed += pixel.R;
                    surfaceGreen += pixel.G;
                    surfaceBlue += pixel.B;
                    continue;
                }

                bool isSemantic = expectHotlight
                    ? pixel.R <= 80 && pixel.G >= 160 && pixel.B >= 160
                    : pixel.R >= 160 && pixel.G >= 160 && pixel.B >= 160;
                if (!isSemantic)
                    continue;
                semanticPixels++;
                semanticRed += pixel.R;
                semanticGreen += pixel.G;
                semanticBlue += pixel.B;
            }
        }

        int interiorPixels = interiorWidth * interiorHeight;
        Assert(surfacePixels >= Math.Max(16, interiorPixels / 5),
            $"High Contrast vector surface is not visibly present in the capture interior; " +
            $"black-like={surfacePixels}/{interiorPixels}. " +
            "A solid foreground rectangle must fail this rendered-pixel gate.");
        Assert(semanticPixels >= Math.Max(8, (interiorWidth + interiorHeight) / 8),
            $"High Contrast vector {(expectHotlight ? "Hotlight" : "WindowText")} glyph paint is not visibly present " +
            $"inside the focus-border mask; semantic={semanticPixels}/{interiorPixels}");

        System.Drawing.Color surface = System.Drawing.Color.FromArgb(
            (int)(surfaceRed / surfacePixels),
            (int)(surfaceGreen / surfacePixels),
            (int)(surfaceBlue / surfacePixels));
        System.Drawing.Color foreground = System.Drawing.Color.FromArgb(
            (int)(semanticRed / semanticPixels),
            (int)(semanticGreen / semanticPixels),
            (int)(semanticBlue / semanticPixels));
        double contrast = ContrastRatio(surface, foreground);
        Assert(contrast >= 4.5,
            $"Rendered High Contrast vector contrast {contrast:F2}:1 is below 4.5:1; " +
            $"surface={surface}, foreground={foreground}");
        Console.WriteLine(
            $"[automation] rendered HC vector interior pixels: {fileName}; inset={interiorInset}; " +
            $"surface={surfacePixels}/{interiorPixels}; semantic={semanticPixels}/{interiorPixels}; " +
            $"contrast={contrast:F2}:1; screenshot={path}");
    }

    private static double ContrastRatio(System.Drawing.Color first, System.Drawing.Color second)
    {
        double firstLuminance = RelativeLuminance(first);
        double secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double RelativeLuminance(System.Drawing.Color color)
    {
        static double Linearize(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R) +
               0.7152 * Linearize(color.G) +
               0.0722 * Linearize(color.B);
    }

    private static AutomationElement? FindMermaidLink(AutomationElement renderer) =>
        renderer.FindAllDescendants().FirstOrDefault(element =>
            element.ControlType == ControlType.Hyperlink &&
            (element.Name ?? string.Empty).Contains("Invokable Mermaid node", StringComparison.Ordinal));

    private static void ProbeAccessibilityLabTextPattern(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        string text = textPattern.DocumentRange.GetText(-1);
        Assert(text.Contains("Accessibility Lab", StringComparison.Ordinal),
            "TextPattern document text must include the lab heading");
        Assert(text.Contains("quick brown fox", StringComparison.OrdinalIgnoreCase),
            "TextPattern document text must include paragraph content");
        Assert(text.Contains("Console.WriteLine", StringComparison.Ordinal),
            "TextPattern document text must include fenced code content");
        Assert(text.Contains("native hosted fallback marker", StringComparison.Ordinal),
            "a declined hosted factory key must restore the original native markdown block");

        var word = textPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                   ?? throw new InvalidOperationException("TextPattern FindText('quick') returned null");
        word.ExpandToEnclosingUnit(TextUnit.Word);
        word.ScrollIntoView(alignToTop: true);
        Thread.Sleep(250);
        var rects = word.GetBoundingRectangles();
        Assert(rects.Length > 0, "TextPattern word range must expose at least one bounding rectangle");
        Assert(rects.All(r => r.Width < renderer.BoundingRectangle.Width / 3 &&
                              r.Height < renderer.BoundingRectangle.Height / 3),
            "TextPattern word bounding rectangles must be glyph/line-sized, not renderer-sized");

        var endpointRange = textPattern.DocumentRange.Clone();
        endpointRange.MoveEndpointByRange(TextPatternRangeEndpoint.Start, word, TextPatternRangeEndpoint.Start);
        endpointRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, word, TextPatternRangeEndpoint.Start);
        int endpointMoved = endpointRange.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Word, 1);
        Assert(endpointMoved == 1, $"MoveEndpointByUnit(Start, Word, 1) from a collapsed range should move one word, moved={endpointMoved}");
        Assert(endpointRange.CompareEndpoints(TextPatternRangeEndpoint.Start, endpointRange, TextPatternRangeEndpoint.End) == 0,
            "MoveEndpointByUnit must keep the range valid by collapsing the opposite endpoint when Start crosses End");

        var blockTextPeer = renderer.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .FirstOrDefault(e => (e.Name ?? string.Empty).Contains("quick brown fox", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Accessibility lab paragraph text peer not found");
        var blockTextPattern = blockTextPeer.Patterns.Text.PatternOrDefault
                               ?? throw new InvalidOperationException("Paragraph text peer must expose TextPattern for Narrator word highlighting");
        var blockWord = blockTextPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                        ?? throw new InvalidOperationException("Paragraph TextPattern FindText('quick') returned null");
        blockWord.ExpandToEnclosingUnit(TextUnit.Word);
        var blockWordRects = blockWord.GetBoundingRectangles();
        Assert(blockWordRects.Length > 0, "Paragraph TextPattern word range must expose bounding rectangles");
        Assert(blockWordRects.All(r => r.Width < blockTextPeer.BoundingRectangle.Width / 2 &&
                                       r.Height < blockTextPeer.BoundingRectangle.Height),
            "Paragraph TextPattern word rectangles must be smaller than the paragraph peer rectangle");
        AutomationElement wordEnclosing = blockWord.GetEnclosingElement();
        Assert(
            string.Equals(
                AutomationIdOrEmpty(wordEnclosing),
                AutomationIdOrEmpty(blockTextPeer),
                StringComparison.Ordinal),
            "TextPattern ranges must return their innermost semantic block as GetEnclosingElement");

        var page = blockWord.Clone();
        page.ExpandToEnclosingUnit(TextUnit.Page);
        Assert(
            page.Compare(textPattern.DocumentRange),
            "unsupported TextUnit.Page must promote to the supported Document unit");

        var movedWord = textPattern.DocumentRange.Clone();
        int moved = movedWord.Move(TextUnit.Word, 1);
        Assert(moved == 1, $"TextPattern Move(Word, 1) should move by one word, moved={moved}");
        string movedWordText = movedWord.GetText(-1).Trim();
        Assert(!string.IsNullOrWhiteSpace(movedWordText) &&
               movedWordText.Length <= 32 &&
               !movedWordText.Contains('\n'),
            $"TextPattern Move(Word, 1) must produce a word-sized range, got '{movedWordText}'");
        movedWord.ScrollIntoView(alignToTop: true);
        Thread.Sleep(250);
        var movedWordRects = movedWord.GetBoundingRectangles();
        Assert(movedWordRects.Length > 0, "Moved word range must expose bounding rectangles");
        Assert(movedWordRects.Sum(r => r.Width) < renderer.BoundingRectangle.Width / 2,
            "Moved word range bounding rectangles must be word-sized, not whole-document-sized");

        var visible = textPattern.GetVisibleRanges();
        Assert(visible.Length > 0, "TextPattern must expose visible ranges");
        Assert(visible[0].GetText(200).Length > 0, "Visible TextPattern range must contain text");

        var offscreen = textPattern.DocumentRange.FindText("Paragraph 5", backward: false, ignoreCase: false)
                        ?? throw new InvalidOperationException("offscreen paragraph range not found");
        offscreen.ScrollIntoView(alignToTop: false);
        Thread.Sleep(500);
        var offscreenRects = offscreen.GetBoundingRectangles();
        Assert(offscreenRects.Any(r => r.Width > 0 && r.Height > 0 && renderer.BoundingRectangle.IntersectsWith(r)),
            "TextPattern.ScrollIntoView must bring the requested offscreen range into the renderer viewport");

        var descendants = renderer.FindAllDescendants();
        Assert(descendants.Any(e => e.ControlType == ControlType.Hyperlink),
            "Accessibility lab must expose hyperlink descendants for TextPattern clients");
    }

    private static void ProbeAccessibilityLabSemanticRoles(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var descendants = renderer.FindAllDescendants();
        Assert(descendants.Any(e => e.ControlType == ControlType.Header), "Accessibility lab must expose heading/header peers");
        var hyperlinks = descendants.Where(e => e.ControlType == ControlType.Hyperlink).ToArray();
        Assert(hyperlinks.Length > 0, "Accessibility lab must expose hyperlink peers");
        Assert(hyperlinks.All(link => link.FindAllChildren().Length == 0),
            "Synthetic hyperlink peers must remain semantic leaves and must not cycle into the renderer visual tree");
        Assert(descendants.Any(e => e.ControlType == ControlType.List), "Accessibility lab must expose list peer");
        Assert(descendants.Any(e => e.ControlType == ControlType.ListItem), "Accessibility lab must expose list item peers");
        Assert(descendants.Any(e => e.ControlType == ControlType.Table), "Accessibility lab must expose table peer");
        Assert(descendants.Any(e => e.ControlType == ControlType.DataItem), "Accessibility lab must expose table cell peers");
        Assert(descendants.Count(e => e.ControlType == ControlType.Image) >= 2,
            "Accessibility lab must expose both block and inline image peers");
        Assert(descendants.Any(e => e.ControlType == ControlType.Image &&
                                    (e.Name ?? string.Empty).Contains("Inline accessibility icon", StringComparison.Ordinal)),
            "Inline markdown image must expose an image peer instead of flattening to plain paragraph text");
        Assert(descendants.Any(e => e.ControlType == ControlType.Button && (e.Name ?? string.Empty).Contains("Native action", StringComparison.Ordinal)),
            "Accessibility lab must expose hosted native button");
        Assert(descendants.All(e => e.ControlType != ControlType.CheckBox),
            "Accessibility lab read-only task state must not expose checkbox semantics");

        AutomationElement ordinaryList = descendants.First(e => e.ControlType == ControlType.List);
        Assert(!ordinaryList.Properties.IsKeyboardFocusable.ValueOrDefault,
            "ordinary synthetic semantic nodes must not inherit renderer focusability");
        renderer.Focus();
        ordinaryList.Focus();
        Assert(!ordinaryList.Properties.HasKeyboardFocus.ValueOrDefault,
            "SetFocus on an ordinary synthetic semantic node must not focus its shared renderer owner");

        var table = descendants.First(e => e.ControlType == ControlType.Table);
        var grid = table.Patterns.Grid.PatternOrDefault
                   ?? throw new InvalidOperationException("Table peer must expose GridPattern");
        Assert(grid.RowCount.Value >= 4, "GridPattern RowCount must include header and body rows");
        Assert(grid.ColumnCount.Value == 3, "GridPattern ColumnCount must be 3 for the lab table");
        Assert(grid.GetItem(0, 0) is not null, "GridPattern.GetItem(0, 0) must return a cell");
    }

    private static void ProbeAccessibilityLabTextAttributes(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        var attributes = renderer.Automation.TextAttributeLibrary;
        var descendants = renderer.FindAllDescendants();

        var hyperlink = descendants.First(e => e.ControlType == ControlType.Hyperlink);
        var hyperlinkRange = textPattern.RangeFromChild(hyperlink);
        Assert(hyperlinkRange.GetText(-1).Contains(hyperlink.Name ?? string.Empty, StringComparison.Ordinal),
            "TextPattern.RangeFromChild(hyperlink) must return the hyperlink text range");
        Assert(
            string.Equals(
                AutomationIdOrEmpty(hyperlinkRange.GetEnclosingElement()),
                AutomationIdOrEmpty(hyperlink),
                StringComparison.Ordinal),
            "an exact hyperlink range must return the hyperlink as its innermost enclosing element");
        AutomationElement hyperlinkParent = hyperlink.Parent
            ?? throw new InvalidOperationException("hyperlink semantic parent not found");
        var parentTextPattern = hyperlinkParent.Patterns.Text.PatternOrDefault
            ?? throw new InvalidOperationException("hyperlink parent must expose TextPattern");
        AutomationElement[] immediateChildren = parentTextPattern.DocumentRange.GetChildren();
        Assert(
            immediateChildren.Any(child => string.Equals(
                AutomationIdOrEmpty(child),
                AutomationIdOrEmpty(hyperlink),
                StringComparison.Ordinal)),
            "TextPattern.GetChildren must return immediate inline semantic children");
        Assert(
            immediateChildren.All(child =>
                child.Parent is { } parent
                && string.Equals(
                    AutomationIdOrEmpty(parent),
                    AutomationIdOrEmpty(hyperlinkParent),
                    StringComparison.Ordinal)),
            "TextPattern.GetChildren must not flatten deeper semantic descendants");

        var image = descendants.First(e => e.ControlType == ControlType.Image &&
                                           (e.Name ?? string.Empty).Contains("Accessibility lab blue square", StringComparison.Ordinal));
        var imageRange = textPattern.RangeFromChild(image);
        Assert(imageRange.GetText(-1).Contains("Accessibility lab blue square", StringComparison.Ordinal),
            "TextPattern.RangeFromChild(image) must return image alt text");

        var customStyleRange = textPattern.DocumentRange.FindText(
            "Ancestor-scoped extension style",
            backward: false,
            ignoreCase: false)
            ?? throw new InvalidOperationException("custom extension style marker range not found");
        Assert(
            string.Equals(
                customStyleRange.GetAttributeValue(attributes.StyleName)?.ToString(),
                "SampleExtensionCallout",
                StringComparison.Ordinal),
            "a non-built-in extension role must remain visible through UIA StyleName");
        Assert(
            TryAttributeNumber(customStyleRange.GetAttributeValue(attributes.FontSize), out double customFontSize) &&
            Math.Abs(customFontSize - 19d) < 0.1d,
            $"ancestor-scoped custom role FontSize must be 19, got {customFontSize}");
        Assert(
            ToColorRefValue(customStyleRange.GetAttributeValue(attributes.ForegroundColor)) == ColorRef(0x8A, 0x2B, 0xE2),
            "ancestor-scoped custom role foreground was not applied");

        var hostedButton = descendants.First(e => e.ControlType == ControlType.Button &&
            (e.Name ?? string.Empty).Contains("Native action", StringComparison.Ordinal));
        var hostedButtonRange = textPattern.RangeFromChild(hostedButton);
        Assert(hostedButtonRange.GetText(-1).Length > 0,
            "TextPattern.RangeFromChild(hosted native button) must return its embedded-object placeholder range");

        var paintedLinkRange = textPattern.DocumentRange.FindText("painted link", backward: false, ignoreCase: false)
                               ?? throw new InvalidOperationException("painted link range not found");
        var underline = paintedLinkRange.GetAttributeValue(attributes.UnderlineStyle);
        Assert(IsNonNoneTextDecoration(underline),
            $"painted link must expose a non-None UnderlineStyle text attribute, got {DescribeAttributeValue(underline)}");
        Assert((paintedLinkRange.GetAttributeValue(attributes.StyleName)?.ToString() ?? string.Empty)
               .Contains("Link", StringComparison.Ordinal),
            "painted link must expose StyleName=Link");
        var collapsedLinkStart = paintedLinkRange.Clone();
        collapsedLinkStart.MoveEndpointByRange(
            TextPatternRangeEndpoint.End,
            collapsedLinkStart,
            TextPatternRangeEndpoint.Start);
        Assert(
            IsNonNoneTextDecoration(collapsedLinkStart.GetAttributeValue(attributes.UnderlineStyle)),
            "a collapsed caret at a half-open format boundary must choose the following run, not Mixed");

        var codeRange = textPattern.DocumentRange.FindText("Console.WriteLine", backward: false, ignoreCase: false)
                        ?? throw new InvalidOperationException("code range not found");
        string fontName = codeRange.GetAttributeValue(attributes.FontName)?.ToString() ?? string.Empty;
        Assert(fontName.Contains("Consolas", StringComparison.OrdinalIgnoreCase),
            $"code range must expose monospace FontName, got {fontName}");

        var quickRange = textPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                         ?? throw new InvalidOperationException("quick range not found");
        Assert(IsTrueAttribute(quickRange.GetAttributeValue(attributes.IsReadOnly)),
            "TextPattern ranges must expose IsReadOnly=true");

        var findUnderline = textPattern.DocumentRange.FindAttribute(attributes.UnderlineStyle, underline, backward: false)
                            ?? throw new InvalidOperationException("FindAttribute(UnderlineStyle) returned null");
        Assert(findUnderline.GetText(-1).Contains("painted link", StringComparison.Ordinal) ||
               findUnderline.GetText(-1).Contains("second painted link", StringComparison.Ordinal),
            "FindAttribute(UnderlineStyle) must return an underlined markdown link range");

        var text2 = renderer.Patterns.Text2.PatternOrDefault
                    ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern2");
        renderer.Focus();
        _ = text2.GetCaretRange(out bool documentCaretActive);
        Assert(documentCaretActive, "focused selectable document must expose an active caret range");
        hyperlink.Focus();
        Retry.WhileFalse(
            () => hyperlink.Properties.HasKeyboardFocus.ValueOrDefault,
            timeout: TimeSpan.FromSeconds(2),
            interval: TimeSpan.FromMilliseconds(50));
        Assert(hyperlink.Properties.HasKeyboardFocus.ValueOrDefault,
            "the virtual hyperlink must own UIA keyboard focus before caret activity is evaluated");
        Thread.Sleep(500);
        Assert(hyperlink.Properties.HasKeyboardFocus.ValueOrDefault,
            "the virtual hyperlink must retain UIA keyboard focus through deferred layout/focus notifications");
        var childCaretRange = text2.GetCaretRange(out _);
        Assert(
            !IsTrueAttribute(childCaretRange.GetAttributeValue(attributes.IsActive)),
            "the caret range returned by TextPattern2 must expose IsActive=false while a virtual hyperlink child owns keyboard focus");
    }

    private static void ProbeAccessibilityLabForcedHighContrast(Window window)
    {
        var toggle = window.FindFirstDescendant(cf => cf.ByAutomationId("ForcedHighContrastToggle"))?.AsToggleButton()
                     ?? throw new InvalidOperationException("ForcedHighContrastToggle not found");

        try
        {
            if (toggle.ToggleState != ToggleState.On)
            {
                toggle.Toggle();
                Thread.Sleep(1200);
            }

        SelectSample(window, "AccessibilityLab");
            Thread.Sleep(1200);

            var status = FindFirstRawDescendantByAutomationId(window, "HighContrastStatus");
            var statusText = status?.Name ?? status?.Properties.Name.ValueOrDefault ?? string.Empty;
            Assert(statusText.Contains("hc:on", StringComparison.Ordinal),
                $"forced high contrast status must be on, got '{statusText}'");

            var renderer = FindRenderer(window);
            var textPattern = renderer.Patterns.Text.PatternOrDefault
                              ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
            var attributes = renderer.Automation.TextAttributeLibrary;

            var bodyRange = textPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                            ?? throw new InvalidOperationException("quick range not found");
            Assert(ToColorRefValue(bodyRange.GetAttributeValue(attributes.ForegroundColor)) == ColorRef(0xFF, 0xFF, 0xFF),
                "forced high contrast body foreground must resolve to WindowText");
            Assert(ToColorRefValue(bodyRange.GetAttributeValue(attributes.BackgroundColor)) == ColorRef(0x00, 0x00, 0x00),
                "forced high contrast body background must resolve to Window");

            var linkRange = textPattern.DocumentRange.FindText("painted link", backward: false, ignoreCase: false)
                            ?? throw new InvalidOperationException("painted link range not found");
            Assert(ToColorRefValue(linkRange.GetAttributeValue(attributes.ForegroundColor)) == ColorRef(0x00, 0xFF, 0xFF),
                "forced high contrast link foreground must resolve to Hotlight");

            var inlineCodeRange = textPattern.DocumentRange.FindText("inline-code-token", backward: false, ignoreCase: false)
                                  ?? throw new InvalidOperationException("inline code range not found");
            Assert(ToColorRefValue(inlineCodeRange.GetAttributeValue(attributes.ForegroundColor)) == ColorRef(0xFF, 0xFF, 0xFF),
                "forced high contrast inline code foreground must resolve to WindowText");
            Assert(ToColorRefValue(inlineCodeRange.GetAttributeValue(attributes.BackgroundColor)) == ColorRef(0x00, 0x00, 0x00),
                "forced high contrast inline code background must resolve to Window");

            var customStyleRange = textPattern.DocumentRange.FindText(
                "Ancestor-scoped extension style",
                backward: false,
                ignoreCase: false)
                ?? throw new InvalidOperationException("custom extension style marker range not found in high contrast");
            Assert(
                TryAttributeNumber(customStyleRange.GetAttributeValue(attributes.FontSize), out double customFontSize) &&
                Math.Abs(customFontSize - 23d) < 0.1d,
                $"High Contrast theme-dictionary FontSize must be 23, got {customFontSize}");
            Assert(
                ToColorRefValue(customStyleRange.GetAttributeValue(attributes.ForegroundColor)) == ColorRef(0xFF, 0xFF, 0xFF),
                "High Contrast must map the custom authored foreground to WindowText");

            var tableHeaderRange = textPattern.DocumentRange.FindText("Feature", backward: false, ignoreCase: false)
                                   ?? throw new InvalidOperationException("table header range not found");
            Assert(ToColorRefValue(tableHeaderRange.GetAttributeValue(attributes.ForegroundColor)) == ColorRef(0x00, 0x00, 0x00),
                "forced high contrast table header foreground must resolve to HighlightText");
            Assert(ToColorRefValue(tableHeaderRange.GetAttributeValue(attributes.BackgroundColor)) == ColorRef(0xFF, 0xFF, 0x00),
                "forced high contrast table header background must resolve to Highlight");
        }
        finally
        {
            if (toggle.ToggleState == ToggleState.On)
            {
                toggle.Toggle();
                Thread.Sleep(800);
            }
        }
    }

    private static void ProbeAccessibilityLabKeyboardOrder(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);

        var focusedPaintedLink = TabIntoRendererFromSampleList(window, renderer);
        Assert(IsPaintedLinkFocus(focusedPaintedLink),
            $"Tab entry should promote renderer root focus to the first painted hyperlink, focused={DescribeFocus(focusedPaintedLink)}");

        Keyboard.Press(VirtualKeyShort.TAB); // hosted button
        Thread.Sleep(250);
        var focusedButton = renderer.Automation.FocusedElement();
        Assert(IsNativeActionButton(focusedButton),
            $"Second Tab should land on hosted native button, focused={DescribeFocus(focusedButton)}");

        Keyboard.Press(VirtualKeyShort.TAB); // first focusable descendant in composite embed
        Thread.Sleep(250);
        var focusedCompositeTextBox = renderer.Automation.FocusedElement();
        Assert(IsCompositeValueTextBox(focusedCompositeTextBox),
            $"Third Tab should land inside the composite hosted embed text box, focused={DescribeFocus(focusedCompositeTextBox)}");

        Keyboard.Press(VirtualKeyShort.TAB); // second focusable descendant in composite embed
        Thread.Sleep(250);
        var focusedCompositeButton = renderer.Automation.FocusedElement();
        Assert(IsCompositeActionButton(focusedCompositeButton),
            $"Fourth Tab should stay inside the composite hosted embed and land on its button, focused={DescribeFocus(focusedCompositeButton)}");

        Keyboard.Press(VirtualKeyShort.TAB); // copy button for the declined hosted-element fallback
        Thread.Sleep(250);
        var focusedCopyButton = renderer.Automation.FocusedElement();
        Assert(
            string.Equals(NameOrEmpty(focusedCopyButton), "Copy code", StringComparison.Ordinal) &&
            AutomationIdOrEmpty(focusedCopyButton).StartsWith("MarkdownCodeCopy-", StringComparison.Ordinal),
            $"Fifth Tab should land on the fallback code-block copy button, focused={DescribeFocus(focusedCopyButton)}");

        var focusedSecondPaintedLink = PressTabExpectPaintedLink(renderer, "Sixth Tab");
        Assert(IsPaintedLinkFocus(focusedSecondPaintedLink),
            $"Sixth Tab should land on second painted hyperlink, focused={DescribeFocus(focusedSecondPaintedLink)}");

        Keyboard.Press(VirtualKeyShort.TAB); // seventh Tab leaves markdown
        Thread.Sleep(300);
        var focusedAfterExit = renderer.Automation.FocusedElement();
        Assert(!IsAccessibilityLabInternalFocus(focusedAfterExit),
            $"Tab at the last markdown item must leave the renderer instead of looping into embeds, focused={DescribeFocus(focusedAfterExit)}");
        TryFocus(window, "main window after markdown keyboard boundary probe");
        Thread.Sleep(250);
    }

    private static AutomationElement TabIntoRendererFromSampleList(Window window, AutomationElement renderer)
    {
        AutomationElement? observedPaintedLink = null;
        var observed = new List<string>();
        using var paintedLinkFocused = new ManualResetEventSlim(false);
        var focusHandler = renderer.Automation.RegisterFocusChangedEvent(element =>
        {
            string description;
            try { description = DescribeFocus(element); }
            catch (Exception ex) { description = $"<unreadable focus event: {ex.Message}>"; }

            lock (observed) observed.Add(description);
            if (IsPaintedLinkFocus(element))
            {
                observedPaintedLink = element;
                paintedLinkFocused.Set();
            }
        });

        var source = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleNav_AccessibilityLab"))
                     ?? throw new InvalidOperationException("Accessibility Lab navigation item not found");
        try
        {
            source.Focus();
            Thread.Sleep(250);

            for (int i = 0; i < 80; i++)
            {
                Keyboard.Press(VirtualKeyShort.TAB);
                if (paintedLinkFocused.Wait(TimeSpan.FromMilliseconds(500)) && observedPaintedLink is not null)
                    return observedPaintedLink;

                var focused = renderer.Automation.FocusedElement();
                if (IsAccessibilityLabInternalFocus(focused))
                {
                    if (paintedLinkFocused.Wait(TimeSpan.FromMilliseconds(750)) && observedPaintedLink is not null)
                        return observedPaintedLink;

                    lock (observed)
                    {
                        throw new InvalidOperationException(
                            $"Tab reached markdown without a virtual hyperlink focus event. Current focus={DescribeFocus(focused)}; events={string.Join(" | ", observed)}");
                    }
                }
            }

            lock (observed)
            {
                throw new InvalidOperationException(
                    $"Tab did not reach the Accessibility Lab renderer within 80 stops; events={string.Join(" | ", observed)}");
            }
        }
        finally
        {
            renderer.Automation.UnregisterFocusChangedEvent(focusHandler);
        }
    }

    private static AutomationElement PressTabExpectPaintedLink(AutomationElement renderer, string stepName)
    {
        AutomationElement? observedPaintedLink = null;
        var observed = new List<string>();
        using var paintedLinkFocused = new ManualResetEventSlim(false);
        var focusHandler = renderer.Automation.RegisterFocusChangedEvent(element =>
        {
            string description;
            try { description = DescribeFocus(element); }
            catch (Exception ex) { description = $"<unreadable focus event: {ex.Message}>"; }

            lock (observed) observed.Add(description);
            if (IsPaintedLinkFocus(element))
            {
                observedPaintedLink = element;
                paintedLinkFocused.Set();
            }
        });

        try
        {
            Keyboard.Press(VirtualKeyShort.TAB);
            if (paintedLinkFocused.Wait(TimeSpan.FromMilliseconds(1000)) && observedPaintedLink is not null)
                return observedPaintedLink;

            var focused = renderer.Automation.FocusedElement();
            lock (observed)
            {
                throw new InvalidOperationException(
                    $"{stepName} did not raise virtual hyperlink focus. Current focus={DescribeFocus(focused)}; events={string.Join(" | ", observed)}");
            }
        }
        finally
        {
            renderer.Automation.UnregisterFocusChangedEvent(focusHandler);
        }
    }

    private static bool IsPaintedLinkFocus(AutomationElement focused)
    {
        return focused.ControlType == ControlType.Hyperlink &&
               !string.IsNullOrWhiteSpace(NameOrEmpty(focused));
    }

    private static bool IsRendererOrDocumentFocus(AutomationElement focused)
    {
        return AutomationIdOrEmpty(focused) == "MarkdownRenderer" ||
               focused.ControlType == ControlType.Document;
    }

    private static bool IsNativeActionButton(AutomationElement focused)
    {
        return focused.ControlType == ControlType.Button &&
               NameOrEmpty(focused).Contains("Native action", StringComparison.Ordinal);
    }

    private static bool IsCompositeValueTextBox(AutomationElement focused)
    {
        return focused.ControlType == ControlType.Edit &&
               (AutomationIdOrEmpty(focused) == "CompositeValueTextBox" ||
                NameOrEmpty(focused).Contains("Composite value", StringComparison.Ordinal));
    }

    private static bool IsCompositeActionButton(AutomationElement focused)
    {
        return focused.ControlType == ControlType.Button &&
               (AutomationIdOrEmpty(focused) == "CompositeActionButton" ||
                NameOrEmpty(focused).Contains("Composite action", StringComparison.Ordinal));
    }

    private static bool IsAccessibilityLabInternalFocus(AutomationElement focused)
    {
        return IsPaintedLinkFocus(focused) ||
               IsRendererOrDocumentFocus(focused) ||
               IsNativeActionButton(focused) ||
               IsCompositeValueTextBox(focused) ||
               IsCompositeActionButton(focused);
    }

    private static string DescribeFocus(AutomationElement focused)
        => $"{focused.ControlType}/{NameOrEmpty(focused)}/AutomationId={AutomationIdOrEmpty(focused)}";

    private static string NameOrEmpty(AutomationElement element)
    {
        try { return element.Name ?? string.Empty; }
        catch
        {
            try { return element.Properties.Name.ValueOrDefault ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    private static string AutomationIdOrEmpty(AutomationElement element)
    {
        try { return element.AutomationId ?? string.Empty; }
        catch
        {
            try { return element.Properties.AutomationId.ValueOrDefault ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    private static string ClassNameOrEmpty(AutomationElement element)
    {
        try { return element.ClassName ?? string.Empty; }
        catch
        {
            try { return element.Properties.ClassName.ValueOrDefault ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    private static void ProbeAccessibilityLabPointerResume(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var button = renderer.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(e => (e.Name ?? string.Empty).Contains("Native action", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Hosted Native action button not found");

        var buttonBounds = button.BoundingRectangle;
        var rendererBounds = renderer.BoundingRectangle;
        var nearbyDocumentPoint = new System.Drawing.Point(
            buttonBounds.Left + buttonBounds.Width / 2,
            Math.Max(rendererBounds.Top + 4, buttonBounds.Top - 12));
        Mouse.Click(nearbyDocumentPoint, FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(300);

        AutomationElement focused = renderer.Automation.FocusedElement();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Keyboard.Press(VirtualKeyShort.TAB);
            Thread.Sleep(300);
            focused = renderer.Automation.FocusedElement();
            if (IsCompositeValueTextBox(focused) || IsCompositeActionButton(focused))
                break;
        }

        Assert(IsCompositeValueTextBox(focused) || IsCompositeActionButton(focused),
            $"Tab after pointer dismissal near hosted controls should resume within markdown focus order, focused={DescribeFocus(focused)}");
    }

    private static void ProbeVirtualization(Window window)
    {
        SelectSample(window, "Virtualization");
        var renderer = FindRenderer(window);
        bool ready = Retry.WhileFalse(
            () => GetRendererDocumentText(renderer).Contains("Embed virtualization", StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(ready, "virtualization sample did not finish rendering");

        static string[] NativeButtonIds(AutomationElement document) =>
            document.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Select(AutomationIdOrEmpty)
                .Where(id => id.EndsWith("-Native", StringComparison.Ordinal))
                .ToArray();

        int realised = ReadRealizedEmbedCount(renderer);
        Assert(realised < 100, $"virtualization expected ≪100 realised embeds, found {realised}");
        Assert(realised > 0, $"virtualization expected some realised embeds, found {realised}");
        string firstButtonId = NativeButtonIds(renderer).FirstOrDefault()
            ?? throw new InvalidOperationException("the initial viewport has no native hosted button");

        var scroll = renderer.Patterns.Scroll.Pattern
            ?? throw new InvalidOperationException("virtualized renderer must expose ScrollPattern");
        Assert(scroll.VerticallyScrollable.ValueOrDefault,
            "virtualized renderer must expose vertical scrolling");
        scroll.SetScrollPercent(-1, 100);
        bool recycled = Retry.WhileFalse(
            () => !NativeButtonIds(renderer).Contains(firstButtonId, StringComparer.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(recycled, "an initial hosted button was not recycled after leaving the viewport");
        int afterScroll = ReadRealizedEmbedCount(renderer);
        Assert(afterScroll < 100, $"virtualization after scroll expected ≪100 realised embeds, found {afterScroll}");
        Assert(afterScroll > 0, "the destination viewport has no native hosted buttons");

        scroll.SetScrollPercent(-1, 0);
        bool returnedToTop = Retry.WhileFalse(
            () => scroll.VerticalScrollPercent.ValueOrDefault is >= 0 and <= 0.5,
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(returnedToTop,
            $"virtualization did not return to the top; scroll percent={scroll.VerticalScrollPercent.ValueOrDefault:0.##}");
        bool restored = Retry.WhileFalse(
            () => NativeButtonIds(renderer).Contains(firstButtonId, StringComparer.Ordinal),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(restored,
            $"the initial hosted button {firstButtonId} was not realized after returning to the top; " +
            $"scroll percent={scroll.VerticalScrollPercent.ValueOrDefault:0.##}");
    }

    private static void ProbeImagesSample(Window window)
    {
        SelectSample(window, "Images");
        Thread.Sleep(1500);
        var renderer = FindRenderer(window);
        var text = GetRendererDocumentText(renderer);
        Assert(text.Length > 0, "Images sample renderer document text must not be empty");
    }

    private static void ProbeLazyImagesSample(Window window)
    {
        SelectSample(window, "LazyImages");
        Thread.Sleep(1500);
        var renderer = FindRenderer(window);
        var text = GetRendererDocumentText(renderer);
        Assert(text.Length > 0, "Lazy Images sample renderer document text must not be empty");
        // Verify renderer has children (block peers in UIA tree)
        var descendants = renderer.FindAllDescendants();
        Assert(descendants.Length > 0, "Lazy Images renderer must expose block peers as descendants");
    }

    private static void ProbeScrollAnchorSample(Window window)
    {
        SelectSample(window, "ScrollAnchor");
        Thread.Sleep(1000);
        var renderer = FindRenderer(window);
        var text = GetRendererDocumentText(renderer);
        Assert(text.Length > 0, "Scroll Anchor sample renderer document text must not be empty");
        // Scroll down and verify content is still available
        renderer.Focus();
        Keyboard.Press(VirtualKeyShort.NEXT); // Page Down
        Thread.Sleep(300);
        Keyboard.Press(VirtualKeyShort.PRIOR); // Page Up
        Thread.Sleep(300);
        var textAfterScroll = GetRendererDocumentText(renderer);
        Assert(textAfterScroll.Length > 0, "Scroll Anchor renderer document text must remain non-empty after scroll");
    }

    private static void ProbeFootnotesSample(Window window)
    {
        SelectSample(window, "Footnotes");
        Thread.Sleep(1200);
        var renderer = FindRenderer(window);
        var text = GetRendererDocumentText(renderer);
        Assert(text.Length > 0, "Footnotes sample renderer document text must not be empty");
        // Verify the rendered text contains footnote markers (superscripts / back-arrows)
        // The renderer exposes document text through TextPattern so Narrator can
        // use text ranges instead of reading the root element Name.
        bool hasFootnoteContent = text.Contains("sentence with a footnote", StringComparison.OrdinalIgnoreCase)
                                  || text.Contains("footnote", StringComparison.OrdinalIgnoreCase);
        Assert(hasFootnoteContent, $"Footnotes renderer content must mention 'footnote', got: {text[..Math.Min(120, text.Length)]}");
    }

    private static void ProbeKeyboardNav(Window window)
    {
        SelectSample(window, "KeyboardNav");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        renderer.Focus();
        Thread.Sleep(200);

        // Tab through links repeatedly; the renderer must survive all presses without
        // throwing or becoming unresponsive. With the boundary-exit fix, Tab at the
        // last link exits the control; further presses may or may not re-enter it.
        for (int i = 0; i < 15; i++)
        {
            Keyboard.Press(VirtualKeyShort.TAB);
            Thread.Sleep(150);
        }

        // Re-focus the renderer and verify Shift+Tab and Escape both work.
        renderer.Focus();
        Thread.Sleep(200);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        Thread.Sleep(200);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        Thread.Sleep(200);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(200);

        // Verify renderer is still responsive
        var textAfter = GetRendererDocumentText(renderer);
        Assert(textAfter.Length > 0, "Keyboard Nav renderer must remain responsive after Tab/Escape traversal");
    }

    private static void ProbeClickDismissesFocus(Window window)
    {
        // Navigate to the Keyboard Nav page, which has keyboard-focusable links.
        SelectSample(window, "KeyboardNav");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        renderer.Focus();
        Thread.Sleep(200);

        // Tab once to show focus ring.
        Keyboard.Press(VirtualKeyShort.TAB);
        Thread.Sleep(200);

        // Click somewhere inside the renderer to dismiss focus.
        var bounds = renderer.BoundingRectangle;
        Mouse.MoveTo((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2));
        Thread.Sleep(100);
        Mouse.LeftClick();
        Thread.Sleep(300);

        // The renderer must still be responsive after the click.
        var textAfter = GetRendererDocumentText(renderer);
        Assert(textAfter.Length > 0, "Renderer must remain responsive after click-dismisses-focus");
    }

    private static void ProbeSelectionDismissesOnExternalPointer(Window window)
    {
        SelectSample(window, "Selection");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        var selectionRange = textPattern.DocumentRange.FindText("Click", backward: false, ignoreCase: true)
                             ?? throw new InvalidOperationException("Selection external-dismiss probe could not find text");
        selectionRange.ExpandToEnclosingUnit(TextUnit.Word);
        selectionRange.Select();
        Thread.Sleep(500);
        Assert(!string.IsNullOrWhiteSpace(GetRendererSelectionText(renderer)),
            "TextPattern.Select must create a UIA-visible selection before dismissal");

        var editor = window.FindFirstDescendant(cf => cf.ByAutomationId("MarkdownEditor"))
                     ?? throw new InvalidOperationException("Markdown source editor not found");
        var bounds = editor.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(bounds.Left + 20, bounds.Top + 20), FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(500);

        Assert(string.IsNullOrEmpty(GetRendererSelectionText(renderer)),
            "clicking another app control must dismiss the markdown selection");
    }

    private static void ProbeSelectionDismissesOnHostedControlPointer(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var point = FindTextPatternPoint(renderer, "quick", "selection hosted-control-dismiss probe");
        Mouse.DoubleClick(point, FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(500);
        Assert(!string.IsNullOrWhiteSpace(GetRendererSelectionText(renderer)),
            "double-clicking renderer text must create a UIA-visible selection before hosted-control dismissal");

        var hostedTextBox = window.FindFirstDescendant(cf => cf.ByAutomationId("CompositeValueTextBox"))
                            ?? throw new InvalidOperationException("Composite hosted TextBox not found");
        var bounds = hostedTextBox.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(500);

        Assert(string.IsNullOrEmpty(GetRendererSelectionText(renderer)),
            "clicking a hosted WinUI control inside the markdown renderer must dismiss the markdown selection");
    }

    private static void ProbeSelectionPersistsAfterPointerRelease(Window window)
    {
        var cases = new[]
        {
            (Sample: "Typography", Start: "Heading 6", End: "Regular paragraph text", Expected: "quick brown fox"),
            (Sample: "Selection", Start: "Click and drag", End: "The selection spans", Expected: "select"),
            (Sample: "Code", Start: "C# with a filename", End: "public sealed class", Expected: "public"),
        };

        foreach (var c in cases)
        {
            SelectSample(window, c.Sample);
            Thread.Sleep(1200);

            var renderer = FindRenderer(window);
            var start = FindTextPatternPoint(renderer, c.Start, $"{c.Sample} selection persist start");
            var end = FindTextPatternPoint(renderer, c.End, $"{c.Sample} selection persist end");
            Assert(Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) >= 24,
                $"{c.Sample} selection persist probe did not find a meaningful drag range: start={start}, end={end}");

            DragMouseThrough(start, end);
            Thread.Sleep(700);

            string selected = GetRendererSelectionText(renderer);
            Assert(!string.IsNullOrWhiteSpace(selected),
                $"{c.Sample} selection must remain active after mouse release");
            Assert(selected.Contains(c.Expected, StringComparison.OrdinalIgnoreCase),
                $"{c.Sample} selection after release should include dragged text, got: {Truncate(selected, 160)}");
        }
    }

    private static void ProbeCtrlCCopiesPointerSelection(Window window)
    {
        SelectSample(window, "Selection");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var start = FindTextPatternPoint(renderer, "Click and drag", "ctrl-c copy drag start");
        var end = FindTextPatternPoint(renderer, "The selection spans", "ctrl-c copy drag end");
        DragMouseThrough(start, end);
        Thread.Sleep(500);

        string selected = GetRendererSelectionText(renderer);
        Assert(!string.IsNullOrWhiteSpace(selected),
            "ctrl-c copy probe must have an active pointer selection before copying");

        bool rendererFocused = Retry.WhileFalse(
            () =>
            {
                TryFocus(renderer, "renderer before Ctrl+C");
                return renderer.Properties.HasKeyboardFocus.ValueOrDefault;
            },
            timeout: TimeSpan.FromSeconds(3),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(rendererFocused, "renderer did not acquire keyboard focus before Ctrl+C");
        Assert(!string.IsNullOrWhiteSpace(GetRendererSelectionText(renderer)),
            "focusing the renderer before Ctrl+C must preserve the pointer selection");

        const string sentinel = "markdown-renderer-clipboard-sentinel";
        SetClipboardText(sentinel);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        bool clipboardChanged = Retry.WhileFalse(
            () => !string.Equals(GetClipboardText(), sentinel, StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(clipboardChanged,
            "Ctrl+C left the clipboard unchanged; renderer likely did not handle the key event");
        string copied = GetClipboardText();
        Assert(copied.Contains("select any text", StringComparison.OrdinalIgnoreCase) ||
               copied.Contains("selection spans", StringComparison.OrdinalIgnoreCase),
            $"Ctrl+C should copy selected rendered text, got: {Truncate(copied, 240)}");
    }

    private static void ProbeTableSelectionRowBorderIsStable(Window window)
    {
        SelectSample(window, "Tables");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var anchorRect = FindTextPatternRect(renderer, "Selection + Copy", "table row-border drag anchor");
        var previousRowRect = FindTextPatternRect(renderer, "Links", "table row-border drag previous row");
        var start = new System.Drawing.Point(anchorRect.Left + anchorRect.Width / 2, anchorRect.Top + anchorRect.Height / 2);
        var border = new System.Drawing.Point(
            anchorRect.Left + anchorRect.Width / 2,
            previousRowRect.Bottom + Math.Max(1, (anchorRect.Top - previousRowRect.Bottom) / 2));

        SendMouseMove(start);
        Thread.Sleep(100);
        SendMouseButton(MouseEventFlags.LeftDown);
        try
        {
            Thread.Sleep(120);
            SendMouseMove(border);
            Thread.Sleep(500);

            string selected = GetRendererSelectionText(renderer);
            Assert(!string.IsNullOrWhiteSpace(selected),
                $"table row-border drag should keep a non-empty selection at border point {border}");
            Assert(!selected.Contains("AOT compatibility", StringComparison.OrdinalIgnoreCase) &&
                   !selected.Contains("Live theme switch", StringComparison.OrdinalIgnoreCase),
                $"table row-border drag must not jump to rows after the anchor, got: {Truncate(selected, 240)}");
        }
        finally
        {
            SendMouseButton(MouseEventFlags.LeftUp);
        }
    }

    private static void ProbeDoubleClickSelectsWord(Window window)
    {
        // Navigate to Typography sample which has plain paragraphs.
        SelectSample(window, "Typography");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        string logPath = FindShakeLog()
            ?? throw new InvalidOperationException("text_shaking2.log not found — sample may not have ShakeLogger enabled");
        var point = FindSelectableTextPatternPoint(logPath, renderer, "Lorem", "double-click word probe");
        Thread.Sleep(900);
        long baseline = new FileInfo(logPath).Length;

        Mouse.DoubleClick(point, FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(700);

        string appended = ReadShakeLogFrom(logPath, baseline);
        Assert(CountOccurrences(appended, "sel-anchor") > 0,
            $"double-click probe did not start selection. Recent log excerpt: {Truncate(appended, 600)}");
        Assert(CountOccurrences(appended, "sel-extend") > 0,
            $"double-click probe did not expand to a word selection. Recent log excerpt: {Truncate(appended, 600)}");
        Assert(CountOccurrences(appended, "sel-rect-phys") > 0,
            $"double-click probe did not draw a selection rectangle. Recent log excerpt: {Truncate(appended, 600)}");
    }

    private static void ProbeTripleClickSelectsLine(Window window)
    {
        // Navigate to Selection sample which has plain paragraphs.
        SelectSample(window, "Selection");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        string logPath = FindShakeLog()
            ?? throw new InvalidOperationException("text_shaking2.log not found — sample may not have ShakeLogger enabled");
        var point = FindSelectableTextPatternPoint(logPath, renderer, "Click and drag", "triple-click line probe");
        Thread.Sleep(900);
        long baseline = new FileInfo(logPath).Length;

        // Triple-click = double-click plus one more click in rapid succession on
        // the same real text point. Use point-targeted helpers so the click is not
        // dependent on whatever position the previous probe left the cursor at.
        Mouse.DoubleClick(point, FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(60);
        Mouse.Click(point, FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(700);

        string appended = ReadShakeLogFrom(logPath, baseline);
        Assert(CountOccurrences(appended, "sel-anchor") > 0,
            $"triple-click probe did not start selection. Recent log excerpt: {Truncate(appended, 600)}");
        Assert(CountOccurrences(appended, "sel-extend") > 0,
            $"triple-click probe did not expand to a line selection. Recent log excerpt: {Truncate(appended, 600)}");
        Assert(CountOccurrences(appended, "sel-rect-phys") > 0,
            $"triple-click probe did not draw a selection rectangle. Recent log excerpt: {Truncate(appended, 600)}");
    }

    private static void ProbeContextMenuCopy(Window window)
    {
        SelectSample(window, "Selection");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var start = FindTextPatternPoint(renderer, "Click and drag", "context-copy selection start");
        var end = FindTextPatternPoint(renderer, "The selection spans", "context-copy selection end");
        DragMouseThrough(start, end);
        Thread.Sleep(400);

        string selected = GetRendererSelectionText(renderer);
        Assert(!string.IsNullOrWhiteSpace(selected),
            "context-copy probe must create a selection before opening the menu");

        const string sentinel = "markdown-renderer-context-copy-sentinel";
        SetClipboardText(sentinel);
        var selectedPoint = new System.Drawing.Point(
            (int)Math.Round(start.X + (end.X - start.X) * 0.5),
            (int)Math.Round(start.Y + (end.Y - start.Y) * 0.5));
        Mouse.MoveTo(selectedPoint);
        Thread.Sleep(100);
        Mouse.RightClick();
        Thread.Sleep(400);

        var copyItem = Retry.WhileNull(() =>
            {
                var inWindow = window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(e => string.Equals(e.Name, "Copy", StringComparison.OrdinalIgnoreCase));
                if (inWindow is not null)
                    return inWindow;

                return renderer.Automation.GetDesktop()
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(e => string.Equals(e.Name, "Copy", StringComparison.OrdinalIgnoreCase) && !e.IsOffscreen);
            },
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result
            ?? throw new InvalidOperationException("selected markdown context menu did not expose a Copy item");

        var copyMarkdownItem = Retry.WhileNull(() =>
            {
                var inWindow = window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(e => string.Equals(e.Name, "Copy as Markdown", StringComparison.OrdinalIgnoreCase));
                if (inWindow is not null)
                    return inWindow;

                return renderer.Automation.GetDesktop()
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(e => string.Equals(e.Name, "Copy as Markdown", StringComparison.OrdinalIgnoreCase) && !e.IsOffscreen);
            },
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result
            ?? throw new InvalidOperationException("selected markdown context menu did not expose a Copy as Markdown item");
        Assert(copyMarkdownItem.IsEnabled,
            "Copy as Markdown must be enabled for a non-empty selection");

        copyItem.Click();
        Thread.Sleep(600);

        string copied = GetClipboardText();
        Assert(!string.Equals(copied, sentinel, StringComparison.Ordinal),
            "context-menu Copy left the clipboard unchanged");
        Assert(copied.Contains("select any text", StringComparison.OrdinalIgnoreCase) ||
               copied.Contains("selection spans", StringComparison.OrdinalIgnoreCase),
            $"context-menu Copy should copy selected rendered text, got: {Truncate(copied, 240)}");
        Assert(GetRendererDocumentText(renderer).Length > 0,
            "renderer must remain responsive after context-menu Copy");
    }

    private static void ProbeTargetAwareContextCommands(Window window)
    {
        SelectSample(window, "AuditMatrix");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        Assert(GetRendererDocumentText(renderer).Contains(
                "Document remains responsive after hostile content",
                StringComparison.Ordinal),
            "target-aware context probe requires the complete audit matrix");

        string copiedLink = InvokeTargetContextCommand(
            window,
            renderer,
            FindTextPatternPointAfterScroll(renderer, "keyboard link", "Copy link target"),
            "MarkdownContextCopyLink",
            "Copy link");
        Assert(string.Equals(copiedLink, "https://example.com/audit-link", StringComparison.Ordinal),
            $"Copy link returned an unexpected target: {Truncate(copiedLink, 240)}");

        string copiedTable = InvokeTargetContextCommand(
            window,
            renderer,
            FindTextPatternPointAfterScroll(renderer, "Surface", "Copy table target"),
            "MarkdownContextCopyTable",
            "Copy table");
        Assert(copiedTable.Contains("Surface\tExpected behavior", StringComparison.Ordinal) &&
               copiedTable.Contains("Table\tRemains readable and selectable", StringComparison.Ordinal),
            $"Copy table did not provide rendered TSV: {Truncate(copiedTable, 320)}");

        string copiedCode = InvokeTargetContextCommand(
            window,
            renderer,
            FindTextPatternPointAfterScroll(renderer, "public static string Audit", "Copy code target"),
            "MarkdownContextCopyCode",
            "Copy code");
        Assert(copiedCode.Contains(
                "public static string Audit() => \"selection and copy remain available\";",
                StringComparison.Ordinal) &&
               !copiedCode.Contains("```", StringComparison.Ordinal),
            $"Copy code did not provide fence-free source: {Truncate(copiedCode, 320)}");

        _ = FindTextPatternPointAfterScroll(renderer, "Safe inline image", "Copy image viewport anchor");
        var image = Retry.WhileNull(
            () => renderer.FindAllDescendants(cf => cf.ByControlType(ControlType.Image))
                .FirstOrDefault(element =>
                    (element.Name ?? string.Empty).Contains(
                        "Safe blue audit square",
                        StringComparison.Ordinal) &&
                    !element.IsOffscreen),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result
            ?? throw new InvalidOperationException("Copy image target was not exposed in the visible UIA tree");
        var imageBounds = image.BoundingRectangle;
        string copiedImage = InvokeTargetContextCommand(
            window,
            renderer,
            new System.Drawing.Point(
                imageBounds.Left + imageBounds.Width / 2,
                imageBounds.Top + imageBounds.Height / 2),
            "MarkdownContextCopyImage",
            "Copy image");
        Assert(copiedImage.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase),
            $"Copy image did not retain its source fallback: {Truncate(copiedImage, 240)}");
    }

    private static string InvokeTargetContextCommand(
        Window window,
        AutomationElement renderer,
        System.Drawing.Point point,
        string automationId,
        string localizedName)
    {
        string sentinel = $"markdown-renderer-{automationId}-sentinel";
        SetClipboardText(sentinel);
        Mouse.MoveTo(point);
        Thread.Sleep(100);
        Mouse.RightClick();

        var item = Retry.WhileNull(
            () =>
            {
                var inWindow = window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(element =>
                        string.Equals(AutomationIdOrEmpty(element), automationId, StringComparison.Ordinal) &&
                        !element.IsOffscreen);
                if (inWindow is not null)
                    return inWindow;

                return renderer.Automation.GetDesktop()
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(element =>
                        string.Equals(AutomationIdOrEmpty(element), automationId, StringComparison.Ordinal) &&
                        string.Equals(NameOrEmpty(element), localizedName, StringComparison.OrdinalIgnoreCase) &&
                        !element.IsOffscreen);
            },
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result
            ?? throw new InvalidOperationException(
                $"target context menu did not expose localized '{localizedName}' ({automationId})");
        Assert(item.IsEnabled, $"{localizedName} must be enabled for its rendered target");
        item.Click();

        bool copied = Retry.WhileFalse(
            () => !string.Equals(GetClipboardText(), sentinel, StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(copied, $"{localizedName} left the clipboard unchanged");
        return GetClipboardText();
    }

    private static System.Drawing.Point FindTextPatternPointAfterScroll(
        AutomationElement renderer,
        string text,
        string description)
    {
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException($"{description}: renderer does not expose TextPattern");
        var range = textPattern.DocumentRange.FindText(text, backward: false, ignoreCase: true)
                    ?? throw new InvalidOperationException($"{description}: TextPattern could not find '{text}'");
        range.ScrollIntoView(alignToTop: false);
        Thread.Sleep(400);
        return FindTextPatternPoint(renderer, text, description);
    }

    private static void CaptureAuditScreenshot(AutomationElement element, string fileName)
    {
        string artifactDir = Path.Combine(
            Path.GetFullPath("."), "artifacts", "screenshots", "markdown-audit");
        Directory.CreateDirectory(artifactDir);
        string path = Path.Combine(artifactDir, fileName);
        using var image = FlaUI.Core.Capturing.Capture.Element(
            element, new FlaUI.Core.Capturing.CaptureSettings());
        image.ToFile(path);
        Console.WriteLine($"[automation] screenshot: {path}");
    }

    /// <summary>
    /// Regression probe for the long-standing "text shake" bug: moving the
    /// pointer over body text (hover only, no clicks) must not trigger any
    /// canvas paint events.  Earlier code partial-invalidated the canvas on
    /// every hover transition to apply a link hover-color tweak; that
    /// re-rasterised glyphs at slightly different sub-pixel positions,
    /// producing visible jitter.  We now do not invalidate at all on hover.
    /// This probe drives mouse motion across the canvas and asserts the
    /// ShakeLogger recorded zero paint events between mouse-down boundaries.
    /// </summary>
    private static void ProbeHoverDoesNotShake(Window window)
    {
        // Pick a sample with mixed text + links so hover crosses link/text
        // boundaries (the original repro condition).
        SelectSample(window, "Typography");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var bounds = renderer.BoundingRectangle;

        // Move the cursor away from the canvas first, then read the current
        // log size as our baseline.  Any paint events fired after this point
        // are attributable to our hover motion.
        Mouse.MoveTo((int)bounds.X - 100, (int)bounds.Y - 100);
        Thread.Sleep(400);

        string logPath = FindShakeLog()
            ?? throw new InvalidOperationException("text_shaking2.log not found — sample may not have ShakeLogger enabled");
        long baseline = new FileInfo(logPath).Length;

        // Drive a slow hover sweep across the top portion of the renderer,
        // which is where body text lives.  No mouse buttons pressed.
        int y = (int)(bounds.Y + 60);
        int xStart = (int)(bounds.X + 20);
        int xEnd = (int)(bounds.X + bounds.Width * 0.7);
        for (int x = xStart; x < xEnd; x += 8)
        {
            Mouse.MoveTo(x, y);
            Thread.Sleep(15);
        }
        Thread.Sleep(400);

        // Read everything appended after baseline.
        string appended;
        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
        {
            fs.Seek(baseline, SeekOrigin.Begin);
            appended = sr.ReadToEnd();
        }

        // During pure hover (no mouse-down), there must be NO inline-paint
        // events AND no region events.  The presence of EITHER means we are
        // re-painting on hover, which is the shake source.
        int paintEvents = CountOccurrences(appended, "inline-paint");
        int regionEvents = CountOccurrences(appended, " region ");
        Assert(paintEvents == 0,
            $"hover-shake regression: {paintEvents} inline-paint event(s) fired during pure hover. " +
            $"Recent log excerpt: {Truncate(appended, 400)}");
        Assert(regionEvents == 0,
            $"hover-shake regression: {regionEvents} canvas region event(s) fired during pure hover. " +
            $"Recent log excerpt: {Truncate(appended, 400)}");
    }

    /// <summary>
    /// Regression probe for the embeds-page shake reported from manual testing:
    /// a text-selection drag on the hosted-embeds sample must not repaint the
    /// DirectWrite canvas. Selection pixels live on a single Win2D adorner;
    /// any appended canvas region/inline-paint event after the drag starts means
    /// mouse-down or drag still dirtied document text and can visibly jitter at
    /// 150% DPI.
    /// </summary>
    private static void ProbeEmbedsSelectionDoesNotShake(Window window)
    {
        SelectSample(window, "Embeds");
        Thread.Sleep(1500);

        var renderer = FindRenderer(window);
        var bounds = renderer.BoundingRectangle;

        Mouse.MoveTo(bounds.X - 100, bounds.Y - 100);
        Thread.Sleep(400);

        string logPath = FindShakeLog()
            ?? throw new InvalidOperationException("text_shaking2.log not found — sample may not have ShakeLogger enabled");

        var start = FindSelectableTextPatternPoint(logPath, renderer, "Stable extensions never receive", "embeds drag start");
        var end = FindSelectableTextPatternPoint(logPath, renderer, "content.AddHostedElement", "embeds drag end");
        Assert(Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) >= 80,
            $"embeds selection probe did not find a meaningful in-text drag range: start={start}, end={end}");
        Thread.Sleep(900);

        long baseline = new FileInfo(logPath).Length;

        DragMouseThrough(start, end);
        Thread.Sleep(700);

        string appended = ReadShakeLogFrom(logPath, baseline);
        int anchorEvents = CountOccurrences(appended, "sel-anchor");
        int dragEvents = CountOccurrences(appended, "ptr-move-drag");
        int extendEvents = CountOccurrences(appended, "sel-extend");
        int selectionRectEvents = CountOccurrences(appended, "sel-rect-phys");
        int adornerDrawEvents = CountOccurrences(appended, "sel-adorner-draw");
        int paintEvents = CountOccurrences(appended, "inline-paint");
        int regionEvents = CountOccurrences(appended, " region ");

        Assert(anchorEvents > 0,
            $"embed selection-shake probe did not start a text selection. " +
            $"Recent log excerpt: {Truncate(appended, 600)}");
        Assert(dragEvents > 0,
            $"embed selection-shake probe did not produce drag movement inside the renderer. " +
            $"Recent log excerpt: {Truncate(appended, 600)}");
        Assert(extendEvents > 0,
            $"embed selection-shake probe did not extend a text selection. " +
            $"Recent log excerpt: {Truncate(appended, 600)}");
        Assert(selectionRectEvents > 0,
            $"embed selection-shake probe did not render selection overlay rectangles. " +
            $"Recent log excerpt: {Truncate(appended, 600)}");
        Assert(adornerDrawEvents > 0,
            $"embed selection-shake probe did not render through the dedicated selection adorner. " +
            $"Recent log excerpt: {Truncate(appended, 600)}");
        Assert(!string.IsNullOrWhiteSpace(GetRendererSelectionText(renderer)),
            $"embed selection-shake probe did not expose the resulting selection through TextPattern. " +
            $"Recent log excerpt: {Truncate(appended, 600)}");
        Assert(paintEvents == 0,
            $"embed selection-shake regression: {paintEvents} inline-paint event(s) fired during embeds-page drag. " +
            $"Recent log excerpt: {Truncate(appended, 600)}");
        Assert(regionEvents == 0,
            $"embed selection-shake regression: {regionEvents} canvas region event(s) fired during embeds-page drag. " +
            $"Recent log excerpt: {Truncate(appended, 600)}");
    }

    private static System.Drawing.Point FindTextPatternPoint(AutomationElement renderer, string text, string description)
    {
        var rect = FindTextPatternRect(renderer, text, description);
        var center = new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        Console.WriteLine($"[automation] {description}: using TextPattern point {center} from '{text}' in rect {FormatRect(rect)}");
        return center;
    }

    private static System.Drawing.Rectangle FindTextPatternRect(AutomationElement renderer, string text, string description)
    {
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException($"{description}: renderer does not expose TextPattern");
        var range = textPattern.DocumentRange.FindText(text, backward: false, ignoreCase: true)
                    ?? throw new InvalidOperationException($"{description}: TextPattern could not find '{text}'");

        var rendererBounds = renderer.BoundingRectangle;
        foreach (var rect in range.GetBoundingRectangles())
        {
            if (rect.Width <= 1 || rect.Height <= 1) continue;
            var center = new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            if (rendererBounds.Contains(center))
            {
                return rect;
            }
        }

        throw new InvalidOperationException(
            $"{description}: TextPattern found '{text}' but returned no usable bounding rectangle inside renderer bounds {FormatRect(rendererBounds)}");
    }

    private static System.Drawing.Point FindSelectableTextPatternPoint(
        string logPath,
        AutomationElement renderer,
        string text,
        string description)
    {
        var point = FindTextPatternPoint(renderer, text, description);
        long baseline = new FileInfo(logPath).Length;
        Mouse.MoveTo(point);
        Thread.Sleep(100);
        Mouse.Click(point, FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(350);

        string appended = ReadShakeLogFrom(logPath, baseline);
        Assert(CountOccurrences(appended, "sel-anchor") > 0,
            $"{description}: TextPattern point {point} from '{text}' was not selectable. Recent log excerpt: {Truncate(appended, 600)}");

        Thread.Sleep(750);
        return point;
    }

    private static string FormatRect(System.Drawing.Rectangle rect)
        => $"x={rect.X},y={rect.Y},w={rect.Width},h={rect.Height}";

    private static bool IsNonNoneTextDecoration(object? value)
    {
        if (value is null) return false;
        string s = value.ToString() ?? string.Empty;
        if (string.Equals(s, "None", StringComparison.OrdinalIgnoreCase)) return false;
        return !TryAttributeNumber(value, out var number) || Math.Abs(number) > 0.001;
    }

    private static bool IsTrueAttribute(object? value) =>
        value is bool b
            ? b
            : bool.TryParse(value?.ToString(), out var parsed) && parsed;

    private static int ToColorRefValue(object? value)
    {
        if (value is null)
            throw new InvalidOperationException("Color attribute returned null");

        if (value is int i) return i;
        if (value is uint u) return unchecked((int)u);
        if (TryAttributeNumber(value, out var number)) return (int)Math.Round(number);

        throw new InvalidOperationException($"Cannot interpret color attribute value {DescribeAttributeValue(value)}");
    }

    private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    private static bool TryAttributeNumber(object value, out double number)
    {
        try
        {
            var type = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
            if (type.IsEnum)
            {
                number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
            }
        }
        catch { }

        number = 0;
        return false;
    }

    private static string DescribeAttributeValue(object? value) =>
        value is null ? "<null>" : $"{value} ({value.GetType().FullName})";

    private static void DragMouseThrough(System.Drawing.Point start, System.Drawing.Point end)
    {
        const int steps = 24;
        SendMouseMove(start);
        Thread.Sleep(100);
        SendMouseButton(MouseEventFlags.LeftDown);
        try
        {
            Thread.Sleep(80);
            for (int i = 1; i <= steps; i++)
            {
                double t = i / (double)steps;
                var point = new System.Drawing.Point(
                    (int)Math.Round(start.X + (end.X - start.X) * t),
                    (int)Math.Round(start.Y + (end.Y - start.Y) * t));
                SendMouseMove(point);
                Thread.Sleep(25);
            }
        }
        finally
        {
            SendMouseButton(MouseEventFlags.LeftUp);
        }
    }

    private static void SendMouseMove(System.Drawing.Point point)
    {
        int vx = GetSystemMetrics(SystemMetricVirtualScreenX);
        int vy = GetSystemMetrics(SystemMetricVirtualScreenY);
        int vw = Math.Max(1, GetSystemMetrics(SystemMetricVirtualScreenWidth));
        int vh = Math.Max(1, GetSystemMetrics(SystemMetricVirtualScreenHeight));
        int absoluteX = (int)Math.Round((point.X - vx) * 65535.0 / (vw - 1));
        int absoluteY = (int)Math.Round((point.Y - vy) * 65535.0 / (vh - 1));
        SendMouseInput(MouseEventFlags.Move | MouseEventFlags.Absolute | MouseEventFlags.VirtualDesk, absoluteX, absoluteY);
    }

    private static void SendMouseButton(MouseEventFlags flags)
        => SendMouseInput(flags, 0, 0);

    private static void SendMouseInput(MouseEventFlags flags, int dx, int dy)
    {
        var input = new Input
        {
            Type = InputMouse,
            MouseInput = new MouseInput
            {
                Dx = dx,
                Dy = dy,
                MouseData = 0,
                Flags = flags,
                Time = 0,
                ExtraInfo = UIntPtr.Zero,
            },
        };
        uint sent = SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Input>());
        if (sent != 1)
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                $"SendInput failed for mouse flags {flags}");
        }
    }

    private static void SetClipboardText(string text)
    {
        WithOpenClipboard(() =>
        {
            if (!EmptyClipboard())
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());

            int byteCount = checked((text.Length + 1) * 2);
            IntPtr handle = GlobalAlloc(GlobalMemoryMoveable | GlobalMemoryZeroInit, (UIntPtr)byteCount);
            if (handle == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());

            bool transferred = false;
            try
            {
                IntPtr locked = GlobalLock(handle);
                if (locked == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                try
                {
                    if (text.Length > 0)
                        System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, locked, text.Length);
                    System.Runtime.InteropServices.Marshal.WriteInt16(locked, text.Length * 2, 0);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(ClipboardFormatUnicodeText, handle) == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                transferred = true;
            }
            finally
            {
                if (!transferred)
                    GlobalFree(handle);
            }
        });
    }

    private static string GetClipboardText()
    {
        return WithOpenClipboard(() =>
        {
            IntPtr handle = GetClipboardData(ClipboardFormatUnicodeText);
            if (handle == IntPtr.Zero)
                return string.Empty;

            IntPtr locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
                return string.Empty;
            try
            {
                return System.Runtime.InteropServices.Marshal.PtrToStringUni(locked) ?? string.Empty;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        });
    }

    private static void WithOpenClipboard(Action action) => WithOpenClipboard<object?>(() =>
    {
        action();
        return null;
    });

    private static T WithOpenClipboard<T>(Func<T> action)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try { return action(); }
                finally { CloseClipboard(); }
            }

            last = new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            Thread.Sleep(50);
        }

        throw new InvalidOperationException("Could not open clipboard for automation probe", last);
    }

    private const uint InputMouse = 0;
    private const uint ClipboardFormatUnicodeText = 13;
    private const uint GlobalMemoryMoveable = 0x0002;
    private const uint GlobalMemoryZeroInit = 0x0040;
    private const int SystemMetricVirtualScreenX = 76;
    private const int SystemMetricVirtualScreenY = 77;
    private const int SystemMetricVirtualScreenWidth = 78;
    private const int SystemMetricVirtualScreenHeight = 79;

    [Flags]
    private enum MouseEventFlags : uint
    {
        Move = 0x0001,
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        Absolute = 0x8000,
        VirtualDesk = 0x4000,
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput MouseInput;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public MouseEventFlags Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPArray), System.Runtime.InteropServices.In] Input[] inputs,
        int inputSize);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);

    private static string? FindShakeLog()
    {
        // Walk up from the running automation exe to find text_shaking2.log
        // produced by ShakeLogger; it lands next to the repo root.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "text_shaking2.log");
            if (File.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static int CountOccurrences(string s, string needle)
    {
        if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    private static string ReadShakeLogFrom(string logPath, long baseline)
    {
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        fs.Seek(baseline, SeekOrigin.Begin);
        return sr.ReadToEnd();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";

    private static AutomationElement FindRenderer(Window window)
        => window.FindFirstDescendant(cf => cf.ByAutomationId("MarkdownRenderer"))
           ?? throw new InvalidOperationException("MarkdownRenderer not found in automation tree");

    private static AutomationElement? FindFirstRawDescendantByAutomationId(
        AutomationElement root,
        string automationId)
    {
        var walker = root.Automation.TreeWalkerFactory.GetRawViewWalker();
        var pending = new Queue<AutomationElement>();
        EnqueueChildren(root);
        while (pending.Count > 0)
        {
            AutomationElement current = pending.Dequeue();
            if (string.Equals(
                    AutomationIdOrEmpty(current),
                    automationId,
                    StringComparison.Ordinal))
            {
                return current;
            }

            EnqueueChildren(current);
        }

        return null;

        void EnqueueChildren(AutomationElement parent)
        {
            AutomationElement? child;
            try { child = walker.GetFirstChild(parent); }
            catch { return; }

            while (child is not null)
            {
                pending.Enqueue(child);
                try { child = walker.GetNextSibling(child); }
                catch { return; }
            }
        }
    }

    private static AutomationElement WaitForDiagnostics(Window window, string expectedCode)
    {
        AutomationElement? diagnostics = null;
        bool ready = Retry.WhileFalse(
            () =>
            {
                diagnostics = window.FindFirstDescendant(
                    cf => cf.ByAutomationId("SampleDiagnosticsMessage"));
                return diagnostics is not null &&
                       GetAutomationElementText(diagnostics).Contains(expectedCode, StringComparison.Ordinal);
            },
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(ready, $"renderer diagnostics did not expose {expectedCode}");
        return diagnostics!;
    }

    private static string GetAutomationElementText(AutomationElement element)
    {
        var textPattern = element.Patterns.Text.PatternOrDefault;
        return textPattern is null
            ? NameOrEmpty(element)
            : textPattern.DocumentRange.GetText(-1);
    }

    private static string GetRendererDocumentText(AutomationElement renderer)
    {
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        return textPattern.DocumentRange.GetText(-1);
    }

    private static string GetRendererSelectionText(AutomationElement renderer)
    {
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        return string.Concat(textPattern.GetSelection().Select(range => range.GetText(-1)));
    }

    private static void SelectSample(Window window, string key)
    {
        AutomationElement item = window.FindFirstDescendant(
            cf => cf.ByAutomationId("SampleNav_" + key))
            ?? throw new InvalidOperationException($"{key} sample navigation item not found");

        if (item.Patterns.ScrollItem.IsSupported)
        {
            try { item.Patterns.ScrollItem.Pattern.ScrollIntoView(); }
            catch { }
        }

        if (item.Patterns.SelectionItem.IsSupported)
        {
            item.Patterns.SelectionItem.Pattern.Select();
        }
        else if (item.Patterns.Invoke.IsSupported)
        {
            item.Patterns.Invoke.Pattern.Invoke();
        }
        else
        {
            item.Click();
        }

        string expectedStatus = "page:" + key;
        bool selected = Retry.WhileFalse(
            () => string.Equals(
                FindFirstRawDescendantByAutomationId(window, "CurrentSamplePage")?.Name,
                expectedStatus,
                StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(selected, $"{key} sample page did not become current");
    }

    private static int CountEmbedButtons(AutomationElement renderer)
        => renderer.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).Length;

    /// <summary>
    /// Reads the realised embed count published by the sample app's hidden
    /// status TextBlock ("realized:N"), which mirrors
    /// MarkdownRendererControl.RealizedEmbedCount via the
    /// EmbedsRealizationChanged event. We intentionally do not read this
    /// from the renderer's own UIA properties to avoid Narrator announcing
    /// it on every scroll.
    /// </summary>
    private static int ReadRealizedEmbedCount(AutomationElement renderer)
    {
        try
        {
            // Find the status TextBlock within the application window tree only.
            // Stop at ControlType.Window so we never search the entire desktop and
            // cannot accidentally read a count from a different application.
            var root = renderer;
            while (root.Parent is not null
                   && root.ControlType != FlaUI.Core.Definitions.ControlType.Window)
                root = root.Parent;
            var status = FindFirstRawDescendantByAutomationId(root, "RealizedEmbedCount");
            string? text = status?.Name ?? status?.Properties.Name.ValueOrDefault;
            if (string.IsNullOrEmpty(text)) return 0;
            const string prefix = "realized:";
            int idx = text.IndexOf(prefix, StringComparison.Ordinal);
            if (idx < 0) return 0;
            return int.TryParse(text.AsSpan(idx + prefix.Length), out var n) ? n : 0;
        }
        catch { return 0; }
    }

    private static void RunProbe(string name, Action probe)
    {
        try
        {
            probe();
            Passes.Add(name);
            Console.WriteLine($"[automation] PASS {name}");
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: {ex.Message}");
            Console.Error.WriteLine($"[automation] FAIL {name}: {ex}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void TryFocus(AutomationElement element, string description)
    {
        try { element.Focus(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[automation] warn: could not focus {description}: {ex.Message}");
        }
    }

    private static void PositionPointerOnWritableInputDesktop(System.Drawing.Point target)
    {
        System.Drawing.Point original = Mouse.Position;
        var probe = new System.Drawing.Point(
            target.X == int.MaxValue ? target.X - 1 : target.X + 1,
            target.Y);
        Mouse.Position = probe;
        Thread.Sleep(50);
        System.Drawing.Point actual = Mouse.Position;
        if (actual != probe)
            throw new InvalidOperationException(
                "Pointer-input infrastructure precondition failed: the automation process cannot move " +
                $"the desktop cursor from {original} to {probe} (actual={actual}). " +
                "Run this probe on an interactive, unlocked, writable input desktop.");

        Mouse.Position = target;
        Thread.Sleep(50);
        actual = Mouse.Position;
        if (actual != target)
            throw new InvalidOperationException(
                "Pointer-input infrastructure precondition failed: the automation process moved a probe cursor " +
                $"but could not position it on the Mermaid link at {target} (actual={actual}).");
    }

    private static void WaitForSampleContent(Window window, Application app)
    {
        var ready = Retry.WhileFalse(() =>
            {
                if (app.HasExited) return true;
                try
                {
                    return window.FindFirstDescendant(cf => cf.ByAutomationId("MarkdownRenderer")) is not null &&
                           window.FindFirstDescendant(cf => cf.ByAutomationId("SampleNavigation")) is not null &&
                           window.FindFirstDescendant(cf => cf.ByAutomationId("SampleNav_FullDemo")) is not null;
                }
                catch { return false; }
            },
            timeout: TimeSpan.FromSeconds(20),
            interval: TimeSpan.FromMilliseconds(250)).Result;

        if (app.HasExited)
            throw new InvalidOperationException("Sample app exited before UIA content became available.");
        if (!ready)
            throw new InvalidOperationException("Sample app did not expose its renderer and side navigation within 20 seconds.");
    }

    private static string? ParseAppPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--app-path") return args[i + 1];
        return null;
    }

    private static Application LaunchSample(string appPath, bool enableDiagnostics)
    {
        var startInfo = new ProcessStartInfo(appPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory,
        };

        if (enableDiagnostics)
            startInfo.Environment[DiagnosticsEnvironmentVariable] = "1";
        startInfo.Environment["MARKDOWN_RENDERER_DISPOSAL_EVIDENCE"] = DisposalEvidencePath;

        return Application.Launch(startInfo);
    }

    private static string? FindDefaultAppPath()
    {
        string root = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(root);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        if (dir is null) return null;
        string sampleDir = Path.Combine(dir.FullName, "MarkdownRenderer", "MarkdownRenderer.Sample", "bin");
        if (!Directory.Exists(sampleDir)) return null;
        var candidates = Directory.GetFiles(sampleDir, "MarkdownRenderer.Sample.exe", SearchOption.AllDirectories);
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static void KillExistingApplicationInstances(string appPath)
    {
        string targetName = Path.GetFileNameWithoutExtension(appPath);
        foreach (var p in Process.GetProcessesByName(targetName))
        {
            using (p)
            {
                try
                {
                    // Only kill processes whose exe path matches exactly to avoid
                    // killing unrelated apps that share the same process name.
                    if (string.Equals(p.MainModule?.FileName, appPath,
                            StringComparison.OrdinalIgnoreCase))
                    { p.Kill(); if (!p.WaitForExit(2000)) Console.Error.WriteLine($"[automation] warn: PID {p.Id} did not exit within 2 s after Kill() — tests may be unreliable"); }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                               or InvalidOperationException)
                {
                    // MainModule access can fail for elevated or cross-bitness processes;
                    // log a warning rather than silently skipping.
                    Console.Error.WriteLine($"[automation] warn: could not inspect PID {p.Id}: {ex.Message}");
                }
                catch { /* unexpected — ignore */ }
            }
        }
    }
}
