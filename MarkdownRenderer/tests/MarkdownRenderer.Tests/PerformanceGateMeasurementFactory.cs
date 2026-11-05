using System.Diagnostics;
using MarkdownRenderer.PerformanceHarness;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Builds the warm-scroll, source-lookup and cancellation sections of the
/// external gate fixture reports from the committed per-trial edge values.
/// </summary>
internal static class PerformanceGateMeasurementFactory
{
    private const int ScrollTrialCount = 5;
    private const int ScrollFramesPerTrial = 2_400;

    /// <remarks>
    /// This method stays whole because ScrollResult is init-only, so every pooled
    /// and regression statistic must be set in one object initializer; the
    /// per-trial values are built by CreateScrollTrial.
    /// </remarks>
    internal static ScrollResult CreateScroll(PerformanceGateValues values, bool candidate)
    {
        double[] uiP95 = candidate
            ? values.ScrollCandidateUiP95Milliseconds
            : values.ScrollReferenceUiP95Milliseconds;
        double[] uiP99 = candidate
            ? values.ScrollCandidateUiP99Milliseconds
            : values.ScrollReferenceUiP99Milliseconds;
        double[] frameP95 = candidate
            ? values.ScrollCandidateFrameP95Milliseconds
            : values.ScrollReferenceFrameP95Milliseconds;
        double[] allocationP95 = candidate
            ? values.ScrollCandidateAllocationP95Bytes
            : values.ScrollReferenceAllocationP95Bytes;
        long frequency = Stopwatch.Frequency;
        var trials = new List<ScrollTrialResult>(ScrollTrialCount);
        for (int index = 0; index < ScrollTrialCount; index++)
        {
            trials.Add(CreateScrollTrial(
                index,
                uiP95[index],
                uiP99[index],
                frameP95[index],
                allocationP95[index],
                frequency));
        }

        List<long> pooledUiTicks = trials
            .SelectMany(static trial => trial.UiThreadWorkElapsedTicks)
            .ToList();
        List<long> pooledFrameTicks = trials
            .SelectMany(static trial => trial.FrameIntervalElapsedTicks)
            .ToList();
        List<long> pooledAllocations = trials
            .SelectMany(static trial => trial.RendererOwnedAllocatedBytesPerFrame)
            .ToList();
        double millisecondsPerTick = 1_000.0 / frequency;
        double pooledAllocationP95 = PerformanceStatistics.Percentile(pooledAllocations, 0.95);
        long maximumAllocation = pooledAllocations.Max();
        return new ScrollResult
        {
            Corpus = PerformanceDocumentFactory.WarmScrollStressCorpus,
            SourceUtf16Bytes = 1024 * 1024,
            StopwatchFrequency = frequency,
            RequestedFrames = ScrollTrialCount * ScrollFramesPerTrial,
            MeasuredFrameIntervals = ScrollTrialCount * ScrollFramesPerTrial,
            FramesWithRendererWork = ScrollTrialCount * ScrollFramesPerTrial,
            RegressionEstimator = PerformanceMeasurementContract.ScrollRegressionEstimator,
            Trials = trials,
            RegressionUiThreadWorkP95Milliseconds = PerformanceStatistics.HodgesLehmann(uiP95),
            RegressionUiThreadWorkP99Milliseconds = PerformanceStatistics.HodgesLehmann(uiP99),
            RegressionFrameTimeP95Milliseconds = PerformanceStatistics.HodgesLehmann(frameP95),
            RegressionRendererOwnedAllocatedBytesPerFrameP95 =
                PerformanceStatistics.HodgesLehmann(allocationP95),
            UiThreadWorkP95Milliseconds = PerformanceStatistics.Percentile(pooledUiTicks, 0.95) *
                millisecondsPerTick,
            UiThreadWorkP99Milliseconds = PerformanceStatistics.Percentile(pooledUiTicks, 0.99) *
                millisecondsPerTick,
            FrameTimeP95Milliseconds = PerformanceStatistics.Percentile(pooledFrameTicks, 0.95) *
                millisecondsPerTick,
            FramesOver16_7Percent = pooledFrameTicks.Count(ticks =>
                ticks * millisecondsPerTick > 16.7) * 100.0 / pooledFrameTicks.Count,
            MaximumFrameStallMilliseconds = 20,
            AllocatedBytesPerFrameP95 = pooledAllocationP95,
            MaximumAllocatedBytesPerFrame = maximumAllocation,
            RendererOwnedAllocatedBytesPerFrameP95 = pooledAllocationP95,
            MaximumRendererOwnedAllocatedBytesPerFrame = maximumAllocation,
            Passed = true,
        };
    }

