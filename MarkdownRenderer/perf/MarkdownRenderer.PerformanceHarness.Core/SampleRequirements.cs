namespace MarkdownRenderer.PerformanceHarness;

public sealed class SampleRequirements
{
    public int FirstViewportIterationsRequired { get; init; } =
        PerformanceMeasurementContract.ReleaseFirstViewportIterations;
    public int FirstViewportTrialsRequired { get; init; } =
        PerformanceMeasurementContract.ReleaseFirstViewportTrials;
    public int FirstViewportWarmupTrialsRequired { get; init; } =
        PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials;
    public int ScrollFramesRequired { get; init; } =
        PerformanceMeasurementContract.ReleaseScrollFrames;
    public int ScrollTrialsRequired { get; init; } =
        PerformanceMeasurementContract.ReleaseScrollTrials;
    public int LifecycleCyclesRequired { get; init; } =
        PerformanceMeasurementContract.ReleaseLifecycleCycles;
    public int CancellationIterationsPerTrialRequired { get; init; } =
        PerformanceMeasurementContract.ReleaseCancellationIterations;
    public int CancellationTrialsRequired { get; init; } =
        PerformanceMeasurementContract.ReleaseCancellationTrials;
    public int SourceLookupMappingCountRequired { get; init; } =
        PerformanceMeasurementContract.SourceLookupMappingCount;
    public int SourceLookupAbsoluteQueryCountRequired { get; init; } =
        PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount;
    public int SourceLookupRegressionWarmupPassesRequired { get; init; } =
        PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses;
    public int SourceLookupRegressionTrialsRequired { get; init; } =
        PerformanceMeasurementContract.SourceLookupRegressionTrialCount;
    public int SourceLookupRegressionBatchSizeRequired { get; init; } =
        PerformanceMeasurementContract.SourceLookupRegressionBatchSize;
    public int SourceLookupRegressionObservationCountRequired { get; init; } =
        PerformanceMeasurementContract.SourceLookupRegressionObservationCount;
    public int SourceLookupRegressionOperationCountRequired { get; init; } =
        PerformanceMeasurementContract.SourceLookupRegressionOperationCount;

    public static SampleRequirements For(PerformanceOptions options)
        => new()
        {
            FirstViewportIterationsRequired = options.FirstViewportIterations,
            FirstViewportTrialsRequired = options.Quick
                ? PerformanceMeasurementContract.QuickFirstViewportTrials
                : PerformanceMeasurementContract.ReleaseFirstViewportTrials,
            FirstViewportWarmupTrialsRequired = options.Quick
                ? PerformanceMeasurementContract.QuickFirstViewportWarmupTrials
                : PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials,
            ScrollFramesRequired = options.ScrollFrames,
            ScrollTrialsRequired = options.ScrollTrials,
            LifecycleCyclesRequired = options.LifecycleCycles,
            CancellationIterationsPerTrialRequired = options.CancellationIterations,
            CancellationTrialsRequired = options.Quick
                ? PerformanceMeasurementContract.QuickCancellationTrials
                : PerformanceMeasurementContract.ReleaseCancellationTrials,
        };
}
