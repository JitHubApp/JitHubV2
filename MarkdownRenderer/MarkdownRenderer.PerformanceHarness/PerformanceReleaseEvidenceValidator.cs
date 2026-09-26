using System.Diagnostics;
using System.Globalization;
using System.Runtime;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Validates the complete, self-contained measurement payload used by release
/// regression comparisons. The producer, in-process evaluator, and external
/// gate must agree on these invariants; a top-level <c>passed</c> flag is never
/// accepted as a substitute for recomputable evidence.
/// </summary>
internal static class PerformanceReleaseEvidenceValidator
{
    private const double LatencyRegressionBudgetPercent = 5;
    private const double AllocationRegressionBudgetPercent = 2;
    private const string RawAllocationScope =
        "all managed allocations observed on the UI thread inside renderer scroll and paint callbacks";
    private const string AllocationGateBasis = "renderer-owned-managed";
    private const string PlatformProjectionAllocationScope =
        "managed projection allocations measured around WinUI and Win2D calls; retained separately from renderer-owned allocations";
    private const string LifecycleDeviceResetMode =
        "snapshot-release-and-canvas-device-trim";

    internal static bool HasCompleteMeasurementPayload(PerformanceReport report)
        => report.SchemaVersion == PerformanceReport.CurrentSchemaVersion &&
           report.ProviderName == PerformanceMeasurementContract.ProviderName &&
           report.IsReleaseEvidence &&
           report.StartedUtc != default &&
           report.StartedUtc.Offset == TimeSpan.Zero &&
           report.CompletedUtc > report.StartedUtc &&
           report.CompletedUtc.Offset == TimeSpan.Zero &&
           PerformanceDeploymentIdentity.IsWellFormed(report.BuildIdentity) &&
           PerformanceArtifactContract.TryValidateStored(report.BuildArtifacts, out _) &&
           PerformanceRuntimeContract.IsValid(report.RuntimeConfiguration) &&
           HasReleaseSampleRequirements(report.SampleRequirements) &&
           IsMachineMetadataComplete(report.Machine) &&
           IsEnvironmentSnapshotComplete(report.CompletionMachine) &&
           IsSameRunEnvironment(report.Machine, report.CompletionMachine) &&
           IsReleasePowerEnvironment(report.Machine) &&
           HasQualifiedRefresh(report.Machine) &&
           HasRequiredFirstViewportProtocol(report.FirstUsableViewport) &&
           AreFirstViewportTrialsWithinReportInterval(
               report.FirstUsableViewport,
               report.StartedUtc,
               report.CompletedUtc) &&
           HasCompleteScrollEvidence(report.Scroll) &&
           HasObservedRefreshEvidence(report.Machine, report.Scroll) &&
           HasCompleteRetainedMemoryEvidence(report.RetainedMemory) &&
           HasCompleteLifecycleEvidence(report.LifecyclePlateau) &&
           HasCompleteSourceLookupEvidence(report.SourceLookup) &&
           HasCompleteCancellationEvidence(report.Cancellation);

    internal static bool HasCompleteCurrentMeasurementPayload(PerformanceReport report)
        => HasCompleteMeasurementPayload(report) &&
           PerformanceArtifactContract.TryValidateCurrentFiles(
               report.BuildArtifacts,
               out _) &&
           PerformanceDeploymentIdentity.TryValidateCurrent(
               report.BuildIdentity,
               report.BuildArtifacts.Executable.Path,
               out _);

    internal static bool IsEligibleBaselineReference(PerformanceReport report)
    {
        RegressionResult regression = report.Regression;
        return HasCompleteMeasurementPayload(report) &&
               report.CompletedUtc > report.StartedUtc &&
               report.Passed &&
               report.Failures.Count == 0 &&
               regression.Mode == "baseline" &&
               string.IsNullOrEmpty(regression.ReferencePath) &&
               string.IsNullOrEmpty(regression.ReferenceReportSha256) &&
               string.IsNullOrEmpty(regression.ReferenceBuildIdentity) &&
               regression.ReferenceEligible &&
               regression.MachineComparable &&
               regression.ComparedLatencyMetrics == 0 &&
               regression.ComparedAllocationMetrics == 0 &&
               regression.DecisionPolicy == PerformanceMeasurementContract.RegressionDecisionPolicy &&
               regression.LatencyRegressionBudgetPercent == LatencyRegressionBudgetPercent &&
               regression.AllocationRegressionBudgetPercent == AllocationRegressionBudgetPercent &&
               regression.Comparisons.Count == 0 &&
               regression.Passed;
    }

