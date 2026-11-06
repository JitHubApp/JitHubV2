using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using static MarkdownRenderer.Sample.Automation.Probes.AccessibilityLabKeyboardProbes;
using static MarkdownRenderer.Sample.Automation.Uia.AutomationElementText;
using static MarkdownRenderer.Sample.Automation.Uia.SampleWindowQueries;

namespace MarkdownRenderer.Sample.Automation;

/// <summary>
/// Runs the --narrator-smoke pass: starts Narrator over the Accessibility Lab and captures what it highlights.
/// </summary>
internal static class NarratorSmoke
{
    internal static int Run(SampleAppLauncher launcher)
    {
        Console.WriteLine($"[narrator-smoke] launching {launcher.AppPath}");
        launcher.KillExistingInstances();
        bool narratorWasRunning = Process.GetProcessesByName("Narrator").Any();
        using var app = launcher.Launch();
        using var automation = new UIA3Automation();

        try
        {
            var window = SampleAppLauncher.WaitForMainWindow(app, automation);
            SampleAppLauncher.WaitForSampleContent(window, app);

            SelectSample(window, "AccessibilityLab");
            Thread.Sleep(1500);

            var renderer = FindRenderer(window);
            renderer.Focus();
            Thread.Sleep(500);
            LogRendererTextGeometry(renderer);

            if (!narratorWasRunning)
                StartNarrator();
            MinimizeNarratorHome(automation);

            CaptureNarratorReading(window, renderer);

            try { window.Close(); }
            catch
            {
                // The finally block below kills the sample if the close request failed.
            }
            return 0;
        }
        finally
        {
            if (!narratorWasRunning)
                StopNarrator();

            try { if (!app.HasExited) app.Kill(); }
            catch
            {
                // The process exited between the check and Kill(); nothing is left to stop.
            }
        }
    }

    private static void LogRendererTextGeometry(AutomationElement renderer)
    {
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("Renderer does not expose TextPattern");
        var quick = textPattern.DocumentRange.FindText("quick", backward: false, ignoreCase: true)
                    ?? throw new InvalidOperationException("Could not find 'quick' via TextPattern");
        quick.ExpandToEnclosingUnit(TextUnit.Word);
        var quickRects = quick.GetBoundingRectangles();
        Console.WriteLine($"[narrator-smoke] renderer name: {renderer.Name}");
        Console.WriteLine($"[narrator-smoke] quick rects: {string.Join("; ", quickRects.Select(FormatRect))}");
        Console.WriteLine($"[narrator-smoke] renderer bounds: {FormatRect(renderer.BoundingRectangle)}");
    }

    private static void StartNarrator()
    {
        var narratorPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Narrator.exe");
        _ = Process.Start(new ProcessStartInfo(narratorPath) { UseShellExecute = true });
        Thread.Sleep(3500);
    }

    private static void StopNarrator()
    {
        foreach (var process in Process.GetProcessesByName("Narrator"))
        {
            try { process.Kill(); }
            catch
            {
                // Narrator already exited, or it is elevated and outlives this run; neither affects the result.
            }
        }
    }

    /// <summary>
    /// Asks Narrator to read the document (Insert+Down) and captures the
    /// renderer before, during and after reading, then once more after Tab
    /// enters the renderer from the sample list.
    /// </summary>
    private static void CaptureNarratorReading(Window window, AutomationElement renderer)
    {
        string artifactDir = Path.Combine(Path.GetFullPath("."), "MarkdownRenderer", "artifacts");
        Directory.CreateDirectory(artifactDir);

        renderer.Focus();
        Thread.Sleep(700);
        CaptureRenderer(renderer, artifactDir, "narrator-before-read.png", "before");

        Keyboard.TypeSimultaneously(VirtualKeyShort.INSERT, VirtualKeyShort.DOWN);
        Thread.Sleep(1700);
        CaptureRenderer(renderer, artifactDir, "narrator-after-read.png", "after");

        Thread.Sleep(1200);
        CaptureRenderer(renderer, artifactDir, "narrator-later-read.png", "later");

        var tabFocus = TabIntoRendererFromSampleList(window, renderer);
        Console.WriteLine($"[narrator-smoke] tab-entry focus event: {DescribeFocus(tabFocus)}");
        Thread.Sleep(1200);
        CaptureRenderer(renderer, artifactDir, "narrator-tab-entry.png", "tab-entry");
    }

    private static void CaptureRenderer(AutomationElement renderer, string artifactDir, string fileName, string label)
    {
        string path = Path.Combine(artifactDir, fileName);
        using (var image = FlaUI.Core.Capturing.Capture.ScreensWithElement(renderer, new FlaUI.Core.Capturing.CaptureSettings()))
            image.ToFile(path);
        Console.WriteLine($"[narrator-smoke] {label} screenshot: {path}");
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
                // Narrator Home is still starting or was replaced mid-query; retry below.
            }

            Thread.Sleep(250);
        }
    }
}
