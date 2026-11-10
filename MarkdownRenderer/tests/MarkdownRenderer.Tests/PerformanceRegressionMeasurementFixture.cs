using System.Diagnostics;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Scenarios;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Builds release-shaped warm-scroll, source-lookup and cancellation results
/// for regression evaluator reports, with named mutations that corrupt one
/// raw population, trial or aggregate value.
/// </summary>
internal static class PerformanceRegressionMeasurementFixture
{
    private const long LookupRegressionTicks = 4_096;

    /// <remarks>
    /// This method stays whole because ScrollResult is init-only, so each
    /// aggregate and the mutation that corrupts it must be set in one object
    /// initializer; the per-trial values are built by CreateScrollTrial.
    /// </remarks>
    internal static ScrollResult CreateScrollResult(
        string? mutation,
        long rendererOwnedAllocationBytes,
        double observedRefreshRateHz)
    {
        long stopwatchFrequency = Stopwatch.Frequency;
        long frameIntervalTicks = checked((long)Math.Round(
            stopwatchFrequency / observedRefreshRateHz));
        double frameIntervalMilliseconds = frameIntervalTicks * 1_000.0 / stopwatchFrequency;
        long uiThreadWorkTicks = checked((long)Math.Round(stopwatchFrequency / 1_000.0));
        double uiThreadWorkMilliseconds = uiThreadWorkTicks * 1_000.0 / stopwatchFrequency;
        int trialCount = PerformanceMeasurementContract.ReleaseScrollTrials;
        List<ScrollTrialResult> trials = Enumerable.Range(0, trialCount)
            .Select(ordinal => CreateScrollTrial(
                mutation,
                ordinal,
                uiThreadWorkTicks,
                frameIntervalTicks,
                rendererOwnedAllocationBytes))
            .ToList();
        if (mutation == "scroll-trial-count")
            trials.RemoveAt(trials.Count - 1);

        int totalFrames = checked(PerformanceMeasurementContract.ReleaseScrollFrames * trialCount);
        return new ScrollResult
        {
            Corpus = PerformanceDocumentFactory.WarmScrollStressCorpus,
            SourceUtf16Bytes = 1024 * 1024,
            StopwatchFrequency = stopwatchFrequency,
            RequestedFrames = totalFrames,
            MeasuredFrameIntervals = totalFrames,
            FramesWithRendererWork = totalFrames,
            RegressionEstimator = mutation == "scroll-estimator"
                ? "median-of-five-trial-p95"
                : PerformanceMeasurementContract.ScrollRegressionEstimator,
            Trials = trials,
            RegressionUiThreadWorkP95Milliseconds = mutation == "scroll-regression"
                ? uiThreadWorkMilliseconds + 1
                : uiThreadWorkMilliseconds,
            RegressionUiThreadWorkP99Milliseconds = uiThreadWorkMilliseconds,
            RegressionFrameTimeP95Milliseconds = frameIntervalMilliseconds,
            RegressionRendererOwnedAllocatedBytesPerFrameP95 = rendererOwnedAllocationBytes,
            UiThreadWorkP95Milliseconds = mutation == "scroll-aggregate-p95"
                ? uiThreadWorkMilliseconds + 1
                : uiThreadWorkMilliseconds,
            UiThreadWorkP99Milliseconds = uiThreadWorkMilliseconds,
            FrameTimeP95Milliseconds = frameIntervalMilliseconds,
            FramesOver16_7Percent = 0,
            MaximumFrameStallMilliseconds = frameIntervalMilliseconds,
            RendererOwnedAllocatedBytesPerFrameP95 = rendererOwnedAllocationBytes,
            MaximumRendererOwnedAllocatedBytesPerFrame = rendererOwnedAllocationBytes,
            RendererOwnedLohAllocationPossible = false,
            MaximumOffscreenRealizedCodeActions = 0,
            Gen2Collections = 0,
            Passed = true,
        };
    }

