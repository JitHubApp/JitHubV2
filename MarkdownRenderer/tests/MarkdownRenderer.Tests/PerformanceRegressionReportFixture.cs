using System.Diagnostics;
using MarkdownRenderer.PerformanceHarness;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Builds release-shaped reports for the regression evaluator tests. Every
/// builder takes an optional named mutation that breaks one piece of evidence,
/// so a test asks either for a valid report or for one specific defect.
/// </summary>
internal static class PerformanceRegressionReportFixture
{
    /// <summary>The first-viewport scenario that viewport mutations target.</summary>
    internal const int TargetViewportBytes = 100 * 1024;

    /// <summary>The first-viewport mode that viewport mutations target.</summary>
    internal const string TargetViewportMode =
        PerformanceMeasurementContract.FirstViewportCacheDisabledMode;

    private static readonly DateTimeOffset ReportStartedUtc =
        DateTimeOffset.Parse("2026-09-10T12:00:00Z");

    internal static PerformanceReport CreateReport(
        BuildArtifactHashes artifacts,
        string buildIdentity,
        int schemaVersion = PerformanceMeasurementContract.SchemaVersion,
        SampleRequirements? sampleRequirements = null,
        string? mutation = null,
        double observedRefreshRateHz = 120,
        IReadOnlyList<double>? targetViewportTrialP95Milliseconds = null,
        int targetViewportBytes = TargetViewportBytes,
        string targetViewportMode = TargetViewportMode,
        long scrollRendererOwnedAllocationBytes = 1)
    {
        var report = new PerformanceReport
        {
            SchemaVersion = schemaVersion,
            ProviderName = mutation == "provider"
                ? "unreviewed-provider"
                : PerformanceMeasurementContract.ProviderName,
            StartedUtc = mutation == "started" ? default : ReportStartedUtc,
            CompletedUtc = SelectCompletedUtc(mutation),
            IsReleaseEvidence = true,
            Passed = mutation != "top-passed",
            BuildIdentity = SelectBuildIdentity(artifacts, buildIdentity, mutation),
            BuildArtifacts = artifacts,
            RuntimeConfiguration = CreateRuntimeConfiguration(mutation),
            MeasurementVisibility = PerformanceVisibilityFixture.CreatePassing(
                ReportStartedUtc,
                intervalOffset: TimeSpan.FromSeconds(1),
                duration: TimeSpan.FromSeconds(10)),
            Machine = PerformanceRegressionMachineFixture.CreateMachine(
                mutation,
                NormalizeObservedRefreshRate(observedRefreshRateHz)),
            SampleRequirements = sampleRequirements ?? new SampleRequirements(),
            Scroll = PerformanceRegressionMeasurementFixture.CreateScrollResult(
                mutation,
                scrollRendererOwnedAllocationBytes,
                mutation == "observed-refresh-raw" ? 119 : observedRefreshRateHz),
            SourceLookup = PerformanceRegressionMeasurementFixture.CreateSourceLookupResult(mutation),
            Cancellation = PerformanceRegressionMeasurementFixture.CreateCancellationResult(mutation),
            LifecyclePlateau = CreateLifecyclePlateau(mutation),
            Regression = CreateBaselineRegression(mutation),
        };
        report.CompletionMachine = PerformanceRegressionMachineFixture.CreateCompletionMachine(
            report.Machine,
            mutation);
        AddScenarioResults(
            report,
            mutation,
            targetViewportTrialP95Milliseconds,
            targetViewportBytes,
            targetViewportMode);

        if (mutation == "retained-count")
            report.RetainedMemory.RemoveAt(report.RetainedMemory.Count - 1);
        if (mutation == "top-failure")
            report.Failures.Add("synthetic failure");

        return report;
    }

    internal static double[] RepeatTrialValues(double value)
        => Enumerable.Repeat(
            value,
            PerformanceMeasurementContract.ReleaseFirstViewportTrials).ToArray();

