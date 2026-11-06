using MarkdownRenderer.PerformanceHarness;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Builds release-shaped first-usable-viewport results for regression
/// evaluator reports: warm-up and measured trials on the frozen Williams
/// schedule, the pooled, robust and stationarity statistics computed the way
/// the harness computes them, and named viewport-* mutations that corrupt one
/// value of the target scenario.
/// </summary>
internal static class PerformanceRegressionFirstViewportFixture
{
    private static readonly DateTimeOffset ReportStartedUtc =
        DateTimeOffset.Parse("2026-09-10T12:00:00Z");

    /// <remarks>
    /// This method stays whole because FirstViewportResult is init-only, so
    /// every derived statistic and the mutation that corrupts it must be set
    /// in one object initializer.
    /// </remarks>
    internal static FirstViewportResult CreateResult(
        int sourceBytes,
        string mode,
        int condition,
        IReadOnlyList<double> trialValues,
        string? mutation)
    {
        if (trialValues.Count != PerformanceMeasurementContract.ReleaseFirstViewportTrials)
            throw new ArgumentException("Exactly six trial values are required.", nameof(trialValues));

        bool mutate = sourceBytes == PerformanceRegressionReportFixture.TargetViewportBytes &&
            (mode == PerformanceRegressionReportFixture.TargetViewportMode ||
             (mode == PerformanceMeasurementContract.FirstViewportCacheHitMode &&
              mutation == "viewport-cache-hit-counter"));
        bool Applies(string name) => mutate && mutation == name;

        List<FirstViewportTrialResult> warmupTrials =
            CreateWarmupTrials(mode, condition, trialValues, mutate, mutation);
        List<FirstViewportTrialResult> trials =
            CreateMeasuredTrials(mode, condition, trialValues, mutate, mutation);
        List<double> flattenedSamples = trials
            .SelectMany(static trial => trial.SamplesMilliseconds)
            .ToList();
        if (Applies("viewport-flattened"))
            flattenedSamples[0] += 1;
        List<double> publicationSamples = trials
            .SelectMany(static trial => trial.PublicationSamplesMilliseconds)
            .ToList();
        if (Applies("viewport-publication-flattened"))
            publicationSamples[0] += 0.25;

        double publicationMaximum = publicationSamples.Max();
        double pooledP95 = PerformanceStatistics.Percentile(flattenedSamples, 0.95);
        double regressionP95 = PerformanceStatistics.HodgesLehmann(
            trials.Select(static trial => trial.P95Milliseconds));
        double dispersion = PerformanceStatistics.RobustDispersionPercent(
            trials.Select(static trial => trial.P95Milliseconds));
        double slope = PerformanceStatistics.TheilSenSlope(
            trials.Select(static trial => (trial.GlobalOrdinal, trial.P95Milliseconds)));
        double projectedDrift = PerformanceStatistics.ProjectedTheilSenDrift(
            trials.Select(static trial => (trial.GlobalOrdinal, trial.P95Milliseconds)));
        double boundaryShift =
            PerformanceFirstViewportExpectations.CalculateWarmupBoundaryShift(warmupTrials, trials);
        double allowance = PerformanceFirstViewportExpectations.CalculateStationarityAllowance(
            mode,
            sourceBytes,
            regressionP95);
        bool stationarityPassed = IsStationary(dispersion, slope, projectedDrift, boundaryShift, allowance);
        double budget = PerformanceFirstViewportExpectations.GetBudgetMilliseconds(sourceBytes, mode);
        return new FirstViewportResult
        {
            Corpus = PerformanceDocumentFactory.GetCorpusName(sourceBytes),
            Mode = mode,
            SourceUtf16Bytes = sourceBytes,
            ParseCacheBudgetBytes = Applies("viewport-cache")
                ? 1
                : mode == PerformanceMeasurementContract.FirstViewportCacheHitMode
                    ? sourceBytes * 8L
                    : 0,
            EngineReuseMode = Applies("viewport-engine-reuse")
                ? "per-sample"
                : PerformanceMeasurementContract.FirstViewportEngineReuseMode,
            TrialStartPolicy = Applies("viewport-trial-policy")
                ? "none"
                : PerformanceMeasurementContract.FirstViewportTrialStartPolicy,
            SchedulePolicy = Applies("viewport-schedule-policy")
                ? "none"
                : PerformanceMeasurementContract.FirstViewportSchedulePolicy,
            RegressionEstimator = Applies("viewport-estimator")
                ? "median-of-five-trial-p95"
                : PerformanceMeasurementContract.FirstViewportRegressionEstimator,
            StationarityPolicy = Applies("viewport-stationarity-policy")
                ? "none"
                : PerformanceMeasurementContract.FirstViewportStationarityPolicy,
            MeasurementThreadAffinityMask = Applies("viewport-affinity")
                ? "0x0000000000000000"
                : "0x0000000000000001",
            MeasurementProcessorGroup = 0,
            MeasurementProcessorNumber = 0,
            MeasurementThreadPriority = Applies("viewport-priority") ? "Highest" : "Normal",
            WarmupTrials = warmupTrials,
            Trials = trials,
            SamplesMilliseconds = flattenedSamples,
            P95Milliseconds = pooledP95,
            PublicationSamplesMilliseconds = publicationSamples,
            PublicationMaximumMilliseconds = Applies("viewport-publication-aggregate-maximum")
                ? publicationMaximum + 0.25
                : publicationMaximum,
            PublicationBudgetMilliseconds = Applies("viewport-publication-budget")
                ? 3
                : PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds,
            RegressionP95Milliseconds = Applies("viewport-regression")
                ? regressionP95 + 1
                : regressionP95,
            RegressionDispersionPercent = Applies("viewport-dispersion-derived")
                ? dispersion + 1
                : dispersion,
            TheilSenSlopeMillisecondsPerGlobalOrdinal = Applies("viewport-slope")
                ? slope + 1
                : slope,
            ProjectedDriftMilliseconds = Applies("viewport-projected-drift")
                ? projectedDrift + 1
                : projectedDrift,
            WarmupBoundaryShiftMilliseconds = Applies("viewport-boundary-shift")
                ? boundaryShift + 1
                : boundaryShift,
            StationarityAllowanceMilliseconds = Applies("viewport-stationarity-allowance")
                ? allowance + 1
                : allowance,
            StationarityEvaluated = !Applies("viewport-stationarity-evaluated"),
            StationarityPassed = Applies("viewport-stationarity-passed")
                ? !stationarityPassed
                : stationarityPassed,
            BudgetMilliseconds = budget,
            Passed = pooledP95 <= budget && stationarityPassed &&
                publicationMaximum <=
                    PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds,
        };
    }

