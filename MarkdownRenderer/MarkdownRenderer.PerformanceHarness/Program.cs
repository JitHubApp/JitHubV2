using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.PerformanceHarness;

public static class Program
{
    internal static string[] Arguments { get; private set; } = [];

    [STAThread]
    private static void Main(string[] args)
    {
        Arguments = args;
        Application.Start((ApplicationInitializationCallbackParams p) =>
        {
            try
            {
                Debug.WriteLine("[PerfHarness] application callback entered");
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                Debug.WriteLine("[PerfHarness] synchronization context installed");
                _ = new App();
                Debug.WriteLine("[PerfHarness] application constructed");
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[PerfHarness] application construction failure: {exception}");
                TryWriteStartupFailureReport(exception);
                Environment.ExitCode = 3;
                Application.Current?.Exit();
            }
        });
    }

    internal static bool TryWriteStartupFailureReport(Exception exception)
    {
        try
        {
            PerformanceOptions options = PerformanceOptions.Parse(Arguments);
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