    internal static SampleRequirements CreateRequirements(string mutation)
        => new()
        {
            LifecycleCyclesRequired = mutation == "lifecycle" ? 99 :
                PerformanceMeasurementContract.ReleaseLifecycleCycles,
            FirstViewportWarmupTrialsRequired = mutation == "first-warmup" ? 0 :
                PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials,
            FirstViewportTrialsRequired = mutation == "first-trials" ? 4 :
                PerformanceMeasurementContract.ReleaseFirstViewportTrials,
            CancellationIterationsPerTrialRequired =
                mutation == "cancellation-iterations-requirement" ? 39 :
                    PerformanceMeasurementContract.ReleaseCancellationIterations,
            CancellationTrialsRequired = mutation == "cancellation-trials-requirement" ? 4 :
                PerformanceMeasurementContract.ReleaseCancellationTrials,
            SourceLookupMappingCountRequired = mutation == "mapping" ? 99_999 :
                PerformanceMeasurementContract.SourceLookupMappingCount,
            SourceLookupAbsoluteQueryCountRequired = mutation == "absolute-query" ? 19_999 :
                PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount,
            SourceLookupRegressionWarmupPassesRequired = mutation == "lookup-warmup" ? 7 :
                PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses,
            SourceLookupRegressionTrialsRequired = mutation == "lookup-trials" ? 4 :
                PerformanceMeasurementContract.SourceLookupRegressionTrialCount,
            SourceLookupRegressionBatchSizeRequired = mutation == "lookup-batch" ? 63 :
                PerformanceMeasurementContract.SourceLookupRegressionBatchSize,
            SourceLookupRegressionObservationCountRequired = mutation == "lookup-observations" ? 127 :
                PerformanceMeasurementContract.SourceLookupRegressionObservationCount,
            SourceLookupRegressionOperationCountRequired = mutation == "lookup-operations" ? 524_287 :
                PerformanceMeasurementContract.SourceLookupRegressionOperationCount,
        };

    /// <summary>
    /// Rounds a refresh rate to the whole-tick frame interval the harness
    /// measures, so the reported observed rate matches the raw scroll ticks.
    /// </summary>
    private static double NormalizeObservedRefreshRate(double requestedRefreshRateHz)
    {
        long intervalTicks = checked((long)Math.Round(
            Stopwatch.Frequency / requestedRefreshRateHz));
        return Stopwatch.Frequency / (double)intervalTicks;
    }

    private static DateTimeOffset SelectCompletedUtc(string? mutation)
        => mutation switch
        {
            "completed" => DateTimeOffset.Parse("2026-09-10T11:59:00Z"),
            "completed-before-last-viewport-trial" => ReportStartedUtc.AddMilliseconds(1),
            _ => DateTimeOffset.Parse("2026-09-10T12:01:00Z"),
        };

    private static string SelectBuildIdentity(
        BuildArtifactHashes artifacts,
        string buildIdentity,
        string? mutation)
        => mutation == "build-identity"
            ? "unversioned-build-label"
            : PerformanceDeploymentIdentity.IsWellFormed(buildIdentity)
                ? buildIdentity
                : PerformanceDeploymentIdentity.CaptureForExecutable(artifacts.Executable.Path);

    private static RuntimeConfigurationEvidence CreateRuntimeConfiguration(string? mutation)
        => new()
        {
            Policy = mutation == "runtime-policy"
                ? "uncontrolled"
                : PerformanceMeasurementContract.RuntimeConfigurationPolicy,
            TieredCompilationEnabled = mutation == "runtime-tiering",
            TieredPgoEnabled = false,
            ConcurrentGcEnabled = false,
            ReadyToRunEnabled = false,
            OverrideEnvironmentVariables = mutation == "runtime-override"
                ? ["DOTNET_TieredCompilation"]
                : [],
            Passed = mutation != "runtime-passed",
        };

