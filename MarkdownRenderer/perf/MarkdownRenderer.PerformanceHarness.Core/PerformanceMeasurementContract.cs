namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Frozen release-evidence sample contract. Keep these values centralized so
/// the harness defaults and serialized requirements cannot silently diverge.
/// </summary>
public static class PerformanceMeasurementContract
{
    public const int SchemaVersion = 12;
    public const string ProviderName = "MarkdownRenderer-Performance";
    public const string RuntimeConfigurationPolicy =
        "tiering-pgo-concurrent-gc-readytorun-disabled;dotnet-complus-overrides-unset-v1";

    public const int ReleaseFirstViewportIterations = 100;
    public const int ReleaseFirstViewportTrials = 6;
    public const int ReleaseFirstViewportWarmupTrials = 3;
    public const int FirstViewportConditionCount = 6;
    public const int ReleaseScrollFrames = 2_400;
    public const int ReleaseScrollTrials = 5;
    public const int ReleaseForegroundReadinessTimeoutMilliseconds = 30_000;
    public const int ReleaseForegroundReadinessPollIntervalMilliseconds = 100;
    public const int ReleaseForegroundReadinessConsecutiveQualifiedSamples = 5;
    public const double TargetRefreshRateHz = 120d;
    public const double MinimumConfiguredRefreshRateHz = 119d;
    public const double ScrollFrameTimeP95BudgetMilliseconds =
        1_000d / TargetRefreshRateHz;
    public const double UiPublicationMaximumBudgetMilliseconds = 2;
    public const int ReleaseLifecycleCycles = 100;
    public const int ReleaseCancellationIterations = 40;
    public const int ReleaseCancellationTrials = 5;

    public const int SourceLookupMappingCount = 100_000;
    public const int SourceLookupAbsoluteQueryCount = 20_000;
    public const int SourceLookupRegressionWarmupPasses = 8;
    public const int SourceLookupRegressionTrialCount = 5;
    public const int SourceLookupRegressionBatchSize = 4_096;
    public const int SourceLookupRegressionObservationCount = 128;
    public const int SourceLookupRegressionOperationCount =
        SourceLookupRegressionBatchSize * SourceLookupRegressionObservationCount;

    public const string FirstViewportCacheDisabledMode = "cache-disabled";
    public const string FirstViewportCacheHitMode = "cache-hit";
    public const string FirstViewportEngineReuseMode =
        "fresh-harness-owned-engine-per-trial";
    public const string FirstViewportTrialStartPolicy =
        "fresh-engine;optional-cache-prime;managed-full-collection;one-settling-presentation;recorded-presentations";
    public const string FirstViewportSchedulePolicy =
        "six-condition-six-period-williams-square-v1";
    public const string FirstViewportStationarityPolicy =
        "mad-plus-theil-sen-projected-drift-plus-warmup-boundary-v1";
    public const string FiveTrialHodgesLehmannRegressionEstimator =
        "hodges-lehmann-of-five-trial-p95";
    public const string SixTrialHodgesLehmannRegressionEstimator =
        "hodges-lehmann-of-six-trial-p95";
    public const string FirstViewportRegressionEstimator =
        SixTrialHodgesLehmannRegressionEstimator;
    public const string SourceLookupRegressionEstimator =
        FiveTrialHodgesLehmannRegressionEstimator;
    public const string ScrollRegressionEstimator =
        FiveTrialHodgesLehmannRegressionEstimator;
    public const string CancellationRegressionEstimator =
        FiveTrialHodgesLehmannRegressionEstimator;
    public const string CancellationTrialStartPolicy =
        "managed-full-collection-then-one-distinct-warmup-per-trial";
    public const string ProcessPowerThrottlingMode =
        "execution-speed=off;ignore-timer-resolution=off";
    public const uint ProcessPowerThrottlingExecutionSpeedMask = 0x1;
    public const uint ProcessPowerThrottlingIgnoreTimerResolutionMask = 0x4;
    public const uint ProcessPowerThrottlingControlMask =
        ProcessPowerThrottlingExecutionSpeedMask |
        ProcessPowerThrottlingIgnoreTimerResolutionMask;
    public const string RegressionDecisionPolicy =
        "max-relative-percent-or-absolute-noise-floor-with-robust-dispersion";

    public const double FirstViewportDispersionBudgetPercent = 25;
    public const double FirstViewportStationarityRelativeAllowancePercent = 5;
    public const double ScrollUiWorkDispersionBudgetPercent = 25;
    public const double ScrollFrameTimeDispersionBudgetPercent = 35;
    public const double SourceLookupDispersionBudgetPercent = 20;
    public const double CancellationDispersionBudgetPercent = 25;
    public const double AllocationDispersionBudgetPercent = 25;

    public const double FirstViewport100KiBCacheDisabledNoiseFloorMilliseconds = 5;
    public const double FirstViewport100KiBCacheHitNoiseFloorMilliseconds = 3;
    public const double FirstViewport1MiBCacheDisabledNoiseFloorMilliseconds = 5;
    public const double FirstViewport1MiBCacheHitNoiseFloorMilliseconds = 5;
    public const double FirstViewport10MiBCacheDisabledNoiseFloorMilliseconds = 3;
    public const double FirstViewport10MiBCacheHitNoiseFloorMilliseconds = 1;
    public const double ScrollUiThreadWorkP95NoiseFloorMilliseconds = 0.05;
    public const double ScrollUiThreadWorkP99NoiseFloorMilliseconds = 0.10;
    public const double ScrollFrameTimeP95NoiseFloorMilliseconds = 2;
    public const double SourceLookupNoiseFloorNanoseconds = 30;
    public const double CancellationNoiseFloorMilliseconds = 0.05;
    public const double RendererOwnedAllocationNoiseFloorBytes = 64;

    public const int QuickFirstViewportIterations = 1;
    public const int QuickFirstViewportTrials = 1;
    public const int QuickFirstViewportWarmupTrials = 1;
    public const int QuickScrollFrames = 30;
    public const int QuickScrollTrials = 1;
    public const int QuickLifecycleCycles = 8;
    public const int QuickCancellationIterations = 1;
    public const int QuickCancellationTrials = 1;

    public static double GetFirstViewportNoiseFloorMilliseconds(
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
    public static int GetFirstViewportCondition(int row, int position)
    {
        ReadOnlySpan<int> firstRow = [0, 1, 5, 2, 4, 3];
        if ((uint)row >= ReleaseFirstViewportTrials)
            throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)position >= FirstViewportConditionCount)
            throw new ArgumentOutOfRangeException(nameof(position));

        return (firstRow[position] + row) % FirstViewportConditionCount;
    }
}