    internal static bool IsMachineComparable(
        MachineMetadata candidate,
        MachineMetadata reference)
        => IsMachineMetadataComplete(candidate) &&
           IsMachineMetadataComplete(reference) &&
           string.Equals(
               candidate.MachineInstanceSha256,
               reference.MachineInstanceSha256,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(candidate.Cpu, reference.Cpu, StringComparison.Ordinal) &&
           candidate.LogicalProcessorCount == reference.LogicalProcessorCount &&
           string.Equals(candidate.OsDescription, reference.OsDescription, StringComparison.Ordinal) &&
           string.Equals(candidate.OsVersion, reference.OsVersion, StringComparison.Ordinal) &&
           string.Equals(candidate.OsArchitecture, reference.OsArchitecture, StringComparison.Ordinal) &&
           string.Equals(candidate.ProcessArchitecture, reference.ProcessArchitecture, StringComparison.Ordinal) &&
           string.Equals(candidate.FrameworkDescription, reference.FrameworkDescription, StringComparison.Ordinal) &&
           Math.Abs(candidate.DpiScale - reference.DpiScale) < 0.01 &&
            Math.Abs(candidate.ViewportWidthDips - reference.ViewportWidthDips) < 1 &&
            Math.Abs(candidate.ViewportHeightDips - reference.ViewportHeightDips) < 1 &&
            string.Equals(
                candidate.DisplayDeviceName,
                reference.DisplayDeviceName,
                StringComparison.Ordinal) &&
            Math.Abs(candidate.ConfiguredRefreshRateHz - reference.ConfiguredRefreshRateHz) < 0.5 &&
           HasQualifiedRefresh(candidate) &&
           HasQualifiedRefresh(reference) &&
           string.Equals(
               candidate.GpuAdapterIdentitySha256,
               reference.GpuAdapterIdentitySha256,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(candidate.GpuDriverVersion, reference.GpuDriverVersion, StringComparison.Ordinal) &&
            string.Equals(
                candidate.DisplayAdapterIdentitySha256,
                reference.DisplayAdapterIdentitySha256,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                candidate.DisplayAdapterDriverVersion,
                reference.DisplayAdapterDriverVersion,
                StringComparison.Ordinal) &&
            string.Equals(candidate.ActivePowerSchemeGuid, reference.ActivePowerSchemeGuid, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(candidate.UserConfiguredAcPowerModeGuid, reference.UserConfiguredAcPowerModeGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.UserConfiguredDcPowerModeGuid, reference.UserConfiguredDcPowerModeGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.EffectivePowerMode, reference.EffectivePowerMode, StringComparison.Ordinal) &&
           string.Equals(candidate.PowerSource, reference.PowerSource, StringComparison.Ordinal) &&
           string.Equals(candidate.EnergySaverState, reference.EnergySaverState, StringComparison.Ordinal) &&
           string.Equals(candidate.ProcessPriorityClass, reference.ProcessPriorityClass, StringComparison.Ordinal) &&
           string.Equals(
               candidate.ProcessPowerThrottlingMode,
               reference.ProcessPowerThrottlingMode,
               StringComparison.Ordinal) &&
           candidate.GcServer == reference.GcServer &&
           string.Equals(candidate.GcLatencyMode, reference.GcLatencyMode, StringComparison.Ordinal);

    internal static bool IsSameRunEnvironment(
        MachineMetadata start,
        MachineMetadata completion)
        => IsMachineMetadataComplete(start) &&
           IsEnvironmentSnapshotComplete(completion) &&
           string.Equals(start.MachineInstanceSha256, completion.MachineInstanceSha256, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(start.Cpu, completion.Cpu, StringComparison.Ordinal) &&
           start.LogicalProcessorCount == completion.LogicalProcessorCount &&
           string.Equals(start.OsDescription, completion.OsDescription, StringComparison.Ordinal) &&
           string.Equals(start.OsVersion, completion.OsVersion, StringComparison.Ordinal) &&
           string.Equals(start.OsArchitecture, completion.OsArchitecture, StringComparison.Ordinal) &&
           string.Equals(start.ProcessArchitecture, completion.ProcessArchitecture, StringComparison.Ordinal) &&
           string.Equals(start.FrameworkDescription, completion.FrameworkDescription, StringComparison.Ordinal) &&
           Math.Abs(start.DpiScale - completion.DpiScale) < 0.01 &&
           Math.Abs(start.ViewportWidthDips - completion.ViewportWidthDips) < 1 &&
           Math.Abs(start.ViewportHeightDips - completion.ViewportHeightDips) < 1 &&
           string.Equals(start.DisplayDeviceName, completion.DisplayDeviceName, StringComparison.Ordinal) &&
           Math.Abs(start.ConfiguredRefreshRateHz - completion.ConfiguredRefreshRateHz) < 0.5 &&
           string.Equals(start.GpuAdapterIdentitySha256, completion.GpuAdapterIdentitySha256, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(start.GpuAdapterLuid, completion.GpuAdapterLuid, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(start.GpuDriverVersion, completion.GpuDriverVersion, StringComparison.Ordinal) &&
            string.Equals(start.DisplayAdapterIdentitySha256, completion.DisplayAdapterIdentitySha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(start.DisplayAdapterLuid, completion.DisplayAdapterLuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(start.DisplayAdapterDriverVersion, completion.DisplayAdapterDriverVersion, StringComparison.Ordinal) &&
           string.Equals(start.ActivePowerSchemeGuid, completion.ActivePowerSchemeGuid, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(start.UserConfiguredAcPowerModeGuid, completion.UserConfiguredAcPowerModeGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(start.UserConfiguredDcPowerModeGuid, completion.UserConfiguredDcPowerModeGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(start.EffectivePowerMode, completion.EffectivePowerMode, StringComparison.Ordinal) &&
           string.Equals(start.PowerSource, completion.PowerSource, StringComparison.Ordinal) &&
           string.Equals(start.EnergySaverState, completion.EnergySaverState, StringComparison.Ordinal) &&
           string.Equals(start.ProcessPriorityClass, completion.ProcessPriorityClass, StringComparison.Ordinal) &&
           string.Equals(start.ProcessPowerThrottlingMode, completion.ProcessPowerThrottlingMode, StringComparison.Ordinal) &&
           start.GcServer == completion.GcServer &&
           string.Equals(start.GcLatencyMode, completion.GcLatencyMode, StringComparison.Ordinal);

    internal static bool HasSameSourceLookupExecutionEnvironment(
        SourceLookupResult candidate,
        SourceLookupResult reference)
        => PinnedMeasurementThreadScope.IsEvidenceValid(
               candidate.MeasurementThreadAffinityMask,
               candidate.MeasurementProcessorGroup,
               candidate.MeasurementProcessorNumber,
               candidate.MeasurementThreadPriority,
               ThreadPriority.Highest) &&
           PinnedMeasurementThreadScope.IsEvidenceValid(
               reference.MeasurementThreadAffinityMask,
               reference.MeasurementProcessorGroup,
               reference.MeasurementProcessorNumber,
               reference.MeasurementThreadPriority,
               ThreadPriority.Highest) &&
           string.Equals(
               candidate.MeasurementThreadAffinityMask,
               reference.MeasurementThreadAffinityMask,
               StringComparison.Ordinal) &&
           candidate.MeasurementProcessorGroup == reference.MeasurementProcessorGroup &&
           candidate.MeasurementProcessorNumber == reference.MeasurementProcessorNumber &&
           string.Equals(
               candidate.MeasurementThreadPriority,
               reference.MeasurementThreadPriority,
               StringComparison.Ordinal);

    internal static bool IsMachineMetadataComplete(MachineMetadata machine)
        => IsEnvironmentSnapshotComplete(machine) &&
           IsFinitePositive(machine.ObservedRefreshRateHz) &&
           machine.IsAtLeast120Hz ==
               (machine.ConfiguredRefreshRateHz >= 119 &&
                machine.ObservedRefreshRateHz >= Math.Max(
                    115,
                    machine.ConfiguredRefreshRateHz * 0.95));

    internal static bool IsEnvironmentSnapshotComplete(MachineMetadata machine)
        => IsSha256(machine.MachineInstanceSha256) &&
           !string.IsNullOrWhiteSpace(machine.Cpu) &&
           machine.LogicalProcessorCount > 0 &&
           !string.IsNullOrWhiteSpace(machine.OsDescription) &&
           !string.IsNullOrWhiteSpace(machine.OsVersion) &&
           !string.IsNullOrWhiteSpace(machine.OsArchitecture) &&
           !string.IsNullOrWhiteSpace(machine.ProcessArchitecture) &&
           !string.IsNullOrWhiteSpace(machine.FrameworkDescription) &&
           IsFinitePositive(machine.DpiScale) &&
           IsFinitePositive(machine.ViewportWidthDips) &&
           IsFinitePositive(machine.ViewportHeightDips) &&
           !string.IsNullOrWhiteSpace(machine.DisplayDeviceName) &&
           IsFinitePositive(machine.ConfiguredRefreshRateHz) &&
           IsSha256(machine.GpuAdapterIdentitySha256) &&
           IsGpuAdapterLuid(machine.GpuAdapterLuid) &&
           !string.IsNullOrWhiteSpace(machine.GpuDriverVersion) &&
           IsSha256(machine.DisplayAdapterIdentitySha256) &&
           IsGpuAdapterLuid(machine.DisplayAdapterLuid) &&
           !string.IsNullOrWhiteSpace(machine.DisplayAdapterDriverVersion) &&
           Guid.TryParseExact(machine.ActivePowerSchemeGuid, "D", out _) &&
           HasConsistentConfiguredPowerModeSupport(machine) &&
           IsEffectivePowerMode(machine.EffectivePowerMode) &&
           machine.PowerSource is "AC" or "Battery" &&
           machine.EnergySaverState is "Off" or "On" &&
           Enum.TryParse(machine.ProcessPriorityClass, ignoreCase: false, out ProcessPriorityClass priority) &&
           Enum.IsDefined(priority) &&
           Enum.GetName(priority) == machine.ProcessPriorityClass &&
           machine.ProcessPowerThrottlingMode ==
               PerformanceMeasurementContract.ProcessPowerThrottlingMode &&
           Enum.TryParse(machine.GcLatencyMode, ignoreCase: false, out GCLatencyMode latencyMode) &&
           Enum.IsDefined(latencyMode) &&
           Enum.GetName(latencyMode) == machine.GcLatencyMode;

    internal static bool IsReleasePowerEnvironment(MachineMetadata machine)
        => machine.PowerSource == "AC" && machine.EnergySaverState == "Off";

    internal static bool HasQualifiedRefresh(MachineMetadata machine)
        => machine.IsAtLeast120Hz &&
           double.IsFinite(machine.ConfiguredRefreshRateHz) &&
           double.IsFinite(machine.ObservedRefreshRateHz) &&
           machine.ConfiguredRefreshRateHz >= 119 &&
           machine.ObservedRefreshRateHz >= Math.Max(
               115,
               machine.ConfiguredRefreshRateHz * 0.95);

    internal static double CalculateObservedRefreshRateHz(ScrollResult scroll)
    {
        if (scroll.StopwatchFrequency <= 0)
            return 0;

        long[] elapsedTicks = scroll.Trials
            .SelectMany(static trial => trial.FrameIntervalElapsedTicks)
            .ToArray();
        if (elapsedTicks.Length == 0 || elapsedTicks.Any(static ticks => ticks <= 0))
            return 0;

        double medianTicks = PerformanceStatistics.Percentile(elapsedTicks, 0.50);
        return medianTicks > 0
            ? scroll.StopwatchFrequency / medianTicks
            : 0;
    }

    private static bool HasObservedRefreshEvidence(
        MachineMetadata machine,
        ScrollResult scroll)
        => NearlyEqual(
            machine.ObservedRefreshRateHz,
            CalculateObservedRefreshRateHz(scroll));

    private static bool IsPowerMode(string value)
        => value.Equals("unsupported", StringComparison.Ordinal) ||
           value.Equals("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("961cc777-2547-4f9d-8174-7d86181b8a7a", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("ded574b5-45a0-4f42-8737-46345c09c238", StringComparison.OrdinalIgnoreCase);

    private static bool HasConsistentConfiguredPowerModeSupport(MachineMetadata machine)
        => IsPowerMode(machine.UserConfiguredAcPowerModeGuid) &&
           IsPowerMode(machine.UserConfiguredDcPowerModeGuid) &&
           (machine.UserConfiguredAcPowerModeGuid == "unsupported") ==
               (machine.UserConfiguredDcPowerModeGuid == "unsupported");

    private static bool IsEffectivePowerMode(string value)
        => value is "unsupported" or
           "BatterySaver" or
           "BetterBattery" or
           "Balanced" or
           "HighPerformance" or
           "MaxPerformance" or
           "GameMode" or
           "MixedReality";

    private static bool HasReleaseSampleRequirements(SampleRequirements requirements)
        => requirements.FirstViewportIterationsRequired ==
               PerformanceMeasurementContract.ReleaseFirstViewportIterations &&
           requirements.FirstViewportTrialsRequired ==
               PerformanceMeasurementContract.ReleaseFirstViewportTrials &&
           requirements.FirstViewportWarmupTrialsRequired ==
               PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials &&
           requirements.ScrollFramesRequired ==
               PerformanceMeasurementContract.ReleaseScrollFrames &&
           requirements.ScrollTrialsRequired ==
               PerformanceMeasurementContract.ReleaseScrollTrials &&
           requirements.LifecycleCyclesRequired ==
               PerformanceMeasurementContract.ReleaseLifecycleCycles &&
           requirements.CancellationIterationsPerTrialRequired ==
               PerformanceMeasurementContract.ReleaseCancellationIterations &&
           requirements.CancellationTrialsRequired ==
               PerformanceMeasurementContract.ReleaseCancellationTrials &&
           requirements.SourceLookupMappingCountRequired ==
               PerformanceMeasurementContract.SourceLookupMappingCount &&
           requirements.SourceLookupAbsoluteQueryCountRequired ==
               PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount &&
           requirements.SourceLookupRegressionWarmupPassesRequired ==
               PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses &&
           requirements.SourceLookupRegressionTrialsRequired ==
               PerformanceMeasurementContract.SourceLookupRegressionTrialCount &&
           requirements.SourceLookupRegressionBatchSizeRequired ==
               PerformanceMeasurementContract.SourceLookupRegressionBatchSize &&
           requirements.SourceLookupRegressionObservationCountRequired ==
               PerformanceMeasurementContract.SourceLookupRegressionObservationCount &&
           requirements.SourceLookupRegressionOperationCountRequired ==
               PerformanceMeasurementContract.SourceLookupRegressionOperationCount;

    internal static bool HasRequiredFirstViewportProtocol(
        IReadOnlyCollection<FirstViewportResult> results)
        => HasFirstViewportProtocol(
            results,
            PerformanceMeasurementContract.ReleaseFirstViewportIterations,
            PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials,
            PerformanceMeasurementContract.ReleaseFirstViewportTrials,
            quick: false);

    internal static bool HasRequiredQuickFirstViewportProtocol(
        IReadOnlyCollection<FirstViewportResult> results)
        => HasFirstViewportProtocol(
            results,
            PerformanceMeasurementContract.QuickFirstViewportIterations,
            PerformanceMeasurementContract.QuickFirstViewportWarmupTrials,
            PerformanceMeasurementContract.QuickFirstViewportTrials,
            quick: true);

    internal static bool HasSameFirstViewportExecutionEnvironment(
        IReadOnlyCollection<FirstViewportResult> candidate,
        IReadOnlyCollection<FirstViewportResult> reference)
    {
        if (!TryGetFirstViewportExecutionEnvironment(candidate, out var candidateEnvironment) ||
            !TryGetFirstViewportExecutionEnvironment(reference, out var referenceEnvironment))
        {
            return false;
        }

        return candidateEnvironment == referenceEnvironment;
    }

    internal static bool AreFirstViewportTrialsWithinReportInterval(
        IReadOnlyCollection<FirstViewportResult> results,
        DateTimeOffset reportStartedUtc,
        DateTimeOffset reportCompletedUtc)
    {
        if (results is null ||
            reportStartedUtc == default ||
            reportStartedUtc.Offset != TimeSpan.Zero ||
            reportCompletedUtc.Offset != TimeSpan.Zero ||
            reportCompletedUtc <= reportStartedUtc)
        {
            return false;
        }

        return results.SelectMany(static result =>
                result.WarmupTrials.Concat(result.Trials))
            .All(trial =>
                trial.StartedUtc > reportStartedUtc &&
                trial.CompletedUtc < reportCompletedUtc);
    }

    private static bool HasFirstViewportProtocol(
        IReadOnlyCollection<FirstViewportResult> results,
        int iterationsPerTrial,
        int warmupTrialCount,
        int measuredTrialCount,
        bool quick)
    {
        if (results is null ||
            results.Count != PerformanceMeasurementContract.FirstViewportConditionCount ||
            iterationsPerTrial <= 0 ||
            warmupTrialCount <= 0 ||
            measuredTrialCount <= 0)
        {
            return false;
        }

        int[] requiredSizes = [100 * 1024, 1024 * 1024, 10 * 1024 * 1024];
        string[] requiredModes =
        [
            PerformanceMeasurementContract.FirstViewportCacheDisabledMode,
            PerformanceMeasurementContract.FirstViewportCacheHitMode,
        ];
        var chronologicalTrials = new List<(FirstViewportTrialResult Trial, int Condition)>(
            checked((warmupTrialCount + measuredTrialCount) *
                    PerformanceMeasurementContract.FirstViewportConditionCount));
        (string AffinityMask, int ProcessorGroup, int ProcessorNumber, string Priority)?
            sharedThread = null;

        int condition = 0;
        foreach (int sourceBytes in requiredSizes)
        {
            foreach (string mode in requiredModes)
            {
                FirstViewportResult[] matches = results.Where(result =>
                        result.SourceUtf16Bytes == sourceBytes && result.Mode == mode)
                    .ToArray();
                if (matches.Length != 1)
                    return false;

                FirstViewportResult result = matches[0];
                if (!PinnedMeasurementThreadScope.IsEvidenceValid(
                        result.MeasurementThreadAffinityMask,
                        result.MeasurementProcessorGroup,
                        result.MeasurementProcessorNumber,
                        result.MeasurementThreadPriority,
                        ThreadPriority.Normal))
                {
                    return false;
                }

                var thread = (
                    result.MeasurementThreadAffinityMask,
                    result.MeasurementProcessorGroup,
                    result.MeasurementProcessorNumber,
                    result.MeasurementThreadPriority);
                sharedThread ??= thread;
                if (sharedThread.Value != thread)
                    return false;

                long expectedCacheBudget =
                    mode == PerformanceMeasurementContract.FirstViewportCacheHitMode
                        ? Math.Max(0, sourceBytes * 8L)
                        : 0;
                double expectedBudget = GetFirstViewportBudgetMilliseconds(sourceBytes, mode);
                if (result.Corpus != PerformanceDocumentFactory.GetCorpusName(sourceBytes) ||
                    result.ParseCacheBudgetBytes != expectedCacheBudget ||
                    result.EngineReuseMode !=
                        PerformanceMeasurementContract.FirstViewportEngineReuseMode ||
                    result.TrialStartPolicy !=
                        PerformanceMeasurementContract.FirstViewportTrialStartPolicy ||
                    result.SchedulePolicy != (quick
                        ? "quick-first-williams-row-non-gating"
                        : PerformanceMeasurementContract.FirstViewportSchedulePolicy) ||
                    result.RegressionEstimator != (quick
                        ? "quick-single-trial-non-gating"
                        : PerformanceMeasurementContract.FirstViewportRegressionEstimator) ||
                    result.StationarityPolicy != (quick
                        ? "quick-not-evaluated"
                        : PerformanceMeasurementContract.FirstViewportStationarityPolicy) ||
                    result.WarmupTrials is null ||
                    result.Trials is null ||
                    result.SamplesMilliseconds is null ||
                    result.PublicationSamplesMilliseconds is null ||
                    result.WarmupTrials.Count != warmupTrialCount ||
                    result.Trials.Count != measuredTrialCount)
                {
                    return false;
                }

                for (int row = 0; row < warmupTrialCount; row++)
                {
                    int position = GetFirstViewportSchedulePosition(condition, row);
                    int globalOrdinal = checked(
                        row * PerformanceMeasurementContract.FirstViewportConditionCount + position);
                    FirstViewportTrialResult trial = result.WarmupTrials[row];
                    if (!HasValidFirstViewportTrial(
                            trial,
                            mode,
                            iterationsPerTrial,
                            row,
                            globalOrdinal,
                            row,
                            position))
                    {
                        return false;
                    }
                    chronologicalTrials.Add((trial, condition));
                }

                for (int row = 0; row < measuredTrialCount; row++)
                {
                    int position = GetFirstViewportSchedulePosition(condition, row);
                    int globalOrdinal = checked(
                        warmupTrialCount *
                            PerformanceMeasurementContract.FirstViewportConditionCount +
                        row * PerformanceMeasurementContract.FirstViewportConditionCount +
                        position);
                    FirstViewportTrialResult trial = result.Trials[row];
                    if (!HasValidFirstViewportTrial(
                            trial,
                            mode,
                            iterationsPerTrial,
                            row,
                            globalOrdinal,
                            row,
                            position))
                    {
                        return false;
                    }
                    chronologicalTrials.Add((trial, condition));
                }

                double[] flattenedSamples = result.Trials
                    .SelectMany(static trial => trial.SamplesMilliseconds)
                    .ToArray();
                double[] publicationSamples = result.Trials
                    .SelectMany(static trial => trial.PublicationSamplesMilliseconds)
                    .ToArray();
                double publicationMaximum = publicationSamples.Length == 0
                    ? double.NaN
                    : publicationSamples.Max();
                double pooledP95 = PerformanceStatistics.Percentile(flattenedSamples, 0.95);
                double regressionP95 = PerformanceStatistics.HodgesLehmann(
                    result.Trials.Select(static trial => trial.P95Milliseconds));
                double dispersion = PerformanceStatistics.RobustDispersionPercent(
                    result.Trials.Select(static trial => trial.P95Milliseconds));
                double allowance = Math.Max(
                    PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
                        mode,
                        sourceBytes),
                    Math.Abs(regressionP95) *
                        PerformanceMeasurementContract.FirstViewportStationarityRelativeAllowancePercent /
                        100d);
                double slope = quick
                    ? 0
                    : PerformanceStatistics.TheilSenSlope(
                        result.Trials.Select(static trial =>
                            (trial.GlobalOrdinal, trial.P95Milliseconds)));
                double projectedDrift = quick
                    ? 0
                    : PerformanceStatistics.ProjectedTheilSenDrift(
                        result.Trials.Select(static trial =>
                            (trial.GlobalOrdinal, trial.P95Milliseconds)));
                double boundaryShift = quick
                    ? 0
                    : Math.Abs(
                        PerformanceStatistics.TwoValueCenter(
                            result.WarmupTrials[^2].P95Milliseconds,
                            result.WarmupTrials[^1].P95Milliseconds) -
                        PerformanceStatistics.TwoValueCenter(
                            result.Trials[0].P95Milliseconds,
                            result.Trials[1].P95Milliseconds));
                bool residualEnvelopePassed = quick ||
                    result.Trials.All(trial =>
                        Math.Abs(trial.P95Milliseconds - regressionP95) <= allowance) &&
                    result.WarmupTrials.TakeLast(2)
                        .Concat(result.Trials.Take(2))
                        .All(trial =>
                            Math.Abs(trial.P95Milliseconds - regressionP95) <= allowance);
                bool stationarityPassed = !quick &&
                    double.IsFinite(dispersion) &&
                    dispersion <= PerformanceMeasurementContract.FirstViewportDispersionBudgetPercent &&
                    double.IsFinite(slope) &&
                    double.IsFinite(projectedDrift) &&
                    Math.Abs(projectedDrift) <= allowance &&
                    double.IsFinite(boundaryShift) &&
                    boundaryShift <= allowance &&
                    residualEnvelopePassed;
                bool populationComplete =
                    result.WarmupTrials.All(static trial => trial.Complete) &&
                    result.Trials.All(static trial => trial.Complete) &&
                    flattenedSamples.Length == checked(iterationsPerTrial * measuredTrialCount) &&
                    publicationSamples.Length == checked(iterationsPerTrial * measuredTrialCount) &&
                    flattenedSamples.All(IsFiniteNonNegative) &&
                    publicationSamples.All(IsFiniteNonNegative) &&
                    IsFiniteNonNegative(pooledP95) &&
                    IsFiniteNonNegative(publicationMaximum) &&
                    IsFiniteNonNegative(regressionP95);
                bool expectedPassed = populationComplete &&
                    pooledP95 <= expectedBudget &&
                    (quick || publicationMaximum <=
                        PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds) &&
                    (quick || stationarityPassed);

                if (result.SamplesMilliseconds.Count != flattenedSamples.Length ||
                    !result.SamplesMilliseconds.SequenceEqual(flattenedSamples) ||
                    result.PublicationSamplesMilliseconds.Count != publicationSamples.Length ||
                    !result.PublicationSamplesMilliseconds.SequenceEqual(publicationSamples) ||
                    !NearlyEqual(result.PublicationMaximumMilliseconds, publicationMaximum) ||
                    result.PublicationBudgetMilliseconds !=
                        PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds ||
                    !NearlyEqual(result.P95Milliseconds, pooledP95) ||
                    !NearlyEqual(result.RegressionP95Milliseconds, regressionP95) ||
                    !NearlyEqual(result.RegressionDispersionPercent, dispersion) ||
                    !NearlyEqual(result.TheilSenSlopeMillisecondsPerGlobalOrdinal, slope) ||
                    !NearlyEqual(result.ProjectedDriftMilliseconds, projectedDrift) ||
                    !NearlyEqual(result.WarmupBoundaryShiftMilliseconds, boundaryShift) ||
                    !NearlyEqual(result.StationarityAllowanceMilliseconds, allowance) ||
                    result.StationarityEvaluated != !quick ||
                    result.StationarityPassed != stationarityPassed ||
                    result.BudgetMilliseconds != expectedBudget ||
                    result.Passed != expectedPassed ||
                    (!quick && !expectedPassed))
                {
                    return false;
                }

                condition++;
            }
        }

        return HasExactChronologicalFirstViewportSchedule(
            chronologicalTrials,
            warmupTrialCount,
            measuredTrialCount);
    }

    private static bool HasValidFirstViewportTrial(
        FirstViewportTrialResult trial,
        string mode,
        int iterationsPerTrial,
        int ordinal,
        int globalOrdinal,
        int scheduleRow,
        int schedulePosition)
    {
        if (trial is null || trial.SamplesMilliseconds is null ||
            trial.PublicationSamplesMilliseconds is null)
            return false;

        int presentations = checked(iterationsPerTrial + 1);
        bool cacheProof = mode switch
        {
            PerformanceMeasurementContract.FirstViewportCacheDisabledMode =>
                trial.CompletedParseCountBeforePrime == 0 &&
                trial.SourceKeyHashCountBeforePrime == 0 &&
                trial.CompletedParseCountAfterPrime == 0 &&
                trial.SourceKeyHashCountAfterPrime == 0 &&
                trial.CompletedParseCountAfterTrial == 0 &&
                trial.SourceKeyHashCountAfterTrial == presentations,
            PerformanceMeasurementContract.FirstViewportCacheHitMode =>
                trial.CompletedParseCountBeforePrime == 0 &&
                trial.SourceKeyHashCountBeforePrime == 0 &&
                trial.CompletedParseCountAfterPrime == 1 &&
                trial.SourceKeyHashCountAfterPrime == 1 &&
                trial.CompletedParseCountAfterTrial == 1 &&
                trial.SourceKeyHashCountAfterTrial == 1,
            _ => false,
        };
        double p95 = PerformanceStatistics.Percentile(trial.SamplesMilliseconds, 0.95);
        bool complete =
            trial.SamplesMilliseconds.Count == iterationsPerTrial &&
            trial.SamplesMilliseconds.All(IsFiniteNonNegative) &&
            trial.PublicationSamplesMilliseconds.Count == iterationsPerTrial &&
            trial.PublicationSamplesMilliseconds.All(IsFiniteNonNegative) &&
            IsFiniteNonNegative(trial.SettlingPublicationMilliseconds) &&
            NearlyEqual(
                trial.PublicationMaximumMilliseconds,
                trial.PublicationSamplesMilliseconds.Count == 0
                    ? double.NaN
                    : trial.PublicationSamplesMilliseconds.Max()) &&
            IsFiniteNonNegative(trial.SettlingElapsedMilliseconds) &&
            IsFiniteNonNegative(p95) &&
            trial.StartedUtc != default &&
            trial.CompletedUtc > trial.StartedUtc &&
            trial.StartedUtc.Offset == TimeSpan.Zero &&
            trial.CompletedUtc.Offset == TimeSpan.Zero &&
            trial.Gen0Collections >= 0 &&
            trial.Gen1Collections >= 0 &&
            trial.Gen2Collections >= 0 &&
            trial.Gen0Collections >= trial.Gen1Collections &&
            trial.Gen1Collections >= trial.Gen2Collections &&
            trial.ProcessAllocatedBytes >= 0 &&
            cacheProof;

        return trial.Ordinal == ordinal &&
               trial.GlobalOrdinal == globalOrdinal &&
               trial.ScheduleRow == scheduleRow &&
               trial.SchedulePosition == schedulePosition &&
               NearlyEqual(trial.P95Milliseconds, p95) &&
               trial.CacheProofPassed == cacheProof &&
               trial.Complete == complete &&
               complete;
    }

    private static bool HasExactChronologicalFirstViewportSchedule(
        IReadOnlyCollection<(FirstViewportTrialResult Trial, int Condition)> observations,
        int warmupTrialCount,
        int measuredTrialCount)
    {
        int conditionCount = PerformanceMeasurementContract.FirstViewportConditionCount;
        int warmupPopulation = checked(warmupTrialCount * conditionCount);
        int expectedPopulation = checked((warmupTrialCount + measuredTrialCount) * conditionCount);
        if (observations.Count != expectedPopulation)
            return false;

        (FirstViewportTrialResult Trial, int Condition)[] ordered = observations
            .OrderBy(static observation => observation.Trial.GlobalOrdinal)
            .ToArray();
        for (int globalOrdinal = 0; globalOrdinal < ordered.Length; globalOrdinal++)
        {
            (FirstViewportTrialResult trial, int condition) = ordered[globalOrdinal];
            bool warmup = globalOrdinal < warmupPopulation;
            int phaseOrdinal = warmup ? globalOrdinal : globalOrdinal - warmupPopulation;
            int row = phaseOrdinal / conditionCount;
            int position = phaseOrdinal % conditionCount;
            if (trial.GlobalOrdinal != globalOrdinal ||
                trial.Ordinal != row ||
                trial.ScheduleRow != row ||
                trial.SchedulePosition != position ||
                condition != PerformanceMeasurementContract.GetFirstViewportCondition(row, position) ||
                (globalOrdinal > 0 &&
                 trial.StartedUtc <= ordered[globalOrdinal - 1].Trial.CompletedUtc))
            {
                return false;
            }
        }

        return true;
    }

    private static int GetFirstViewportSchedulePosition(int condition, int row)
    {
        for (int position = 0;
             position < PerformanceMeasurementContract.FirstViewportConditionCount;
             position++)
        {
            if (PerformanceMeasurementContract.GetFirstViewportCondition(row, position) == condition)
                return position;
        }

        throw new UnreachableException("The frozen Williams row omitted a first-viewport condition.");
    }

    private static double GetFirstViewportBudgetMilliseconds(int sourceBytes, string mode)
        => sourceBytes switch
        {
            100 * 1024 when mode == PerformanceMeasurementContract.FirstViewportCacheHitMode => 75,
            100 * 1024 => 125,
            1024 * 1024 => 250,
            10 * 1024 * 1024 => 750,
            _ => double.NaN,
        };

    private static bool TryGetFirstViewportExecutionEnvironment(
        IReadOnlyCollection<FirstViewportResult> results,
        out (string AffinityMask, int ProcessorGroup, int ProcessorNumber, string Priority) environment)
    {
        environment = default;
        if (results is null ||
            results.Count != PerformanceMeasurementContract.FirstViewportConditionCount)
        {
            return false;
        }

        FirstViewportResult first = results.First();
        environment = (
            first.MeasurementThreadAffinityMask,
            first.MeasurementProcessorGroup,
            first.MeasurementProcessorNumber,
            first.MeasurementThreadPriority);
        var expected = environment;
        return PinnedMeasurementThreadScope.IsEvidenceValid(
                   environment.AffinityMask,
                   environment.ProcessorGroup,
                   environment.ProcessorNumber,
                   environment.Priority,
                   ThreadPriority.Normal) &&
               results.All(result =>
                   result.MeasurementThreadAffinityMask == expected.AffinityMask &&
                   result.MeasurementProcessorGroup == expected.ProcessorGroup &&
                   result.MeasurementProcessorNumber == expected.ProcessorNumber &&
                   result.MeasurementThreadPriority == expected.Priority);
    }

    private static bool HasCompleteScrollEvidence(ScrollResult scroll)
    {
        if (scroll.RegressionEstimator != PerformanceMeasurementContract.ScrollRegressionEstimator ||
            !ScrollEvidenceValidator.TryValidate(
                scroll,
                PerformanceMeasurementContract.ReleaseScrollFrames,
                PerformanceMeasurementContract.ReleaseScrollTrials,
                out ScrollEvidenceStatistics statistics))
        {
            return false;
        }

        bool diagnosticsValid =
            scroll.Corpus == PerformanceDocumentFactory.WarmScrollStressCorpus &&
            scroll.SourceUtf16Bytes == 1024 * 1024 &&
            scroll.StopwatchFrequency == Stopwatch.Frequency &&
            scroll.RawAllocationScope == RawAllocationScope &&
            scroll.AllocationGateBasis == AllocationGateBasis &&
            scroll.PlatformProjectionAllocationScope == PlatformProjectionAllocationScope &&
            AllFiniteNonNegative(
                scroll.ScrollCallbackWorkP95Milliseconds,
                scroll.PaintCallbackWorkP95Milliseconds,
                scroll.AllocatedBytesPerFrameP95,
                scroll.PlatformProjectionAllocatedBytesPerFrameP95,
                scroll.ScrollAllocatedBytesPerFrameP95,
                scroll.PaintAllocatedBytesPerFrameP95,
                scroll.ScrollLazyLayoutAllocatedBytesP95,
                scroll.ScrollAdornerAllocatedBytesP95,
                scroll.ScrollRealizationAllocatedBytesP95,
                scroll.ScrollHighlightingAllocatedBytesP95,
                scroll.PaintSchedulingAllocatedBytesP95,
                scroll.PaintPlatformSessionAllocatedBytesP95,
                scroll.SnapshotPaintAllocatedBytesP95,
                scroll.InteractivePaintAllocatedBytesP95,
                scroll.InlinePaintAllocatedBytesP95,
                scroll.TablePaintAllocatedBytesP95,
                scroll.CodePaintAllocatedBytesP95,
                scroll.OtherPaintAllocatedBytesP95) &&
            scroll.MaximumAllocatedBytesPerFrame >= 0 &&
            scroll.MaximumPlatformProjectionAllocatedBytesPerFrame >= 0 &&
            scroll.MaximumRealizedCodeActions >= 0 &&
            scroll.MaximumOffscreenRealizedCodeActions == 0 &&
            scroll.Gen2Collections == 0 &&
            scroll.Trials.All(static trial =>
                AllFiniteNonNegative(
                    trial.ScrollCallbackWorkP95Milliseconds,
                    trial.PaintCallbackWorkP95Milliseconds,
                    trial.AllocatedBytesPerFrameP95,
                    trial.PlatformProjectionAllocatedBytesPerFrameP95,
                    trial.ScrollAllocatedBytesPerFrameP95,
                    trial.PaintAllocatedBytesPerFrameP95,
                    trial.ScrollLazyLayoutAllocatedBytesP95,
                    trial.ScrollAdornerAllocatedBytesP95,
                    trial.ScrollRealizationAllocatedBytesP95,
                    trial.ScrollHighlightingAllocatedBytesP95,
                    trial.PaintSchedulingAllocatedBytesP95,
                    trial.PaintPlatformSessionAllocatedBytesP95,
                    trial.SnapshotPaintAllocatedBytesP95,
                    trial.InteractivePaintAllocatedBytesP95,
                    trial.InlinePaintAllocatedBytesP95,
                    trial.TablePaintAllocatedBytesP95,
                    trial.CodePaintAllocatedBytesP95,
                    trial.OtherPaintAllocatedBytesP95) &&
                trial.MaximumAllocatedBytesPerFrame >= 0 &&
                trial.MaximumPlatformProjectionAllocatedBytesPerFrame >= 0 &&
                trial.MaximumRealizedCodeActions >= 0 &&
                trial.MaximumOffscreenRealizedCodeActions == 0 &&
                trial.Gen2Collections == 0);

        return diagnosticsValid &&
               PerformanceStatistics.RobustDispersionPercent(
                   statistics.Trials.Select(static trial => trial.UiThreadWorkP95Milliseconds)) <=
                   PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent &&
               PerformanceStatistics.RobustDispersionPercent(
                   statistics.Trials.Select(static trial => trial.UiThreadWorkP99Milliseconds)) <=
                   PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent &&
               PerformanceStatistics.RobustDispersionPercent(
                   statistics.Trials.Select(static trial => trial.FrameTimeP95Milliseconds)) <=
                   PerformanceMeasurementContract.ScrollFrameTimeDispersionBudgetPercent &&
               PerformanceStatistics.RobustDispersionPercent(
                   statistics.Trials.Select(
                       static trial => trial.RendererOwnedAllocatedBytesPerFrameP95)) <=
                   PerformanceMeasurementContract.AllocationDispersionBudgetPercent &&
               scroll.UiThreadWorkP95BudgetMilliseconds == 4 &&
               scroll.UiThreadWorkP99BudgetMilliseconds == 6 &&
               scroll.FrameTimeP95BudgetMilliseconds ==
                   PerformanceMeasurementContract.ScrollFrameTimeP95BudgetMilliseconds &&
               scroll.FramesOver16_7PercentBudget == 1 &&
               scroll.MaximumFrameStallBudgetMilliseconds == 50 &&
               scroll.AllocationBudgetBytesPerFrame == 4096 &&
               statistics.UiThreadWorkP95Milliseconds <=
                   scroll.UiThreadWorkP95BudgetMilliseconds &&
               statistics.UiThreadWorkP99Milliseconds <=
                   scroll.UiThreadWorkP99BudgetMilliseconds &&
               statistics.FrameTimeP95Milliseconds <=
                   scroll.FrameTimeP95BudgetMilliseconds &&
               statistics.FramesOver16_7Percent < scroll.FramesOver16_7PercentBudget &&
               statistics.MaximumFrameStallMilliseconds <=
                   scroll.MaximumFrameStallBudgetMilliseconds &&
               statistics.MaximumRendererOwnedAllocatedBytesPerFrame <=
                   scroll.AllocationBudgetBytesPerFrame &&
               !statistics.RendererOwnedLohAllocationPossible &&
               scroll.Passed;
    }

    private static bool HasCompleteRetainedMemoryEvidence(
        IReadOnlyCollection<RetainedMemoryResult> results)
    {
        int[] requiredSizes = [100 * 1024, 1024 * 1024, 10 * 1024 * 1024];
        if (results.Count != requiredSizes.Length)
            return false;

        return requiredSizes.All(sourceBytes =>
        {
            RetainedMemoryResult[] matches = results
                .Where(result => result.SourceUtf16Bytes == sourceBytes)
                .ToArray();
            long expectedBudget = checked(sourceBytes * 8L + 32L * 1024 * 1024);
            return matches.Length == 1 &&
                   matches[0].Corpus == PerformanceDocumentFactory.GetCorpusName(sourceBytes) &&
                   matches[0].ManagedDeltaBytes >= 0 &&
                   matches[0].PrivateDeltaBytes >= 0 &&
                   matches[0].BudgetBytes == expectedBudget &&
                   matches[0].ManagedDeltaBytes <= expectedBudget &&
                   matches[0].PrivateDeltaBytes <= expectedBudget &&
                   matches[0].Passed;
        });
    }

    private static bool HasCompleteLifecycleEvidence(LifecyclePlateauResult lifecycle)
    {
        double managedGrowth = PerformanceStatistics.GrowthPercent(
            lifecycle.Checkpoint100ManagedBytes,
            lifecycle.Checkpoint80ManagedBytes);
        double privateGrowth = PerformanceStatistics.GrowthPercent(
            lifecycle.Checkpoint100PrivateBytes,
            lifecycle.Checkpoint80PrivateBytes);
        return lifecycle.RequiredCycles == PerformanceMeasurementContract.ReleaseLifecycleCycles &&
               lifecycle.CompletedCycles == lifecycle.RequiredCycles &&
               lifecycle.SyntheticDeviceResets == 4 &&
               lifecycle.SyntheticDeviceResets == lifecycle.CompletedCycles / 25 &&
               lifecycle.DeviceResetMode == LifecycleDeviceResetMode &&
               lifecycle.ActualDeviceLossEvents >= 0 &&
               lifecycle.Checkpoint80ManagedBytes > 0 &&
               lifecycle.Checkpoint100ManagedBytes > 0 &&
               lifecycle.Checkpoint80PrivateBytes > 0 &&
               lifecycle.Checkpoint100PrivateBytes > 0 &&
               IsFiniteNonNegative(lifecycle.ManagedGrowthPercent) &&
               IsFiniteNonNegative(lifecycle.PrivateGrowthPercent) &&
               IsFiniteNonNegative(lifecycle.GrowthPercent) &&
               NearlyEqual(lifecycle.ManagedGrowthPercent, managedGrowth) &&
               NearlyEqual(lifecycle.PrivateGrowthPercent, privateGrowth) &&
               NearlyEqual(lifecycle.GrowthPercent, Math.Max(managedGrowth, privateGrowth)) &&
               lifecycle.GrowthBudgetPercent == 5 &&
               lifecycle.GrowthPercent <= lifecycle.GrowthBudgetPercent &&
               lifecycle.RenderFailures == 0 &&
               lifecycle.Passed;
    }

    private static bool HasCompleteSourceLookupEvidence(SourceLookupResult lookup)
    {
        if (lookup.MappingCount != PerformanceMeasurementContract.SourceLookupMappingCount ||
            lookup.QueryCount != PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount ||
            lookup.StopwatchFrequency != Stopwatch.Frequency ||
            !PinnedMeasurementThreadScope.IsEvidenceValid(
                lookup.MeasurementThreadAffinityMask,
                lookup.MeasurementProcessorGroup,
                lookup.MeasurementProcessorNumber,
                lookup.MeasurementThreadPriority,
                ThreadPriority.Highest) ||
            lookup.SamplesElapsedTicks.Count !=
                PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount ||
            lookup.SamplesElapsedTicks.Any(static ticks => ticks < 0) ||
            !IsFinitePositive(lookup.P95Nanoseconds) ||
            !NearlyEqual(
                lookup.P95Nanoseconds,
                PerformanceStatistics.Percentile(lookup.SamplesElapsedTicks, 0.95) *
                (1_000_000_000.0 / lookup.StopwatchFrequency)) ||
            lookup.P95BudgetNanoseconds != 25_000 ||
            lookup.P95Nanoseconds > lookup.P95BudgetNanoseconds ||
            lookup.AllocatedBytes != 0 ||
            lookup.RegressionWarmupPasses !=
                PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses ||
            lookup.RegressionEstimator !=
                PerformanceMeasurementContract.SourceLookupRegressionEstimator ||
            lookup.RegressionTrialCount !=
                PerformanceMeasurementContract.SourceLookupRegressionTrialCount ||
            lookup.RegressionBatchSize !=
                PerformanceMeasurementContract.SourceLookupRegressionBatchSize ||
            lookup.RegressionObservationCount !=
                PerformanceMeasurementContract.SourceLookupRegressionObservationCount ||
            lookup.RegressionOperationCount !=
                PerformanceMeasurementContract.SourceLookupRegressionOperationCount ||
            lookup.RegressionTrials.Count !=
                PerformanceMeasurementContract.SourceLookupRegressionTrialCount ||
            !lookup.RegressionTrials.Select(static trial => trial.Ordinal).SequenceEqual(
                Enumerable.Range(
                    0,
                    PerformanceMeasurementContract.SourceLookupRegressionTrialCount)))
        {
            return false;
        }

        double nanosecondsPerTick = 1_000_000_000.0 / lookup.StopwatchFrequency;
        foreach (SourceLookupTrialResult trial in lookup.RegressionTrials)
        {
            if (trial.ObservationCount !=
                    PerformanceMeasurementContract.SourceLookupRegressionObservationCount ||
                trial.OperationCount !=
                    PerformanceMeasurementContract.SourceLookupRegressionOperationCount ||
                trial.SamplesElapsedTicks.Count != trial.ObservationCount ||
                trial.SamplesNanosecondsPerLookup.Count != trial.ObservationCount ||
                !trial.SamplesElapsedTicks.Zip(
                        trial.SamplesNanosecondsPerLookup,
                        (ticks, nanoseconds) =>
                            ticks > 0 &&
                            IsFinitePositive(nanoseconds) &&
                            NearlyEqual(
                                nanoseconds,
                                ticks * nanosecondsPerTick /
                                PerformanceMeasurementContract.SourceLookupRegressionBatchSize))
                    .All(static matches => matches) ||
                !NearlyEqual(
                    trial.P95Nanoseconds,
                    PerformanceStatistics.Percentile(
                        trial.SamplesNanosecondsPerLookup,
                        0.95)) ||
                trial.AllocatedBytes != 0 ||
                !trial.Complete)
            {
                return false;
            }
        }

        return lookup.RegressionAllocatedBytes == 0 &&
               NearlyEqual(
                   lookup.RegressionP95Nanoseconds,
                   PerformanceStatistics.HodgesLehmann(
                       lookup.RegressionTrials.Select(
                           static trial => trial.P95Nanoseconds))) &&
               PerformanceStatistics.RobustDispersionPercent(
                   lookup.RegressionTrials.Select(static trial => trial.P95Nanoseconds)) <=
                   PerformanceMeasurementContract.SourceLookupDispersionBudgetPercent &&
               lookup.Passed;
    }

    private static bool HasCompleteCancellationEvidence(CancellationResult cancellation)
        => cancellation.TrialStartPolicy ==
               PerformanceMeasurementContract.CancellationTrialStartPolicy &&
           cancellation.RegressionEstimator ==
               PerformanceMeasurementContract.CancellationRegressionEstimator &&
           CancellationEvidenceValidator.TryValidate(
               cancellation,
               PerformanceMeasurementContract.ReleaseCancellationIterations,
               PerformanceMeasurementContract.ReleaseCancellationTrials,
               out CancellationEvidenceStatistics statistics) &&
           statistics.WarmupP95Milliseconds <=
               CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
           statistics.PooledP95Milliseconds <=
               CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
           statistics.WarmupStaleCommits == 0 &&
           statistics.StaleCommits == 0 &&
           statistics.RegressionDispersionPercent <=
               PerformanceMeasurementContract.CancellationDispersionBudgetPercent &&
           cancellation.P95BudgetMilliseconds ==
               CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
           cancellation.Trials.All(trial =>
               trial.WarmupElapsedMilliseconds <=
                   CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
               trial.WarmupStaleCommits == 0 &&
               trial.P95Milliseconds <=
                   CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
               trial.StaleCommits == 0) &&
           cancellation.Passed;

    private static bool IsGpuAdapterLuid(string? value)
    {
        if (value is null)
            return false;

        string[] parts = value.Split(':');
        return parts.Length == 2 &&
               parts.All(static part =>
                   part.Length == 8 &&
                   uint.TryParse(
                       part,
                       NumberStyles.AllowHexSpecifier,
                       CultureInfo.InvariantCulture,
                       out _));
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } &&
           value.All(static character =>
               character is >= '0' and <= '9' or
               >= 'a' and <= 'f' or
               >= 'A' and <= 'F');

    private static bool AllFiniteNonNegative(params double[] values)
        => values.All(IsFiniteNonNegative);

    private static bool IsFinitePositive(double value)
        => double.IsFinite(value) && value > 0;

    private static bool IsFiniteNonNegative(double value)
        => double.IsFinite(value) && value >= 0;

    private static bool NearlyEqual(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right))
            return false;
        double scale = Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= scale * 1e-12;
    }
}
