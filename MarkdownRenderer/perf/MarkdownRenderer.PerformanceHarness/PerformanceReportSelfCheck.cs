using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Deployment;
using MarkdownRenderer.PerformanceHarness.Core.Events;
using MarkdownRenderer.PerformanceHarness.Core.Evidence;
using MarkdownRenderer.PerformanceHarness.Core.Reports;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Forms the run's verdict: checks the frozen report against the invocation,
/// its build, runtime and machine evidence, and every gate, adds a failure
/// for each violation, and sets <see cref="PerformanceReport.Passed"/>. A
/// quick run applies the non-gating smoke checks instead of the release
/// gates.
/// </summary>
internal sealed class PerformanceReportSelfCheck
{
    private readonly PerformanceReport _report;
    private readonly PerformanceOptions _options;
    private readonly PerformanceEventListener _events;
    private readonly MeasurementRendererHost _rendererHost;

    internal PerformanceReportSelfCheck(
        PerformanceReport report,
        PerformanceOptions options,
        PerformanceEventListener events,
        MeasurementRendererHost rendererHost)
    {
        _report = report;
        _options = options;
        _events = events;
        _rendererHost = rendererHost;
    }

    internal void Validate()
    {
        if (!_options.Quick)
        {
            foreach (string error in _events.ProviderErrors)
                _report.Failures.Add($"ETW provider error: {error}");
        }

        if (_report.SchemaVersion != PerformanceReport.CurrentSchemaVersion)
            _report.Failures.Add("Performance report schema version was not the current version.");
        if (_report.ProviderName != PerformanceMeasurementContract.ProviderName)
            _report.Failures.Add("Performance report provider name was not the frozen provider.");
        if (!PerformanceDeploymentIdentity.IsWellFormed(_report.BuildIdentity))
        {
            _report.Failures.Add(
                "Performance report build identity was not a runtime-output-manifest-v1 digest.");
        }
        if (_report.IsReleaseEvidence == _options.Quick)
            _report.Failures.Add("Performance report release-evidence mode did not match the invocation mode.");
        ValidateSampleRequirements();
        ValidateBuildArtifacts();
        ValidateRuntimeConfiguration();
        ValidateMachineMetadata();

        if (_options.Quick)
        {
            ValidateQuickSmoke();
            _report.Passed = !_report.IsReleaseEvidence && _report.Failures.Count == 0;
            return;
        }

        ValidateReleaseGates();
        _report.Passed = _report.IsReleaseEvidence && _report.Failures.Count == 0;
    }

    private void ValidateReleaseGates()
    {
        if (!FirstViewportEvidenceValidator.HasRequiredFirstViewportProtocol(
                _report.FirstUsableViewport))
        {
            _report.Failures.Add(
                "First-viewport cache-disabled/cache-hit evidence violated its Williams schedule, raw trial, pinned-thread, cache-proof, stationarity, UI-publication, or absolute-budget contract.");
        }

        if (!ScrollResultCheck.IsCompleteAndPassing(_report))
            _report.Failures.Add("Warm 120 Hz scroll frame/work/allocation/GC gate failed.");
        RetainedMemoryResultCheck.AddFailures(_report);
        if (!LifecyclePlateauResultCheck.IsCompleteAndPassing(_report))
            _report.Failures.Add("The 100-cycle retained-memory plateau gate failed.");
        if (!SourceLookupResultCheck.IsCompleteAndPassing(_report))
            _report.Failures.Add("The 100,000-entry source-map lookup gate failed.");
        if (!CancellationResultCheck.IsCompleteAndPassing(_report))
            _report.Failures.Add("The cancellation/stale-commit gate failed.");
        if (!RegressionResultCheck.IsCompleteAndPassing(_report, _options))
            _report.Failures.Add(
                "The same-machine hybrid >5% latency/>2% allocation regression gate failed, was noisy, or lacked a reference.");
        if (!MachineEvidenceValidator.HasQualifiedRefresh(_report.Machine))
        {
            _report.Failures.Add(
                $"The active display did not provide measured 120 Hz evidence " +
                $"(configured {_report.Machine.ConfiguredRefreshRateHz:N2} Hz, " +
                $"observed {_report.Machine.ObservedRefreshRateHz:N2} Hz).");
        }

        if (!PerformanceReleaseEvidenceValidator.HasCompleteMeasurementPayload(_report))
        {
            _report.Failures.Add(
                "The release report did not satisfy the complete self-verifying evidence contract.");
        }

        if (!MeasurementVisibilityEvidenceValidator.IsValid(
                _report.MeasurementVisibility,
                _report.StartedUtc,
                _report.CompletedUtc,
                out string visibilityFailure))
        {
            _report.Failures.Add($"Measurement desktop visibility gate failed: {visibilityFailure}");
        }
    }

    private void ValidateBuildArtifacts()
    {
        if (!PerformanceArtifactContract.TryValidateCurrentFiles(
                _report.BuildArtifacts,
                out string failure))
        {
            _report.Failures.Add(failure);
            return;
        }

        if (!PerformanceDeploymentIdentity.TryValidateCurrent(
                _report.BuildIdentity,
                _report.BuildArtifacts.Executable.Path,
                out failure))
        {
            _report.Failures.Add(failure);
        }
    }

