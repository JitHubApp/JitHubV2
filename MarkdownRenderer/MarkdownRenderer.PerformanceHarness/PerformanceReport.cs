using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Security.Cryptography;

namespace MarkdownRenderer.PerformanceHarness;

internal sealed class PerformanceReport
{
    internal const int CurrentSchemaVersion = PerformanceMeasurementContract.SchemaVersion;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ProviderName { get; init; } = PerformanceMeasurementContract.ProviderName;
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public bool IsReleaseEvidence { get; init; }
    public bool Passed { get; set; }
    public string BuildIdentity { get; init; } = string.Empty;
    public BuildArtifactHashes BuildArtifacts { get; init; } = new();
    public RuntimeConfigurationEvidence RuntimeConfiguration { get; init; } = new();
    public MachineMetadata Machine { get; set; } = new();
    public MachineMetadata CompletionMachine { get; set; } = new();
    public SampleRequirements SampleRequirements { get; init; } = new();
    public List<FirstViewportResult> FirstUsableViewport { get; init; } = [];
    public ScrollResult Scroll { get; set; } = new();
    public List<RetainedMemoryResult> RetainedMemory { get; init; } = [];
    public LifecyclePlateauResult LifecyclePlateau { get; set; } = new();
    public SourceLookupResult SourceLookup { get; set; } = new();
    public CancellationResult Cancellation { get; set; } = new();
    public RegressionResult Regression { get; set; } = new();
    public List<string> Failures { get; init; } = [];

    internal static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    internal static PerformanceReportEvidence ReadEvidence(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Reference performance report was not found.", path);
        byte[] bytes = File.ReadAllBytes(path);
        PerformanceReport report;
        try
        {
            report = JsonSerializer.Deserialize<PerformanceReport>(bytes, JsonOptions)
                ?? throw new InvalidDataException($"Performance report '{path}' was empty.");
            if (!HasNonNullReferenceElements(report))
            {
                throw new InvalidDataException(
                    $"Performance report '{path}' contained a null collection element.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Performance report '{path}' did not satisfy the explicit schema contract.",
                exception);
        }

        return new PerformanceReportEvidence(
            report,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo =>
        {
            // Evidence is only trustworthy when every producer-emitted member is
            // explicitly present. Defaults remain useful while constructing a
            // report, but must never repair a truncated reference during read.
            if (typeInfo.Kind != JsonTypeInfoKind.Object ||
                typeInfo.Type.Assembly != typeof(PerformanceReport).Assembly ||
                typeInfo.Type.Namespace != typeof(PerformanceReport).Namespace)
            {
                return;
            }

            foreach (JsonPropertyInfo property in typeInfo.Properties)
                property.IsRequired = true;
        });

        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false,
            TypeInfoResolver = resolver,
        };
    }

    private static bool HasNonNullReferenceElements(PerformanceReport report)
    {
        if (report.RuntimeConfiguration is null ||
            report.RuntimeConfiguration.OverrideEnvironmentVariables is null ||
            report.RuntimeConfiguration.OverrideEnvironmentVariables.Any(static item => item is null) ||
            report.FirstUsableViewport is null ||
            report.FirstUsableViewport.Any(static item => item is null) ||
            report.RetainedMemory is null ||
            report.RetainedMemory.Any(static item => item is null) ||
            report.Failures is null ||
            report.Failures.Any(static item => item is null) ||
            report.Scroll is null ||
            report.Scroll.Trials is null ||
            report.Scroll.Trials.Any(static item => item is null) ||
            report.SourceLookup is null ||
            report.SourceLookup.RegressionTrials is null ||
            report.SourceLookup.RegressionTrials.Any(static item => item is null) ||
            report.Cancellation is null ||
            report.Cancellation.Trials is null ||
            report.Cancellation.Trials.Any(static item => item is null) ||
            report.Regression is null ||
            report.Regression.Comparisons is null ||
            report.Regression.Comparisons.Any(static item => item is null))
        {
            return false;
        }

        return report.FirstUsableViewport.All(static result =>
            result.WarmupTrials is not null &&
            !result.WarmupTrials.Any(static trial => trial is null) &&
            result.Trials is not null &&
            !result.Trials.Any(static trial => trial is null) &&
            result.PublicationSamplesMilliseconds is not null &&
            result.WarmupTrials.All(static trial =>
                trial.PublicationSamplesMilliseconds is not null) &&
            result.Trials.All(static trial =>
                trial.PublicationSamplesMilliseconds is not null));
    }
}

internal readonly record struct PerformanceReportEvidence(
    PerformanceReport Report,
    string Sha256);