    private static List<FirstViewportTrialResult> CreateWarmupTrials(
        string mode,
        int condition,
        IReadOnlyList<double> trialValues,
        bool mutate,
        string? mutation)
    {
        double warmupValue = mutate && mutation == "viewport-boundary-series"
            ? trialValues[0] + 20
            : trialValues[0];
        List<FirstViewportTrialResult> warmupTrials = Enumerable.Range(
                0,
                PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials)
            .Select(row => CreateTrial(mode, condition, row, warmup: true, warmupValue, mutate, mutation))
            .ToList();
        if (mutate && mutation == "viewport-warmup-count")
            warmupTrials.RemoveAt(warmupTrials.Count - 1);
        return warmupTrials;
    }

    private static List<FirstViewportTrialResult> CreateMeasuredTrials(
        string mode,
        int condition,
        IReadOnlyList<double> trialValues,
        bool mutate,
        string? mutation)
    {
        List<FirstViewportTrialResult> trials = trialValues
            .Select((value, row) => CreateTrial(mode, condition, row, warmup: false, value, mutate, mutation))
            .ToList();
        if (mutate && mutation == "viewport-trial-count")
            trials.RemoveAt(trials.Count - 1);
        return trials;
    }

    private static bool IsStationary(
        double dispersion,
        double slope,
        double projectedDrift,
        double boundaryShift,
        double allowance)
        => double.IsFinite(dispersion) &&
            dispersion <= PerformanceMeasurementContract.FirstViewportDispersionBudgetPercent &&
            double.IsFinite(slope) &&
            double.IsFinite(projectedDrift) &&
            Math.Abs(projectedDrift) <= allowance &&
            double.IsFinite(boundaryShift) &&
            boundaryShift <= allowance;

