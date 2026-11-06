using MarkdownRenderer.PerformanceHarness;

namespace MarkdownRenderer.Tests;

internal static class PerformanceVisibilityFixture
{
    internal static MeasurementVisibilityEvidence CreatePassing(
        DateTimeOffset reportStartedUtc,
        IReadOnlyList<MeasurementVisibilitySample>? samples = null,
        int foregroundLossEvents = 0,
        int desktopSwitchEvents = 0,
        int captureFailures = 0,
        bool foregroundHookInstalled = true,
        bool desktopSwitchHookInstalled = true,
        bool windowEventHookInstalled = true,
        DateTimeOffset? completedUtc = null,
        TimeSpan? intervalOffset = null,
        TimeSpan? duration = null)
    {
        const long frequency = 10_000_000;
        TimeSpan actualDuration = duration ?? TimeSpan.FromSeconds(2);
        long durationTicks = checked((long)Math.Round(actualDuration.TotalSeconds * frequency));
        DateTimeOffset startedUtc = reportStartedUtc.Add(intervalOffset ?? TimeSpan.FromSeconds(1));
        DateTimeOffset endUtc = completedUtc ?? startedUtc.Add(actualDuration);
        int sampleCount = Math.Max(
            2,
            (int)Math.Ceiling(actualDuration.TotalSeconds / 0.5) + 1);
        List<MeasurementVisibilitySample> actualSamples = samples is null
            ? Enumerable.Range(0, sampleCount)
                .Select(index => new MeasurementVisibilitySample
                {
                    ElapsedTicks = index * durationTicks / (sampleCount - 1),
                    Source = index == 0 ? "start" : index == sampleCount - 1 ? "complete" : "poll",
                    CaptureSucceeded = true,
                    TargetWasForeground = true,
                    TargetWasVisible = true,
                    TargetWasMinimized = false,
                    TargetWasCloaked = false,
                    TargetWasWithinWorkArea = true,
                    TargetWasUnoccluded = true,
                })
                .ToList()
            : [.. samples];

        return new MeasurementVisibilityEvidence
        {
            StartedUtc = startedUtc,
            CompletedUtc = endUtc,
            StopwatchFrequency = frequency,
            DurationTicks = durationTicks,
            ForegroundHookInstalled = foregroundHookInstalled,
            DesktopSwitchHookInstalled = desktopSwitchHookInstalled,
            WindowEventHookInstalled = windowEventHookInstalled,
            ForegroundLossEvents = foregroundLossEvents,
            DesktopSwitchEvents = desktopSwitchEvents,
            CaptureFailures = captureFailures,
            Samples = actualSamples,
        };
    }
}
