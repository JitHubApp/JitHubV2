namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Frozen release-evidence sample contract. Keep these values centralized so
/// the harness defaults and serialized requirements cannot silently diverge.
/// </summary>
internal static class PerformanceMeasurementContract
{
    internal const int SchemaVersion = 10;
    internal const string ProviderName = "MarkdownRenderer-Performance";
    internal const string RuntimeConfigurationPolicy =
        "tiering-pgo-concurrent-gc-readytorun-disabled;dotnet-complus-overrides-unset-v1";

    internal const int ReleaseFirstViewportIterations = 100;
    internal const int ReleaseFirstViewportTrials = 6;
    internal const int ReleaseFirstViewportWarmupTrials = 3;
    internal const int FirstViewportConditionCount = 6;
    internal const int ReleaseScrollFrames = 2_400;
    internal const int ReleaseScrollTrials = 5;
    internal const double TargetRefreshRateHz = 120d;
    internal const double ScrollFrameTimeP95BudgetMilliseconds =
        1_000d / TargetRefreshRateHz;
    internal const int ReleaseLifecycleCycles = 100;
    internal const int ReleaseCancellationIterations = 40;
    internal const int ReleaseCancellationTrials = 5;

    internal const int SourceLookupMappingCount = 100_000;
    internal const int SourceLookupAbsoluteQueryCount = 20_000;
    internal const int SourceLookupRegressionWarmupPasses = 8;
    internal const int SourceLookupRegressionTrialCount = 5;
    internal const int SourceLookupRegressionBatchSize = 4_096;
    internal const int SourceLookupRegressionObservationCount = 128;
    internal const int SourceLookupRegressionOperationCount =
        SourceLookupRegressionBatchSize * SourceLookupRegressionObservationCount;

    internal const string FirstViewportCacheDisabledMode = "cache-disabled";
    internal const string FirstViewportCacheHitMode = "cache-hit";
    internal const string FirstViewportEngineReuseMode =
        "fresh-harness-owned-engine-per-trial";
    internal const string FirstViewportTrialStartPolicy =
        "fresh-engine;optional-cache-prime;managed-full-collection;one-settling-presentation;recorded-presentations";
    internal const string FirstViewportSchedulePolicy =
        "six-condition-six-period-williams-square-v1";
    internal const string FirstViewportStationarityPolicy =
        "mad-plus-theil-sen-projected-drift-plus-warmup-boundary-v1";
    internal const string FiveTrialHodgesLehmannRegressionEstimator =
        "hodges-lehmann-of-five-trial-p95";
    internal const string SixTrialHodgesLehmannRegressionEstimator =
        "hodges-lehmann-of-six-trial-p95";
    internal const string FirstViewportRegressionEstimator =
        SixTrialHodgesLehmannRegressionEstimator;
    internal const string SourceLookupRegressionEstimator =
        FiveTrialHodgesLehmannRegressionEstimator;
    internal const string ScrollRegressionEstimator =
        FiveTrialHodgesLehmannRegressionEstimator;
    internal const string CancellationRegressionEstimator =
        FiveTrialHodgesLehmannRegressionEstimator;
    internal const string CancellationTrialStartPolicy =
        "managed-full-collection-then-one-distinct-warmup-per-trial";
    internal const string ProcessPowerThrottlingMode =
        "execution-speed=off;ignore-timer-resolution=off";
    internal const uint ProcessPowerThrottlingExecutionSpeedMask = 0x1;
    internal const uint ProcessPowerThrottlingIgnoreTimerResolutionMask = 0x4;
    internal const uint ProcessPowerThrottlingControlMask =
        ProcessPowerThrottlingExecutionSpeedMask |
        ProcessPowerThrottlingIgnoreTimerResolutionMask;
    internal const string RegressionDecisionPolicy =
        "max-relative-percent-or-absolute-noise-floor-with-robust-dispersion";

    internal const double FirstViewportDispersionBudgetPercent = 25;
    internal const double FirstViewportStationarityRelativeAllowancePercent = 5;
    internal const double ScrollUiWorkDispersionBudgetPercent = 25;
    internal const double ScrollFrameTimeDispersionBudgetPercent = 35;
    internal const double SourceLookupDispersionBudgetPercent = 20;
    internal const double CancellationDispersionBudgetPercent = 25;
    internal const double AllocationDispersionBudgetPercent = 25;

    internal const double FirstViewport100KiBCacheDisabledNoiseFloorMilliseconds = 5;
    internal const double FirstViewport100KiBCacheHitNoiseFloorMilliseconds = 3;
    internal const double FirstViewport1MiBCacheDisabledNoiseFloorMilliseconds = 5;
    internal const double FirstViewport1MiBCacheHitNoiseFloorMilliseconds = 5;
    internal const double FirstViewport10MiBCacheDisabledNoiseFloorMilliseconds = 3;
    internal const double FirstViewport10MiBCacheHitNoiseFloorMilliseconds = 1;
    internal const double ScrollUiThreadWorkP95NoiseFloorMilliseconds = 0.05;
    internal const double ScrollUiThreadWorkP99NoiseFloorMilliseconds = 0.10;
    internal const double ScrollFrameTimeP95NoiseFloorMilliseconds = 2;
    internal const double SourceLookupNoiseFloorNanoseconds = 30;
    internal const double CancellationNoiseFloorMilliseconds = 0.05;
    internal const double RendererOwnedAllocationNoiseFloorBytes = 64;

    internal const int QuickFirstViewportIterations = 1;
    internal const int QuickFirstViewportTrials = 1;
    internal const int QuickFirstViewportWarmupTrials = 1;
    internal const int QuickScrollFrames = 30;
    internal const int QuickScrollTrials = 1;
    internal const int QuickLifecycleCycles = 8;
    internal const int QuickCancellationIterations = 1;
    internal const int QuickCancellationTrials = 1;

    internal static double GetFirstViewportNoiseFloorMilliseconds(
        string mode,
        int sourceUtf16Bytes)
        => (mode, sourceUtf16Bytes) switch
        {
            (FirstViewportCacheDisabledMode, 100 * 1024) => FirstViewport100KiBCacheDisabledNoiseFloorMilliseconds,
            (FirstViewportCacheHitMode, 100 * 1024) => FirstViewport100KiBCacheHitNoiseFloorMilliseconds,
            (FirstViewportCacheDisabledMode, 1024 * 1024) => FirstViewport1MiBCacheDisabledNoiseFloorMilliseconds,
            (FirstViewportCacheHitMode, 1024 * 1024) => FirstViewport1MiBCacheHitNoiseFloorMilliseconds,
            (FirstViewportCacheDisabledMode, 10 * 1024 * 1024) => FirstViewport10MiBCacheDisabledNoiseFloorMilliseconds,
            (FirstViewportCacheHitMode, 10 * 1024 * 1024) => FirstViewport10MiBCacheHitNoiseFloorMilliseconds,
            _ => double.NaN,
        };

    /// <summary>
    /// Returns the condition at a position in the frozen six-by-six Williams
    /// design. Every condition occupies every period once, and every ordered
    /// first-order carryover occurs once across the six rows.
    /// </summary>
    internal static int GetFirstViewportCondition(int row, int position)
    {
        ReadOnlySpan<int> firstRow = [0, 1, 5, 2, 4, 3];
        if ((uint)row >= ReleaseFirstViewportTrials)
            throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)position >= FirstViewportConditionCount)
            throw new ArgumentOutOfRangeException(nameof(position));

        return (firstRow[position] + row) % FirstViewportConditionCount;
    }
}
