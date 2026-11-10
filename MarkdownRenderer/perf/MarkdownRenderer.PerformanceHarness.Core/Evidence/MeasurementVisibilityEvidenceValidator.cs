using MarkdownRenderer.PerformanceHarness.Core.Reports;

namespace MarkdownRenderer.PerformanceHarness.Core.Evidence;

public static class MeasurementVisibilityEvidenceValidator
{
    public static bool IsValid(
        MeasurementVisibilityEvidence? evidence,
        DateTimeOffset reportStartedUtc,
        DateTimeOffset reportCompletedUtc,
        out string failure)
    {
        // The checks run in this order so the first failing rule names the
        // failure, as the release gate reports it.
        failure = FindRecordingFailure(evidence, reportStartedUtc, reportCompletedUtc) ??
            FindSampleCoverageFailure(evidence!) ??
            FindDurationDisagreement(evidence!) ??
            string.Empty;
        return failure.Length == 0;
    }

    private static string? FindRecordingFailure(
        MeasurementVisibilityEvidence? evidence,
        DateTimeOffset reportStartedUtc,
        DateTimeOffset reportCompletedUtc)
    {
        if (evidence is null || evidence.Samples is null)
            return "Measurement visibility evidence was missing.";

        if (evidence.Policy != MeasurementVisibilityEvidence.CurrentPolicy ||
            evidence.PollIntervalMs != MeasurementVisibilityEvidence.PollIntervalMilliseconds ||
            evidence.MaximumSampleGapMs != MeasurementVisibilityEvidence.MaximumSampleGapMilliseconds)
        {
            return "Measurement visibility evidence used an unknown or altered policy.";
        }

        if (reportStartedUtc == default || reportCompletedUtc <= reportStartedUtc ||
            reportStartedUtc.Offset != TimeSpan.Zero || reportCompletedUtc.Offset != TimeSpan.Zero ||
            evidence.StartedUtc <= reportStartedUtc ||
            evidence.CompletedUtc < evidence.StartedUtc ||
            evidence.CompletedUtc > reportCompletedUtc ||
            evidence.StartedUtc.Offset != TimeSpan.Zero ||
            evidence.CompletedUtc.Offset != TimeSpan.Zero)
        {
            return "Measurement visibility timestamps did not fit inside the report interval.";
        }

        if (evidence.StopwatchFrequency <= 0 || evidence.StopwatchFrequency > 1_000_000_000 ||
            evidence.DurationTicks <= 0 ||
            evidence.DurationTicks > evidence.StopwatchFrequency * 86_400L ||
            !evidence.ForegroundHookInstalled ||
            !evidence.DesktopSwitchHookInstalled ||
            !evidence.WindowEventHookInstalled ||
            evidence.ForegroundLossEvents != 0 ||
            evidence.DesktopSwitchEvents != 0 ||
            evidence.CaptureFailures != 0)
        {
            return "Visibility monitoring was unavailable or recorded foreground, desktop, or capture failures.";
        }

        return null;
    }

    private static string? FindSampleCoverageFailure(MeasurementVisibilityEvidence evidence)
    {
        IReadOnlyList<MeasurementVisibilitySample> samples = evidence.Samples;
        if (samples.Count < 2 || samples.Count > 500_000)
            return "Visibility evidence did not cover the measurement interval.";

        long maximumGapTicks = evidence.StopwatchFrequency *
            evidence.MaximumSampleGapMs / 1_000;
        long firstMaximumTicks = Math.Max(1, evidence.StopwatchFrequency / 2);
        long lastSampleMaximumAgeTicks = maximumGapTicks;
        long previousTicks = -1;
        foreach (MeasurementVisibilitySample sample in samples)
        {
            if (sample is null || !IsSafeSampleSource(sample.Source) || sample.ElapsedTicks < 0 ||
                sample.ElapsedTicks <= previousTicks ||
                sample.ElapsedTicks > evidence.DurationTicks)
            {
                return "Visibility samples had an unknown source, were unordered, or fell outside their monotonic interval.";
            }

            if (!IsQualifiedSample(sample))
                return "The benchmark window lost foreground, visibility, or unobstructed coverage during measurement.";

            if (previousTicks >= 0 && sample.ElapsedTicks - previousTicks > maximumGapTicks)
                return "Visibility sampling left an uncovered gap in the measurement interval.";

            previousTicks = sample.ElapsedTicks;
        }

        if (samples[0].ElapsedTicks > firstMaximumTicks ||
            evidence.DurationTicks - samples[^1].ElapsedTicks > lastSampleMaximumAgeTicks)
        {
            return "Visibility samples did not bracket the complete measurement interval.";
        }

        return null;
    }

    private static string? FindDurationDisagreement(MeasurementVisibilityEvidence evidence)
    {
        TimeSpan measuredDuration = TimeSpan.FromSeconds(
            (double)evidence.DurationTicks / evidence.StopwatchFrequency);
        TimeSpan recordedDuration = evidence.CompletedUtc - evidence.StartedUtc;
        return Math.Abs((measuredDuration - recordedDuration).TotalMilliseconds) > 1_000
            ? "Monotonic and UTC visibility durations disagreed by more than one second."
            : null;
    }

    public static bool IsQualifiedSample(MeasurementVisibilitySample? sample)
        => sample is
        {
            CaptureSucceeded: true,
            TargetWasForeground: true,
            TargetWasVisible: true,
            TargetWasMinimized: false,
            TargetWasCloaked: false,
            TargetWasWithinWorkArea: true,
            TargetWasUnoccluded: true,
        };

    public static string DescribeUnqualifiedSample(MeasurementVisibilitySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!sample.CaptureSucceeded)
            return "the benchmark window could not be inspected";
        if (!sample.TargetWasForeground)
            return "the benchmark window lost foreground";
        if (!sample.TargetWasVisible)
            return "the benchmark window became hidden";
        if (sample.TargetWasMinimized)
            return "the benchmark window was minimized";
        if (sample.TargetWasCloaked)
            return "the benchmark window was cloaked";
        if (!sample.TargetWasWithinWorkArea)
            return "the benchmark window left the monitor work area";
        if (!sample.TargetWasUnoccluded)
            return "the benchmark window became occluded";
        return "the benchmark window no longer met the visibility policy";
    }

    private static bool IsSafeSampleSource(string source)
        => source is "start" or "complete" or "poll" or "desktop-switch" or
            "foreground-event" or "window-event";
}