internal sealed class MachineMetadata
{
    public string MachineInstanceSha256 { get; init; } = string.Empty;
    public string Cpu { get; init; } = string.Empty;
    public int LogicalProcessorCount { get; init; }
    public string OsDescription { get; init; } = string.Empty;
    public string OsVersion { get; init; } = string.Empty;
    public string OsArchitecture { get; init; } = string.Empty;
    public string ProcessArchitecture { get; init; } = string.Empty;
    public string FrameworkDescription { get; init; } = string.Empty;
    public double DpiScale { get; init; }
    public double ViewportWidthDips { get; init; }
    public double ViewportHeightDips { get; init; }
    public string DisplayDeviceName { get; init; } = string.Empty;
    public double ConfiguredRefreshRateHz { get; init; }
    public double ObservedRefreshRateHz { get; set; }
    public bool IsAtLeast120Hz { get; set; }
    public string GpuAdapterIdentitySha256 { get; init; } = string.Empty;
    public string GpuAdapterLuid { get; init; } = string.Empty;
    public string GpuDriverVersion { get; init; } = string.Empty;
    public string DisplayAdapterIdentitySha256 { get; init; } = string.Empty;
    public string DisplayAdapterLuid { get; init; } = string.Empty;
    public string DisplayAdapterDriverVersion { get; init; } = string.Empty;
    public string ActivePowerSchemeGuid { get; init; } = string.Empty;
    public string UserConfiguredAcPowerModeGuid { get; init; } = string.Empty;
    public string UserConfiguredDcPowerModeGuid { get; init; } = string.Empty;
    public string EffectivePowerMode { get; init; } = string.Empty;
    public string PowerSource { get; init; } = string.Empty;
    public string EnergySaverState { get; init; } = string.Empty;
    public string ProcessPriorityClass { get; init; } = string.Empty;
    public string ProcessPowerThrottlingMode { get; init; } = string.Empty;
    public bool GcServer { get; init; }
    public string GcLatencyMode { get; init; } = string.Empty;
}

internal sealed class SampleRequirements
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

    internal static SampleRequirements For(PerformanceOptions options)
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

internal sealed class BuildArtifactHashes
{
    public string HashAlgorithm { get; init; } = "SHA-256";
    public BuildArtifactHash Executable { get; init; } = new();
    public BuildArtifactHash PerformanceHarnessDll { get; init; } = new();
    public BuildArtifactHash MarkdownRendererDll { get; init; } = new();
    public BuildArtifactHash MarkdownRendererCoreDll { get; init; } = new();
    public BuildArtifactHash RuntimeConfig { get; init; } = new();
}

internal sealed class BuildArtifactHash
{
    public string FileName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}

internal sealed class RuntimeConfigurationEvidence
{
    public string Policy { get; init; } = string.Empty;
    public bool TieredCompilationEnabled { get; init; }
    public bool TieredPgoEnabled { get; init; }
    public bool ConcurrentGcEnabled { get; init; }
    public bool ReadyToRunEnabled { get; init; }
    public List<string> OverrideEnvironmentVariables { get; init; } = [];
    public bool Passed { get; init; }
}

