using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace MarkdownRenderer.Sample.Automation;

/// <summary>
/// Starts the sample executable for one driver run and waits until its window
/// exposes the renderer and side navigation. It owns the path of the disposal
/// evidence file that the sample writes when its renderer is disposed.
/// </summary>
internal sealed class SampleAppLauncher
{
    internal const string DisposalEvidenceVariable = "MARKDOWN_RENDERER_DISPOSAL_EVIDENCE";

    internal SampleAppLauncher(string appPath, string disposalEvidencePath)
    {
        AppPath = appPath;
        DisposalEvidencePath = disposalEvidencePath;
    }

    internal string AppPath { get; }

    internal string DisposalEvidencePath { get; }

    /// <summary>
    /// Uses the disposal evidence path from the environment when the caller set
    /// one, and otherwise a fresh temporary file for this run.
    /// </summary>
    internal static SampleAppLauncher Create(string appPath) =>
        new(
            appPath,
            Environment.GetEnvironmentVariable(DisposalEvidenceVariable) ?? Path.Combine(
                Path.GetTempPath(), $"MarkdownRenderer-disposal-{Guid.NewGuid():N}.json"));

    internal Application Launch()
    {
        var startInfo = new ProcessStartInfo(AppPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(AppPath) ?? Environment.CurrentDirectory,
        };

        startInfo.Environment[DisposalEvidenceVariable] = DisposalEvidencePath;

        return Application.Launch(startInfo);
    }

    internal static Window WaitForMainWindow(Application app, UIA3Automation automation) =>
        Retry.WhileNull(() =>
            {
                try { return app.GetMainWindow(automation); }
                catch
                {
                    // The window is still being created; keep polling until the timeout.
                    return null;
                }
            },
            timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(250)).Result
            ?? throw new InvalidOperationException("Main window did not appear.");

    internal static void WaitForSampleContent(Window window, Application app)
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
                catch
                {
                    // The UIA tree is still being built; keep polling until the timeout.
                    return false;
                }
            },
            timeout: TimeSpan.FromSeconds(20),
            interval: TimeSpan.FromMilliseconds(250)).Result;

        if (app.HasExited)
            throw new InvalidOperationException("Sample app exited before UIA content became available.");
        if (!ready)
            throw new InvalidOperationException("Sample app did not expose its renderer and side navigation within 20 seconds.");
    }

    /// <summary>
    /// Finds the most recently built MarkdownRenderer.Sample.exe under the
    /// repository's sample bin folder, or returns <see langword="null"/>.
    /// </summary>
    internal static string? FindDefaultAppPath()
    {
        string root = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(root);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        if (dir is null) return null;
        string sampleDir = Path.Combine(dir.FullName, "MarkdownRenderer", "samples", "MarkdownRenderer.Sample", "bin");
        if (!Directory.Exists(sampleDir)) return null;
        var candidates = Directory.GetFiles(sampleDir, "MarkdownRenderer.Sample.exe", SearchOption.AllDirectories);
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    internal void KillExistingInstances()
    {
        string targetName = Path.GetFileNameWithoutExtension(AppPath);
        foreach (var p in Process.GetProcessesByName(targetName))
        {
            using (p)
            {
                try
                {
                    // Only kill processes whose exe path matches exactly to avoid
                    // killing unrelated apps that share the same process name.
                    if (string.Equals(p.MainModule?.FileName, AppPath,
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
                catch
                {
                    // Any other failure means this process is not ours to stop;
                    // the launch below still starts a fresh sample.
                }
            }
        }
    }
}
