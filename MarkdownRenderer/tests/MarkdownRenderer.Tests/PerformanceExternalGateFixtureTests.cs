using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Runs the repository's external performance-gate script against a
/// release-shaped baseline/candidate pair (<see cref="PerformanceGateEvidence"/>)
/// and checks that it accepts the committed edge values and rejects each named
/// evidence mutation in <see cref="PerformanceGateMutationCatalog"/>.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because the rejection theories list
/// every named mutation as its own [InlineData] case, so each one is reported
/// and rerun on its own; the mutations themselves live in the
/// PerformanceGate*Mutations tables.
/// </remarks>
public sealed class PerformanceExternalGateFixtureTests
{
    [Fact]
    public void CommittedSchema12FixturePassesBaselineAndCandidateAtExactInclusiveEdges()
    {
        using var evidence = PerformanceGateEvidence.Create();
        PerformanceGateResult baseline = evidence.RunGate(evidence.BaselinePath, "Baseline");
        PerformanceGateResult candidate = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.True(baseline.Passed, baseline.Output);
        Assert.True(candidate.Passed, candidate.Output);
    }

    [Fact]
    public void SourceLookupDiagnosticTrialTelemetryIsAcceptedWhenPresentOrAbsent()
    {
        using var evidence = PerformanceGateEvidence.Create();
        PerformanceGateResult populated = evidence.RunGate(evidence.CandidatePath, "Candidate");
        Assert.True(populated.Passed, populated.Output);

        evidence.ApplyMutation("source-lookup-diagnostic-unsupported-counters");
        PerformanceGateResult unavailableCounters = evidence.RunGate(evidence.CandidatePath, "Candidate");
        Assert.True(unavailableCounters.Passed, unavailableCounters.Output);

        evidence.ApplyMutation("source-lookup-diagnostic-fields-absent");
        PerformanceGateResult legacyShape = evidence.RunGate(evidence.CandidatePath, "Candidate");
        Assert.True(legacyShape.Passed, legacyShape.Output);
    }

