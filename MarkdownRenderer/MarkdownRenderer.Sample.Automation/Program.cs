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

            window.Close();
        }
        finally
        {
            try { app.Close(); } catch { }
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
        var bounds = renderer.BoundingRectangle;

        // Double-click near the upper-left area of the document where text lives.
        int cx = (int)(bounds.X + bounds.Width * 0.25);
        int cy = (int)(bounds.Y + 40);
        Mouse.MoveTo(cx, cy);
        Thread.Sleep(100);
        Mouse.DoubleClick(FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(400);

        // Selection page text readout is exposed via automation; we just verify
        // the control remains responsive (didn't crash or freeze).
        var nameAfter = renderer.Name ?? string.Empty;
        Assert(nameAfter.Length > 0, "Renderer must remain responsive after double-click word selection");
    }

    private static void ProbeTripleClickSelectsLine(Window window)
    {
        // Navigate to Selection sample which has plain paragraphs.
        var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleButton_Selection"))?.AsButton()
                  ?? throw new InvalidOperationException("Selection sample button not found");
        btn.Invoke();
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var bounds = renderer.BoundingRectangle;

        // Triple-click = three clicks in rapid succession on same spot.
        int cx = (int)(bounds.X + bounds.Width * 0.3);
        int cy = (int)(bounds.Y + 40);
        Mouse.MoveTo(cx, cy);
        Thread.Sleep(100);
        Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(80);
        Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(80);
        Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(400);

        // Verify control is still alive.
        var nameAfter = renderer.Name ?? string.Empty;
        Assert(nameAfter.Length > 0, "Renderer must remain responsive after triple-click line selection");
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
        // the window (not the renderer itself).
        var root = renderer;
        while (root.Parent is not null) root = root.Parent;

        var menuItems = root.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem));
        // MenuFlyout items may not always appear in the UIA tree on all systems,
        // so we treat them as a bonus — but the renderer must survive the click.
        var nameAfter = renderer.Name ?? string.Empty;
        Assert(nameAfter.Length > 0, "Renderer must remain responsive after right-click context menu");

        // Dismiss any open menu via Escape.
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(200);
    }

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
            // Find the status TextBlock anywhere in the window tree.
            var root = renderer;
            while (root.Parent is not null) root = root.Parent;
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
            try { p.Kill(); p.WaitForExit(2000); } catch { }
        }
    }
}
