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
            "Images", "Embeds", "RTL", "Virtualization", "Selection", "Full_Demo",
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
        Assert(realised < 100, $"virtualization expected ≪300 realised embeds, found {realised}");
        Assert(realised > 0, $"virtualization expected some realised embeds, found {realised}");

        renderer.Focus();
        for (int i = 0; i < 5; i++)
        {
            Keyboard.Press(VirtualKeyShort.NEXT); // Page Down
            Thread.Sleep(120);
        }
        Thread.Sleep(400);
        int afterScroll = ReadRealizedEmbedCount(renderer);
        Assert(afterScroll < 100, $"virtualization after scroll expected ≪300 realised embeds, found {afterScroll}");
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