    internal static SourceLookupResult CreateLookup(PerformanceGateValues values, bool candidate)
    {
        const long absoluteTicks = 5;
        long[] regressionTicks = candidate
            ? values.SourceLookupCandidateTrialBatchTicks
            : values.SourceLookupReferenceTrialBatchTicks;
        double nanosecondsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
        var trials = new List<SourceLookupTrialResult>(
            PerformanceMeasurementContract.SourceLookupRegressionTrialCount);
        for (int ordinal = 0; ordinal < regressionTicks.Length; ordinal++)
            trials.Add(CreateLookupTrial(ordinal, regressionTicks[ordinal], nanosecondsPerTick));

        return new SourceLookupResult
        {
            MappingCount = 100_000,
            QueryCount = 20_000,
            StopwatchFrequency = Stopwatch.Frequency,
            MeasurementThreadAffinityMask = "0x0000000000000001",
            MeasurementProcessorGroup = 0,
            MeasurementProcessorNumber = 0,
            MeasurementThreadPriority = "Highest",
            SamplesElapsedTicks = Enumerable.Repeat(absoluteTicks, 20_000).ToList(),
            P95Nanoseconds = absoluteTicks * nanosecondsPerTick,
            AllocatedBytes = 0,
            RegressionWarmupPasses = PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses,
            RegressionBatchSize = PerformanceMeasurementContract.SourceLookupRegressionBatchSize,
            RegressionObservationCount = PerformanceMeasurementContract.SourceLookupRegressionObservationCount,
            RegressionOperationCount = PerformanceMeasurementContract.SourceLookupRegressionOperationCount,
            RegressionEstimator = PerformanceMeasurementContract.SourceLookupRegressionEstimator,
            RegressionTrialCount = PerformanceMeasurementContract.SourceLookupRegressionTrialCount,
            RegressionTrials = trials,
            RegressionP95Nanoseconds = PerformanceStatistics.HodgesLehmann(
                trials.Select(static trial => trial.P95Nanoseconds)),
            RegressionAllocatedBytes = trials.Sum(static trial => trial.AllocatedBytes),
            Passed = true,
        };
    }

    internal static CancellationResult CreateCancellation(PerformanceGateValues values, bool candidate)
    {
        double[] measurements = candidate
            ? values.CancellationCandidateTrialP95Milliseconds
            : values.CancellationReferenceTrialP95Milliseconds;
        double[] warmups = candidate
            ? values.CancellationCandidateWarmupMilliseconds
            : values.CancellationReferenceWarmupMilliseconds;
        var trials = new List<CancellationTrialResult>(
            PerformanceMeasurementContract.ReleaseCancellationTrials);
        for (int ordinal = 0; ordinal < PerformanceMeasurementContract.ReleaseCancellationTrials; ordinal++)
            trials.Add(CreateCancellationTrial(ordinal, measurements[ordinal], warmups[ordinal]));

        List<double> pooledSamples = trials
            .SelectMany(static trial => trial.SamplesMilliseconds)
            .ToList();
        return new CancellationResult
        {
            RequestedIterations = pooledSamples.Count,
            IterationsPerTrial = PerformanceMeasurementContract.ReleaseCancellationIterations,
            WarmupIterations = trials.Count,
            WarmupElapsedMilliseconds = PerformanceStatistics.Percentile(warmups, 0.95),
            WarmupStaleCommits = 0,
            ObservedCancellations = pooledSamples.Count,
            TrialStartPolicy = PerformanceMeasurementContract.CancellationTrialStartPolicy,
            RegressionEstimator = PerformanceMeasurementContract.CancellationRegressionEstimator,
            Trials = trials,
            SamplesMilliseconds = pooledSamples,
            P95Milliseconds = PerformanceStatistics.Percentile(pooledSamples, 0.95),
            RegressionP95Milliseconds = PerformanceStatistics.HodgesLehmann(measurements),
            P95BudgetMilliseconds = CancellationMeasurementPlan.AbsoluteBudgetMilliseconds,
            StaleCommits = 0,
            Passed = true,
        };
    }