    internal static SourceLookupResult CreateSourceLookupResult(string? mutation)
    {
        long stopwatchFrequency = Stopwatch.Frequency;
        const long absoluteTicks = 100;
        double absoluteNanoseconds = absoluteTicks * 1_000_000_000.0 / stopwatchFrequency;
        var absoluteSamples = Enumerable.Repeat(
            absoluteTicks,
            PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount).ToList();
        if (mutation == "actual-lookup-absolute-count")
            absoluteSamples.RemoveAt(absoluteSamples.Count - 1);
        double regressionNanoseconds = GetLookupRegressionNanoseconds(stopwatchFrequency);
        List<SourceLookupTrialResult> trials = CreateSourceLookupTrials(mutation, regressionNanoseconds);

        return new SourceLookupResult
        {
            MappingCount = mutation == "actual-mapping" ? 99_999 :
                PerformanceMeasurementContract.SourceLookupMappingCount,
            QueryCount = mutation == "actual-query" ? 19_999 :
                PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount,
            StopwatchFrequency = stopwatchFrequency,
            MeasurementThreadAffinityMask = mutation == "valid-affinity-two"
                ? "0x0000000000000002"
                : "0x0000000000000001",
            MeasurementProcessorGroup = 0,
            MeasurementProcessorNumber = mutation == "valid-affinity-two" ? 1 : 0,
            MeasurementThreadPriority = "Highest",
            SamplesElapsedTicks = absoluteSamples,
            P95Nanoseconds = mutation == "actual-lookup-absolute-p95"
                ? absoluteNanoseconds + 1
                : absoluteNanoseconds,
            AllocatedBytes = mutation == "actual-lookup-allocated" ? 1 : 0,
            RegressionWarmupPasses = mutation == "actual-lookup-warmup" ? 7 :
                PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses,
            RegressionEstimator = mutation == "actual-lookup-estimator"
                ? "median-of-five-trial-p95"
                : PerformanceMeasurementContract.SourceLookupRegressionEstimator,
            RegressionTrialCount = PerformanceMeasurementContract.SourceLookupRegressionTrialCount,
            RegressionBatchSize = mutation == "actual-lookup-batch" ? 63 :
                PerformanceMeasurementContract.SourceLookupRegressionBatchSize,
            RegressionObservationCount = mutation == "actual-lookup-observations" ? 127 :
                PerformanceMeasurementContract.SourceLookupRegressionObservationCount,
            RegressionOperationCount = mutation == "actual-lookup-operations" ? 524_287 :
                PerformanceMeasurementContract.SourceLookupRegressionOperationCount,
            RegressionTrials = trials,
            RegressionP95Nanoseconds = mutation == "actual-lookup-hl"
                ? regressionNanoseconds + 1
                : regressionNanoseconds,
            RegressionAllocatedBytes = 0,
            Passed = true,
        };
    }

    internal static CancellationResult CreateCancellationResult(string? mutation)
    {
        int iterations = PerformanceMeasurementContract.ReleaseCancellationIterations;
        int trialCount = PerformanceMeasurementContract.ReleaseCancellationTrials;
        List<CancellationTrialResult> trials = Enumerable.Range(0, trialCount)
            .Select(ordinal => CreateCancellationTrial(mutation, ordinal, iterations))
            .ToList();
        if (mutation == "cancellation-trial-count")
            trials.RemoveAt(trials.Count - 1);
        List<double> flattenedSamples = trials
            .SelectMany(static trial => trial.SamplesMilliseconds)
            .ToList();
        if (mutation == "cancellation-flattened")
            flattenedSamples[0] = 2;

        return new CancellationResult
        {
            RequestedIterations = mutation == "cancellation-requested"
                ? iterations * trialCount - 1
                : iterations * trialCount,
            IterationsPerTrial = iterations,
            WarmupIterations = trialCount,
            WarmupElapsedMilliseconds = 1,
            WarmupStaleCommits = trials.Sum(static trial => trial.WarmupStaleCommits),
            ObservedCancellations = mutation == "cancellation-observed"
                ? iterations * trialCount - 1
                : iterations * trialCount,
            TrialStartPolicy = mutation == "cancellation-policy"
                ? "none"
                : PerformanceMeasurementContract.CancellationTrialStartPolicy,
            RegressionEstimator = mutation == "cancellation-estimator"
                ? "median-of-five-trial-p95"
                : PerformanceMeasurementContract.CancellationRegressionEstimator,
            Trials = trials,
            SamplesMilliseconds = flattenedSamples,
            P95Milliseconds = 1,
            RegressionP95Milliseconds = mutation == "cancellation-regression" ? 2 : 1,
            StaleCommits = trials.Sum(static trial => trial.StaleCommits),
            Passed = true,
        };
    }

