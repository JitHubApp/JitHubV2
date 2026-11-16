using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Deployment;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Scenarios;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;

using static MarkdownRenderer.PerformanceHarness.Core.Evidence.EvidenceValueRules;

namespace MarkdownRenderer.PerformanceHarness.Core.Evidence;

/// <summary>
/// Validates the complete, self-contained measurement payload used by release
/// regression comparisons. The producer, in-process evaluator, and external
/// gate must agree on these invariants; a top-level <c>passed</c> flag is never
/// accepted as a substitute for recomputable evidence.
/// </summary>
public static class PerformanceReleaseEvidenceValidator
{
    private const double LatencyRegressionBudgetPercent = 5;
    private const double AllocationRegressionBudgetPercent = 2;
    private const string LifecycleDeviceResetMode =
        "snapshot-release-and-canvas-device-trim";

    public static bool HasCompleteMeasurementPayload(PerformanceReport report)
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
           MeasurementVisibilityEvidenceValidator.IsValid(
               report.MeasurementVisibility,
               report.StartedUtc,
               report.CompletedUtc,
               out _) &&
           HasReleaseSampleRequirements(report.SampleRequirements) &&
           MachineEvidenceValidator.IsMachineMetadataComplete(report.Machine) &&
           MachineEvidenceValidator.IsEnvironmentSnapshotComplete(report.CompletionMachine) &&
           MachineEvidenceValidator.IsSameRunEnvironment(report.Machine, report.CompletionMachine) &&
           MachineEvidenceValidator.IsReleasePowerEnvironment(report.Machine) &&
           MachineEvidenceValidator.HasQualifiedRefresh(report.Machine) &&
           FirstViewportEvidenceValidator.HasRequiredFirstViewportProtocol(report.FirstUsableViewport) &&
           FirstViewportEvidenceValidator.AreFirstViewportTrialsWithinReportInterval(
               report.FirstUsableViewport,
               report.StartedUtc,
               report.CompletedUtc) &&
           ScrollReleaseEvidenceValidator.HasCompleteReleaseEvidence(report.Scroll) &&
           MachineEvidenceValidator.HasObservedRefreshEvidence(report.Machine, report.Scroll) &&
           HasCompleteRetainedMemoryEvidence(report.RetainedMemory) &&
           HasCompleteLifecycleEvidence(report.LifecyclePlateau) &&
           SourceLookupEvidenceValidator.HasCompleteReleaseEvidence(report.SourceLookup) &&
           HasCompleteCancellationEvidence(report.Cancellation);

    public static bool HasCompleteCurrentMeasurementPayload(PerformanceReport report)
        => HasCompleteMeasurementPayload(report) &&
           PerformanceArtifactContract.TryValidateCurrentFiles(
               report.BuildArtifacts,
               out _) &&
           PerformanceDeploymentIdentity.TryValidateCurrent(
               report.BuildIdentity,
               report.BuildArtifacts.Executable.Path,
               out _);

    public static bool IsEligibleBaselineReference(PerformanceReport report)
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
}