    /// <remarks>
    /// This method stays whole because FirstViewportTrialResult is init-only,
    /// so each per-trial value and the mutation that corrupts it on the first
    /// trial must be set in one object initializer.
    /// </remarks>
    private static FirstViewportTrialResult CreateTrial(
        string mode,
        int condition,
        int row,
        bool warmup,
        double value,
        bool mutate,
        string? mutation)
    {
        int position = PerformanceFirstViewportExpectations.GetSchedulePosition(condition, row);
        int globalOrdinal = checked(
            (warmup ? row : PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials + row) *
            PerformanceMeasurementContract.FirstViewportConditionCount +
            position);
        DateTimeOffset started = ReportStartedUtc.AddMilliseconds(1 + (globalOrdinal * 2));
        bool firstMeasured = mutate && !warmup && row == 0;
        bool Applies(string name) => firstMeasured && mutation == name;
        if (mutate && warmup && mutation == "viewport-boundary-endpoint-spike")
            value = row switch { 1 => 0, 2 => 20, _ => value };

        bool cacheHit = mode == PerformanceMeasurementContract.FirstViewportCacheHitMode;
        int iterations = PerformanceMeasurementContract.ReleaseFirstViewportIterations;
        return new FirstViewportTrialResult
        {
            Ordinal = Applies("viewport-trial-order") ? 1 : row,
            GlobalOrdinal = Applies("viewport-global-order") ? 0 : globalOrdinal,
            ScheduleRow = Applies("viewport-schedule-row") ? row + 1 : row,
            SchedulePosition = Applies("viewport-schedule-position")
                ? (position + 1) % PerformanceMeasurementContract.FirstViewportConditionCount
                : position,
            StartedUtc = mutation switch
            {
                "viewport-chronology" when firstMeasured => started.ToOffset(TimeSpan.FromHours(-7)),
                "viewport-before-report" when mutate && warmup && row == 0 => started.AddMilliseconds(-2),
                _ => started,
            },
            CompletedUtc = started.AddMilliseconds(Applies("viewport-timestamp-order") ? -1 : 1),
            SettlingElapsedMilliseconds = Applies("viewport-settling") ? -1 : 1,
            SettlingPublicationMilliseconds = Applies("viewport-publication-settling") ? -1 : 1,
            SamplesMilliseconds = Enumerable.Repeat(value, iterations).ToList(),
            PublicationSamplesMilliseconds = Enumerable.Repeat(
                Applies("viewport-publication-overbudget") ? 2.1 : 1d,
                Applies("viewport-publication-count") ? iterations - 1 : iterations).ToList(),
            PublicationMaximumMilliseconds = Applies("viewport-publication-trial-maximum") ? 1.25 :
                Applies("viewport-publication-overbudget") ? 2.1 : 1,
            P95Milliseconds = Applies("viewport-trial-p95") ? value + 1 : value,
            Gen0Collections = Applies("viewport-gc") ? -1 : 0,
            Gen1Collections = Applies("viewport-gc-hierarchy") ? 1 : 0,
            Gen2Collections = 0,
            ProcessAllocatedBytes = Applies("viewport-allocation") ? -1 : 0,
            CompletedParseCountBeforePrime = 0,
            CompletedParseCountAfterPrime = cacheHit ? 1 : 0,
            CompletedParseCountAfterTrial = cacheHit ? 1 : 0,
            SourceKeyHashCountBeforePrime = 0,
            SourceKeyHashCountAfterPrime = cacheHit ? 1 : 0,
            SourceKeyHashCountAfterTrial =
                Applies("viewport-cache-counter") || Applies("viewport-cache-hit-counter")
                    ? 0
                    : cacheHit ? 1 : iterations + 1,
            CacheProofPassed = !Applies("viewport-cache-proof"),
            Complete = true,
        };
    }
}