    private static ScrollTrialResult CreateScrollTrial(
        string? mutation,
        int ordinal,
        long uiThreadWorkTicks,
        long frameIntervalTicks,
        long rendererOwnedAllocationBytes)
    {
        long stopwatchFrequency = Stopwatch.Frequency;
        double frameIntervalMilliseconds = frameIntervalTicks * 1_000.0 / stopwatchFrequency;
        double uiThreadWorkMilliseconds = uiThreadWorkTicks * 1_000.0 / stopwatchFrequency;
        int framesPerTrial = PerformanceMeasurementContract.ReleaseScrollFrames;
        bool first = ordinal == 0;
        return new ScrollTrialResult
        {
            Ordinal = mutation == "scroll-trial-order" && first ? 1 : ordinal,
            FirstLogicalFrameId = mutation == "scroll-first-frame" && first
                ? 0
                : checked((long)ordinal * framesPerTrial + 1),
            RequestedFrames = mutation == "scroll-requested-frames" && first
                ? framesPerTrial - 1
                : framesPerTrial,
            MeasuredFrameIntervals = framesPerTrial,
            FramesWithRendererWork = framesPerTrial,
            UiThreadWorkElapsedTicks = CreateRawPopulation(
                uiThreadWorkTicks,
                framesPerTrial,
                removeLast: first && mutation == "scroll-ui-raw-count",
                firstValue: first && mutation == "scroll-ui-raw-negative" ? -1 : null),
            FrameIntervalElapsedTicks = CreateRawPopulation(
                frameIntervalTicks,
                framesPerTrial,
                removeLast: first && mutation == "scroll-frame-raw-count",
                firstValue: first && mutation == "scroll-frame-raw-zero" ? 0 : null),
            RendererOwnedAllocatedBytesPerFrame = CreateRawPopulation(
                rendererOwnedAllocationBytes,
                framesPerTrial,
                removeLast: first && mutation == "scroll-allocation-raw-count",
                firstValue: first && mutation == "scroll-allocation-raw-negative" ? -1 : null),
            UiThreadWorkP95Milliseconds = mutation == "scroll-trial-p95" && first
                ? uiThreadWorkMilliseconds + 1
                : uiThreadWorkMilliseconds,
            UiThreadWorkP99Milliseconds = uiThreadWorkMilliseconds,
            FrameTimeP95Milliseconds = frameIntervalMilliseconds,
            FramesOver16_7Percent = 0,
            MaximumFrameStallMilliseconds = frameIntervalMilliseconds,
            RendererOwnedAllocatedBytesPerFrameP95 = rendererOwnedAllocationBytes,
            MaximumRendererOwnedAllocatedBytesPerFrame = rendererOwnedAllocationBytes,
            MaximumOffscreenRealizedCodeActions = 0,
            Gen2Collections = 0,
            Complete = true,
        };
    }

    private static List<long> CreateRawPopulation(
        long value,
        int count,
        bool removeLast,
        long? firstValue)
    {
        List<long> population = Enumerable.Repeat(value, count).ToList();
        if (removeLast)
            population.RemoveAt(population.Count - 1);
        if (firstValue is long replacement)
            population[0] = replacement;
        return population;
    }

    private static double GetLookupRegressionNanoseconds(long stopwatchFrequency)
        => LookupRegressionTicks * 1_000_000_000.0 /
            stopwatchFrequency /
            PerformanceMeasurementContract.SourceLookupRegressionBatchSize;

    private static List<SourceLookupTrialResult> CreateSourceLookupTrials(
        string? mutation,
        double regressionNanoseconds)
    {
        int observations = PerformanceMeasurementContract.SourceLookupRegressionObservationCount;
        List<SourceLookupTrialResult> trials = Enumerable.Range(
                0,
                PerformanceMeasurementContract.SourceLookupRegressionTrialCount)
            .Select(ordinal => new SourceLookupTrialResult
            {
                Ordinal = mutation == "actual-lookup-order" && ordinal == 0 ? 1 : ordinal,
                ObservationCount = observations,
                OperationCount = PerformanceMeasurementContract.SourceLookupRegressionOperationCount,
                SamplesElapsedTicks = Enumerable.Repeat(LookupRegressionTicks, observations).ToList(),
                SamplesNanosecondsPerLookup =
                    Enumerable.Repeat(regressionNanoseconds, observations).ToList(),
                P95Nanoseconds = mutation == "actual-lookup-trial-p95" && ordinal == 0
                    ? regressionNanoseconds + 1
                    : regressionNanoseconds,
                AllocatedBytes = 0,
                Complete = true,
            })
            .ToList();
        if (mutation == "actual-lookup-trials")
            trials.RemoveAt(trials.Count - 1);
        if (mutation == "actual-lookup-ticks")
            trials[0].SamplesNanosecondsPerLookup[0] = regressionNanoseconds + 1;
        return trials;
    }

    private static CancellationTrialResult CreateCancellationTrial(
        string? mutation,
        int ordinal,
        int iterations)
    {
        IReadOnlyList<CancellationMeasurementIteration> plan =
            CancellationMeasurementPlan.CreateTrial(ordinal, iterations);
        bool first = ordinal == 0;
        var samples = Enumerable.Repeat(1.0, iterations).ToList();
        if (mutation == "cancellation-sample-count" && first)
            samples.RemoveAt(samples.Count - 1);

        return new CancellationTrialResult
        {
            Ordinal = mutation == "cancellation-trial-order" && first ? 1 : ordinal,
            WarmupSourceSeed = mutation == "cancellation-warmup-seed" && first
                ? plan[0].SourceSeed + 1
                : plan[0].SourceSeed,
            WarmupElapsedMilliseconds = 1,
            WarmupStaleCommits = mutation == "cancellation-warmup-stale" && first ? 1 : 0,
            FirstRecordedSourceSeed = mutation == "cancellation-recorded-seed" && first
                ? plan[1].SourceSeed + 1
                : plan[1].SourceSeed,
            RequestedIterations = iterations,
            ObservedCancellations = iterations,
            SamplesMilliseconds = samples,
            P95Milliseconds = mutation == "cancellation-trial-p95" && first ? 2 : 1,
            StaleCommits = mutation == "cancellation-stale" && first ? 1 : 0,
            Complete = true,
        };
    }
}