    private void ValidateRuntimeConfiguration()
    {
        if (!PerformanceRuntimeContract.IsValid(_report.RuntimeConfiguration))
        {
            _report.Failures.Add(
                "Runtime configuration did not disable tiering, PGO, concurrent GC, and ReadyToRun or contained DOTNET_/COMPlus_ overrides.");
        }
    }

    private void ValidateSampleRequirements()
    {
        SampleRequirements requirements = _report.SampleRequirements;
        int expectedFirstViewportIterations = _options.Quick
            ? PerformanceMeasurementContract.QuickFirstViewportIterations
            : PerformanceMeasurementContract.ReleaseFirstViewportIterations;
        int expectedScrollFrames = _options.Quick
            ? PerformanceMeasurementContract.QuickScrollFrames
            : PerformanceMeasurementContract.ReleaseScrollFrames;
        int expectedFirstViewportTrials = _options.Quick
            ? PerformanceMeasurementContract.QuickFirstViewportTrials
            : PerformanceMeasurementContract.ReleaseFirstViewportTrials;
        int expectedFirstViewportWarmupTrials = _options.Quick
            ? PerformanceMeasurementContract.QuickFirstViewportWarmupTrials
            : PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials;
        int expectedScrollTrials = _options.Quick
            ? PerformanceMeasurementContract.QuickScrollTrials
            : PerformanceMeasurementContract.ReleaseScrollTrials;
        int expectedLifecycleCycles = _options.Quick
            ? PerformanceMeasurementContract.QuickLifecycleCycles
            : PerformanceMeasurementContract.ReleaseLifecycleCycles;
        int expectedCancellationIterations = _options.Quick
            ? PerformanceMeasurementContract.QuickCancellationIterations
            : PerformanceMeasurementContract.ReleaseCancellationIterations;
        int expectedCancellationTrials = _options.Quick
            ? PerformanceMeasurementContract.QuickCancellationTrials
            : PerformanceMeasurementContract.ReleaseCancellationTrials;
        if (requirements.FirstViewportIterationsRequired !=
                expectedFirstViewportIterations ||
            requirements.FirstViewportWarmupTrialsRequired !=
                expectedFirstViewportWarmupTrials ||
            requirements.FirstViewportTrialsRequired != expectedFirstViewportTrials ||
            requirements.ScrollFramesRequired !=
                expectedScrollFrames ||
            requirements.ScrollTrialsRequired !=
                expectedScrollTrials ||
            requirements.LifecycleCyclesRequired !=
                expectedLifecycleCycles ||
            requirements.CancellationIterationsPerTrialRequired !=
                expectedCancellationIterations ||
            requirements.CancellationTrialsRequired != expectedCancellationTrials ||
            requirements.SourceLookupMappingCountRequired !=
                PerformanceMeasurementContract.SourceLookupMappingCount ||
            requirements.SourceLookupAbsoluteQueryCountRequired !=
                PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount ||
            requirements.SourceLookupRegressionWarmupPassesRequired !=
                PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses ||
            requirements.SourceLookupRegressionTrialsRequired !=
                PerformanceMeasurementContract.SourceLookupRegressionTrialCount ||
            requirements.SourceLookupRegressionBatchSizeRequired !=
                PerformanceMeasurementContract.SourceLookupRegressionBatchSize ||
            requirements.SourceLookupRegressionObservationCountRequired !=
                PerformanceMeasurementContract.SourceLookupRegressionObservationCount ||
            requirements.SourceLookupRegressionOperationCountRequired !=
                PerformanceMeasurementContract.SourceLookupRegressionOperationCount)
        {
            _report.Failures.Add("Performance sample requirements did not match the frozen invocation contract.");
        }
    }

    private void ValidateQuickSmoke()
    {
        foreach (string failure in QuickPerformanceSmokeValidator.Validate(
                     _report,
                     _rendererHost.RenderFailures,
                     _events.ProviderErrors))
        {
            _report.Failures.Add(failure);
        }
    }

    private void ValidateMachineMetadata()
    {
        if (!MachineEvidenceValidator.IsMachineMetadataComplete(_report.Machine))
        {
            _report.Failures.Add(
                "Machine fingerprint/GPU/driver/power/scheduler/GC/display metadata was incomplete or inconsistent.");
        }
        if (!MachineEvidenceValidator.IsEnvironmentSnapshotComplete(
                _report.CompletionMachine))
        {
            _report.Failures.Add(
                "Completion machine/environment metadata was incomplete or inconsistent.");
        }
        if (!MachineEvidenceValidator.IsSameRunEnvironment(
                _report.Machine,
                _report.CompletionMachine))
        {
            _report.Failures.Add(
                "The display, graphics, power, scheduler, GC, DPI, or viewport environment changed during measurement.");
        }
        if (!_options.Quick &&
            !MachineEvidenceValidator.IsReleasePowerEnvironment(_report.Machine))
        {
            _report.Failures.Add(
                "Release evidence requires AC power with energy saver disabled.");
        }
    }
}