internal sealed class FirstViewportResult
{
    public string Corpus { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public int SourceUtf16Bytes { get; init; }
    public long ParseCacheBudgetBytes { get; init; }
    public string EngineReuseMode { get; init; } = string.Empty;
    public string TrialStartPolicy { get; init; } = string.Empty;
    public string SchedulePolicy { get; init; } = string.Empty;
    public string RegressionEstimator { get; init; } = string.Empty;
    public string StationarityPolicy { get; init; } = string.Empty;
    public string MeasurementThreadAffinityMask { get; init; } = string.Empty;
    public int MeasurementProcessorGroup { get; init; }
    public int MeasurementProcessorNumber { get; init; }
    public string MeasurementThreadPriority { get; init; } = string.Empty;
    public List<FirstViewportTrialResult> WarmupTrials { get; init; } = [];
    public List<FirstViewportTrialResult> Trials { get; init; } = [];
    public List<double> SamplesMilliseconds { get; init; } = [];
    public double P95Milliseconds { get; init; }
    public List<double> PublicationSamplesMilliseconds { get; init; } = [];
    public double PublicationMaximumMilliseconds { get; init; }
    public double PublicationBudgetMilliseconds { get; init; }
    public double RegressionP95Milliseconds { get; init; }
    public double RegressionDispersionPercent { get; init; }
    public double TheilSenSlopeMillisecondsPerGlobalOrdinal { get; init; }
    public double ProjectedDriftMilliseconds { get; init; }
    public double WarmupBoundaryShiftMilliseconds { get; init; }
    public double StationarityAllowanceMilliseconds { get; init; }
    public bool StationarityEvaluated { get; init; }
    public bool StationarityPassed { get; init; }
    public double BudgetMilliseconds { get; init; }
    public bool Passed { get; init; }
}

internal sealed class ScrollResult
{
    public string Corpus { get; init; } = string.Empty;
    public int SourceUtf16Bytes { get; init; }
    public long StopwatchFrequency { get; init; }
    public int RequestedFrames { get; init; }
    public int MeasuredFrameIntervals { get; init; }
    public int FramesWithRendererWork { get; init; }
    public string RegressionEstimator { get; init; } = string.Empty;
    public List<ScrollTrialResult> Trials { get; init; } = [];
    public double RegressionUiThreadWorkP95Milliseconds { get; init; }
    public double RegressionUiThreadWorkP99Milliseconds { get; init; }
    public double RegressionFrameTimeP95Milliseconds { get; init; }
    public double RegressionRendererOwnedAllocatedBytesPerFrameP95 { get; init; }
    public double UiThreadWorkP95Milliseconds { get; init; }
    public double UiThreadWorkP99Milliseconds { get; init; }
    public double ScrollCallbackWorkP95Milliseconds { get; init; }
    public double PaintCallbackWorkP95Milliseconds { get; init; }
    public double UiThreadWorkP95BudgetMilliseconds { get; init; } = 4;
    public double UiThreadWorkP99BudgetMilliseconds { get; init; } = 6;
    public double FrameTimeP95Milliseconds { get; init; }
    public double FrameTimeP95BudgetMilliseconds { get; init; } =
        PerformanceMeasurementContract.ScrollFrameTimeP95BudgetMilliseconds;
    public double FramesOver16_7Percent { get; init; }
    public double FramesOver16_7PercentBudget { get; init; } = 1;
    public double MaximumFrameStallMilliseconds { get; init; }
    public double MaximumFrameStallBudgetMilliseconds { get; init; } = 50;
    public double AllocatedBytesPerFrameP95 { get; init; }
    public long MaximumAllocatedBytesPerFrame { get; init; }
    public string RawAllocationScope { get; init; } =
        "all managed allocations observed on the UI thread inside renderer scroll and paint callbacks";
    public string AllocationGateBasis { get; init; } = "renderer-owned-managed";
    public double RendererOwnedAllocatedBytesPerFrameP95 { get; init; }
    public long MaximumRendererOwnedAllocatedBytesPerFrame { get; init; }
    public double PlatformProjectionAllocatedBytesPerFrameP95 { get; init; }
    public long MaximumPlatformProjectionAllocatedBytesPerFrame { get; init; }
    public string PlatformProjectionAllocationScope { get; init; } =
        "managed projection allocations measured around WinUI and Win2D calls; retained separately from renderer-owned allocations";
    public double ScrollAllocatedBytesPerFrameP95 { get; init; }
    public double PaintAllocatedBytesPerFrameP95 { get; init; }
    public double ScrollLazyLayoutAllocatedBytesP95 { get; init; }
    public double ScrollAdornerAllocatedBytesP95 { get; init; }
    public double ScrollRealizationAllocatedBytesP95 { get; init; }
    public double ScrollHighlightingAllocatedBytesP95 { get; init; }
    public double PaintSchedulingAllocatedBytesP95 { get; init; }
    public double PaintPlatformSessionAllocatedBytesP95 { get; init; }
    public double SnapshotPaintAllocatedBytesP95 { get; init; }
    public double InteractivePaintAllocatedBytesP95 { get; init; }
    public double InlinePaintAllocatedBytesP95 { get; init; }
    public double TablePaintAllocatedBytesP95 { get; init; }
    public double CodePaintAllocatedBytesP95 { get; init; }
    public double OtherPaintAllocatedBytesP95 { get; init; }
    public long AllocationBudgetBytesPerFrame { get; init; } = 4096;
    public bool LohAllocationPossible { get; init; }
    public bool RendererOwnedLohAllocationPossible { get; init; }
    public long MaximumRealizedCodeActions { get; init; }
    public long MaximumOffscreenRealizedCodeActions { get; init; }
    public int Gen2Collections { get; init; }
    public bool Passed { get; init; }
}

internal sealed class RetainedMemoryResult
{
    public string Corpus { get; init; } = string.Empty;
    public int SourceUtf16Bytes { get; init; }
    public long ManagedDeltaBytes { get; init; }
    public long PrivateDeltaBytes { get; init; }
    public long BudgetBytes { get; init; }
    public bool Passed { get; init; }
}

internal sealed class LifecyclePlateauResult
{
    public int CompletedCycles { get; init; }
    public int RequiredCycles { get; init; } = 100;
    public int SyntheticDeviceResets { get; init; }
    public string DeviceResetMode { get; init; } = "snapshot-release-and-canvas-device-trim";
    public int ActualDeviceLossEvents { get; init; }
    public long Checkpoint80ManagedBytes { get; init; }
    public long Checkpoint100ManagedBytes { get; init; }
    public long Checkpoint80PrivateBytes { get; init; }
    public long Checkpoint100PrivateBytes { get; init; }
    public double ManagedGrowthPercent { get; init; }
    public double PrivateGrowthPercent { get; init; }
    public double GrowthPercent { get; init; }
    public double GrowthBudgetPercent { get; init; } = 5;
    public int RenderFailures { get; init; }
    public bool Passed { get; init; }
}

internal sealed class SourceLookupResult
{
    public int MappingCount { get; init; }
    public int QueryCount { get; init; }
    public long StopwatchFrequency { get; init; }
    public string MeasurementThreadAffinityMask { get; init; } = string.Empty;
    public int MeasurementProcessorGroup { get; init; }
    public int MeasurementProcessorNumber { get; init; }
    public string MeasurementThreadPriority { get; init; } = string.Empty;
    public List<long> SamplesElapsedTicks { get; init; } = [];
    public double P95Nanoseconds { get; init; }
    public double P95BudgetNanoseconds { get; init; } = 25_000;
    public long AllocatedBytes { get; init; }
    public int RegressionWarmupPasses { get; init; }
    public int RegressionBatchSize { get; init; }
    public int RegressionObservationCount { get; init; }
    public int RegressionOperationCount { get; init; }
    public double RegressionP95Nanoseconds { get; init; }
    public long RegressionAllocatedBytes { get; init; }
    public string RegressionEstimator { get; init; } = string.Empty;
    public int RegressionTrialCount { get; init; }
    public List<SourceLookupTrialResult> RegressionTrials { get; init; } = [];
    public bool Passed { get; init; }
}

internal sealed class FirstViewportTrialResult
{
    public int Ordinal { get; init; }
    public int GlobalOrdinal { get; init; }
    public int ScheduleRow { get; init; }
    public int SchedulePosition { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public double SettlingElapsedMilliseconds { get; init; }
    public double SettlingPublicationMilliseconds { get; init; }
    public List<double> SamplesMilliseconds { get; init; } = [];
    public List<double> PublicationSamplesMilliseconds { get; init; } = [];
    public double PublicationMaximumMilliseconds { get; init; }
    public double P95Milliseconds { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public long ProcessAllocatedBytes { get; init; }
    public int CompletedParseCountBeforePrime { get; init; }
    public int CompletedParseCountAfterPrime { get; init; }
    public int CompletedParseCountAfterTrial { get; init; }
    public long SourceKeyHashCountBeforePrime { get; init; }
    public long SourceKeyHashCountAfterPrime { get; init; }
    public long SourceKeyHashCountAfterTrial { get; init; }
    public bool CacheProofPassed { get; init; }
    public bool Complete { get; init; }
}

internal sealed class SourceLookupTrialResult
{
    public int Ordinal { get; init; }
    public int ObservationCount { get; init; }
    public int OperationCount { get; init; }
    public List<long> SamplesElapsedTicks { get; init; } = [];
    public List<double> SamplesNanosecondsPerLookup { get; init; } = [];
    public double P95Nanoseconds { get; init; }
    public long AllocatedBytes { get; init; }
    public bool Complete { get; init; }
}

internal sealed class CancellationResult
{
    public int RequestedIterations { get; init; }
    public int IterationsPerTrial { get; init; }
    public int WarmupIterations { get; init; }
    public double WarmupElapsedMilliseconds { get; init; }
    public int WarmupStaleCommits { get; init; }
    public int ObservedCancellations { get; init; }
    public string TrialStartPolicy { get; init; } = string.Empty;
    public string RegressionEstimator { get; init; } = string.Empty;
    public List<CancellationTrialResult> Trials { get; init; } = [];
    public List<double> SamplesMilliseconds { get; init; } = [];
    public double P95Milliseconds { get; init; }
    public double RegressionP95Milliseconds { get; init; }
    public double P95BudgetMilliseconds { get; init; } =
        CancellationMeasurementPlan.AbsoluteBudgetMilliseconds;
    public int StaleCommits { get; init; }
    public bool Passed { get; init; }
}

internal sealed class CancellationTrialResult
{
    public int Ordinal { get; init; }
    public int WarmupSourceSeed { get; init; }
    public double WarmupElapsedMilliseconds { get; init; }
    public int WarmupStaleCommits { get; init; }
    public int FirstRecordedSourceSeed { get; init; }
    public int RequestedIterations { get; init; }
    public int ObservedCancellations { get; init; }
    public List<double> SamplesMilliseconds { get; init; } = [];
    public double P95Milliseconds { get; init; }
    public int StaleCommits { get; init; }
    public bool Complete { get; init; }
}

internal sealed class ScrollTrialResult
{
    public int Ordinal { get; init; }
    public long FirstLogicalFrameId { get; init; }
    public int RequestedFrames { get; init; }
    public int MeasuredFrameIntervals { get; init; }
    public int FramesWithRendererWork { get; init; }
    public List<long> UiThreadWorkElapsedTicks { get; init; } = [];
    public List<long> FrameIntervalElapsedTicks { get; init; } = [];
    public List<long> RendererOwnedAllocatedBytesPerFrame { get; init; } = [];
    public double UiThreadWorkP95Milliseconds { get; init; }
    public double UiThreadWorkP99Milliseconds { get; init; }
    public double ScrollCallbackWorkP95Milliseconds { get; init; }
    public double PaintCallbackWorkP95Milliseconds { get; init; }
    public double FrameTimeP95Milliseconds { get; init; }
    public double FramesOver16_7Percent { get; init; }
    public double MaximumFrameStallMilliseconds { get; init; }
    public double AllocatedBytesPerFrameP95 { get; init; }
    public long MaximumAllocatedBytesPerFrame { get; init; }
    public double RendererOwnedAllocatedBytesPerFrameP95 { get; init; }
    public long MaximumRendererOwnedAllocatedBytesPerFrame { get; init; }
    public double PlatformProjectionAllocatedBytesPerFrameP95 { get; init; }
    public long MaximumPlatformProjectionAllocatedBytesPerFrame { get; init; }
    public double ScrollAllocatedBytesPerFrameP95 { get; init; }
    public double PaintAllocatedBytesPerFrameP95 { get; init; }
    public double ScrollLazyLayoutAllocatedBytesP95 { get; init; }
    public double ScrollAdornerAllocatedBytesP95 { get; init; }
    public double ScrollRealizationAllocatedBytesP95 { get; init; }
    public double ScrollHighlightingAllocatedBytesP95 { get; init; }
    public double PaintSchedulingAllocatedBytesP95 { get; init; }
    public double PaintPlatformSessionAllocatedBytesP95 { get; init; }
    public double SnapshotPaintAllocatedBytesP95 { get; init; }
    public double InteractivePaintAllocatedBytesP95 { get; init; }
    public double InlinePaintAllocatedBytesP95 { get; init; }
    public double TablePaintAllocatedBytesP95 { get; init; }
    public double CodePaintAllocatedBytesP95 { get; init; }
    public double OtherPaintAllocatedBytesP95 { get; init; }
    public long MaximumRealizedCodeActions { get; init; }
    public long MaximumOffscreenRealizedCodeActions { get; init; }
    public int Gen2Collections { get; init; }
    public bool Complete { get; init; }
}

internal sealed class RegressionResult
{
    public string Mode { get; init; } = string.Empty;
    public string? ReferencePath { get; init; }
    public string? ReferenceReportSha256 { get; init; }
    public string? ReferenceBuildIdentity { get; init; }
    public bool ReferenceEligible { get; init; }
    public bool MachineComparable { get; init; }
    public int ComparedLatencyMetrics { get; init; }
    public int ComparedAllocationMetrics { get; init; }
    public string DecisionPolicy { get; init; } =
        PerformanceMeasurementContract.RegressionDecisionPolicy;
    public double LatencyRegressionBudgetPercent { get; init; } = 5;
    public double AllocationRegressionBudgetPercent { get; init; } = 2;
    public List<RegressionComparison> Comparisons { get; init; } = [];
    public bool Passed { get; init; }
}

internal sealed class RegressionComparison
{
    public string Metric { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public double Reference { get; init; }
    public double Candidate { get; init; }
    public double AbsoluteDelta { get; init; }
    public double DeltaPercent { get; init; }
    public double BudgetPercent { get; init; }
    public double RelativeAllowance { get; init; }
    public double AbsoluteNoiseFloor { get; init; }
    public double AllowedAbsoluteDelta { get; init; }
    public double ReferenceDispersionPercent { get; init; }
    public double CandidateDispersionPercent { get; init; }
    public double StabilityBudgetPercent { get; init; }
    public string Decision { get; init; } = string.Empty;
    public bool Passed { get; init; }
}
