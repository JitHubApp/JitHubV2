using System.Runtime.InteropServices;
using System.Diagnostics;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Measurement;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.PerformanceHarness;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.Start((ApplicationInitializationCallbackParams p) =>
        {
            try
            {
                Debug.WriteLine("[PerfHarness] application callback entered");
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                Debug.WriteLine("[PerfHarness] synchronization context installed");
                _ = new App(args);
                Debug.WriteLine("[PerfHarness] application constructed");
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[PerfHarness] application construction failure: {exception}");
                TryWriteStartupFailureReport(args, exception);
                Environment.ExitCode = 3;
                Application.Current?.Exit();
            }
        });
    }

    internal static bool TryWriteStartupFailureReport(string[] arguments, Exception exception)
    {
        try
        {
            PerformanceOptions options = PerformanceOptions.Parse(arguments);
            var clock = new MonotonicUtcClock();
            DateTimeOffset startedUtc = clock.GetUtcNow();
            var report = new PerformanceReport
            {
                StartedUtc = startedUtc,
                CompletedUtc = clock.GetUtcNow(),
                IsReleaseEvidence = !options.Quick,
                BuildIdentity = "startup-failure",
                SampleRequirements = SampleRequirements.For(options),
                Passed = false,
            };
            report.Failures.Add($"Harness startup failure: {exception}");
            WriteReport(options.OutputPath, report);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void WriteReport(string path, PerformanceReport report)
        => PerformanceReportWriter.WriteNew(path, report);
}