    private static ScrollTrialResult CreateScrollTrial(
        int index,
        double uiP95,
        double uiP99,
        double frameP95,
        double allocationP95,
        long frequency)
    {
        long uiP95Ticks = MillisecondsToTicks(uiP95, frequency);
        long uiP99Ticks = MillisecondsToTicks(uiP99, frequency);
        long frameP95Ticks = MillisecondsToTicks(frameP95, frequency);
        long allocation = checked((long)allocationP95);
        List<long> uiTicks = Enumerable.Repeat(uiP95Ticks, 2_280)
            .Concat(Enumerable.Repeat(uiP99Ticks, 120))
            .ToList();
        List<long> frameTicks = Enumerable.Repeat(frameP95Ticks, 2_377)
            .Concat(Enumerable.Repeat(MillisecondsToTicks(20, frequency), 23))
            .ToList();
        List<long> allocations = Enumerable.Repeat(allocation, ScrollFramesPerTrial).ToList();
        return new ScrollTrialResult
        {
            Ordinal = index,
            FirstLogicalFrameId = 10_000L + index * 10_000_000L,
            RequestedFrames = ScrollFramesPerTrial,
            MeasuredFrameIntervals = ScrollFramesPerTrial,
            FramesWithRendererWork = ScrollFramesPerTrial,
            UiThreadWorkElapsedTicks = uiTicks,
            FrameIntervalElapsedTicks = frameTicks,
            RendererOwnedAllocatedBytesPerFrame = allocations,
            UiThreadWorkP95Milliseconds = uiP95,
            UiThreadWorkP99Milliseconds = uiP99,
            FrameTimeP95Milliseconds = frameP95,
            FramesOver16_7Percent = 23 * 100.0 / ScrollFramesPerTrial,
            MaximumFrameStallMilliseconds = 20,
            AllocatedBytesPerFrameP95 = allocation,
            MaximumAllocatedBytesPerFrame = allocation,
            RendererOwnedAllocatedBytesPerFrameP95 = allocationP95,
            MaximumRendererOwnedAllocatedBytesPerFrame = allocation,
            Complete = true,
        };
    }

    private static SourceLookupTrialResult CreateLookupTrial(
        int ordinal,
        long batchTicks,
        double nanosecondsPerTick)
    {
        long elapsedStopwatchTicks = checked(
            batchTicks *
            PerformanceMeasurementContract.SourceLookupRegressionObservationCount +
            100);
        long elapsed100NsTicks = (long)Math.Round(
            elapsedStopwatchTicks * 10_000_000d / Stopwatch.Frequency,
            MidpointRounding.AwayFromZero);
        DateTimeOffset startedUtc = DateTimeOffset.UnixEpoch.AddMinutes(ordinal);
        double nanoseconds = batchTicks * nanosecondsPerTick /
            PerformanceMeasurementContract.SourceLookupRegressionBatchSize;
        return new SourceLookupTrialResult
        {
            Ordinal = ordinal,
            ObservationCount = PerformanceMeasurementContract.SourceLookupRegressionObservationCount,
            OperationCount = PerformanceMeasurementContract.SourceLookupRegressionOperationCount,
            SamplesElapsedTicks = Enumerable.Repeat(
                batchTicks,
                PerformanceMeasurementContract.SourceLookupRegressionObservationCount).ToList(),
            SamplesNanosecondsPerLookup = Enumerable.Repeat(
                nanoseconds,
                PerformanceMeasurementContract.SourceLookupRegressionObservationCount).ToList(),
            P95Nanoseconds = nanoseconds,
            AllocatedBytes = 0,
            StartedUtc = startedUtc,
            CompletedUtc = startedUtc.AddTicks(Math.Max(1, elapsed100NsTicks)),
            ElapsedStopwatchTicks = elapsedStopwatchTicks,
            ThreadCpuTime100NsTicks = Math.Max(0, elapsed100NsTicks - 1),
            ThreadCycleDelta = 0,
            Gen0Collections = 0,
            Gen1Collections = 0,
            Gen2Collections = 0,
            Complete = true,
        };
    }

    private static CancellationTrialResult CreateCancellationTrial(
        int ordinal,
        double measurement,
        double warmup)
    {
        IReadOnlyList<CancellationMeasurementIteration> plan =
            CancellationMeasurementPlan.CreateTrial(
                ordinal,
                PerformanceMeasurementContract.ReleaseCancellationIterations);
        return new CancellationTrialResult
        {
            Ordinal = ordinal,
            WarmupSourceSeed = plan[0].SourceSeed,
            WarmupElapsedMilliseconds = warmup,
            WarmupStaleCommits = 0,
            FirstRecordedSourceSeed = plan[1].SourceSeed,
            RequestedIterations = PerformanceMeasurementContract.ReleaseCancellationIterations,
            ObservedCancellations = PerformanceMeasurementContract.ReleaseCancellationIterations,
            SamplesMilliseconds = Enumerable.Repeat(
                measurement,
                PerformanceMeasurementContract.ReleaseCancellationIterations).ToList(),
            P95Milliseconds = measurement,
            StaleCommits = 0,
            Complete = true,
        };
    }

    private static long MillisecondsToTicks(double milliseconds, long frequency)
        => checked((long)Math.Round(
            milliseconds * frequency / 1_000.0,
            MidpointRounding.AwayFromZero));
}
