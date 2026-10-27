namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Recomputes every gate-bearing warm-scroll statistic from the serialized,
/// per-frame evidence. Aggregate populations are the ordered concatenation of
/// the complete trial populations; no aggregate raw samples are serialized.
/// </summary>
internal static class ScrollEvidenceValidator
{
    private const long LargeObjectHeapThresholdBytes = 85_000;

    internal static bool TryValidate(
        ScrollResult scroll,
        int framesPerTrial,
        int requiredTrials,
        out ScrollEvidenceStatistics statistics)
    {
        statistics = null!;
        if (scroll.StopwatchFrequency <= 0 || framesPerTrial <= 0 || requiredTrials <= 0)
            return false;

        int expectedTotalFrames;
        try
        {
            expectedTotalFrames = checked(framesPerTrial * requiredTrials);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (scroll.Trials.Count != requiredTrials ||
            scroll.RequestedFrames != expectedTotalFrames ||
            scroll.MeasuredFrameIntervals != expectedTotalFrames ||
            scroll.FramesWithRendererWork != expectedTotalFrames)
        {
            return false;
        }

        var trialStatistics = new List<ScrollTrialEvidenceStatistics>(requiredTrials);
        var allUiThreadWorkTicks = new List<long>(expectedTotalFrames);
        var allFrameIntervalTicks = new List<long>(expectedTotalFrames);
        var allRendererOwnedAllocatedBytes = new List<long>(expectedTotalFrames);
        long previousLastFrameId = 0;

        for (int ordinal = 0; ordinal < requiredTrials; ordinal++)
        {
            ScrollTrialResult trial = scroll.Trials[ordinal];
            if (trial.Ordinal != ordinal ||
                trial.FirstLogicalFrameId <= previousLastFrameId ||
                trial.FirstLogicalFrameId > long.MaxValue - framesPerTrial + 1L ||
                trial.RequestedFrames != framesPerTrial ||
                trial.MeasuredFrameIntervals != framesPerTrial ||
                trial.FramesWithRendererWork != framesPerTrial ||
                trial.UiThreadWorkElapsedTicks.Count != framesPerTrial ||
                trial.FrameIntervalElapsedTicks.Count != framesPerTrial ||
                trial.RendererOwnedAllocatedBytesPerFrame.Count != framesPerTrial ||
                trial.UiThreadWorkElapsedTicks.Any(static value => value < 0) ||
                trial.FrameIntervalElapsedTicks.Any(static value => value <= 0) ||
                trial.RendererOwnedAllocatedBytesPerFrame.Any(static value => value < 0) ||
                !trial.Complete)
            {
                return false;
            }

            previousLastFrameId = trial.FirstLogicalFrameId + framesPerTrial - 1L;
            ScrollTrialEvidenceStatistics computed = ComputeTrial(
                trial.UiThreadWorkElapsedTicks,
                trial.FrameIntervalElapsedTicks,
                trial.RendererOwnedAllocatedBytesPerFrame,
                scroll.StopwatchFrequency);
            if (!MatchesReportedTrial(trial, computed))
                return false;

            trialStatistics.Add(computed);
            allUiThreadWorkTicks.AddRange(trial.UiThreadWorkElapsedTicks);
            allFrameIntervalTicks.AddRange(trial.FrameIntervalElapsedTicks);
            allRendererOwnedAllocatedBytes.AddRange(trial.RendererOwnedAllocatedBytesPerFrame);
        }

        ScrollTrialEvidenceStatistics pooled = ComputeTrial(
            allUiThreadWorkTicks,
            allFrameIntervalTicks,
            allRendererOwnedAllocatedBytes,
            scroll.StopwatchFrequency);
        double regressionUiThreadWorkP95Milliseconds = PerformanceStatistics.HodgesLehmann(
            trialStatistics.Select(static trial => trial.UiThreadWorkP95Milliseconds));
        double regressionUiThreadWorkP99Milliseconds = PerformanceStatistics.HodgesLehmann(
            trialStatistics.Select(static trial => trial.UiThreadWorkP99Milliseconds));
        double regressionFrameTimeP95Milliseconds = PerformanceStatistics.HodgesLehmann(
            trialStatistics.Select(static trial => trial.FrameTimeP95Milliseconds));
        double regressionRendererOwnedAllocatedBytesPerFrameP95 = PerformanceStatistics.HodgesLehmann(
            trialStatistics.Select(
                static trial => trial.RendererOwnedAllocatedBytesPerFrameP95));

        statistics = new ScrollEvidenceStatistics(
            trialStatistics,
            pooled.UiThreadWorkP95Milliseconds,
            pooled.UiThreadWorkP99Milliseconds,
            pooled.FrameTimeP95Milliseconds,
            pooled.FramesOver16_7Percent,
            pooled.MaximumFrameStallMilliseconds,
            pooled.RendererOwnedAllocatedBytesPerFrameP95,
            pooled.MaximumRendererOwnedAllocatedBytesPerFrame,
            pooled.MaximumRendererOwnedAllocatedBytesPerFrame >= LargeObjectHeapThresholdBytes,
            regressionUiThreadWorkP95Milliseconds,
            regressionUiThreadWorkP99Milliseconds,
            regressionFrameTimeP95Milliseconds,
            regressionRendererOwnedAllocatedBytesPerFrameP95);

        return MatchesReportedAggregate(scroll, statistics);
    }

    private static ScrollTrialEvidenceStatistics ComputeTrial(
        IReadOnlyCollection<long> uiThreadWorkElapsedTicks,
        IReadOnlyCollection<long> frameIntervalElapsedTicks,
        IReadOnlyCollection<long> rendererOwnedAllocatedBytesPerFrame,
        long stopwatchFrequency)
    {
        double millisecondsPerTick = 1000.0 / stopwatchFrequency;
        double frameTimeP95Milliseconds =
            PerformanceStatistics.Percentile(frameIntervalElapsedTicks, 0.95) *
            millisecondsPerTick;
        double maximumFrameStallMilliseconds = frameIntervalElapsedTicks.Max() *
            millisecondsPerTick;
        double framesOver16_7Percent = frameIntervalElapsedTicks.Count(
            ticks => ticks * millisecondsPerTick > 16.7) * 100.0 /
            frameIntervalElapsedTicks.Count;

        return new ScrollTrialEvidenceStatistics(
            PerformanceStatistics.Percentile(uiThreadWorkElapsedTicks, 0.95) *
                millisecondsPerTick,
            PerformanceStatistics.Percentile(uiThreadWorkElapsedTicks, 0.99) *
                millisecondsPerTick,
            frameTimeP95Milliseconds,
            framesOver16_7Percent,
            maximumFrameStallMilliseconds,
            PerformanceStatistics.Percentile(rendererOwnedAllocatedBytesPerFrame, 0.95),
            rendererOwnedAllocatedBytesPerFrame.Max());
    }

    private static bool MatchesReportedTrial(
        ScrollTrialResult reported,
        ScrollTrialEvidenceStatistics computed)
        => NearlyEqual(
               reported.UiThreadWorkP95Milliseconds,
               computed.UiThreadWorkP95Milliseconds) &&
           NearlyEqual(
               reported.UiThreadWorkP99Milliseconds,
               computed.UiThreadWorkP99Milliseconds) &&
           NearlyEqual(reported.FrameTimeP95Milliseconds, computed.FrameTimeP95Milliseconds) &&
           NearlyEqual(reported.FramesOver16_7Percent, computed.FramesOver16_7Percent) &&
           NearlyEqual(
               reported.MaximumFrameStallMilliseconds,
               computed.MaximumFrameStallMilliseconds) &&
           NearlyEqual(
               reported.RendererOwnedAllocatedBytesPerFrameP95,
               computed.RendererOwnedAllocatedBytesPerFrameP95) &&
           reported.MaximumRendererOwnedAllocatedBytesPerFrame ==
               computed.MaximumRendererOwnedAllocatedBytesPerFrame;

    private static bool MatchesReportedAggregate(
        ScrollResult reported,
        ScrollEvidenceStatistics computed)
        => NearlyEqual(
               reported.UiThreadWorkP95Milliseconds,
               computed.UiThreadWorkP95Milliseconds) &&
           NearlyEqual(
               reported.UiThreadWorkP99Milliseconds,
               computed.UiThreadWorkP99Milliseconds) &&
           NearlyEqual(reported.FrameTimeP95Milliseconds, computed.FrameTimeP95Milliseconds) &&
           NearlyEqual(reported.FramesOver16_7Percent, computed.FramesOver16_7Percent) &&
           NearlyEqual(
               reported.MaximumFrameStallMilliseconds,
               computed.MaximumFrameStallMilliseconds) &&
           NearlyEqual(
               reported.RendererOwnedAllocatedBytesPerFrameP95,
               computed.RendererOwnedAllocatedBytesPerFrameP95) &&
           reported.MaximumRendererOwnedAllocatedBytesPerFrame ==
               computed.MaximumRendererOwnedAllocatedBytesPerFrame &&
           reported.RendererOwnedLohAllocationPossible ==
               computed.RendererOwnedLohAllocationPossible &&
           NearlyEqual(
               reported.RegressionUiThreadWorkP95Milliseconds,
               computed.RegressionUiThreadWorkP95Milliseconds) &&
           NearlyEqual(
               reported.RegressionUiThreadWorkP99Milliseconds,
               computed.RegressionUiThreadWorkP99Milliseconds) &&
           NearlyEqual(
               reported.RegressionFrameTimeP95Milliseconds,
               computed.RegressionFrameTimeP95Milliseconds) &&
           NearlyEqual(
               reported.RegressionRendererOwnedAllocatedBytesPerFrameP95,
               computed.RegressionRendererOwnedAllocatedBytesPerFrameP95);

    private static bool NearlyEqual(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right))
            return false;
        double scale = Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= scale * 1e-12;
    }
}

internal sealed record ScrollEvidenceStatistics(
    IReadOnlyList<ScrollTrialEvidenceStatistics> Trials,
    double UiThreadWorkP95Milliseconds,
    double UiThreadWorkP99Milliseconds,
    double FrameTimeP95Milliseconds,
    double FramesOver16_7Percent,
    double MaximumFrameStallMilliseconds,
    double RendererOwnedAllocatedBytesPerFrameP95,
    long MaximumRendererOwnedAllocatedBytesPerFrame,
    bool RendererOwnedLohAllocationPossible,
    double RegressionUiThreadWorkP95Milliseconds,
    double RegressionUiThreadWorkP99Milliseconds,
    double RegressionFrameTimeP95Milliseconds,
    double RegressionRendererOwnedAllocatedBytesPerFrameP95);

internal readonly record struct ScrollTrialEvidenceStatistics(
    double UiThreadWorkP95Milliseconds,
    double UiThreadWorkP99Milliseconds,
    double FrameTimeP95Milliseconds,
    double FramesOver16_7Percent,
    double MaximumFrameStallMilliseconds,
    double RendererOwnedAllocatedBytesPerFrameP95,
    long MaximumRendererOwnedAllocatedBytesPerFrame);
