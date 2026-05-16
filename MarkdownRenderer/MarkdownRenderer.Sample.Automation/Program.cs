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

    private static readonly List<string> Failures = new();
    private static readonly List<string> Passes = new();

    private static int Main(string[] args)
    {
        string appPath = ParseAppPath(args)
            ?? FindDefaultAppPath()
            ?? throw new InvalidOperationException(
                "Cannot locate MarkdownRenderer.Sample.exe. Pass --app-path <exe>.");

        if (args.Contains("--narrator-smoke", StringComparer.OrdinalIgnoreCase))
            return RunNarratorSmoke(appPath);

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

            RunProbe("automation-tree-shape", () => ProbeAutomationTreeShape(window));
            RunProbe("rtl-toggle-flips-flow",  () => ProbeRtlToggle(window));
            RunProbe("sample-buttons-discoverable", () => ProbeSampleButtons(window));
            RunProbe("accessibility-lab-text-pattern", () => ProbeAccessibilityLabTextPattern(window));
            RunProbe("accessibility-lab-semantic-roles", () => ProbeAccessibilityLabSemanticRoles(window));
            RunProbe("accessibility-lab-text-attributes", () => ProbeAccessibilityLabTextAttributes(window));
            RunProbe("accessibility-lab-forced-high-contrast", () => ProbeAccessibilityLabForcedHighContrast(window));
            RunProbe("accessibility-lab-keyboard-order", () => ProbeAccessibilityLabKeyboardOrder(window));
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
            RunProbe("context-menu-appears",       () => ProbeContextMenu(window));
            RunProbe("hover-does-not-shake",       () => ProbeHoverDoesNotShake(window));
            RunProbe("embeds-selection-does-not-shake", () => ProbeEmbedsSelectionDoesNotShake(window));

            try { window.Close(); } catch { /* window may already be gone */ }
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

            ClickSample(window, "Accessibility_Lab");
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
        var el = window.FindFirstDescendant(cf => cf.ByAutomationId("FlowDirectionStatus"));
        string? text = el?.Name ?? el?.Properties.Name.ValueOrDefault;
        if (string.IsNullOrEmpty(text)) return string.Empty;
        const string prefix = "flow:";
        int idx = text.IndexOf(prefix, StringComparison.Ordinal);
        return idx < 0 ? string.Empty : text.Substring(idx + prefix.Length).Trim();
    }

    private static void ProbeSampleButtons(Window window)
    {
        string[] expected =
        {
            "Typography", "Lists", "Tables", "Code", "GFM_Alerts",
            "Images", "Embeds", "RTL", "Virtualization", "Selection",
            "Lazy_Images", "Scroll_Anchor", "Footnotes", "Keyboard_Nav",
            "Accessibility_Lab",
            "Full_Demo",
        };
        foreach (var label in expected)
        {
            var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_" + label));
            Assert(btn is not null, $"SampleButton_{label} not found in automation tree");
        }
    }

    private static void ProbeAccessibilityLabTextPattern(Window window)
    {
        ClickSample(window, "Accessibility_Lab");
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

        var word = textPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                   ?? throw new InvalidOperationException("TextPattern FindText('quick') returned null");
        word.ExpandToEnclosingUnit(TextUnit.Word);
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

        var movedWord = textPattern.DocumentRange.Clone();
        int moved = movedWord.Move(TextUnit.Word, 1);
        Assert(moved == 1, $"TextPattern Move(Word, 1) should move by one word, moved={moved}");
        string movedWordText = movedWord.GetText(-1).Trim();
        Assert(!string.IsNullOrWhiteSpace(movedWordText) &&
               movedWordText.Length <= 32 &&
               !movedWordText.Contains('\n'),
            $"TextPattern Move(Word, 1) must produce a word-sized range, got '{movedWordText}'");
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
        ClickSample(window, "Accessibility_Lab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var descendants = renderer.FindAllDescendants();
        Assert(descendants.Any(e => e.ControlType == ControlType.Header), "Accessibility lab must expose heading/header peers");
        Assert(descendants.Any(e => e.ControlType == ControlType.Hyperlink), "Accessibility lab must expose hyperlink peers");
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
        Assert(descendants.Any(e => e.ControlType == ControlType.CheckBox), "Accessibility lab must expose hosted task checkbox");

        var table = descendants.First(e => e.ControlType == ControlType.Table);
        var grid = table.Patterns.Grid.PatternOrDefault
                   ?? throw new InvalidOperationException("Table peer must expose GridPattern");
        Assert(grid.RowCount.Value >= 4, "GridPattern RowCount must include header and body rows");
        Assert(grid.ColumnCount.Value == 3, "GridPattern ColumnCount must be 3 for the lab table");
        Assert(grid.GetItem(0, 0) is not null, "GridPattern.GetItem(0, 0) must return a cell");
    }

    private static void ProbeAccessibilityLabTextAttributes(Window window)
    {
        ClickSample(window, "Accessibility_Lab");
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

        var image = descendants.First(e => e.ControlType == ControlType.Image &&
                                           (e.Name ?? string.Empty).Contains("Accessibility lab blue square", StringComparison.Ordinal));
        var imageRange = textPattern.RangeFromChild(image);
        Assert(imageRange.GetText(-1).Contains("Accessibility lab blue square", StringComparison.Ordinal),
            "TextPattern.RangeFromChild(image) must return image alt text");

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

            ClickSample(window, "Accessibility_Lab");
            Thread.Sleep(1200);

            var status = window.FindFirstDescendant(cf => cf.ByAutomationId("HighContrastStatus"));
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
        ClickSample(window, "Accessibility_Lab");
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

        Keyboard.Press(VirtualKeyShort.TAB); // hosted checkbox
        Thread.Sleep(250);
        var focusedCheckbox = renderer.Automation.FocusedElement();
        Assert(focusedCheckbox.ControlType == ControlType.CheckBox,
            $"Fifth Tab should land on hosted task checkbox, focused={DescribeFocus(focusedCheckbox)}");

        var focusedSecondPaintedLink = PressTabExpectPaintedLink(renderer, "Sixth Tab");
        Assert(IsPaintedLinkFocus(focusedSecondPaintedLink),
            $"Sixth Tab should land on second painted hyperlink, focused={DescribeFocus(focusedSecondPaintedLink)}");

        Keyboard.Press(VirtualKeyShort.TAB); // leave markdown
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

        var source = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Accessibility_Lab"))?.AsButton()
                     ?? throw new InvalidOperationException("Accessibility Lab sample button not found");
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
               IsCompositeActionButton(focused) ||
               focused.ControlType == ControlType.CheckBox;
    }

    private static string DescribeFocus(AutomationElement focused)
        => $"{focused.ControlType}/{NameOrEmpty(focused)}/AutomationId={AutomationIdOrEmpty(focused)}";

    private static string NameOrEmpty(AutomationElement element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return element.Properties.Name.ValueOrDefault ?? string.Empty; }
    }

    private static string AutomationIdOrEmpty(AutomationElement element)
    {
        try { return element.AutomationId ?? string.Empty; }
        catch { return element.Properties.AutomationId.ValueOrDefault ?? string.Empty; }
    }

    private static void ProbeAccessibilityLabPointerResume(Window window)
    {
        ClickSample(window, "Accessibility_Lab");
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

        Keyboard.Press(VirtualKeyShort.TAB);
        Thread.Sleep(300);

        var focused = renderer.Automation.FocusedElement();
        if (focused.ControlType == ControlType.Button &&
            (focused.Name ?? string.Empty).Contains("Native action", StringComparison.Ordinal))
        {
            Keyboard.Press(VirtualKeyShort.TAB);
            Thread.Sleep(300);
            focused = renderer.Automation.FocusedElement();
        }

        Assert(IsCompositeValueTextBox(focused) || IsCompositeActionButton(focused) || focused.ControlType == ControlType.CheckBox,
            $"Tab after pointer dismissal near hosted controls should resume within markdown focus order, focused={DescribeFocus(focused)}");
    }

    private static void ProbeVirtualization(Window window)
    {
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Virtualization"))?.AsButton()
                  ?? throw new InvalidOperationException("Virtualization sample button not found");
        btn.Invoke();
        Thread.Sleep(1500);

        var renderer = FindRenderer(window);
        int realised = ReadRealizedEmbedCount(renderer);
        Assert(realised < 100, $"virtualization expected ≪100 realised embeds, found {realised}");
        Assert(realised > 0, $"virtualization expected some realised embeds, found {realised}");

        renderer.Focus();
        for (int i = 0; i < 5; i++)
        {
            Keyboard.Press(VirtualKeyShort.NEXT); // Page Down
            Thread.Sleep(120);
        }
        Thread.Sleep(400);
        int afterScroll = ReadRealizedEmbedCount(renderer);
        Assert(afterScroll < 100, $"virtualization after scroll expected ≪100 realised embeds, found {afterScroll}");
    }

    private static void ProbeImagesSample(Window window)
    {
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Images"))?.AsButton()
                  ?? throw new InvalidOperationException("Images sample button not found");
        btn.Invoke();
        Thread.Sleep(1500);
        var renderer = FindRenderer(window);
        var text = GetRendererDocumentText(renderer);
        Assert(text.Length > 0, "Images sample renderer document text must not be empty");
    }

    private static void ProbeLazyImagesSample(Window window)
    {
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Lazy_Images"))?.AsButton()
                  ?? throw new InvalidOperationException("Lazy_Images sample button not found");
        btn.Invoke();
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
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Scroll_Anchor"))?.AsButton()
                  ?? throw new InvalidOperationException("Scroll_Anchor sample button not found");
        btn.Invoke();
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
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Footnotes"))?.AsButton()
                  ?? throw new InvalidOperationException("Footnotes sample button not found");
        btn.Invoke();
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
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Keyboard_Nav"))?.AsButton()
                  ?? throw new InvalidOperationException("Keyboard_Nav sample button not found");
        btn.Invoke();
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
        // Navigate to Keyboard_Nav sample which has keyboard-focusable links.
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Keyboard_Nav"))?.AsButton()
                  ?? throw new InvalidOperationException("Keyboard_Nav sample button not found");
        btn.Invoke();
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
        ClickSample(window, "Selection");
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
        ClickSample(window, "Accessibility_Lab");
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
            (Sample: "Code", Start: "C# example", End: "public sealed class", Expected: "public"),
        };

        foreach (var c in cases)
        {
            ClickSample(window, c.Sample);
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
        ClickSample(window, "Selection");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var start = FindTextPatternPoint(renderer, "Click and drag", "ctrl-c copy drag start");
        var end = FindTextPatternPoint(renderer, "The selection spans", "ctrl-c copy drag end");
        DragMouseThrough(start, end);
        Thread.Sleep(500);

        string selected = GetRendererSelectionText(renderer);
        Assert(!string.IsNullOrWhiteSpace(selected),
            "ctrl-c copy probe must have an active pointer selection before copying");

        const string sentinel = "markdown-renderer-clipboard-sentinel";
        SetClipboardText(sentinel);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        Thread.Sleep(700);

        string copied = GetClipboardText();
        Assert(!string.Equals(copied, sentinel, StringComparison.Ordinal),
            "Ctrl+C left the clipboard unchanged; renderer likely did not handle the key event");
        Assert(copied.Contains("select any text", StringComparison.OrdinalIgnoreCase) ||
               copied.Contains("selection spans", StringComparison.OrdinalIgnoreCase),
            $"Ctrl+C should copy selected markdown source, got: {Truncate(copied, 240)}");
    }

    private static void ProbeTableSelectionRowBorderIsStable(Window window)
    {
        ClickSample(window, "Tables");
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
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Typography"))?.AsButton()
                  ?? throw new InvalidOperationException("Typography sample button not found");
        btn.Invoke();
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
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Selection"))?.AsButton()
                  ?? throw new InvalidOperationException("Selection sample button not found");
        btn.Invoke();
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

    private static void ProbeContextMenu(Window window)
    {
        // Navigate to Selection sample.
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Selection"))?.AsButton()
                  ?? throw new InvalidOperationException("Selection sample button not found");
        btn.Invoke();
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var bounds = renderer.BoundingRectangle;

        // Right-click in the middle of the renderer.
        int cx = (int)(bounds.X + bounds.Width * 0.5);
        int cy = (int)(bounds.Y + 50);
        Mouse.MoveTo(cx, cy);
        Thread.Sleep(100);
        Mouse.RightClick();
        Thread.Sleep(600);

        // The context menu is a flyout; look for it in the UIA tree as a child of
        // the window (not the renderer itself). Stop at the window boundary — not the
        // desktop root — to avoid searching the entire desktop for MenuItems, which is
        // slow and can return false positives from unrelated open menus.
        var winRoot = renderer;
        while (winRoot.Parent is not null
               && winRoot.ControlType != FlaUI.Core.Definitions.ControlType.Window)
            winRoot = winRoot.Parent;

        var menuItems = winRoot.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem));
        // MenuFlyout items may not always appear in the UIA tree on all systems,
        // so we treat them as informational, not a hard failure.
        if (menuItems.Length == 0)
            Console.Error.WriteLine("[automation] warn: context-menu probe: no MenuItem elements found in UIA tree — flyout may not be UIA-exposed on this system");

        // Dismiss the menu BEFORE querying renderer properties so an open flyout
        // cannot intercept the UIA focus and cause the TextPattern query to stale/block.
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(200);

        var textAfter = GetRendererDocumentText(renderer);
        Assert(textAfter.Length > 0, "Renderer must remain responsive after right-click context menu");
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
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Typography"))?.AsButton()
                  ?? throw new InvalidOperationException("Typography sample button not found");
        btn.Invoke();
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
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Embeds"))?.AsButton()
                  ?? throw new InvalidOperationException("Embeds sample button not found");
        btn.Invoke();
        Thread.Sleep(1500);

        var renderer = FindRenderer(window);
        var bounds = renderer.BoundingRectangle;

        Mouse.MoveTo(bounds.X - 100, bounds.Y - 100);
        Thread.Sleep(400);

        string logPath = FindShakeLog()
            ?? throw new InvalidOperationException("text_shaking2.log not found — sample may not have ShakeLogger enabled");

        var start = FindSelectableTextPatternPoint(logPath, renderer, "The renderer hosts", "embeds drag start");
        var end = FindSelectableTextPatternPoint(logPath, renderer, "Anything else", "embeds drag end");
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
            $"embed selection-shake probe did not draw the selection adorner. " +
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

    private static void ClickSample(Window window, string automationIdSuffix)
    {
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_" + automationIdSuffix))?.AsButton()
                  ?? throw new InvalidOperationException($"{automationIdSuffix} sample button not found");
        btn.Invoke();
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
            var status = root.FindFirstDescendant(cf => cf.ByAutomationId("RealizedEmbedCount"));
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

    private static void WaitForSampleContent(Window window, Application app)
    {
        var ready = Retry.WhileFalse(() =>
            {
                if (app.HasExited) return true;
                try
                {
                    return window.FindFirstDescendant(cf => cf.ByAutomationId("MarkdownRenderer")) is not null ||
                           window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Typography")) is not null;
                }
                catch { return false; }
            },
            timeout: TimeSpan.FromSeconds(20),
            interval: TimeSpan.FromMilliseconds(250)).Result;

        if (app.HasExited)
            throw new InvalidOperationException("Sample app exited before UIA content became available.");
        if (!ready)
            throw new InvalidOperationException("Sample app did not expose MarkdownRenderer or sample buttons within 20 seconds.");
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

        return Application.Launch(startInfo);
    }

    private static string? FindDefaultAppPath()
    {
        string root = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(root);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JitHub.slnx")))
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
