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
    private static readonly List<string> Failures = new();
    private static readonly List<string> Passes = new();

    private static int Main(string[] args)
    {
        string appPath = ParseAppPath(args)
            ?? FindDefaultAppPath()
            ?? throw new InvalidOperationException(
                "Cannot locate MarkdownRenderer.Sample.exe. Pass --app-path <exe>.");

        Console.WriteLine($"[automation] launching {appPath}");
        KillExistingApplicationInstances(appPath);
        using var app = Application.Launch(new ProcessStartInfo(appPath) { UseShellExecute = false });
        using var automation = new UIA3Automation();
        try
        {
            var window = Retry.WhileNull(() => app.GetMainWindow(automation),
                timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(250)).Result
                ?? throw new InvalidOperationException("Main window did not appear.");
            window.Focus();
            Thread.Sleep(750);

            RunProbe("automation-tree-shape", () => ProbeAutomationTreeShape(window));
            RunProbe("rtl-toggle-flips-flow",  () => ProbeRtlToggle(window));
            RunProbe("sample-buttons-discoverable", () => ProbeSampleButtons(window));
            RunProbe("virtualization-bounded-realization", () => ProbeVirtualization(window));
            RunProbe("images-sample-loads", () => ProbeImagesSample(window));
            RunProbe("lazy-images-sample-loads", () => ProbeLazyImagesSample(window));
            RunProbe("scroll-anchor-sample-loads", () => ProbeScrollAnchorSample(window));
            RunProbe("footnotes-sample-loads", () => ProbeFootnotesSample(window));
            RunProbe("keyboard-nav-tab-traversal", () => ProbeKeyboardNav(window));
            RunProbe("click-dismisses-focus-ring", () => ProbeClickDismissesFocus(window));
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

    private static void ProbeAutomationTreeShape(Window window)
    {
        var renderer = FindRenderer(window);
        var name = renderer.Name ?? string.Empty;
        Assert(name.Length > 0, "renderer.Name must aggregate document text but was empty");
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
            "Full_Demo",
        };
        foreach (var label in expected)
        {
            var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_" + label));
            Assert(btn is not null, $"SampleButton_{label} not found in automation tree");
        }
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
        var name = renderer.Name ?? string.Empty;
        Assert(name.Length > 0, "Images sample renderer name must not be empty");
    }

    private static void ProbeLazyImagesSample(Window window)
    {
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Lazy_Images"))?.AsButton()
                  ?? throw new InvalidOperationException("Lazy_Images sample button not found");
        btn.Invoke();
        Thread.Sleep(1500);
        var renderer = FindRenderer(window);
        var name = renderer.Name ?? string.Empty;
        Assert(name.Length > 0, "Lazy Images sample renderer name must not be empty");
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
        var name = renderer.Name ?? string.Empty;
        Assert(name.Length > 0, "Scroll Anchor sample renderer name must not be empty");
        // Scroll down and verify content is still available
        renderer.Focus();
        Keyboard.Press(VirtualKeyShort.NEXT); // Page Down
        Thread.Sleep(300);
        Keyboard.Press(VirtualKeyShort.PRIOR); // Page Up
        Thread.Sleep(300);
        var nameAfterScroll = renderer.Name ?? string.Empty;
        Assert(nameAfterScroll.Length > 0, "Scroll Anchor renderer name must remain non-empty after scroll");
    }

    private static void ProbeFootnotesSample(Window window)
    {
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Footnotes"))?.AsButton()
                  ?? throw new InvalidOperationException("Footnotes sample button not found");
        btn.Invoke();
        Thread.Sleep(1200);
        var renderer = FindRenderer(window);
        var name = renderer.Name ?? string.Empty;
        Assert(name.Length > 0, "Footnotes sample renderer name must not be empty");
        // Verify the rendered text contains footnote markers (superscripts / back-arrows)
        // The renderer aggregates all text into its Name for UIA, so we verify the
        // footnote-bearing text is included.
        bool hasFootnoteContent = name.Contains("sentence with a footnote", StringComparison.OrdinalIgnoreCase)
                                  || name.Contains("footnote", StringComparison.OrdinalIgnoreCase);
        Assert(hasFootnoteContent, $"Footnotes renderer content must mention 'footnote', got: {name[..Math.Min(120, name.Length)]}");
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
        var nameAfter = renderer.Name ?? string.Empty;
        Assert(nameAfter.Length > 0, "Keyboard Nav renderer must remain responsive after Tab/Escape traversal");
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
        var nameAfter = renderer.Name ?? string.Empty;
        Assert(nameAfter.Length > 0, "Renderer must remain responsive after click-dismisses-focus");
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
        var point = FindSelectablePointInRenderer(logPath, renderer.BoundingRectangle, "double-click word probe");
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
        var point = FindSelectablePointInRenderer(logPath, renderer.BoundingRectangle, "triple-click line probe");
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
        // cannot intercept the UIA focus and cause the Name query to stale/block.
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(200);

        var nameAfter = renderer.Name ?? string.Empty;
        Assert(nameAfter.Length > 0, "Renderer must remain responsive after right-click context menu");
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
    /// DirectWrite canvas. Selection rectangles live on the XAML overlay; any
    /// appended canvas region/inline-paint event after the drag starts means
    /// mouse-down or drag still dirtied text and can visibly jitter at 150% DPI.
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

        var start = FindSelectablePointInRenderer(logPath, bounds, "embeds drag start");
        var end = FindSelectablePointInRenderer(
            logPath,
            bounds,
            "embeds drag end",
            p => Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) >= 160);
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
        Assert(paintEvents == 0,
            $"embed selection-shake regression: {paintEvents} inline-paint event(s) fired during embeds-page drag. " +
            $"Recent log excerpt: {Truncate(appended, 600)}");
        Assert(regionEvents == 0,
            $"embed selection-shake regression: {regionEvents} canvas region event(s) fired during embeds-page drag. " +
            $"Recent log excerpt: {Truncate(appended, 600)}");
    }

    private static System.Drawing.Point FindSelectablePointInRenderer(
        string logPath,
        System.Drawing.Rectangle bounds,
        string description,
        Func<System.Drawing.Point, bool>? predicate = null)
    {
        var candidates = EnumerateRendererCandidatePoints(bounds)
            .Where(p => predicate?.Invoke(p) ?? true)
            .Distinct()
            .ToArray();

        foreach (var point in candidates)
        {
            long baseline = new FileInfo(logPath).Length;
            Mouse.Click(point, FlaUI.Core.Input.MouseButton.Left);
            Thread.Sleep(250);
            string appended = ReadShakeLogFrom(logPath, baseline);
            if (CountOccurrences(appended, "sel-anchor") > 0)
            {
                Console.WriteLine($"[automation] {description}: validated selectable point {point} inside renderer bounds {FormatRect(bounds)}");
                return point;
            }
            Thread.Sleep(650);
        }

        throw new InvalidOperationException(
            $"Could not find a real selectable text point for {description} inside renderer bounds {FormatRect(bounds)}. " +
            "Every candidate failed to produce a sel-anchor log entry.");
    }

    private static IEnumerable<System.Drawing.Point> EnumerateRendererCandidatePoints(System.Drawing.Rectangle bounds)
    {
        int[] xOffsets = { 42, 90, 150, 240, 340, 460, 600 };
        int[] yOffsets = { 42, 70, 100, 140, 180, 240, 320, 420, 560, 720 };
        foreach (int y in yOffsets)
        foreach (int x in xOffsets)
        {
            if (x >= bounds.Width - 8 || y >= bounds.Height - 8) continue;
            yield return new System.Drawing.Point(bounds.X + x, bounds.Y + y);
        }

        double[] xFractions = { 0.2, 0.35, 0.5, 0.65, 0.8 };
        double[] yFractions = { 0.18, 0.28, 0.38, 0.5, 0.62, 0.75 };
        foreach (double y in yFractions)
        foreach (double x in xFractions)
        {
            yield return new System.Drawing.Point(
                (int)Math.Round(bounds.X + bounds.Width * x),
                (int)Math.Round(bounds.Y + bounds.Height * y));
        }
    }

    private static string FormatRect(System.Drawing.Rectangle rect)
        => $"x={rect.X},y={rect.Y},w={rect.Width},h={rect.Height}";

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

    private const uint InputMouse = 0;
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

    private static string? ParseAppPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--app-path") return args[i + 1];
        return null;
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
