using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Scenarios;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;
using MarkdownRenderer.Tests.TestSupport;


namespace MarkdownRenderer.Tests.Engineering.PerformanceGates;

/// <summary>
/// Builds one release-shaped first-usable-viewport scenario for the external
/// gate fixture: three warm-up and the measured trials on the frozen
/// six-condition schedule, with the pooled, robust and stationarity statistics
/// computed the way the harness computes them.
/// </summary>
internal static class PerformanceGateFirstViewportFactory
{
    private const int WarmupTrialCount = 3;
    private const int SamplesPerTrial = 100;

    /// <remarks>
    /// This method stays whole because FirstViewportResult is init-only, so
    /// every pooled, robust and stationarity statistic must be set in one
    /// object initializer.
    /// </remarks>
    internal static FirstViewportResult CreateResult(
        DateTimeOffset reportStartedUtc,
        int scenario,
        int sourceBytes,
        string mode,
        double[] trialMeasurements)
    {
        List<FirstViewportTrialResult> warmupTrials = Enumerable.Range(0, WarmupTrialCount)
            .Select(ordinal => CreateTrial(
                reportStartedUtc,
                scenario,
                mode,
                ordinal,
                warmup: true,
                trialMeasurements[Math.Min(ordinal, trialMeasurements.Length - 1)]))
            .ToList();
        List<FirstViewportTrialResult> trials = trialMeasurements
            .Select((measurement, ordinal) => CreateTrial(
                reportStartedUtc,
                scenario,
                mode,
                ordinal,
                warmup: false,
                measurement))
            .ToList();
        List<double> pooledSamples = trials
            .SelectMany(static trial => trial.SamplesMilliseconds)
            .ToList();
        double regressionP95 = PerformanceStatistics.HodgesLehmann(
            trials.Select(static trial => trial.P95Milliseconds));
        return new FirstViewportResult
        {
            Corpus = PerformanceDocumentFactory.GetCorpusName(sourceBytes),
            Mode = mode,
            SourceUtf16Bytes = sourceBytes,
            ParseCacheBudgetBytes = mode ==
                PerformanceMeasurementContract.FirstViewportCacheDisabledMode
                    ? 0
                    : sourceBytes * 8L,
            EngineReuseMode = PerformanceMeasurementContract.FirstViewportEngineReuseMode,
            TrialStartPolicy = PerformanceMeasurementContract.FirstViewportTrialStartPolicy,
            SchedulePolicy = PerformanceMeasurementContract.FirstViewportSchedulePolicy,
            RegressionEstimator = PerformanceMeasurementContract.FirstViewportRegressionEstimator,
            StationarityPolicy = PerformanceMeasurementContract.FirstViewportStationarityPolicy,
            MeasurementThreadAffinityMask = "0x0000000000000001",
            MeasurementProcessorGroup = 0,
            MeasurementProcessorNumber = 0,
            MeasurementThreadPriority = "Normal",
            WarmupTrials = warmupTrials,
            Trials = trials,
            SamplesMilliseconds = pooledSamples,
            P95Milliseconds = PerformanceStatistics.Percentile(pooledSamples, 0.95),
            PublicationSamplesMilliseconds = Enumerable.Repeat(1d, pooledSamples.Count).ToList(),
            PublicationMaximumMilliseconds = 1,
            PublicationBudgetMilliseconds =
                PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds,
            RegressionP95Milliseconds = regressionP95,
            RegressionDispersionPercent = PerformanceStatistics.RobustDispersionPercent(
                trials.Select(static trial => trial.P95Milliseconds)),
            TheilSenSlopeMillisecondsPerGlobalOrdinal = PerformanceStatistics.TheilSenSlope(
                trials.Select(static trial => (trial.GlobalOrdinal, trial.P95Milliseconds))),
            ProjectedDriftMilliseconds = PerformanceStatistics.ProjectedTheilSenDrift(
                trials.Select(static trial => (trial.GlobalOrdinal, trial.P95Milliseconds))),
            WarmupBoundaryShiftMilliseconds =
                PerformanceFirstViewportExpectations.CalculateWarmupBoundaryShift(warmupTrials, trials),
            StationarityAllowanceMilliseconds =
                PerformanceFirstViewportExpectations.CalculateStationarityAllowance(
                    mode,
                    sourceBytes,
                    regressionP95),
            StationarityEvaluated = true,
            StationarityPassed = true,
            BudgetMilliseconds =
                PerformanceFirstViewportExpectations.GetBudgetMilliseconds(sourceBytes, mode),
            Passed = true,
        };
    }

    private static FirstViewportTrialResult CreateTrial(
        DateTimeOffset reportStartedUtc,
        int scenario,
        string mode,
        int ordinal,
        bool warmup,
        double measurement)
    {
        int schedulePosition = PerformanceFirstViewportExpectations.GetSchedulePosition(
            scenario,
            row: ordinal);
        int conditionCount = PerformanceMeasurementContract.FirstViewportConditionCount;
        int globalOrdinal = (warmup ? 0 : WarmupTrialCount * conditionCount) +
            ordinal * conditionCount + schedulePosition;
        bool cacheHit = mode == PerformanceMeasurementContract.FirstViewportCacheHitMode;
        DateTimeOffset trialStartedUtc = reportStartedUtc.AddMilliseconds(globalOrdinal * 2 + 1);
        return new FirstViewportTrialResult
        {
            Ordinal = ordinal,
            GlobalOrdinal = globalOrdinal,
            ScheduleRow = ordinal,
            SchedulePosition = schedulePosition,
            StartedUtc = trialStartedUtc,
            CompletedUtc = trialStartedUtc.AddMilliseconds(1),
            SettlingElapsedMilliseconds = measurement,
            SettlingPublicationMilliseconds = 1,
            SamplesMilliseconds = Enumerable.Repeat(measurement, SamplesPerTrial).ToList(),
            PublicationSamplesMilliseconds = Enumerable.Repeat(1d, SamplesPerTrial).ToList(),
            PublicationMaximumMilliseconds = 1,
            P95Milliseconds = measurement,
            Gen0Collections = 0,
            Gen1Collections = 0,
            Gen2Collections = 0,
            ProcessAllocatedBytes = 1_000,
            CompletedParseCountBeforePrime = 0,
            CompletedParseCountAfterPrime = cacheHit ? 1 : 0,
            CompletedParseCountAfterTrial = cacheHit ? 1 : 0,
            SourceKeyHashCountBeforePrime = 0,
            SourceKeyHashCountAfterPrime = cacheHit ? 1 : 0,
            SourceKeyHashCountAfterTrial = cacheHit ? 1 : 101,
            CacheProofPassed = true,
            Complete = true,
        };
    }
}