    [Theory]
    [InlineData("source-lookup-trial-unknown-member")]
    [InlineData("reference-source-lookup-trial-unknown-member")]
    public void StrictSchemaRejectsUnknownSourceLookupTrialProperties(string mutation)
    {
        using var evidence = PerformanceGateEvidence.Create();
        evidence.ApplyMutation(mutation);

        PerformanceGateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.False(result.Passed, result.Output);
        Assert.Contains("unmapped JSON", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unexpectedDiagnostic", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("source-lookup-diagnostic-timestamp-order")]
    [InlineData("source-lookup-diagnostic-timestamp-pair")]
    [InlineData("source-lookup-diagnostic-elapsed-negative")]
    [InlineData("source-lookup-diagnostic-elapsed-fractional")]
    [InlineData("source-lookup-diagnostic-elapsed-shorter-than-samples")]
    [InlineData("source-lookup-diagnostic-cpu-negative")]
    [InlineData("source-lookup-diagnostic-cpu-fractional")]
    [InlineData("source-lookup-diagnostic-cpu-exceeds-stopwatch")]
    [InlineData("source-lookup-diagnostic-cycles-negative")]
    [InlineData("source-lookup-diagnostic-cycles-fractional")]
    [InlineData("source-lookup-diagnostic-gc-negative")]
    [InlineData("source-lookup-diagnostic-gc-fractional")]
    [InlineData("reference-source-lookup-diagnostic-timestamp-order")]
    public void ExternalGateRejectsMalformedSourceLookupDiagnosticTelemetry(string mutation)
    {
        using var evidence = PerformanceGateEvidence.Create();
        evidence.ApplyMutation(mutation);

        PerformanceGateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.False(result.Passed, $"Mutation '{mutation}' unexpectedly passed. {result.Output}");
    }

    [Fact]
    public void QualifiedObservedRefreshRatesNeedNotMatchAcrossReports()
    {
        using var evidence = PerformanceGateEvidence.Create();

        Assert.True(evidence.ObservedRefreshRatesDiffer());

        PerformanceGateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.True(result.Passed, result.Output);
    }

    [Fact]
    public void ValidPerRunGpuAdapterLuidsNeedNotMatchAcrossReports()
    {
        using var evidence = PerformanceGateEvidence.Create();
        evidence.SetCandidateGpuAdapterLuid("00000003:00000004");

        PerformanceGateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.True(result.Passed, result.Output);
    }

    [Fact]
    public void ValidPerRunDisplayAdapterLuidsNeedNotMatchAcrossReports()
    {
        using var evidence = PerformanceGateEvidence.Create();
        evidence.SetCandidateDisplayAdapterLuid("00000007:00000008");

        PerformanceGateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.True(result.Passed, result.Output);
    }

    [Fact]
    public void DifferentDeploymentIdentitiesMayShareTheCriticalArtifactHashes()
    {
        using var evidence = PerformanceGateEvidence.Create();
        evidence.AddPrivateRuntimeDependencyAndRefreshCandidateIdentity();

        PerformanceGateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.True(result.Passed, result.Output);
    }

    [Fact]
    public void PdbAndXmlFilesDoNotChangeTheDeploymentIdentity()
    {
        using var evidence = PerformanceGateEvidence.Create();
        evidence.AddExcludedDeploymentFiles();

        PerformanceGateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.True(result.Passed, result.Output);
    }

    [Fact]
    public void CurrentReportInsideDeploymentRootIsRejected()
    {
        using var evidence = PerformanceGateEvidence.Create();

        PerformanceGateResult result = evidence.RunCandidateFromDeploymentRoot();

        Assert.False(result.Passed, result.Output);
    }

    [Fact]
    public void ReferenceReportInsideDeploymentRootIsRejected()
    {
        using var evidence = PerformanceGateEvidence.Create();

        PerformanceGateResult result = evidence.RunCandidateWithReferenceInsideDeploymentRoot();

        Assert.False(result.Passed, result.Output);
    }

    [Fact]
    public void EmptyChildDirectoryReparsePointIsRejected()
    {
        using var evidence = PerformanceGateEvidence.Create();

        PerformanceGateResult result = evidence.RunCandidateWithEmptyDirectoryReparsePoint();

        Assert.False(result.Passed, result.Output);
        Assert.Contains("reparse point", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExcludedExtensionFileReparsePointIsRejectedBeforeExclusion()
    {
        using var evidence = PerformanceGateEvidence.Create();

        PerformanceGateResult result = evidence.RunCandidateWithExcludedFileReparsePoint();

        Assert.False(result.Passed, result.Output);
        Assert.Contains("reparse point", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReferenceReportReachedThroughNestedLinksIntoDeploymentRootIsRejected()
    {
        using var evidence = PerformanceGateEvidence.Create();

        PerformanceGateResult result = evidence.RunCandidateWithReferenceThroughNestedLinks();

        Assert.False(result.Passed, result.Output);
        Assert.Contains(
            "reference report path must be outside",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> CompletionEnvironmentMutations()
    {
        string[] mutations =
        [
            "missing-object",
            "null-object",
            "missing-field",
            "null-field",
            "unknown-field",
            "machine-instance",
            "cpu",
            "logical-processors",
            "os-description",
            "os-version",
            "os-architecture",
            "process-architecture",
            "framework",
            "dpi",
            "viewport-width",
            "viewport-height",
            "display-device",
            "configured-refresh",
            "gpu-identity",
            "gpu-luid",
            "gpu-driver",
            "display-adapter-identity",
            "display-adapter-luid",
            "display-adapter-driver",
            "power-scheme",
            "ac-power-mode",
            "dc-power-mode",
            "power-source",
            "energy-saver",
            "effective-power-mode",
            "priority",
            "throttling",
            "gc-server",
            "gc-latency",
        ];
        foreach (string mutation in mutations)
        {
            yield return [$"completion-{mutation}"];
            yield return [$"reference-completion-{mutation}"];
        }
    }

    [Theory]
    [MemberData(nameof(CompletionEnvironmentMutations))]
    public void ExternalGateRejectsCompletionEnvironmentMutation(string mutation)
    {
        using var evidence = PerformanceGateEvidence.Create();
        evidence.ApplyMutation(mutation);

        PerformanceGateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.False(result.Passed, $"Mutation '{mutation}' unexpectedly passed. {result.Output}");
    }

    [Theory]
    [InlineData("measurement-visibility-missing")]
    [InlineData("measurement-visibility-foreground-loss")]
    [InlineData("measurement-visibility-occluded")]
    [InlineData("measurement-visibility-private-source")]
    public void ExternalGateRejectsInvalidMeasurementVisibilityEvidence(string mutation)
    {
        using var evidence = PerformanceGateEvidence.Create();
        evidence.ApplyMutation(mutation);

        PerformanceGateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.False(result.Passed, $"Mutation '{mutation}' unexpectedly passed. {result.Output}");
    }

    [Theory]
    [InlineData("schema-version")]
    [InlineData("provider-name")]
    [InlineData("timestamp-order")]
    [InlineData("timestamp-non-utc")]
    [InlineData("machine-fingerprint")]
    [InlineData("machine-os-description")]
    [InlineData("machine-gpu-luid")]
    [InlineData("machine-gpu-identity")]
    [InlineData("machine-gpu-driver")]
    [InlineData("machine-display-device")]
    [InlineData("machine-display-adapter-identity")]
    [InlineData("machine-display-adapter-luid")]
    [InlineData("machine-display-adapter-driver")]
    [InlineData("machine-power-guid")]
    [InlineData("machine-ac-power-mode")]
    [InlineData("machine-dc-power-mode")]
    [InlineData("machine-power-source")]
    [InlineData("machine-power-source-casing")]
    [InlineData("machine-energy-saver")]
    [InlineData("machine-energy-saver-casing")]
    [InlineData("machine-power-pair")]
    [InlineData("machine-power-mode-casing")]
    [InlineData("machine-effective-power-mode")]
    [InlineData("machine-effective-power-mode-casing")]
    [InlineData("machine-priority")]
    [InlineData("machine-priority-casing")]
    [InlineData("machine-throttling")]
    [InlineData("machine-gc-server")]
    [InlineData("machine-gc-latency")]
    [InlineData("machine-gc-latency-casing")]
    [InlineData("missing-top-member")]
    [InlineData("missing-nested-member")]
    [InlineData("null-top-member")]
    [InlineData("null-nested-member")]
    [InlineData("unknown-top-member")]
    [InlineData("unknown-nested-member")]
    [InlineData("unknown-trial-member")]
    [InlineData("null-first-viewport-element")]
    [InlineData("null-retained-memory-element")]
    [InlineData("null-scroll-trial-element")]
    [InlineData("null-lookup-trial-element")]
    [InlineData("null-cancellation-trial-element")]
    [InlineData("null-comparison-element")]
    [InlineData("duplicate-json-property")]
    [InlineData("case-colliding-json-property")]
    [InlineData("fractional-int32")]
    [InlineData("fractional-int64")]
    [InlineData("string-first-viewport-sample")]
    [InlineData("null-scroll-raw-sample")]
    [InlineData("string-lookup-raw-sample")]
    [InlineData("null-cancellation-sample")]
    [InlineData("number-failure-element")]
    [InlineData("decision-policy")]
    [InlineData("runtime-policy")]
    [InlineData("runtime-tiering")]
    [InlineData("runtime-overrides")]
    [InlineData("runtime-passed")]
    [InlineData("runtimeconfig-content")]
    [InlineData("reference-runtime-policy")]
    [InlineData("reference-runtime-overrides")]
    [InlineData("reference-runtime-passed")]
    [InlineData("observed-refresh")]
    [InlineData("median")]
    [InlineData("first-viewport-required-warmup")]
    [InlineData("first-viewport-required-trials")]
    [InlineData("first-viewport-warmup")]
    [InlineData("first-viewport-cache-disabled")]
    [InlineData("first-viewport-cache-hit")]
    [InlineData("first-viewport-engine-reuse")]
    [InlineData("first-viewport-trial-policy")]
    [InlineData("first-viewport-regression-estimator")]
    [InlineData("first-viewport-trial-list-count")]
    [InlineData("first-viewport-trial-ordinal")]
    [InlineData("first-viewport-trial-sample-count")]
    [InlineData("first-viewport-trial-p95")]
    [InlineData("first-viewport-publication-sample-count")]
    [InlineData("first-viewport-publication-trial-maximum")]
    [InlineData("first-viewport-publication-concatenation")]
    [InlineData("first-viewport-publication-aggregate-maximum")]
    [InlineData("first-viewport-publication-budget")]
    [InlineData("first-viewport-publication-overbudget")]
    [InlineData("first-viewport-trial-complete")]
    [InlineData("first-viewport-concatenation")]
    [InlineData("first-viewport-pooled-p95")]
    [InlineData("first-viewport-regression-p95")]
    [InlineData("first-viewport-budget")]
    [InlineData("schema10-first-viewport-scenario-order")]
    [InlineData("schema10-first-viewport-warmup-count")]
    [InlineData("schema10-first-viewport-warmup-global-ordinal")]
    [InlineData("schema10-first-viewport-global-ordinal")]
    [InlineData("schema10-first-viewport-schedule-position")]
    [InlineData("schema10-first-viewport-timestamp")]
    [InlineData("schema10-first-viewport-timestamp-overlap")]
    [InlineData("schema10-first-viewport-timestamp-equal")]
    [InlineData("schema10-first-viewport-timestamp-non-utc")]
    [InlineData("schema10-first-viewport-timestamp-outside-report")]
    [InlineData("schema10-first-viewport-first-start-equals-report")]
    [InlineData("schema10-first-viewport-adjacent-timestamps-equal")]
    [InlineData("schema10-first-viewport-last-completion-equals-report")]
    [InlineData("schema10-first-viewport-settling")]
    [InlineData("schema10-first-viewport-cache-counter")]
    [InlineData("schema10-first-viewport-cache-proof")]
    [InlineData("schema10-first-viewport-allocation")]
    [InlineData("schema10-first-viewport-gc-hierarchy")]
    [InlineData("schema10-first-viewport-pin")]
    [InlineData("schema10-first-viewport-pin-consistency")]
    [InlineData("schema10-first-viewport-processor-group")]
    [InlineData("schema10-first-viewport-processor-number")]
    [InlineData("schema10-first-viewport-stationarity-policy")]
    [InlineData("schema10-first-viewport-dispersion")]
    [InlineData("schema10-first-viewport-slope")]
    [InlineData("schema10-first-viewport-projected-drift")]
    [InlineData("schema10-first-viewport-boundary")]
    [InlineData("schema10-first-viewport-allowance")]
    [InlineData("schema10-first-viewport-stationarity-evaluated")]
    [InlineData("schema10-first-viewport-stationarity-passed")]
    [InlineData("schema10-first-viewport-order-drift")]
    [InlineData("schema10-first-viewport-catastrophic-trial")]
    [InlineData("schema10-first-viewport-boundary-endpoint")]
    [InlineData("reference-schema10-first-viewport-scenario-order")]
    [InlineData("reference-schema10-first-viewport-global-ordinal")]
    [InlineData("reference-schema10-first-viewport-timestamp-non-utc")]
    [InlineData("reference-schema10-first-viewport-cache-counter")]
    [InlineData("reference-schema10-first-viewport-pin")]
    [InlineData("reference-schema10-first-viewport-stationarity-passed")]
    [InlineData("reference-schema10-first-viewport-order-drift")]
    [InlineData("lookup-required-trials")]
    [InlineData("lookup-required-warmups")]
    [InlineData("lookup-required-batch")]
    [InlineData("lookup-required-observations")]
    [InlineData("lookup-required-operations")]
    [InlineData("lookup-estimator")]
    [InlineData("lookup-frequency")]
    [InlineData("lookup-top-warmups")]
    [InlineData("lookup-top-batch")]
    [InlineData("lookup-top-observations")]
    [InlineData("lookup-top-operations")]
    [InlineData("lookup-declared-trial-count")]
    [InlineData("lookup-trial-list-count")]
    [InlineData("lookup-trial-ordinal")]
    [InlineData("lookup-trial-observation-count")]
    [InlineData("lookup-trial-operation-count")]
    [InlineData("lookup-trial-sample-count")]
    [InlineData("lookup-normalization")]
    [InlineData("lookup-trial-p95")]
    [InlineData("lookup-trial-complete")]
    [InlineData("lookup-trial-allocation")]
    [InlineData("lookup-aggregate-allocation")]
    [InlineData("lookup-legacy-flat-samples")]
    [InlineData("lookup-p95")]
    [InlineData("lookup-absolute-sample")]
    [InlineData("lookup-absolute-p95")]
    [InlineData("lookup-absolute-budget")]
    [InlineData("lookup-absolute-allocation")]
    [InlineData("lookup-report-passed")]
    [InlineData("lookup-affinity-format")]
    [InlineData("lookup-affinity-multiple-bits")]
    [InlineData("lookup-processor-group")]
    [InlineData("lookup-processor-number")]
    [InlineData("lookup-thread-priority")]
    [InlineData("reference-bytes")]
    [InlineData("duplicate-comparison")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-reference")]
    [InlineData("wrong-candidate")]
    [InlineData("wrong-delta")]
    [InlineData("comparison-absolute-delta")]
    [InlineData("comparison-budget")]
    [InlineData("comparison-relative-allowance")]
    [InlineData("comparison-floor")]
    [InlineData("comparison-allowed-delta")]
    [InlineData("comparison-reference-dispersion")]
    [InlineData("comparison-candidate-dispersion")]
    [InlineData("comparison-stability-budget")]
    [InlineData("comparison-decision")]
    [InlineData("comparison-passed")]
    [InlineData("comparison-counts")]
    [InlineData("first-viewport-comparison-name")]
    [InlineData("artifact-file")]
    [InlineData("artifact-path-alias")]
    [InlineData("deployment-identity-format")]
    [InlineData("deployment-identity-lowercase")]
    [InlineData("deployment-unmanifested-file")]
    [InlineData("same-build-identity-critical-hash-mismatch")]
    [InlineData("reference-extra-first-viewport")]
    [InlineData("reference-lifecycle-population")]
    [InlineData("reference-retained-corpus")]
    [InlineData("reference-lifecycle-derived-growth")]
    [InlineData("reference-lifecycle-budget")]
    [InlineData("reference-lifecycle-device-mode")]
    [InlineData("reference-lookup-mapping-population")]
    [InlineData("reference-lookup-query-population")]
    [InlineData("reference-lookup-mapping-count")]
    [InlineData("reference-lookup-query-count")]
    [InlineData("reference-schema-version")]
    [InlineData("reference-provider-name")]
    [InlineData("reference-timestamp-order")]
    [InlineData("reference-timestamp-non-utc")]
    [InlineData("reference-failures")]
    [InlineData("reference-top-passed")]
    [InlineData("reference-machine-fingerprint")]
    [InlineData("reference-machine-os-description")]
    [InlineData("reference-machine-gpu-luid")]
    [InlineData("reference-machine-gpu-identity")]
    [InlineData("reference-machine-gpu-driver")]
    [InlineData("reference-machine-display-device")]
    [InlineData("reference-machine-display-adapter-identity")]
    [InlineData("reference-machine-display-adapter-luid")]
    [InlineData("reference-machine-display-adapter-driver")]
    [InlineData("reference-machine-power-guid")]
    [InlineData("reference-machine-ac-power-mode")]
    [InlineData("reference-machine-dc-power-mode")]
    [InlineData("reference-machine-power-source")]
    [InlineData("reference-machine-power-source-casing")]
    [InlineData("reference-machine-energy-saver")]
    [InlineData("reference-machine-energy-saver-casing")]
    [InlineData("reference-machine-power-pair")]
    [InlineData("reference-machine-power-mode-casing")]
    [InlineData("reference-machine-effective-power-mode")]
    [InlineData("reference-machine-effective-power-mode-casing")]
    [InlineData("reference-machine-priority")]
    [InlineData("reference-machine-priority-casing")]
    [InlineData("reference-machine-throttling")]
    [InlineData("reference-machine-gc-server")]
    [InlineData("reference-machine-gc-latency")]
    [InlineData("reference-machine-gc-latency-casing")]
    [InlineData("reference-missing-top-member")]
    [InlineData("reference-missing-nested-member")]
    [InlineData("reference-null-top-member")]
    [InlineData("reference-null-nested-member")]
    [InlineData("reference-unknown-top-member")]
    [InlineData("reference-unknown-nested-member")]
    [InlineData("reference-unknown-trial-member")]
    [InlineData("reference-null-first-viewport-element")]
    [InlineData("reference-null-retained-memory-element")]
    [InlineData("reference-null-scroll-trial-element")]
    [InlineData("reference-null-lookup-trial-element")]
    [InlineData("reference-null-cancellation-trial-element")]
    [InlineData("reference-null-comparison-element")]
    [InlineData("reference-duplicate-json-property")]
    [InlineData("reference-case-colliding-json-property")]
    [InlineData("reference-fractional-int32")]
    [InlineData("reference-fractional-int64")]
    [InlineData("reference-string-first-viewport-sample")]
    [InlineData("reference-null-scroll-raw-sample")]
    [InlineData("reference-string-lookup-raw-sample")]
    [InlineData("reference-null-cancellation-sample")]
    [InlineData("reference-number-failure-element")]
    [InlineData("reference-decision-policy")]
    [InlineData("reference-observed-refresh")]
    [InlineData("reference-first-viewport-required-warmup")]
    [InlineData("reference-first-viewport-protocol")]
    [InlineData("reference-first-viewport-cache")]
    [InlineData("reference-first-viewport-engine-reuse")]
    [InlineData("reference-first-viewport-required-trials")]
    [InlineData("reference-first-viewport-trial-policy")]
    [InlineData("reference-first-viewport-regression-estimator")]
    [InlineData("reference-first-viewport-trial-list-count")]
    [InlineData("reference-first-viewport-trial-ordinal")]
    [InlineData("reference-first-viewport-trial-sample-count")]
    [InlineData("reference-first-viewport-trial-p95")]
    [InlineData("reference-first-viewport-trial-complete")]
    [InlineData("reference-first-viewport-concatenation")]
    [InlineData("reference-first-viewport-pooled-p95")]
    [InlineData("reference-first-viewport-regression-p95")]
    [InlineData("reference-first-viewport-budget")]
    [InlineData("reference-lookup-required-trials")]
    [InlineData("reference-lookup-required-warmups")]
    [InlineData("reference-lookup-required-batch")]
    [InlineData("reference-lookup-estimator")]
    [InlineData("reference-lookup-frequency")]
    [InlineData("reference-lookup-top-batch")]
    [InlineData("reference-lookup-top-warmups")]
    [InlineData("reference-lookup-normalization")]
    [InlineData("reference-lookup-trial-p95")]
    [InlineData("reference-lookup-top-median")]
    [InlineData("reference-lookup-affinity-mismatch")]
    [InlineData("reference-lookup-group-mismatch")]
    [InlineData("reference-lookup-absolute-sample")]
    [InlineData("reference-lookup-absolute-p95")]
    [InlineData("reference-lookup-absolute-budget")]
    [InlineData("reference-lookup-absolute-allocation")]
    [InlineData("reference-lookup-report-passed")]
    [InlineData("scroll-frequency")]
    [InlineData("scroll-raw-scope")]
    [InlineData("scroll-platform-scope")]
    [InlineData("scroll-trial-count")]
    [InlineData("scroll-trial-ordinal")]
    [InlineData("scroll-trial-frame-order")]
    [InlineData("scroll-trial-population")]
    [InlineData("scroll-raw-ui-count")]
    [InlineData("scroll-raw-frame-count")]
    [InlineData("scroll-raw-allocation-count")]
    [InlineData("scroll-raw-ui-negative")]
    [InlineData("scroll-raw-frame-zero")]
    [InlineData("scroll-raw-allocation-negative")]
    [InlineData("scroll-trial-ui-p95")]
    [InlineData("scroll-trial-ui-p99")]
    [InlineData("scroll-trial-frame-p95")]
    [InlineData("scroll-trial-frame-over")]
    [InlineData("scroll-trial-frame-max")]
    [InlineData("scroll-trial-allocation-p95")]
    [InlineData("scroll-trial-allocation-max")]
    [InlineData("scroll-trial-complete")]
    [InlineData("scroll-pooled-ui-p95")]
    [InlineData("scroll-pooled-ui-p99")]
    [InlineData("scroll-pooled-frame-p95")]
    [InlineData("scroll-pooled-frame-over")]
    [InlineData("scroll-pooled-frame-max")]
    [InlineData("scroll-pooled-allocation-p95")]
    [InlineData("scroll-pooled-allocation-max")]
    [InlineData("scroll-pooled-loh")]
    [InlineData("scroll-regression-ui-p95")]
    [InlineData("scroll-regression-ui-p99")]
    [InlineData("scroll-regression-frame-p95")]
    [InlineData("scroll-regression-allocation-p95")]
    [InlineData("scroll-report-passed")]
    [InlineData("scroll-frame-budget")]
    [InlineData("reference-scroll-frequency")]
    [InlineData("reference-scroll-raw-scope")]
    [InlineData("reference-scroll-platform-scope")]
    [InlineData("reference-scroll-raw-ui-count")]
    [InlineData("reference-scroll-raw-frame-count")]
    [InlineData("reference-scroll-raw-allocation-count")]
    [InlineData("reference-scroll-trial-ui-p95")]
    [InlineData("reference-scroll-trial-ui-p99")]
    [InlineData("reference-scroll-trial-frame-p95")]
    [InlineData("reference-scroll-trial-frame-over")]
    [InlineData("reference-scroll-trial-frame-max")]
    [InlineData("reference-scroll-trial-allocation-p95")]
    [InlineData("reference-scroll-trial-allocation-max")]
    [InlineData("reference-scroll-pooled-ui-p95")]
    [InlineData("reference-scroll-pooled-ui-p99")]
    [InlineData("reference-scroll-pooled-frame-p95")]
    [InlineData("reference-scroll-pooled-frame-over")]
    [InlineData("reference-scroll-pooled-frame-max")]
    [InlineData("reference-scroll-pooled-allocation-p95")]
    [InlineData("reference-scroll-pooled-allocation-max")]
    [InlineData("reference-scroll-pooled-loh")]
    [InlineData("reference-scroll-regression-ui-p95")]
    [InlineData("reference-scroll-regression-ui-p99")]
    [InlineData("reference-scroll-regression-frame-p95")]
    [InlineData("reference-scroll-regression-allocation-p95")]
    [InlineData("reference-scroll-report-passed")]
    [InlineData("reference-scroll-frame-budget")]
    [InlineData("cancellation-required-iterations")]
    [InlineData("cancellation-required-trials")]
    [InlineData("cancellation-iterations-per-trial")]
    [InlineData("cancellation-total")]
    [InlineData("cancellation-warmup-count")]
    [InlineData("cancellation-observed")]
    [InlineData("cancellation-policy")]
    [InlineData("cancellation-estimator")]
    [InlineData("cancellation-trial-count")]
    [InlineData("cancellation-trial-ordinal")]
    [InlineData("cancellation-warmup-seed")]
    [InlineData("cancellation-first-recorded-seed")]
    [InlineData("cancellation-trial-requested")]
    [InlineData("cancellation-trial-observed")]
    [InlineData("cancellation-trial-sample-count")]
    [InlineData("cancellation-trial-negative-sample")]
    [InlineData("cancellation-trial-warmup-negative")]
    [InlineData("cancellation-trial-warmup-stale")]
    [InlineData("cancellation-trial-p95")]
    [InlineData("cancellation-trial-stale")]
    [InlineData("cancellation-trial-complete")]
    [InlineData("cancellation-concatenation")]
    [InlineData("cancellation-warmup-p95")]
    [InlineData("cancellation-warmup-stale")]
    [InlineData("cancellation-pooled-p95")]
    [InlineData("cancellation-regression-p95")]
    [InlineData("cancellation-budget")]
    [InlineData("cancellation-stale")]
    [InlineData("cancellation-report-passed")]
    [InlineData("reference-cancellation-required-iterations")]
    [InlineData("reference-cancellation-required-trials")]
    [InlineData("reference-cancellation-policy")]
    [InlineData("reference-cancellation-estimator")]
    [InlineData("reference-cancellation-trial-count")]
    [InlineData("reference-cancellation-trial-ordinal")]
    [InlineData("reference-cancellation-warmup-seed")]
    [InlineData("reference-cancellation-first-recorded-seed")]
    [InlineData("reference-cancellation-trial-sample-count")]
    [InlineData("reference-cancellation-trial-negative-sample")]
    [InlineData("reference-cancellation-trial-warmup-stale")]
    [InlineData("reference-cancellation-trial-p95")]
    [InlineData("reference-cancellation-trial-stale")]
    [InlineData("reference-cancellation-trial-complete")]
    [InlineData("reference-cancellation-concatenation")]
    [InlineData("reference-cancellation-warmup-p95")]
    [InlineData("reference-cancellation-pooled-p95")]
    [InlineData("reference-cancellation-regression-p95")]
    [InlineData("reference-cancellation-budget")]
    [InlineData("reference-cancellation-report-passed")]
    [InlineData("lifecycle-derived-growth")]
    [InlineData("lifecycle-device-mode")]
    [InlineData("retained-corpus")]
    [InlineData("negative-first-frame")]
    [InlineData("reference-negative-first-frame")]
    [InlineData("scroll-budget")]
    [InlineData("negative-device-loss")]
    [InlineData("exclusive-threshold")]
    public void ExternalGateRejectsEvidenceMutation(string mutation)
    {
        using var evidence = PerformanceGateEvidence.Create();
        evidence.ApplyMutation(mutation);

        PerformanceGateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.False(result.Passed, $"Mutation '{mutation}' unexpectedly passed. {result.Output}");
    }
}