    private static LifecyclePlateauResult CreateLifecyclePlateau(string? mutation)
        => new()
        {
            CompletedCycles = PerformanceMeasurementContract.ReleaseLifecycleCycles,
            RequiredCycles = PerformanceMeasurementContract.ReleaseLifecycleCycles,
            SyntheticDeviceResets = 4,
            DeviceResetMode = "snapshot-release-and-canvas-device-trim",
            ActualDeviceLossEvents = 0,
            Checkpoint80ManagedBytes = 1_000,
            Checkpoint100ManagedBytes = 1_000,
            Checkpoint80PrivateBytes = 1_000,
            Checkpoint100PrivateBytes = 1_000,
            ManagedGrowthPercent = 0,
            PrivateGrowthPercent = 0,
            GrowthPercent = 0,
            GrowthBudgetPercent = 5,
            RenderFailures = 0,
            Passed = mutation != "lifecycle-result",
        };

    private static RegressionResult CreateBaselineRegression(string? mutation)
        => new()
        {
            Mode = "baseline",
            DecisionPolicy = mutation == "regression-decision-policy"
                ? "relative-only"
                : PerformanceMeasurementContract.RegressionDecisionPolicy,
            ReferenceEligible = true,
            MachineComparable = true,
            Passed = true,
        };

    private static void AddScenarioResults(
        PerformanceReport report,
        string? mutation,
        IReadOnlyList<double>? targetViewportTrialP95Milliseconds,
        int targetViewportBytes,
        string targetViewportMode)
    {
        int condition = 0;
        foreach (int sourceBytes in new[] { 100 * 1024, 1024 * 1024, 10 * 1024 * 1024 })
        {
            foreach (string mode in new[]
                     {
                         PerformanceMeasurementContract.FirstViewportCacheDisabledMode,
                         PerformanceMeasurementContract.FirstViewportCacheHitMode,
                     })
            {
                IReadOnlyList<double> trialValues =
                    SelectFixedTrialValues(sourceBytes, mode, mutation) ??
                    (sourceBytes == targetViewportBytes && mode == targetViewportMode &&
                     targetViewportTrialP95Milliseconds is not null
                        ? targetViewportTrialP95Milliseconds
                        : RepeatTrialValues(1));
                report.FirstUsableViewport.Add(
                    PerformanceRegressionFirstViewportFixture.CreateResult(
                        sourceBytes,
                        mode,
                        condition,
                        trialValues,
                        mutation));
                condition++;
            }

            report.RetainedMemory.Add(CreateRetainedMemory(sourceBytes, mutation));
        }
    }

    /// <summary>
    /// Returns the fixed trial series that a target-viewport stationarity
    /// mutation needs, or null when the mutation leaves the series alone.
    /// </summary>
    private static double[]? SelectFixedTrialValues(int sourceBytes, string mode, string? mutation)
    {
        if (sourceBytes != TargetViewportBytes || mode != TargetViewportMode)
            return null;

        return mutation switch
        {
            "viewport-dispersion" => [1, 1, 1, 10, 10, 10],
            "viewport-residual-spike" => [10, 10, 10, 10, 10, 20],
            "viewport-boundary-endpoint-spike" => RepeatTrialValues(10),
            "viewport-drift-series" => [50, 52, 54, 56, 58, 60],
            _ => null,
        };
    }

    private static RetainedMemoryResult CreateRetainedMemory(int sourceBytes, string? mutation)
        => new()
        {
            Corpus = PerformanceDocumentFactory.GetCorpusName(sourceBytes),
            SourceUtf16Bytes = sourceBytes,
            ManagedDeltaBytes = 0,
            PrivateDeltaBytes = 0,
            BudgetBytes = mutation == "retained-budget" && sourceBytes == 100 * 1024
                ? 1
                : checked(sourceBytes * 8L + 32L * 1024 * 1024),
            Passed = true,
        };
}
