using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using MarkdownRenderer.Sample.Automation.Applications;
using MarkdownRenderer.Sample.Automation.Uia;
using static MarkdownRenderer.Sample.Automation.Uia.ProbeAssert;

namespace MarkdownRenderer.Sample.Automation.Probes;

/// <summary>
/// Probes that closing the sample window disposes the renderer and exits the
/// process cleanly.
/// </summary>
internal static class LifecycleProbes
{
    internal static void ProbeWindowCloseLifecycle(Window window, FlaUI.Core.Application app, string disposalEvidencePath)
    {
        // Retain the native handle before closing: GetProcessById after exit
        // cannot recover a reliable exit code and PID reuse is not evidence.
        using var process = Process.GetProcessById(app.ProcessId);
        _ = process.Handle;
        window.Close();
        bool exited = process.WaitForExit(10_000);
        bool disposalObserved = File.Exists(disposalEvidencePath) &&
            DisposalEvidence.ShowsCompletedDisposal(File.ReadAllText(disposalEvidencePath), process.Id);
        string? failure = ProcessExitValidation.GetFailure(exited, exited ? process.ExitCode : null, disposalObserved);
        Assert(failure is null, failure ?? string.Empty);
    }
}
