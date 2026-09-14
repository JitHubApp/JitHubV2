using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PerformanceExternalGateFixtureTests
{
    [Fact]
    public void CommittedSchema10FixturePassesBaselineAndCandidateAtExactInclusiveEdges()
    {
        using var evidence = GateEvidence.Create();
        GateResult baseline = evidence.RunGate(evidence.BaselinePath, "Baseline");
        GateResult candidate = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.True(baseline.Passed, baseline.Output);
        Assert.True(candidate.Passed, candidate.Output);
    }

    [Fact]
    public void QualifiedObservedRefreshRatesNeedNotMatchAcrossReports()
    {
        using var evidence = GateEvidence.Create();

        Assert.True(evidence.ObservedRefreshRatesDiffer());

        GateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.True(result.Passed, result.Output);
    }

    [Fact]
    public void ValidPerRunGpuAdapterLuidsNeedNotMatchAcrossReports()
    {
        using var evidence = GateEvidence.Create();
        evidence.SetCandidateGpuAdapterLuid("00000003:00000004");

        GateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.True(result.Passed, result.Output);
    }

    [Fact]
    public void ValidPerRunDisplayAdapterLuidsNeedNotMatchAcrossReports()
    {
        using var evidence = GateEvidence.Create();
        evidence.SetCandidateDisplayAdapterLuid("00000007:00000008");

        GateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.True(result.Passed, result.Output);
    }

    [Fact]
    public void DifferentDeploymentIdentitiesMayShareTheFiveCriticalArtifactHashes()
    {
        using var evidence = GateEvidence.Create();
        evidence.AddPrivateRuntimeDependencyAndRefreshCandidateIdentity();

        GateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.True(result.Passed, result.Output);
    }

    [Fact]
    public void PdbAndXmlFilesDoNotChangeTheDeploymentIdentity()
    {
        using var evidence = GateEvidence.Create();
        evidence.AddExcludedDeploymentFiles();

        GateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.True(result.Passed, result.Output);
    }

    [Fact]
    public void CurrentReportInsideDeploymentRootIsRejected()
    {
        using var evidence = GateEvidence.Create();

        GateResult result = evidence.RunCandidateFromDeploymentRoot();

        Assert.False(result.Passed, result.Output);
    }

    [Fact]
    public void ReferenceReportInsideDeploymentRootIsRejected()
    {
        using var evidence = GateEvidence.Create();

        GateResult result = evidence.RunCandidateWithReferenceInsideDeploymentRoot();

        Assert.False(result.Passed, result.Output);
    }

    [Fact]
    public void EmptyChildDirectoryReparsePointIsRejected()
    {
        using var evidence = GateEvidence.Create();

        GateResult result = evidence.RunCandidateWithEmptyDirectoryReparsePoint();

        Assert.False(result.Passed, result.Output);
        Assert.Contains("reparse point", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExcludedExtensionFileReparsePointIsRejectedBeforeExclusion()
    {
        using var evidence = GateEvidence.Create();

        GateResult result = evidence.RunCandidateWithExcludedFileReparsePoint();

        Assert.False(result.Passed, result.Output);
        Assert.Contains("reparse point", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReferenceReportReachedThroughNestedLinksIntoDeploymentRootIsRejected()
    {
        using var evidence = GateEvidence.Create();

        GateResult result = evidence.RunCandidateWithReferenceThroughNestedLinks();

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
        using var evidence = GateEvidence.Create();
        evidence.ApplyMutation(mutation);

        GateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

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
        using var evidence = GateEvidence.Create();
        evidence.ApplyMutation(mutation);

        GateResult result = evidence.RunGate(evidence.CandidatePath, "Candidate");

        Assert.False(result.Passed, $"Mutation '{mutation}' unexpectedly passed. {result.Output}");
    }

    private sealed class GateEvidence : IDisposable
    {
        private readonly string _directory;
        private readonly string _scriptPath;
        private readonly string _executableArtifactPath;

        private GateEvidence(
            string directory,
            string scriptPath,
            string executableArtifactPath,
            string baselinePath,
            string candidatePath)
        {
            _directory = directory;
            _scriptPath = scriptPath;
            _executableArtifactPath = executableArtifactPath;
            BaselinePath = baselinePath;
            CandidatePath = candidatePath;
        }

        internal string BaselinePath { get; }
        internal string CandidatePath { get; }

        internal static GateEvidence Create()
        {
            string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "PerformanceFixtures");
            GateValues values = JsonSerializer.Deserialize<GateValues>(
                File.ReadAllText(Path.Combine(fixtureDirectory, "performance-gate-schema10-values.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The schema-10 external-gate fixture was empty.");
            string directory = Path.Combine(
                Path.GetTempPath(),
                $"markdown-renderer-gate-fixture-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            string deploymentDirectory = Path.Combine(directory, "deployment");
            Directory.CreateDirectory(deploymentDirectory);

            BuildArtifactHashes artifacts = CreateArtifacts(
                deploymentDirectory,
                out string executablePath);
            string buildIdentity =
                PerformanceDeploymentIdentity.CaptureForExecutable(executablePath);
            PerformanceReport baseline = CreateReport(
                values,
                artifacts,
                buildIdentity,
                candidate: false);
            string baselinePath = Path.Combine(directory, "baseline.json");
            string candidatePath = Path.Combine(directory, "candidate.json");
            File.WriteAllText(
                baselinePath,
                JsonSerializer.Serialize(baseline, PerformanceReport.JsonOptions));

            PerformanceReport candidate = CreateReport(
                values,
                artifacts,
                buildIdentity,
                candidate: true);
            candidate.Regression = RegressionEvaluator.Evaluate(
                candidate,
                PerformanceOptions.Parse(
                [
                    "--output", candidatePath,
                    "--reference", baselinePath,
                ]));
            candidate.Passed = candidate.Regression.Passed;
            File.WriteAllText(
                candidatePath,
                JsonSerializer.Serialize(candidate, PerformanceReport.JsonOptions));

            return new GateEvidence(
                directory,
                Path.Combine(fixtureDirectory, "Test-MarkdownWinUIPerformanceGates.ps1"),
                executablePath,
                baselinePath,
                candidatePath);
        }

        internal void ApplyMutation(string mutation)
        {
            if (mutation == "deployment-unmanifested-file")
            {
                File.WriteAllText(
                    Path.Combine(GetDeploymentDirectory(), "unmanifested-runtime-dependency.dll"),
                    "unmanifested");
                return;
            }
            if (mutation == "same-build-identity-critical-hash-mismatch")
            {
                ApplyReferenceMutation((baseline, _) =>
                    baseline["buildArtifacts"]!["executable"]!["sha256"] = new string('0', 64));
                return;
            }
            if (mutation == "duplicate-json-property")
            {
                ApplyRawDuplicateProperty(CandidatePath, "providerName", "providerName");
                return;
            }
            if (mutation == "case-colliding-json-property")
            {
                ApplyRawDuplicateProperty(CandidatePath, "providerName", "ProviderName");
                return;
            }
            if (mutation == "reference-duplicate-json-property")
            {
                ApplyRawReferenceMutation(text => AddDuplicateProperty(
                    text,
                    "providerName",
                    "providerName"));
                return;
            }
            if (mutation == "reference-case-colliding-json-property")
            {
                ApplyRawReferenceMutation(text => AddDuplicateProperty(
                    text,
                    "providerName",
                    "ProviderName"));
                return;
            }
            if (mutation == "runtimeconfig-content")
            {
                JsonObject candidate = JsonNode.Parse(File.ReadAllText(CandidatePath))!.AsObject();
                string runtimeConfigPath = candidate["buildArtifacts"]!["runtimeConfig"]!["path"]!
                    .GetValue<string>();
                File.WriteAllText(
                    runtimeConfigPath,
                    """
                    {
                      "runtimeOptions": {
                        "configProperties": {
                          "System.Runtime.TieredCompilation": true,
                          "System.Runtime.TieredPGO": false,
                          "System.GC.Concurrent": false,
                          "System.Runtime.ReadyToRun": false
                        }
                      }
                    }
                    """);
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(runtimeConfigPath)));
                ApplyReferenceMutation((baseline, candidateReport) =>
                {
                    baseline["buildArtifacts"]!["runtimeConfig"]!["sha256"] = hash;
                    candidateReport["buildArtifacts"]!["runtimeConfig"]!["sha256"] = hash;
                    candidateReport["buildIdentity"] =
                        PerformanceDeploymentIdentity.CaptureForExecutable(
                            _executableArtifactPath);
                });
                return;
            }

            const string referenceRuntimePrefix = "reference-runtime-";
            if (mutation.StartsWith(referenceRuntimePrefix, StringComparison.Ordinal))
            {
                ApplyReferenceMutation((baseline, _) => ApplyRuntimeMutation(
                    baseline,
                    mutation[referenceRuntimePrefix.Length..]));
                return;
            }

            const string referenceSchema10FirstViewportPrefix =
                "reference-schema10-first-viewport-";
            if (mutation.StartsWith(referenceSchema10FirstViewportPrefix, StringComparison.Ordinal))
            {
                ApplyReferenceMutation((baseline, _) => ApplySchema10FirstViewportMutation(
                    baseline,
                    mutation[referenceSchema10FirstViewportPrefix.Length..]));
                return;
            }

            const string referenceCompletionPrefix = "reference-completion-";
            if (mutation.StartsWith(referenceCompletionPrefix, StringComparison.Ordinal))
            {
                ApplyReferenceMutation((baseline, _) => ApplyCompletionMutation(
                    baseline,
                    mutation[referenceCompletionPrefix.Length..]));
                return;
            }

            const string completionPrefix = "completion-";
            if (mutation.StartsWith(completionPrefix, StringComparison.Ordinal))
            {
                JsonObject candidate = JsonNode.Parse(File.ReadAllText(CandidatePath))!.AsObject();
                ApplyCompletionMutation(candidate, mutation[completionPrefix.Length..]);
                File.WriteAllText(
                    CandidatePath,
                    candidate.ToJsonString(PerformanceReport.JsonOptions));
                return;
            }

            const string referenceMachinePrefix = "reference-machine-";
            if (mutation.StartsWith(referenceMachinePrefix, StringComparison.Ordinal))
            {
                ApplyReferenceMutation((baseline, _) => ApplyMachineMutation(
                    baseline,
                    "machine-" + mutation[referenceMachinePrefix.Length..]));
                return;
            }

            const string referenceScrollPrefix = "reference-scroll-";
            if (mutation.StartsWith(referenceScrollPrefix, StringComparison.Ordinal))
            {
                ApplyReferenceMutation((baseline, _) => ApplyScrollMutation(
                    baseline,
                    "scroll-" + mutation[referenceScrollPrefix.Length..]));
                return;
            }

            const string referenceCancellationPrefix = "reference-cancellation-";
            if (mutation.StartsWith(referenceCancellationPrefix, StringComparison.Ordinal))
            {
                ApplyReferenceMutation((baseline, _) => ApplyCancellationMutation(
                    baseline,
                    "cancellation-" + mutation[referenceCancellationPrefix.Length..]));
                return;
            }

            switch (mutation)
            {
                case "reference-bytes":
                    File.AppendAllText(BaselinePath, " ");
                    return;
                case "artifact-file":
                    File.AppendAllText(_executableArtifactPath, "tampered");
                    return;
                case "artifact-path-alias":
                    ApplyReferenceMutation((baseline, candidate) =>
                    {
                        JsonObject baselineArtifacts = baseline["buildArtifacts"]!.AsObject();
                        JsonObject candidateArtifacts = candidate["buildArtifacts"]!.AsObject();
                        JsonObject baselineExecutable = baselineArtifacts["executable"]!.AsObject();
                        JsonObject candidateExecutable = candidateArtifacts["executable"]!.AsObject();
                        JsonObject baselineHarness = baselineArtifacts["performanceHarnessDll"]!.AsObject();
                        JsonObject candidateHarness = candidateArtifacts["performanceHarnessDll"]!.AsObject();
                        baselineHarness["path"] = baselineExecutable["path"]!.DeepClone();
                        baselineHarness["sha256"] = baselineExecutable["sha256"]!.DeepClone();
                        candidateHarness["path"] = candidateExecutable["path"]!.DeepClone();
                        candidateHarness["sha256"] = candidateExecutable["sha256"]!.DeepClone();
                    });
                    return;
                case "reference-extra-first-viewport":
                    ApplyReferenceMutation((baseline, _) =>
                    {
                        JsonNode extra = baseline["firstUsableViewport"]![0]!.DeepClone();
                        extra["mode"] = "extra";
                        baseline["firstUsableViewport"]!.AsArray().Add(extra);
                    });
                    return;
                case "reference-lifecycle-population":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sampleRequirements"]!["lifecycleCyclesRequired"] = 99);
                    return;
                case "reference-retained-corpus":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["retainedMemory"]![0]!["corpus"] = "wrong-corpus");
                    return;
                case "reference-lifecycle-derived-growth":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["lifecyclePlateau"]!["checkpoint100ManagedBytes"] = 2);
                    return;
                case "reference-lifecycle-budget":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["lifecyclePlateau"]!["growthBudgetPercent"] = 6);
                    return;
                case "reference-lifecycle-device-mode":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["lifecyclePlateau"]!["deviceResetMode"] = "wrong-mode");
                    return;
                case "reference-lookup-mapping-population":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sampleRequirements"]!["sourceLookupMappingCountRequired"] = 99_999);
                    return;
                case "reference-lookup-query-population":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sampleRequirements"]!["sourceLookupAbsoluteQueryCountRequired"] = 19_999);
                    return;
                case "reference-lookup-mapping-count":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["mappingCount"] = 99_999);
                    return;
                case "reference-lookup-query-count":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["queryCount"] = 19_999);
                    return;
                case "reference-schema-version":
                    ApplyReferenceMutation((baseline, _) => baseline["schemaVersion"] = 9);
                    return;
                case "reference-provider-name":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["providerName"] = "wrong-provider");
                    return;
                case "reference-timestamp-order":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["completedUtc"] = baseline["startedUtc"]!.DeepClone());
                    return;
                case "reference-timestamp-non-utc":
                    ApplyReferenceMutation((baseline, _) =>
                    {
                        DateTimeOffset started = DateTimeOffset.Parse(
                            baseline["startedUtc"]!.GetValue<string>(),
                            System.Globalization.CultureInfo.InvariantCulture);
                        baseline["startedUtc"] = started
                            .ToOffset(TimeSpan.FromHours(-7))
                            .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                    });
                    return;
                case "reference-failures":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["failures"]!.AsArray().Add("failure"));
                    return;
                case "reference-top-passed":
                    ApplyReferenceMutation((baseline, _) => baseline["passed"] = false);
                    return;
                case "reference-missing-top-member":
                    ApplyReferenceMutation((baseline, _) => baseline.Remove("providerName"));
                    return;
                case "reference-missing-nested-member":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["machine"]!.AsObject().Remove("gpuDriverVersion"));
                    return;
                case "reference-null-top-member":
                    ApplyReferenceMutation((baseline, _) => baseline["buildIdentity"] = null);
                    return;
                case "reference-null-nested-member":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["machine"]!["gpuDriverVersion"] = null);
                    return;
                case "reference-unknown-top-member":
                    ApplyReferenceMutation((baseline, _) => baseline["unexpected"] = 1);
                    return;
                case "reference-unknown-nested-member":
                    ApplyReferenceMutation((baseline, _) => baseline["machine"]!["unexpected"] = 1);
                    return;
                case "reference-unknown-trial-member":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["scroll"]!["trials"]![0]!["unexpected"] = 1);
                    return;
                case "reference-null-first-viewport-element":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["firstUsableViewport"]![0] = null);
                    return;
                case "reference-null-retained-memory-element":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["retainedMemory"]![0] = null);
                    return;
                case "reference-null-scroll-trial-element":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["scroll"]!["trials"]![0] = null);
                    return;
                case "reference-null-lookup-trial-element":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["regressionTrials"]![0] = null);
                    return;
                case "reference-null-cancellation-trial-element":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["cancellation"]!["trials"]![0] = null);
                    return;
                case "reference-null-comparison-element":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["regression"]!["comparisons"]!.AsArray().Add(null));
                    return;
                case "reference-fractional-int32":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sampleRequirements"]!["firstViewportIterationsRequired"] = 100.5);
                    return;
                case "reference-fractional-int64":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["scroll"]!["stopwatchFrequency"] = Stopwatch.Frequency + 0.5);
                    return;
                case "reference-string-first-viewport-sample":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["firstUsableViewport"]![0]!["samplesMilliseconds"]![0] = "invalid");
                    return;
                case "reference-null-scroll-raw-sample":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["scroll"]!["trials"]![0]!["uiThreadWorkElapsedTicks"]![0] = null);
                    return;
                case "reference-string-lookup-raw-sample":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["samplesElapsedTicks"]![0] = "invalid");
                    return;
                case "reference-null-cancellation-sample":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["cancellation"]!["samplesMilliseconds"]![0] = null);
                    return;
                case "reference-number-failure-element":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["failures"]!.AsArray().Add(1));
                    return;
                case "reference-decision-policy":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["regression"]!["decisionPolicy"] = "wrong-policy");
                    return;
                case "reference-observed-refresh":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["machine"]!["observedRefreshRateHz"] = 100);
                    return;
                case "reference-first-viewport-required-warmup":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sampleRequirements"]!["firstViewportWarmupTrialsRequired"] = 0);
                    return;
                case "reference-first-viewport-protocol":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["firstUsableViewport"]![0]!["schedulePolicy"] = "wrong-policy");
                    return;
                case "reference-first-viewport-cache":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["firstUsableViewport"]![1]!["parseCacheBudgetBytes"] = 0);
                    return;
                case "reference-first-viewport-engine-reuse":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["firstUsableViewport"]![0]!["engineReuseMode"] = "per-iteration");
                    return;
                case "reference-first-viewport-required-trials":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sampleRequirements"]!["firstViewportTrialsRequired"] = 4);
                    return;
                case "reference-first-viewport-trial-policy":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["firstUsableViewport"]![0]!["trialStartPolicy"] = "wrong-policy");
                    return;
                case "reference-first-viewport-regression-estimator":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["firstUsableViewport"]![0]!["regressionEstimator"] = "wrong-estimator");
                    return;
                case "reference-first-viewport-trial-list-count":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["firstUsableViewport"]![0]!["trials"]!.AsArray().RemoveAt(5));
                    return;
                case "reference-first-viewport-trial-ordinal":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["firstUsableViewport"]![0]!["trials"]![0]!["ordinal"] = 1);
                    return;
                case "reference-first-viewport-trial-sample-count":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["firstUsableViewport"]![0]!["trials"]![0]!["samplesMilliseconds"]!
                            .AsArray().RemoveAt(99));
                    return;
                case "reference-first-viewport-trial-p95":
                    ApplyReferenceMutation((baseline, _) =>
                    {
                        JsonNode trial = baseline["firstUsableViewport"]![0]!["trials"]![0]!;
                        trial["p95Milliseconds"] = trial["p95Milliseconds"]!.GetValue<double>() + 1;
                    });
                    return;
                case "reference-first-viewport-trial-complete":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["firstUsableViewport"]![0]!["trials"]![0]!["complete"] = false);
                    return;
                case "reference-first-viewport-concatenation":
                    ApplyReferenceMutation((baseline, _) =>
                        SwapPooledFirstViewportSamples(baseline));
                    return;
                case "reference-first-viewport-pooled-p95":
                    ApplyReferenceMutation((baseline, _) =>
                    {
                        JsonNode viewport = baseline["firstUsableViewport"]![0]!;
                        viewport["p95Milliseconds"] = viewport["p95Milliseconds"]!.GetValue<double>() + 1;
                    });
                    return;
                case "reference-first-viewport-regression-p95":
                    ApplyReferenceMutation((baseline, _) =>
                    {
                        JsonNode viewport = baseline["firstUsableViewport"]![0]!;
                        viewport["regressionP95Milliseconds"] =
                            viewport["regressionP95Milliseconds"]!.GetValue<double>() + 1;
                    });
                    return;
                case "reference-first-viewport-budget":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["firstUsableViewport"]![0]!["budgetMilliseconds"] = 124);
                    return;
                case "reference-lookup-required-trials":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sampleRequirements"]!["sourceLookupRegressionTrialsRequired"] = 4);
                    return;
                case "reference-lookup-required-warmups":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sampleRequirements"]!["sourceLookupRegressionWarmupPassesRequired"] = 7);
                    return;
                case "reference-lookup-required-batch":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sampleRequirements"]!["sourceLookupRegressionBatchSizeRequired"] = 4_095);
                    return;
                case "reference-lookup-estimator":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["regressionEstimator"] = "wrong-estimator");
                    return;
                case "reference-lookup-frequency":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["stopwatchFrequency"] = Stopwatch.Frequency + 1);
                    return;
                case "reference-lookup-top-batch":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["regressionBatchSize"] = 4_095);
                    return;
                case "reference-lookup-top-warmups":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["regressionWarmupPasses"] = 7);
                    return;
                case "reference-lookup-normalization":
                    ApplyReferenceMutation((baseline, _) =>
                    {
                        JsonObject trial = baseline["sourceLookup"]!["regressionTrials"]![0]!.AsObject();
                        trial["samplesNanosecondsPerLookup"]![0] =
                            trial["samplesNanosecondsPerLookup"]![0]!.GetValue<double>() + 1;
                    });
                    return;
                case "reference-lookup-trial-p95":
                    ApplyReferenceMutation((baseline, _) =>
                    {
                        JsonObject trial = baseline["sourceLookup"]!["regressionTrials"]![0]!.AsObject();
                        trial["p95Nanoseconds"] = trial["p95Nanoseconds"]!.GetValue<double>() + 1;
                    });
                    return;
                case "reference-lookup-top-median":
                    ApplyReferenceMutation((baseline, _) =>
                    {
                        JsonNode lookup = baseline["sourceLookup"]!;
                        lookup["regressionP95Nanoseconds"] =
                            lookup["regressionP95Nanoseconds"]!.GetValue<double>() + 1;
                    });
                    return;
                case "reference-lookup-affinity-mismatch":
                    ApplyReferenceMutation((baseline, _) =>
                    {
                        JsonNode lookup = baseline["sourceLookup"]!;
                        lookup["measurementThreadAffinityMask"] = "0x0000000000000002";
                        lookup["measurementProcessorNumber"] = 1;
                    });
                    return;
                case "reference-lookup-group-mismatch":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["measurementProcessorGroup"] = 1);
                    return;
                case "reference-lookup-absolute-sample":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["samplesElapsedTicks"]![0] = -1);
                    return;
                case "reference-lookup-absolute-p95":
                    ApplyReferenceMutation((baseline, _) =>
                    {
                        JsonNode lookup = baseline["sourceLookup"]!;
                        lookup["p95Nanoseconds"] = lookup["p95Nanoseconds"]!.GetValue<double>() + 1;
                    });
                    return;
                case "reference-lookup-absolute-budget":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["p95BudgetNanoseconds"] = 24_999);
                    return;
                case "reference-lookup-absolute-allocation":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["allocatedBytes"] = 1);
                    return;
                case "reference-lookup-report-passed":
                    ApplyReferenceMutation((baseline, _) =>
                        baseline["sourceLookup"]!["passed"] = false);
                    return;
                case "reference-negative-first-frame":
                    ApplyReferenceMutation((baseline, _) =>
                    {
                        JsonArray trials = baseline["scroll"]!["trials"]!.AsArray();
                        for (int index = 0; index < trials.Count; index++)
                            trials[index]!["firstLogicalFrameId"] = -50_000_000L + index * 10_000_000L;
                    });
                    return;
            }

            JsonObject report = JsonNode.Parse(File.ReadAllText(CandidatePath))!.AsObject();
            const string runtimePrefix = "runtime-";
            if (mutation.StartsWith(runtimePrefix, StringComparison.Ordinal))
            {
                ApplyRuntimeMutation(report, mutation[runtimePrefix.Length..]);
                File.WriteAllText(CandidatePath, report.ToJsonString(PerformanceReport.JsonOptions));
                return;
            }
            const string schema10FirstViewportPrefix = "schema10-first-viewport-";
            if (mutation.StartsWith(schema10FirstViewportPrefix, StringComparison.Ordinal))
            {
                ApplySchema10FirstViewportMutation(
                    report,
                    mutation[schema10FirstViewportPrefix.Length..]);
                File.WriteAllText(CandidatePath, report.ToJsonString(PerformanceReport.JsonOptions));
                return;
            }
            if (mutation.StartsWith("machine-", StringComparison.Ordinal))
            {
                ApplyMachineMutation(report, mutation);
                File.WriteAllText(CandidatePath, report.ToJsonString(PerformanceReport.JsonOptions));
                return;
            }
            if (mutation.StartsWith("scroll-", StringComparison.Ordinal) &&
                mutation is not "scroll-budget")
            {
                ApplyScrollMutation(report, mutation);
                File.WriteAllText(CandidatePath, report.ToJsonString(PerformanceReport.JsonOptions));
                return;
            }
            if (mutation.StartsWith("cancellation-", StringComparison.Ordinal))
            {
                ApplyCancellationMutation(report, mutation);
                File.WriteAllText(CandidatePath, report.ToJsonString(PerformanceReport.JsonOptions));
                return;
            }
            JsonObject scroll = report["scroll"]!.AsObject();
            JsonObject lookup = report["sourceLookup"]!.AsObject();
            JsonArray comparisons = report["regression"]!["comparisons"]!.AsArray();
            JsonObject firstComparison = comparisons
                .Select(static node => node!.AsObject())
                .Single(static comparison =>
                    comparison["metric"]!.GetValue<string>() ==
                        "firstViewport.cache-disabled.102400.p95Ms.hodgesLehmann");
            switch (mutation)
            {
                case "schema-version":
                    report["schemaVersion"] = 9;
                    break;
                case "provider-name":
                    report["providerName"] = "wrong-provider";
                    break;
                case "timestamp-order":
                    report["completedUtc"] = report["startedUtc"]!.DeepClone();
                    break;
                case "timestamp-non-utc":
                    DateTimeOffset reportStarted = DateTimeOffset.Parse(
                        report["startedUtc"]!.GetValue<string>(),
                        System.Globalization.CultureInfo.InvariantCulture);
                    report["startedUtc"] = reportStarted
                        .ToOffset(TimeSpan.FromHours(-7))
                        .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "deployment-identity-format":
                    report["buildIdentity"] = "not-a-deployment-identity";
                    break;
                case "deployment-identity-lowercase":
                    report["buildIdentity"] = report["buildIdentity"]!
                        .GetValue<string>()
                        .ToLowerInvariant();
                    break;
                case "missing-top-member":
                    report.Remove("providerName");
                    break;
                case "missing-nested-member":
                    report["machine"]!.AsObject().Remove("gpuDriverVersion");
                    break;
                case "null-top-member":
                    report["buildIdentity"] = null;
                    break;
                case "null-nested-member":
                    report["machine"]!["gpuDriverVersion"] = null;
                    break;
                case "unknown-top-member":
                    report["unexpected"] = 1;
                    break;
                case "unknown-nested-member":
                    report["machine"]!["unexpected"] = 1;
                    break;
                case "unknown-trial-member":
                    report["scroll"]!["trials"]![0]!["unexpected"] = 1;
                    break;
                case "null-first-viewport-element":
                    report["firstUsableViewport"]![0] = null;
                    break;
                case "null-retained-memory-element":
                    report["retainedMemory"]![0] = null;
                    break;
                case "null-scroll-trial-element":
                    report["scroll"]!["trials"]![0] = null;
                    break;
                case "null-lookup-trial-element":
                    report["sourceLookup"]!["regressionTrials"]![0] = null;
                    break;
                case "null-cancellation-trial-element":
                    report["cancellation"]!["trials"]![0] = null;
                    break;
                case "null-comparison-element":
                    comparisons[0] = null;
                    break;
                case "fractional-int32":
                    report["sampleRequirements"]!["firstViewportIterationsRequired"] = 100.5;
                    break;
                case "fractional-int64":
                    report["scroll"]!["stopwatchFrequency"] = Stopwatch.Frequency + 0.5;
                    break;
                case "string-first-viewport-sample":
                    report["firstUsableViewport"]![0]!["samplesMilliseconds"]![0] = "invalid";
                    break;
                case "null-scroll-raw-sample":
                    report["scroll"]!["trials"]![0]!["uiThreadWorkElapsedTicks"]![0] = null;
                    break;
                case "string-lookup-raw-sample":
                    report["sourceLookup"]!["samplesElapsedTicks"]![0] = "invalid";
                    break;
                case "null-cancellation-sample":
                    report["cancellation"]!["samplesMilliseconds"]![0] = null;
                    break;
                case "number-failure-element":
                    report["failures"]!.AsArray().Add(1);
                    break;
                case "decision-policy":
                    report["regression"]!["decisionPolicy"] = "wrong-policy";
                    break;
                case "observed-refresh":
                    report["machine"]!["observedRefreshRateHz"] = 100;
                    break;
                case "median":
                    scroll["regressionUiThreadWorkP95Milliseconds"] =
                        scroll["regressionUiThreadWorkP95Milliseconds"]!.GetValue<double>() + 1;
                    break;
                case "first-viewport-required-warmup":
                    report["sampleRequirements"]!["firstViewportWarmupTrialsRequired"] = 0;
                    break;
                case "first-viewport-required-trials":
                    report["sampleRequirements"]!["firstViewportTrialsRequired"] = 4;
                    break;
                case "first-viewport-warmup":
                    report["firstUsableViewport"]![0]!["warmupTrials"]!.AsArray().RemoveAt(2);
                    break;
                case "first-viewport-cache-disabled":
                    report["firstUsableViewport"]![0]!["parseCacheBudgetBytes"] = 1;
                    break;
                case "first-viewport-cache-hit":
                    report["firstUsableViewport"]![1]!["parseCacheBudgetBytes"] = 0;
                    break;
                case "first-viewport-engine-reuse":
                    report["firstUsableViewport"]![0]!["engineReuseMode"] = "per-iteration";
                    break;
                case "first-viewport-trial-policy":
                    report["firstUsableViewport"]![0]!["trialStartPolicy"] = "wrong-policy";
                    break;
                case "first-viewport-regression-estimator":
                    report["firstUsableViewport"]![0]!["regressionEstimator"] = "wrong-estimator";
                    break;
                case "first-viewport-trial-list-count":
                    report["firstUsableViewport"]![0]!["trials"]!.AsArray().RemoveAt(5);
                    break;
                case "first-viewport-trial-ordinal":
                    report["firstUsableViewport"]![0]!["trials"]![0]!["ordinal"] = 1;
                    break;
                case "first-viewport-trial-sample-count":
                    report["firstUsableViewport"]![0]!["trials"]![0]!["samplesMilliseconds"]!
                        .AsArray().RemoveAt(99);
                    break;
                case "first-viewport-trial-p95":
                    JsonNode viewportTrial = report["firstUsableViewport"]![0]!["trials"]![0]!;
                    viewportTrial["p95Milliseconds"] =
                        viewportTrial["p95Milliseconds"]!.GetValue<double>() + 1;
                    break;
                case "first-viewport-trial-complete":
                    report["firstUsableViewport"]![0]!["trials"]![0]!["complete"] = false;
                    break;
                case "first-viewport-concatenation":
                    SwapPooledFirstViewportSamples(report);
                    break;
                case "first-viewport-pooled-p95":
                    JsonNode pooledViewport = report["firstUsableViewport"]![0]!;
                    pooledViewport["p95Milliseconds"] =
                        pooledViewport["p95Milliseconds"]!.GetValue<double>() + 1;
                    break;
                case "first-viewport-regression-p95":
                    JsonNode regressionViewport = report["firstUsableViewport"]![0]!;
                    regressionViewport["regressionP95Milliseconds"] =
                        regressionViewport["regressionP95Milliseconds"]!.GetValue<double>() + 1;
                    break;
                case "first-viewport-budget":
                    report["firstUsableViewport"]![0]!["budgetMilliseconds"] = 124;
                    break;
                case "lookup-required-trials":
                    report["sampleRequirements"]!["sourceLookupRegressionTrialsRequired"] = 4;
                    break;
                case "lookup-required-warmups":
                    report["sampleRequirements"]!["sourceLookupRegressionWarmupPassesRequired"] = 7;
                    break;
                case "lookup-required-batch":
                    report["sampleRequirements"]!["sourceLookupRegressionBatchSizeRequired"] = 4_095;
                    break;
                case "lookup-required-observations":
                    report["sampleRequirements"]!["sourceLookupRegressionObservationCountRequired"] = 127;
                    break;
                case "lookup-required-operations":
                    report["sampleRequirements"]!["sourceLookupRegressionOperationCountRequired"] = 524_287;
                    break;
                case "lookup-estimator":
                    lookup["regressionEstimator"] = "wrong-estimator";
                    break;
                case "lookup-frequency":
                    lookup["stopwatchFrequency"] = Stopwatch.Frequency + 1;
                    break;
                case "lookup-top-warmups":
                    lookup["regressionWarmupPasses"] = 7;
                    break;
                case "lookup-top-batch":
                    lookup["regressionBatchSize"] = 4_095;
                    break;
                case "lookup-top-observations":
                    lookup["regressionObservationCount"] = 127;
                    break;
                case "lookup-top-operations":
                    lookup["regressionOperationCount"] = 524_287;
                    break;
                case "lookup-declared-trial-count":
                    lookup["regressionTrialCount"] = 4;
                    break;
                case "lookup-trial-list-count":
                    lookup["regressionTrials"]!.AsArray().RemoveAt(4);
                    break;
                case "lookup-trial-ordinal":
                    lookup["regressionTrials"]![0]!["ordinal"] = 1;
                    break;
                case "lookup-trial-observation-count":
                    lookup["regressionTrials"]![0]!["observationCount"] = 127;
                    break;
                case "lookup-trial-operation-count":
                    lookup["regressionTrials"]![0]!["operationCount"] = 524_287;
                    break;
                case "lookup-trial-sample-count":
                    lookup["regressionTrials"]![0]!["samplesElapsedTicks"]!.AsArray().RemoveAt(127);
                    break;
                case "lookup-normalization":
                    lookup["regressionTrials"]![0]!["samplesNanosecondsPerLookup"]![0] =
                        lookup["regressionTrials"]![0]!["samplesNanosecondsPerLookup"]![0]!
                            .GetValue<double>() + 1;
                    break;
                case "lookup-trial-p95":
                    lookup["regressionTrials"]![0]!["p95Nanoseconds"] =
                        lookup["regressionTrials"]![0]!["p95Nanoseconds"]!.GetValue<double>() + 1;
                    break;
                case "lookup-trial-complete":
                    lookup["regressionTrials"]![0]!["complete"] = false;
                    break;
                case "lookup-trial-allocation":
                    lookup["regressionTrials"]![0]!["allocatedBytes"] = 1;
                    break;
                case "lookup-aggregate-allocation":
                    lookup["regressionAllocatedBytes"] = 1;
                    break;
                case "lookup-legacy-flat-samples":
                    lookup["regressionSamplesElapsedTicks"] = JsonNode.Parse("[1]");
                    lookup["regressionSamplesNanosecondsPerLookup"] = JsonNode.Parse("[1]");
                    break;
                case "lookup-p95":
                    lookup["regressionP95Nanoseconds"] =
                        lookup["regressionP95Nanoseconds"]!.GetValue<double>() + 1;
                    break;
                case "lookup-absolute-sample":
                    lookup["samplesElapsedTicks"]![0] = -1;
                    break;
                case "lookup-absolute-p95":
                    lookup["p95Nanoseconds"] =
                        lookup["p95Nanoseconds"]!.GetValue<double>() + 1;
                    break;
                case "lookup-absolute-budget":
                    lookup["p95BudgetNanoseconds"] = 24_999;
                    break;
                case "lookup-absolute-allocation":
                    lookup["allocatedBytes"] = 1;
                    break;
                case "lookup-report-passed":
                    lookup["passed"] = false;
                    break;
                case "lookup-affinity-format":
                    lookup["measurementThreadAffinityMask"] = "0x1";
                    break;
                case "lookup-affinity-multiple-bits":
                    lookup["measurementThreadAffinityMask"] = "0x0000000000000003";
                    break;
                case "lookup-processor-group":
                    lookup["measurementProcessorGroup"] = -1;
                    break;
                case "lookup-processor-number":
                    lookup["measurementProcessorNumber"] = 1;
                    break;
                case "lookup-thread-priority":
                    lookup["measurementThreadPriority"] = "Normal";
                    break;
                case "duplicate-comparison":
                    comparisons.Add(firstComparison.DeepClone());
                    break;
                case "wrong-kind":
                    firstComparison["kind"] = "allocation";
                    break;
                case "wrong-reference":
                    firstComparison["reference"] =
                        firstComparison["reference"]!.GetValue<double>() + 1;
                    break;
                case "wrong-candidate":
                    firstComparison["candidate"] =
                        firstComparison["candidate"]!.GetValue<double>() + 1;
                    break;
                case "wrong-delta":
                    firstComparison["deltaPercent"] =
                        firstComparison["deltaPercent"]!.GetValue<double>() - 1;
                    break;
                case "comparison-absolute-delta":
                    firstComparison["absoluteDelta"] =
                        firstComparison["absoluteDelta"]!.GetValue<double>() + 1;
                    break;
                case "comparison-budget":
                    firstComparison["budgetPercent"] = 4;
                    break;
                case "comparison-relative-allowance":
                    firstComparison["relativeAllowance"] =
                        firstComparison["relativeAllowance"]!.GetValue<double>() + 1;
                    break;
                case "comparison-floor":
                    firstComparison["absoluteNoiseFloor"] = 4;
                    break;
                case "comparison-allowed-delta":
                    firstComparison["allowedAbsoluteDelta"] =
                        firstComparison["allowedAbsoluteDelta"]!.GetValue<double>() + 1;
                    break;
                case "comparison-reference-dispersion":
                    firstComparison["referenceDispersionPercent"] =
                        firstComparison["referenceDispersionPercent"]!.GetValue<double>() + 1;
                    break;
                case "comparison-candidate-dispersion":
                    firstComparison["candidateDispersionPercent"] =
                        firstComparison["candidateDispersionPercent"]!.GetValue<double>() + 1;
                    break;
                case "comparison-stability-budget":
                    firstComparison["stabilityBudgetPercent"] = 24;
                    break;
                case "comparison-decision":
                    firstComparison["decision"] = "regression";
                    break;
                case "comparison-passed":
                    firstComparison["passed"] = false;
                    break;
                case "comparison-counts":
                    report["regression"]!["comparedLatencyMetrics"] = 10;
                    break;
                case "first-viewport-comparison-name":
                    firstComparison["metric"] = "firstViewport.cache-disabled.102400.p95Ms";
                    break;
                case "exclusive-threshold":
                    scroll["framesOver16_7Percent"] = 1.0;
                    break;
                case "lifecycle-derived-growth":
                    report["lifecyclePlateau"]!["checkpoint100ManagedBytes"] = 2;
                    break;
                case "lifecycle-device-mode":
                    report["lifecyclePlateau"]!["deviceResetMode"] = "wrong-mode";
                    break;
                case "retained-corpus":
                    report["retainedMemory"]![0]!["corpus"] = "wrong-corpus";
                    break;
                case "negative-first-frame":
                    JsonArray trials = scroll["trials"]!.AsArray();
                    for (int index = 0; index < trials.Count; index++)
                        trials[index]!["firstLogicalFrameId"] = -50_000_000L + index * 10_000_000L;
                    break;
                case "scroll-budget":
                    scroll["uiThreadWorkP95BudgetMilliseconds"] = 3;
                    break;
                case "negative-device-loss":
                    report["lifecyclePlateau"]!["actualDeviceLossEvents"] = -1;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }

            File.WriteAllText(CandidatePath, report.ToJsonString(PerformanceReport.JsonOptions));
        }

        internal bool ObservedRefreshRatesDiffer()
        {
            JsonObject baseline = JsonNode.Parse(File.ReadAllText(BaselinePath))!.AsObject();
            JsonObject candidate = JsonNode.Parse(File.ReadAllText(CandidatePath))!.AsObject();
            double baselineRefresh = baseline["machine"]!["observedRefreshRateHz"]!.GetValue<double>();
            double candidateRefresh = candidate["machine"]!["observedRefreshRateHz"]!.GetValue<double>();
            return baselineRefresh != candidateRefresh;
        }

        internal void SetCandidateGpuAdapterLuid(string gpuAdapterLuid)
        {
            JsonObject report = JsonNode.Parse(File.ReadAllText(CandidatePath))!.AsObject();
            report["machine"]!["gpuAdapterLuid"] = gpuAdapterLuid;
            report["completionMachine"]!["gpuAdapterLuid"] = gpuAdapterLuid;
            File.WriteAllText(CandidatePath, report.ToJsonString(PerformanceReport.JsonOptions));
        }

        internal void SetCandidateDisplayAdapterLuid(string displayAdapterLuid)
        {
            JsonObject report = JsonNode.Parse(File.ReadAllText(CandidatePath))!.AsObject();
            report["machine"]!["displayAdapterLuid"] = displayAdapterLuid;
            report["completionMachine"]!["displayAdapterLuid"] = displayAdapterLuid;
            File.WriteAllText(CandidatePath, report.ToJsonString(PerformanceReport.JsonOptions));
        }

        internal void AddPrivateRuntimeDependencyAndRefreshCandidateIdentity()
        {
            string nestedDirectory = Path.Combine(GetDeploymentDirectory(), "plugins", "nested");
            Directory.CreateDirectory(nestedDirectory);
            File.WriteAllText(
                Path.Combine(nestedDirectory, "Private.Runtime.Dependency.dll"),
                "private-runtime-dependency");

            JsonObject candidate = JsonNode.Parse(File.ReadAllText(CandidatePath))!.AsObject();
            candidate["buildIdentity"] =
                PerformanceDeploymentIdentity.CaptureForExecutable(_executableArtifactPath);
            File.WriteAllText(CandidatePath, candidate.ToJsonString(PerformanceReport.JsonOptions));
        }

        internal void AddExcludedDeploymentFiles()
        {
            string nestedDirectory = Path.Combine(GetDeploymentDirectory(), "symbols");
            Directory.CreateDirectory(nestedDirectory);
            File.WriteAllText(Path.Combine(nestedDirectory, "fixture.PDB"), "symbols");
            File.WriteAllText(Path.Combine(nestedDirectory, "fixture.Xml"), "documentation");
        }

        internal GateResult RunCandidateWithEmptyDirectoryReparsePoint()
        {
            string externalDirectory = Path.Combine(_directory, "external-runtime");
            Directory.CreateDirectory(externalDirectory);
            string link = Path.Combine(GetDeploymentDirectory(), "linked-runtime");
            Directory.CreateSymbolicLink(link, externalDirectory);
            try
            {
                return RunGate(CandidatePath, "Candidate");
            }
            finally
            {
                Directory.Delete(link);
            }
        }

        internal GateResult RunCandidateWithExcludedFileReparsePoint()
        {
            string target = Path.Combine(_directory, "external-symbols.txt");
            File.WriteAllText(target, "external-symbols");
            string link = Path.Combine(GetDeploymentDirectory(), "linked-symbols.PDB");
            File.CreateSymbolicLink(link, target);
            try
            {
                return RunGate(CandidatePath, "Candidate");
            }
            finally
            {
                File.Delete(link);
            }
        }

        internal GateResult RunCandidateWithReferenceThroughNestedLinks()
        {
            string reportsDirectory = Path.Combine(GetDeploymentDirectory(), "reports");
            Directory.CreateDirectory(reportsDirectory);
            string insideReferencePath = Path.Combine(reportsDirectory, "reference-report.json");
            File.Copy(BaselinePath, insideReferencePath);

            string deploymentLink = Path.Combine(_directory, "deployment-link");
            string nestedLink = Path.Combine(_directory, "nested-deployment-link");
            Directory.CreateSymbolicLink(deploymentLink, GetDeploymentDirectory());
            Directory.CreateSymbolicLink(
                nestedLink,
                Path.Combine(deploymentLink, "reports"));
            try
            {
                string linkedReferencePath = Path.Combine(nestedLink, "reference-report.json");
                JsonObject candidate = JsonNode.Parse(File.ReadAllText(CandidatePath))!.AsObject();
                candidate["regression"]!["referencePath"] = linkedReferencePath;
                candidate["regression"]!["referenceReportSha256"] = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(insideReferencePath)));
                candidate["buildIdentity"] =
                    PerformanceDeploymentIdentity.CaptureForExecutable(_executableArtifactPath);
                File.WriteAllText(
                    CandidatePath,
                    candidate.ToJsonString(PerformanceReport.JsonOptions));
                return RunGate(CandidatePath, "Candidate");
            }
            finally
            {
                Directory.Delete(nestedLink);
                Directory.Delete(deploymentLink);
            }
        }

        internal GateResult RunCandidateFromDeploymentRoot()
        {
            string insidePath = Path.Combine(GetDeploymentDirectory(), "candidate-report.json");
            File.Copy(CandidatePath, insidePath);
            return RunGate(insidePath, "Candidate");
        }

        internal GateResult RunCandidateWithReferenceInsideDeploymentRoot()
        {
            string insideReferencePath = Path.Combine(
                GetDeploymentDirectory(),
                "reference-report.json");
            File.Copy(BaselinePath, insideReferencePath);

            JsonObject candidate = JsonNode.Parse(File.ReadAllText(CandidatePath))!.AsObject();
            candidate["regression"]!["referencePath"] = insideReferencePath;
            candidate["regression"]!["referenceReportSha256"] = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(insideReferencePath)));
            candidate["buildIdentity"] =
                PerformanceDeploymentIdentity.CaptureForExecutable(_executableArtifactPath);
            File.WriteAllText(CandidatePath, candidate.ToJsonString(PerformanceReport.JsonOptions));
            return RunGate(CandidatePath, "Candidate");
        }

        private string GetDeploymentDirectory()
            => Path.GetDirectoryName(_executableArtifactPath)
                ?? throw new InvalidOperationException("The fixture executable has no parent directory.");

        private static void ApplyMachineMutation(JsonObject report, string mutation)
        {
            JsonObject machine = report["machine"]!.AsObject();
            switch (mutation)
            {
                case "machine-fingerprint":
                    machine["machineInstanceSha256"] = new string('B', 64);
                    break;
                case "machine-os-description":
                    machine["osDescription"] = "other-fixture-os";
                    break;
                case "machine-gpu-luid":
                    machine["gpuAdapterLuid"] = "invalid-luid";
                    break;
                case "machine-gpu-identity":
                    machine["gpuAdapterIdentitySha256"] = new string('D', 64);
                    break;
                case "machine-gpu-driver":
                    machine["gpuDriverVersion"] = "31.0.15.9999";
                    break;
                case "machine-display-device":
                    machine["displayDeviceName"] = string.Empty;
                    break;
                case "machine-display-adapter-identity":
                    machine["displayAdapterIdentitySha256"] = new string('E', 64);
                    break;
                case "machine-display-adapter-luid":
                    machine["displayAdapterLuid"] = "invalid-luid";
                    break;
                case "machine-display-adapter-driver":
                    machine["displayAdapterDriverVersion"] = "31.0.15.9999";
                    break;
                case "machine-power-guid":
                    machine["activePowerSchemeGuid"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
                    break;
                case "machine-ac-power-mode":
                    machine["userConfiguredAcPowerModeGuid"] = "not-a-power-mode";
                    break;
                case "machine-dc-power-mode":
                    machine["userConfiguredDcPowerModeGuid"] =
                        "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
                    break;
                case "machine-power-source":
                    machine["powerSource"] = "Battery";
                    break;
                case "machine-power-source-casing":
                    machine["powerSource"] = "ac";
                    break;
                case "machine-energy-saver":
                    machine["energySaverState"] = "On";
                    break;
                case "machine-energy-saver-casing":
                    machine["energySaverState"] = "off";
                    break;
                case "machine-power-pair":
                    machine["userConfiguredDcPowerModeGuid"] = "unsupported";
                    break;
                case "machine-power-mode-casing":
                    machine["userConfiguredAcPowerModeGuid"] = "Unsupported";
                    machine["userConfiguredDcPowerModeGuid"] = "Unsupported";
                    break;
                case "machine-effective-power-mode":
                    machine["effectivePowerMode"] = "Balanced";
                    break;
                case "machine-effective-power-mode-casing":
                    machine["effectivePowerMode"] = "highperformance";
                    break;
                case "machine-priority":
                    machine["processPriorityClass"] = "Normal";
                    break;
                case "machine-priority-casing":
                    machine["processPriorityClass"] = "high";
                    break;
                case "machine-throttling":
                    machine["processPowerThrottlingMode"] =
                        "execution-speed=on;ignore-timer-resolution=off";
                    break;
                case "machine-gc-server":
                    machine["gcServer"] = true;
                    break;
                case "machine-gc-latency":
                    machine["gcLatencyMode"] = "LowLatency";
                    break;
                case "machine-gc-latency-casing":
                    machine["gcLatencyMode"] = "interactive";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }

            JsonObject completion = report["completionMachine"]!.AsObject();
            string[] mutatedProperties = mutation switch
            {
                "machine-fingerprint" => ["machineInstanceSha256"],
                "machine-os-description" => ["osDescription"],
                "machine-gpu-luid" => ["gpuAdapterLuid"],
                "machine-gpu-identity" => ["gpuAdapterIdentitySha256"],
                "machine-gpu-driver" => ["gpuDriverVersion"],
                "machine-display-device" => ["displayDeviceName"],
                "machine-display-adapter-identity" => ["displayAdapterIdentitySha256"],
                "machine-display-adapter-luid" => ["displayAdapterLuid"],
                "machine-display-adapter-driver" => ["displayAdapterDriverVersion"],
                "machine-power-guid" => ["activePowerSchemeGuid"],
                "machine-ac-power-mode" => ["userConfiguredAcPowerModeGuid"],
                "machine-dc-power-mode" => ["userConfiguredDcPowerModeGuid"],
                "machine-power-source" or "machine-power-source-casing" => ["powerSource"],
                "machine-energy-saver" or "machine-energy-saver-casing" => ["energySaverState"],
                "machine-power-pair" => ["userConfiguredDcPowerModeGuid"],
                "machine-power-mode-casing" =>
                    ["userConfiguredAcPowerModeGuid", "userConfiguredDcPowerModeGuid"],
                "machine-effective-power-mode" or "machine-effective-power-mode-casing" =>
                    ["effectivePowerMode"],
                "machine-priority" or "machine-priority-casing" => ["processPriorityClass"],
                "machine-throttling" => ["processPowerThrottlingMode"],
                "machine-gc-server" => ["gcServer"],
                "machine-gc-latency" or "machine-gc-latency-casing" => ["gcLatencyMode"],
                _ => throw new UnreachableException(),
            };
            foreach (string property in mutatedProperties)
                completion[property] = machine[property]!.DeepClone();
        }

        private static void ApplyCompletionMutation(JsonObject report, string mutation)
        {
            if (mutation == "missing-object")
            {
                report.Remove("completionMachine");
                return;
            }
            if (mutation == "null-object")
            {
                report["completionMachine"] = null;
                return;
            }

            JsonObject completion = report["completionMachine"]!.AsObject();
            switch (mutation)
            {
                case "missing-field":
                    completion.Remove("displayAdapterDriverVersion");
                    break;
                case "null-field":
                    completion["displayAdapterDriverVersion"] = null;
                    break;
                case "unknown-field":
                    completion["unexpected"] = 1;
                    break;
                case "machine-instance":
                    completion["machineInstanceSha256"] = new string('B', 64);
                    break;
                case "cpu":
                    completion["cpu"] = "other-cpu";
                    break;
                case "logical-processors":
                    completion["logicalProcessorCount"] = 9;
                    break;
                case "os-description":
                    completion["osDescription"] = "other-os";
                    break;
                case "os-version":
                    completion["osVersion"] = "10.0.99999";
                    break;
                case "os-architecture":
                    completion["osArchitecture"] = "Arm64";
                    break;
                case "process-architecture":
                    completion["processArchitecture"] = "X86";
                    break;
                case "framework":
                    completion["frameworkDescription"] = ".NET other";
                    break;
                case "dpi":
                    completion["dpiScale"] = 2;
                    break;
                case "viewport-width":
                    completion["viewportWidthDips"] = 1101;
                    break;
                case "viewport-height":
                    completion["viewportHeightDips"] = 761;
                    break;
                case "display-device":
                    completion["displayDeviceName"] = @"\\.\DISPLAY2";
                    break;
                case "configured-refresh":
                    completion["configuredRefreshRateHz"] = 121;
                    break;
                case "gpu-identity":
                    completion["gpuAdapterIdentitySha256"] = new string('E', 64);
                    break;
                case "gpu-luid":
                    completion["gpuAdapterLuid"] = "00000009:0000000A";
                    break;
                case "gpu-driver":
                    completion["gpuDriverVersion"] = "31.0.15.9999";
                    break;
                case "display-adapter-identity":
                    completion["displayAdapterIdentitySha256"] = new string('F', 64);
                    break;
                case "display-adapter-luid":
                    completion["displayAdapterLuid"] = "0000000B:0000000C";
                    break;
                case "display-adapter-driver":
                    completion["displayAdapterDriverVersion"] = "31.0.15.9999";
                    break;
                case "power-scheme":
                    completion["activePowerSchemeGuid"] =
                        "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
                    break;
                case "ac-power-mode":
                    completion["userConfiguredAcPowerModeGuid"] =
                        "961cc777-2547-4f9d-8174-7d86181b8a7a";
                    break;
                case "dc-power-mode":
                    completion["userConfiguredDcPowerModeGuid"] =
                        "ded574b5-45a0-4f42-8737-46345c09c238";
                    break;
                case "power-source":
                    completion["powerSource"] = "Battery";
                    break;
                case "energy-saver":
                    completion["energySaverState"] = "On";
                    break;
                case "effective-power-mode":
                    completion["effectivePowerMode"] = "Balanced";
                    break;
                case "priority":
                    completion["processPriorityClass"] = "Normal";
                    break;
                case "throttling":
                    completion["processPowerThrottlingMode"] =
                        "execution-speed=on;ignore-timer-resolution=off";
                    break;
                case "gc-server":
                    completion["gcServer"] = true;
                    break;
                case "gc-latency":
                    completion["gcLatencyMode"] = "LowLatency";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
        }

        private static string AddDuplicateProperty(
            string json,
            string existingName,
            string duplicateName)
        {
            string marker = $"\"{existingName}\":";
            string replacement = $"\"{duplicateName}\": \"duplicate\",{Environment.NewLine}  {marker}";
            string mutated = json.Replace(marker, replacement, StringComparison.Ordinal);
            Assert.NotEqual(json, mutated);
            return mutated;
        }

        private static void ApplyRawDuplicateProperty(
            string path,
            string existingName,
            string duplicateName)
            => File.WriteAllText(
                path,
                AddDuplicateProperty(File.ReadAllText(path), existingName, duplicateName));

        private void ApplyRawReferenceMutation(Func<string, string> mutation)
        {
            File.WriteAllText(BaselinePath, mutation(File.ReadAllText(BaselinePath)));
            JsonObject candidate = JsonNode.Parse(File.ReadAllText(CandidatePath))!.AsObject();
            candidate["regression"]!["referenceReportSha256"] =
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(BaselinePath)));
            File.WriteAllText(CandidatePath, candidate.ToJsonString(PerformanceReport.JsonOptions));
        }

        private static void ApplyScrollMutation(JsonObject report, string mutation)
        {
            JsonObject scroll = report["scroll"]!.AsObject();
            JsonArray trials = scroll["trials"]!.AsArray();
            JsonObject trial = trials[0]!.AsObject();
            switch (mutation)
            {
                case "scroll-frequency":
                    scroll["stopwatchFrequency"] =
                        scroll["stopwatchFrequency"]!.GetValue<long>() + 1;
                    break;
                case "scroll-raw-scope":
                    scroll["rawAllocationScope"] = "wrong-scope";
                    break;
                case "scroll-platform-scope":
                    scroll["platformProjectionAllocationScope"] = "wrong-scope";
                    break;
                case "scroll-trial-count":
                    trials.RemoveAt(4);
                    break;
                case "scroll-trial-ordinal":
                    trial["ordinal"] = 1;
                    break;
                case "scroll-trial-frame-order":
                    trials[1]!["firstLogicalFrameId"] =
                        trial["firstLogicalFrameId"]!.GetValue<long>() + 1;
                    break;
                case "scroll-trial-population":
                    trial["requestedFrames"] = 2_399;
                    break;
                case "scroll-raw-ui-count":
                    trial["uiThreadWorkElapsedTicks"]!.AsArray().RemoveAt(2_399);
                    break;
                case "scroll-raw-frame-count":
                    trial["frameIntervalElapsedTicks"]!.AsArray().RemoveAt(2_399);
                    break;
                case "scroll-raw-allocation-count":
                    trial["rendererOwnedAllocatedBytesPerFrame"]!.AsArray().RemoveAt(2_399);
                    break;
                case "scroll-raw-ui-negative":
                    trial["uiThreadWorkElapsedTicks"]![0] = -1;
                    break;
                case "scroll-raw-frame-zero":
                    trial["frameIntervalElapsedTicks"]![0] = 0;
                    break;
                case "scroll-raw-allocation-negative":
                    trial["rendererOwnedAllocatedBytesPerFrame"]![0] = -1;
                    break;
                case "scroll-trial-ui-p95":
                    AddOne(trial, "uiThreadWorkP95Milliseconds");
                    break;
                case "scroll-trial-ui-p99":
                    AddOne(trial, "uiThreadWorkP99Milliseconds");
                    break;
                case "scroll-trial-frame-p95":
                    AddOne(trial, "frameTimeP95Milliseconds");
                    break;
                case "scroll-trial-frame-over":
                    AddOne(trial, "framesOver16_7Percent");
                    break;
                case "scroll-trial-frame-max":
                    AddOne(trial, "maximumFrameStallMilliseconds");
                    break;
                case "scroll-trial-allocation-p95":
                    AddOne(trial, "rendererOwnedAllocatedBytesPerFrameP95");
                    break;
                case "scroll-trial-allocation-max":
                    trial["maximumRendererOwnedAllocatedBytesPerFrame"] =
                        trial["maximumRendererOwnedAllocatedBytesPerFrame"]!.GetValue<long>() + 1;
                    break;
                case "scroll-trial-complete":
                    trial["complete"] = false;
                    break;
                case "scroll-pooled-ui-p95":
                    AddOne(scroll, "uiThreadWorkP95Milliseconds");
                    break;
                case "scroll-pooled-ui-p99":
                    AddOne(scroll, "uiThreadWorkP99Milliseconds");
                    break;
                case "scroll-pooled-frame-p95":
                    AddOne(scroll, "frameTimeP95Milliseconds");
                    break;
                case "scroll-pooled-frame-over":
                    AddOne(scroll, "framesOver16_7Percent");
                    break;
                case "scroll-pooled-frame-max":
                    AddOne(scroll, "maximumFrameStallMilliseconds");
                    break;
                case "scroll-pooled-allocation-p95":
                    AddOne(scroll, "rendererOwnedAllocatedBytesPerFrameP95");
                    break;
                case "scroll-pooled-allocation-max":
                    scroll["maximumRendererOwnedAllocatedBytesPerFrame"] =
                        scroll["maximumRendererOwnedAllocatedBytesPerFrame"]!.GetValue<long>() + 1;
                    break;
                case "scroll-pooled-loh":
                    scroll["rendererOwnedLohAllocationPossible"] = true;
                    break;
                case "scroll-regression-ui-p95":
                    AddOne(scroll, "regressionUiThreadWorkP95Milliseconds");
                    break;
                case "scroll-regression-ui-p99":
                    AddOne(scroll, "regressionUiThreadWorkP99Milliseconds");
                    break;
                case "scroll-regression-frame-p95":
                    AddOne(scroll, "regressionFrameTimeP95Milliseconds");
                    break;
                case "scroll-regression-allocation-p95":
                    AddOne(scroll, "regressionRendererOwnedAllocatedBytesPerFrameP95");
                    break;
                case "scroll-report-passed":
                    scroll["passed"] = false;
                    break;
                case "scroll-frame-budget":
                    scroll["frameTimeP95BudgetMilliseconds"] =
                        PerformanceMeasurementContract.ScrollFrameTimeP95BudgetMilliseconds - 0.001;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
        }

        private static void ApplyCancellationMutation(JsonObject report, string mutation)
        {
            JsonObject cancellation = report["cancellation"]!.AsObject();
            JsonArray trials = cancellation["trials"]!.AsArray();
            JsonObject trial = trials[0]!.AsObject();
            switch (mutation)
            {
                case "cancellation-required-iterations":
                    report["sampleRequirements"]!["cancellationIterationsPerTrialRequired"] = 39;
                    break;
                case "cancellation-required-trials":
                    report["sampleRequirements"]!["cancellationTrialsRequired"] = 4;
                    break;
                case "cancellation-iterations-per-trial":
                    cancellation["iterationsPerTrial"] = 39;
                    break;
                case "cancellation-total":
                    cancellation["requestedIterations"] = 199;
                    break;
                case "cancellation-warmup-count":
                    cancellation["warmupIterations"] = 4;
                    break;
                case "cancellation-observed":
                    cancellation["observedCancellations"] = 199;
                    break;
                case "cancellation-policy":
                    cancellation["trialStartPolicy"] = "wrong-policy";
                    break;
                case "cancellation-estimator":
                    cancellation["regressionEstimator"] = "wrong-estimator";
                    break;
                case "cancellation-trial-count":
                    trials.RemoveAt(4);
                    break;
                case "cancellation-trial-ordinal":
                    trial["ordinal"] = 1;
                    break;
                case "cancellation-warmup-seed":
                    trial["warmupSourceSeed"] = trial["warmupSourceSeed"]!.GetValue<int>() + 1;
                    break;
                case "cancellation-first-recorded-seed":
                    trial["firstRecordedSourceSeed"] =
                        trial["firstRecordedSourceSeed"]!.GetValue<int>() + 1;
                    break;
                case "cancellation-trial-requested":
                    trial["requestedIterations"] = 39;
                    break;
                case "cancellation-trial-observed":
                    trial["observedCancellations"] = 39;
                    break;
                case "cancellation-trial-sample-count":
                    trial["samplesMilliseconds"]!.AsArray().RemoveAt(39);
                    break;
                case "cancellation-trial-negative-sample":
                    trial["samplesMilliseconds"]![0] = -1;
                    break;
                case "cancellation-trial-warmup-negative":
                    trial["warmupElapsedMilliseconds"] = -1;
                    break;
                case "cancellation-trial-warmup-stale":
                    trial["warmupStaleCommits"] = 1;
                    break;
                case "cancellation-trial-p95":
                    AddOne(trial, "p95Milliseconds");
                    break;
                case "cancellation-trial-stale":
                    trial["staleCommits"] = 1;
                    break;
                case "cancellation-trial-complete":
                    trial["complete"] = false;
                    break;
                case "cancellation-concatenation":
                    JsonArray samples = cancellation["samplesMilliseconds"]!.AsArray();
                    JsonNode first = samples[0]!.DeepClone();
                    samples[0] = samples[40]!.DeepClone();
                    samples[40] = first;
                    break;
                case "cancellation-warmup-p95":
                    AddOne(cancellation, "warmupElapsedMilliseconds");
                    break;
                case "cancellation-warmup-stale":
                    cancellation["warmupStaleCommits"] = 1;
                    break;
                case "cancellation-pooled-p95":
                    AddOne(cancellation, "p95Milliseconds");
                    break;
                case "cancellation-regression-p95":
                    AddOne(cancellation, "regressionP95Milliseconds");
                    break;
                case "cancellation-budget":
                    cancellation["p95BudgetMilliseconds"] = 15;
                    break;
                case "cancellation-stale":
                    cancellation["staleCommits"] = 1;
                    break;
                case "cancellation-report-passed":
                    cancellation["passed"] = false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
        }

        private static void ApplyRuntimeMutation(JsonObject report, string mutation)
        {
            JsonObject runtime = report["runtimeConfiguration"]!.AsObject();
            switch (mutation)
            {
                case "policy":
                    runtime["policy"] = "wrong-policy";
                    break;
                case "tiering":
                    runtime["tieredCompilationEnabled"] = true;
                    break;
                case "overrides":
                    runtime["overrideEnvironmentVariables"]!.AsArray().Add("DOTNET_TieredPGO");
                    break;
                case "passed":
                    runtime["passed"] = false;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
        }

        private static void ApplySchema10FirstViewportMutation(
            JsonObject report,
            string mutation)
        {
            JsonArray viewports = report["firstUsableViewport"]!.AsArray();
            JsonObject viewport = viewports[0]!.AsObject();
            JsonObject trial = viewport["trials"]![0]!.AsObject();
            JsonObject warmup = viewport["warmupTrials"]![0]!.AsObject();
            switch (mutation)
            {
                case "scenario-order":
                    JsonNode first = viewports[0]!.DeepClone();
                    viewports[0] = viewports[1]!.DeepClone();
                    viewports[1] = first;
                    break;
                case "warmup-count":
                    viewport["warmupTrials"]!.AsArray().RemoveAt(2);
                    break;
                case "warmup-global-ordinal":
                    warmup["globalOrdinal"] = warmup["globalOrdinal"]!.GetValue<int>() + 1;
                    break;
                case "global-ordinal":
                    trial["globalOrdinal"] = trial["globalOrdinal"]!.GetValue<int>() + 1;
                    break;
                case "schedule-position":
                    trial["schedulePosition"] = (trial["schedulePosition"]!.GetValue<int>() + 1) % 6;
                    break;
                case "timestamp":
                    DateTimeOffset started = DateTimeOffset.Parse(
                        trial["startedUtc"]!.GetValue<string>(),
                        System.Globalization.CultureInfo.InvariantCulture);
                    trial["completedUtc"] = started.AddMilliseconds(-1);
                    break;
                case "timestamp-overlap":
                    trial["startedUtc"] = DateTimeOffset.UnixEpoch;
                    trial["completedUtc"] = DateTimeOffset.UnixEpoch.AddMilliseconds(1);
                    break;
                case "timestamp-equal":
                    DateTimeOffset sharedTimestamp = DateTimeOffset.Parse(
                        report["startedUtc"]!.GetValue<string>(),
                        System.Globalization.CultureInfo.InvariantCulture).AddMilliseconds(100);
                    foreach (JsonObject scenario in viewports.Select(static node => node!.AsObject()))
                    {
                        foreach (JsonObject currentTrial in scenario["warmupTrials"]!.AsArray()
                                     .Concat(scenario["trials"]!.AsArray())
                                     .Select(static node => node!.AsObject()))
                        {
                            currentTrial["startedUtc"] = sharedTimestamp;
                            currentTrial["completedUtc"] = sharedTimestamp;
                        }
                    }
                    break;
                case "timestamp-non-utc":
                    DateTimeOffset originalStarted = DateTimeOffset.Parse(
                        trial["startedUtc"]!.GetValue<string>(),
                        System.Globalization.CultureInfo.InvariantCulture);
                    DateTimeOffset originalCompleted = DateTimeOffset.Parse(
                        trial["completedUtc"]!.GetValue<string>(),
                        System.Globalization.CultureInfo.InvariantCulture);
                    trial["startedUtc"] = originalStarted
                        .ToOffset(TimeSpan.FromHours(-7))
                        .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                    trial["completedUtc"] = originalCompleted
                        .ToOffset(TimeSpan.FromHours(-7))
                        .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "timestamp-outside-report":
                    DateTimeOffset reportCompleted = DateTimeOffset.Parse(
                        report["completedUtc"]!.GetValue<string>(),
                        System.Globalization.CultureInfo.InvariantCulture);
                    trial["startedUtc"] = reportCompleted.AddMilliseconds(1);
                    trial["completedUtc"] = reportCompleted.AddMilliseconds(2);
                    break;
                case "first-start-equals-report":
                    FindFirstViewportTrialByGlobalOrdinal(viewports, 0)["startedUtc"] =
                        report["startedUtc"]!.DeepClone();
                    break;
                case "adjacent-timestamps-equal":
                    JsonObject precedingTrial = FindFirstViewportTrialByGlobalOrdinal(viewports, 0);
                    FindFirstViewportTrialByGlobalOrdinal(viewports, 1)["startedUtc"] =
                        precedingTrial["completedUtc"]!.DeepClone();
                    break;
                case "last-completion-equals-report":
                    FindFirstViewportTrialByGlobalOrdinal(viewports, 53)["completedUtc"] =
                        report["completedUtc"]!.DeepClone();
                    break;
                case "settling":
                    trial["settlingElapsedMilliseconds"] = -1;
                    break;
                case "cache-counter":
                    trial["sourceKeyHashCountAfterTrial"] = 100;
                    break;
                case "cache-proof":
                    trial["cacheProofPassed"] = false;
                    break;
                case "allocation":
                    trial["processAllocatedBytes"] = -1;
                    break;
                case "gc-hierarchy":
                    trial["gen0Collections"] = 0;
                    trial["gen1Collections"] = 1;
                    trial["gen2Collections"] = 1;
                    break;
                case "pin":
                    viewport["measurementThreadPriority"] = "Highest";
                    break;
                case "pin-consistency":
                    viewports[1]!["measurementThreadAffinityMask"] = "0x0000000000000002";
                    viewports[1]!["measurementProcessorNumber"] = 1;
                    break;
                case "processor-group":
                    viewport["measurementProcessorGroup"] = int.MaxValue;
                    break;
                case "processor-number":
                    viewport["measurementProcessorNumber"] = 1;
                    break;
                case "stationarity-policy":
                    viewport["stationarityPolicy"] = "wrong-policy";
                    break;
                case "dispersion":
                    viewport["regressionDispersionPercent"] =
                        viewport["regressionDispersionPercent"]!.GetValue<double>() + 1;
                    break;
                case "slope":
                    viewport["theilSenSlopeMillisecondsPerGlobalOrdinal"] =
                        viewport["theilSenSlopeMillisecondsPerGlobalOrdinal"]!.GetValue<double>() + 1;
                    break;
                case "projected-drift":
                    viewport["projectedDriftMilliseconds"] =
                        viewport["projectedDriftMilliseconds"]!.GetValue<double>() + 1;
                    break;
                case "boundary":
                    viewport["warmupBoundaryShiftMilliseconds"] =
                        viewport["warmupBoundaryShiftMilliseconds"]!.GetValue<double>() + 1;
                    break;
                case "allowance":
                    viewport["stationarityAllowanceMilliseconds"] =
                        viewport["stationarityAllowanceMilliseconds"]!.GetValue<double>() + 1;
                    break;
                case "stationarity-evaluated":
                    viewport["stationarityEvaluated"] = false;
                    break;
                case "stationarity-passed":
                    viewport["stationarityPassed"] = false;
                    break;
                case "order-drift":
                    ApplyOrderDriftMutation(viewports[1]!.AsObject());
                    break;
                case "catastrophic-trial":
                    ApplyCatastrophicTrialMutation(report, viewport);
                    break;
                case "boundary-endpoint":
                    ApplyBoundaryEndpointMutation(viewport);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
        }

        private static void ApplyOrderDriftMutation(JsonObject viewport)
        {
            JsonArray trials = viewport["trials"]!.AsArray();
            double[] orderedP95 = trials
                .Select(static trial => trial!["p95Milliseconds"]!.GetValue<double>())
                .Order()
                .ToArray();
            var pooled = new List<double>(600);
            var observations = new List<(int GlobalOrdinal, double Value)>(6);
            for (int ordinal = 0; ordinal < trials.Count; ordinal++)
            {
                JsonObject trial = trials[ordinal]!.AsObject();
                double value = orderedP95[ordinal];
                double[] samples = Enumerable.Repeat(value, 100).ToArray();
                trial["samplesMilliseconds"] = JsonSerializer.SerializeToNode(samples);
                trial["p95Milliseconds"] = value;
                pooled.AddRange(samples);
                observations.Add((trial["globalOrdinal"]!.GetValue<int>(), value));
            }

            viewport["samplesMilliseconds"] = JsonSerializer.SerializeToNode(pooled);
            viewport["p95Milliseconds"] = PerformanceStatistics.Percentile(pooled, 0.95);
            viewport["regressionP95Milliseconds"] =
                PerformanceStatistics.HodgesLehmann(orderedP95);
            viewport["regressionDispersionPercent"] =
                PerformanceStatistics.RobustDispersionPercent(orderedP95);
            viewport["theilSenSlopeMillisecondsPerGlobalOrdinal"] =
                PerformanceStatistics.TheilSenSlope(observations);
            viewport["projectedDriftMilliseconds"] =
                PerformanceStatistics.ProjectedTheilSenDrift(observations);
        }

        private static JsonObject FindFirstViewportTrialByGlobalOrdinal(
            JsonArray viewports,
            int globalOrdinal)
            => viewports
                .SelectMany(static viewport => viewport!["warmupTrials"]!.AsArray()
                    .Concat(viewport["trials"]!.AsArray()))
                .Select(static trial => trial!.AsObject())
                .Single(trial => trial["globalOrdinal"]!.GetValue<int>() == globalOrdinal);

        private static void ApplyCatastrophicTrialMutation(
            JsonObject report,
            JsonObject viewport)
        {
            JsonArray warmups = viewport["warmupTrials"]!.AsArray();
            foreach (JsonObject warmup in warmups.Select(static node => node!.AsObject()))
            {
                double[] samples = Enumerable.Repeat(1d, 100).ToArray();
                warmup["samplesMilliseconds"] = JsonSerializer.SerializeToNode(samples);
                warmup["p95Milliseconds"] = 1d;
            }

            JsonArray trials = viewport["trials"]!.AsArray();
            var pooled = new List<double>(600);
            var observations = new List<(int GlobalOrdinal, double Value)>(6);
            for (int ordinal = 0; ordinal < trials.Count; ordinal++)
            {
                JsonObject trial = trials[ordinal]!.AsObject();
                double[] samples = ordinal == 2
                    ? Enumerable.Repeat(1d, 94).Concat(Enumerable.Repeat(1_000d, 6)).ToArray()
                    : Enumerable.Repeat(1d, 100).ToArray();
                double p95 = PerformanceStatistics.Percentile(samples, 0.95);
                trial["samplesMilliseconds"] = JsonSerializer.SerializeToNode(samples);
                trial["p95Milliseconds"] = p95;
                pooled.AddRange(samples);
                observations.Add((trial["globalOrdinal"]!.GetValue<int>(), p95));
            }

            double[] trialP95 = observations.Select(static item => item.Value).ToArray();
            double regressionP95 = PerformanceStatistics.HodgesLehmann(trialP95);
            viewport["samplesMilliseconds"] = JsonSerializer.SerializeToNode(pooled);
            viewport["p95Milliseconds"] = PerformanceStatistics.Percentile(pooled, 0.95);
            viewport["regressionP95Milliseconds"] = regressionP95;
            viewport["regressionDispersionPercent"] =
                PerformanceStatistics.RobustDispersionPercent(trialP95);
            viewport["theilSenSlopeMillisecondsPerGlobalOrdinal"] =
                PerformanceStatistics.TheilSenSlope(observations);
            viewport["projectedDriftMilliseconds"] =
                PerformanceStatistics.ProjectedTheilSenDrift(observations);
            viewport["warmupBoundaryShiftMilliseconds"] = 0d;
            viewport["stationarityAllowanceMilliseconds"] = 5d;
            viewport["stationarityEvaluated"] = true;
            viewport["stationarityPassed"] = true;
            viewport["passed"] = true;

            JsonObject comparison = report["regression"]!["comparisons"]!.AsArray()
                .Select(static node => node!.AsObject())
                .Single(static item => item["metric"]!.GetValue<string>() ==
                    "firstViewport.cache-disabled.102400.p95Ms.hodgesLehmann");
            double reference = comparison["reference"]!.GetValue<double>();
            comparison["candidate"] = regressionP95;
            comparison["absoluteDelta"] = regressionP95 - reference;
            comparison["deltaPercent"] = ((regressionP95 / reference) - 1d) * 100d;
            comparison["candidateDispersionPercent"] = 0d;
            comparison["decision"] = "pass";
            comparison["passed"] = true;
        }

        private static void ApplyBoundaryEndpointMutation(JsonObject viewport)
        {
            JsonArray measuredTrials = viewport["trials"]!.AsArray();
            double measuredCenter = PerformanceStatistics.TwoValueCenter(
                measuredTrials[0]!["p95Milliseconds"]!.GetValue<double>(),
                measuredTrials[1]!["p95Milliseconds"]!.GetValue<double>());
            double allowance = viewport["stationarityAllowanceMilliseconds"]!.GetValue<double>();
            double lower = measuredCenter - allowance - 1d;
            double upper = measuredCenter + allowance + 1d;
            JsonArray warmups = viewport["warmupTrials"]!.AsArray();
            foreach ((int index, double value) in new[] { (1, lower), (2, upper) })
            {
                JsonObject warmup = warmups[index]!.AsObject();
                warmup["samplesMilliseconds"] = JsonSerializer.SerializeToNode(
                    Enumerable.Repeat(value, 100).ToArray());
                warmup["p95Milliseconds"] = value;
            }
            viewport["warmupBoundaryShiftMilliseconds"] = 0d;
        }

        private static void AddOne(JsonObject owner, string propertyName)
            => owner[propertyName] = owner[propertyName]!.GetValue<double>() + 1;

        private static void SwapPooledFirstViewportSamples(JsonObject report)
        {
            JsonArray samples = report["firstUsableViewport"]![0]!["samplesMilliseconds"]!.AsArray();
            JsonNode first = samples[0]!.DeepClone();
            JsonNode firstOfSecondTrial = samples[100]!.DeepClone();
            samples[0] = firstOfSecondTrial;
            samples[100] = first;
        }

        private void ApplyReferenceMutation(Action<JsonObject, JsonObject> mutation)
        {
            JsonObject baseline = JsonNode.Parse(File.ReadAllText(BaselinePath))!.AsObject();
            JsonObject candidate = JsonNode.Parse(File.ReadAllText(CandidatePath))!.AsObject();
            mutation(baseline, candidate);
            File.WriteAllText(BaselinePath, baseline.ToJsonString(PerformanceReport.JsonOptions));
            candidate["regression"]!["referenceReportSha256"] =
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(BaselinePath)));
            File.WriteAllText(CandidatePath, candidate.ToJsonString(PerformanceReport.JsonOptions));
        }

        internal GateResult RunGate(string reportPath, string mode)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = _directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(_scriptPath);
            startInfo.ArgumentList.Add("-ReportPath");
            startInfo.ArgumentList.Add(reportPath);
            startInfo.ArgumentList.Add("-RequiredRegressionMode");
            startInfo.ArgumentList.Add(mode);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start PowerShell for the external gate fixture.");
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The external performance gate fixture timed out.");
            }
            Task.WaitAll(standardOutput, standardError);
            string output = standardOutput.Result + Environment.NewLine + standardError.Result;
            return new GateResult(process.ExitCode == 0, output);
        }

        public void Dispose()
            => Directory.Delete(_directory, recursive: true);

        private static BuildArtifactHashes CreateArtifacts(
            string directory,
            out string executablePath)
        {
            BuildArtifactHash Capture(string fileName)
            {
                string path = Path.Combine(directory, fileName);
                string contents = fileName.EndsWith(".runtimeconfig.json", StringComparison.Ordinal)
                    ? """
                      {
                        "runtimeOptions": {
                          "configProperties": {
                            "System.Runtime.TieredCompilation": false,
                            "System.Runtime.TieredPGO": false,
                            "System.GC.Concurrent": false,
                            "System.Runtime.ReadyToRun": false
                          }
                        }
                      }
                      """
                    : $"schema-10-fixture:{fileName}";
                File.WriteAllText(path, contents);
                return new BuildArtifactHash
                {
                    FileName = fileName,
                    Path = path,
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                };
            }

            BuildArtifactHash executable = Capture("MarkdownRenderer.PerformanceHarness.exe");
            executablePath = executable.Path;
            return new BuildArtifactHashes
            {
                Executable = executable,
                PerformanceHarnessDll = Capture("MarkdownRenderer.PerformanceHarness.dll"),
                MarkdownRendererDll = Capture("MarkdownRenderer.dll"),
                MarkdownRendererCoreDll = Capture("MarkdownRenderer.Core.dll"),
                RuntimeConfig = Capture("MarkdownRenderer.PerformanceHarness.runtimeconfig.json"),
            };
        }

        private static PerformanceReport CreateReport(
            GateValues values,
            BuildArtifactHashes artifacts,
            string buildIdentity,
            bool candidate)
        {
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
            ScrollResult scroll = CreateScroll(values, candidate);
            var report = new PerformanceReport
            {
                ProviderName = PerformanceMeasurementContract.ProviderName,
                StartedUtc = startedUtc,
                CompletedUtc = startedUtc.AddSeconds(1),
                IsReleaseEvidence = true,
                BuildIdentity = buildIdentity,
                BuildArtifacts = artifacts,
                RuntimeConfiguration = new RuntimeConfigurationEvidence
                {
                    Policy = PerformanceMeasurementContract.RuntimeConfigurationPolicy,
                    TieredCompilationEnabled = false,
                    TieredPgoEnabled = false,
                    ConcurrentGcEnabled = false,
                    ReadyToRunEnabled = false,
                    OverrideEnvironmentVariables = [],
                    Passed = true,
                },
                Machine = CreateMachineMetadata(scroll, completion: false),
                CompletionMachine = CreateMachineMetadata(scroll, completion: true),
                Scroll = scroll,
                LifecyclePlateau = new LifecyclePlateauResult
                {
                    RequiredCycles = 100,
                    CompletedCycles = 100,
                    SyntheticDeviceResets = 4,
                    Checkpoint80ManagedBytes = 1,
                    Checkpoint100ManagedBytes = 1,
                    Checkpoint80PrivateBytes = 1,
                    Checkpoint100PrivateBytes = 1,
                    ManagedGrowthPercent = 0,
                    PrivateGrowthPercent = 0,
                    GrowthPercent = 0,
                    RenderFailures = 0,
                    Passed = true,
                },
                SourceLookup = CreateLookup(values, candidate),
                Cancellation = CreateCancellation(values, candidate),
                Regression = new RegressionResult
                {
                    Mode = "baseline",
                    DecisionPolicy = PerformanceMeasurementContract.RegressionDecisionPolicy,
                    ReferenceEligible = true,
                    MachineComparable = true,
                    Passed = true,
                },
                Passed = true,
            };

            int scenario = 0;
            foreach (int sourceBytes in new[] { 100 * 1024, 1024 * 1024, 10 * 1024 * 1024 })
            {
                foreach (string mode in new[]
                         {
                             PerformanceMeasurementContract.FirstViewportCacheDisabledMode,
                             PerformanceMeasurementContract.FirstViewportCacheHitMode,
                         })
                {
                    double[] trialMeasurements = candidate
                        ? values.FirstViewportCandidateTrialP95Milliseconds[scenario]
                        : values.FirstViewportReferenceTrialP95Milliseconds[scenario];

                    FirstViewportTrialResult CreateTrial(
                        int ordinal,
                        bool warmup,
                        double measurement)
                    {
                        int schedulePosition = Enumerable.Range(0, 6).Single(position =>
                            PerformanceMeasurementContract.GetFirstViewportCondition(
                                ordinal,
                                position) == scenario);
                        int globalOrdinal = (warmup ? 0 : 18) + ordinal * 6 + schedulePosition;
                        bool cacheHit = mode == PerformanceMeasurementContract.FirstViewportCacheHitMode;
                        DateTimeOffset trialStartedUtc = startedUtc.AddMilliseconds(globalOrdinal * 2 + 1);
                        return new FirstViewportTrialResult
                        {
                            Ordinal = ordinal,
                            GlobalOrdinal = globalOrdinal,
                            ScheduleRow = ordinal,
                            SchedulePosition = schedulePosition,
                            StartedUtc = trialStartedUtc,
                            CompletedUtc = trialStartedUtc.AddMilliseconds(1),
                            SettlingElapsedMilliseconds = measurement,
                            SamplesMilliseconds = Enumerable.Repeat(measurement, 100).ToList(),
                            P95Milliseconds = measurement,
                            Gen0Collections = 0,
                            Gen1Collections = 0,
                            Gen2Collections = 0,
                            ProcessAllocatedBytes = 1_000,
                            CompletedParseCountBeforePrime = 0,
                            CompletedParseCountAfterPrime = cacheHit ? 1 : 0,
                            CompletedParseCountAfterTrial = cacheHit ? 1 : 0,
                            SourceKeyHashCountBeforePrime = 0,
                            SourceKeyHashCountAfterPrime = cacheHit ? 1 : 0,
                            SourceKeyHashCountAfterTrial = cacheHit ? 1 : 101,
                            CacheProofPassed = true,
                            Complete = true,
                        };
                    }

                    List<FirstViewportTrialResult> warmupTrials = Enumerable.Range(0, 3)
                        .Select(ordinal => CreateTrial(
                            ordinal,
                            warmup: true,
                            trialMeasurements[Math.Min(ordinal, trialMeasurements.Length - 1)]))
                        .ToList();
                    List<FirstViewportTrialResult> trials = trialMeasurements
                        .Select((measurement, ordinal) => CreateTrial(
                            ordinal,
                            warmup: false,
                            measurement))
                        .ToList();
                    List<double> pooledSamples = trials
                        .SelectMany(static trial => trial.SamplesMilliseconds)
                        .ToList();
                    double budget = sourceBytes switch
                    {
                        100 * 1024 when mode == PerformanceMeasurementContract.FirstViewportCacheHitMode => 75,
                        100 * 1024 => 125,
                        1024 * 1024 => 250,
                        _ => 750,
                    };
                    double regressionP95 = PerformanceStatistics.HodgesLehmann(
                        trials.Select(static trial => trial.P95Milliseconds));
                    double dispersion = PerformanceStatistics.RobustDispersionPercent(
                        trials.Select(static trial => trial.P95Milliseconds));
                    double slope = PerformanceStatistics.TheilSenSlope(
                        trials.Select(static trial =>
                            (trial.GlobalOrdinal, trial.P95Milliseconds)));
                    double projectedDrift = PerformanceStatistics.ProjectedTheilSenDrift(
                        trials.Select(static trial =>
                            (trial.GlobalOrdinal, trial.P95Milliseconds)));
                    double boundaryShift = Math.Abs(
                        PerformanceStatistics.TwoValueCenter(
                            warmupTrials[^2].P95Milliseconds,
                            warmupTrials[^1].P95Milliseconds) -
                        PerformanceStatistics.TwoValueCenter(
                            trials[0].P95Milliseconds,
                            trials[1].P95Milliseconds));
                    double stationarityAllowance = Math.Max(
                        PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
                            mode,
                            sourceBytes),
                        Math.Abs(regressionP95) *
                        PerformanceMeasurementContract.FirstViewportStationarityRelativeAllowancePercent /
                        100d);
                    report.FirstUsableViewport.Add(new FirstViewportResult
                    {
                        Corpus = PerformanceDocumentFactory.GetCorpusName(sourceBytes),
                        Mode = mode,
                        SourceUtf16Bytes = sourceBytes,
                        ParseCacheBudgetBytes = mode ==
                            PerformanceMeasurementContract.FirstViewportCacheDisabledMode
                                ? 0
                                : sourceBytes * 8L,
                        EngineReuseMode = PerformanceMeasurementContract.FirstViewportEngineReuseMode,
                        TrialStartPolicy = PerformanceMeasurementContract.FirstViewportTrialStartPolicy,
                        SchedulePolicy = PerformanceMeasurementContract.FirstViewportSchedulePolicy,
                        RegressionEstimator = PerformanceMeasurementContract.FirstViewportRegressionEstimator,
                        StationarityPolicy = PerformanceMeasurementContract.FirstViewportStationarityPolicy,
                        MeasurementThreadAffinityMask = "0x0000000000000001",
                        MeasurementProcessorGroup = 0,
                        MeasurementProcessorNumber = 0,
                        MeasurementThreadPriority = "Normal",
                        WarmupTrials = warmupTrials,
                        Trials = trials,
                        SamplesMilliseconds = pooledSamples,
                        P95Milliseconds = PerformanceStatistics.Percentile(pooledSamples, 0.95),
                        RegressionP95Milliseconds = regressionP95,
                        RegressionDispersionPercent = dispersion,
                        TheilSenSlopeMillisecondsPerGlobalOrdinal = slope,
                        ProjectedDriftMilliseconds = projectedDrift,
                        WarmupBoundaryShiftMilliseconds = boundaryShift,
                        StationarityAllowanceMilliseconds = stationarityAllowance,
                        StationarityEvaluated = true,
                        StationarityPassed = true,
                        BudgetMilliseconds = budget,
                        Passed = true,
                    });
                    scenario++;
                }

                long memoryBudget = sourceBytes * 8L + 32L * 1024 * 1024;
                report.RetainedMemory.Add(new RetainedMemoryResult
                {
                    Corpus = PerformanceDocumentFactory.GetCorpusName(sourceBytes),
                    SourceUtf16Bytes = sourceBytes,
                    ManagedDeltaBytes = memoryBudget,
                    PrivateDeltaBytes = memoryBudget,
                    BudgetBytes = memoryBudget,
                    Passed = true,
                });
            }

            return report;
        }

        private static MachineMetadata CreateMachineMetadata(
            ScrollResult scroll,
            bool completion)
            => new()
            {
                MachineInstanceSha256 = new string('A', 64),
                Cpu = "fixture-cpu",
                LogicalProcessorCount = 8,
                OsDescription = "fixture-os",
                OsVersion = "10.0.26100",
                OsArchitecture = "X64",
                ProcessArchitecture = "X64",
                FrameworkDescription = ".NET fixture",
                DpiScale = 1,
                ViewportWidthDips = 1100,
                ViewportHeightDips = 760,
                DisplayDeviceName = @"\\.\DISPLAY1",
                ConfiguredRefreshRateHz = 120,
                ObservedRefreshRateHz = completion
                    ? 0
                    : PerformanceReleaseEvidenceValidator.CalculateObservedRefreshRateHz(scroll),
                IsAtLeast120Hz = !completion,
                GpuAdapterLuid = "00000001:00000002",
                GpuAdapterIdentitySha256 = new string('C', 64),
                GpuDriverVersion = "31.0.15.1234",
                DisplayAdapterIdentitySha256 = new string('D', 64),
                DisplayAdapterLuid = "00000005:00000006",
                DisplayAdapterDriverVersion = "31.0.15.1234",
                ActivePowerSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
                UserConfiguredAcPowerModeGuid = "ded574b5-45a0-4f42-8737-46345c09c238",
                UserConfiguredDcPowerModeGuid = "961cc777-2547-4f9d-8174-7d86181b8a7a",
                PowerSource = "AC",
                EnergySaverState = "Off",
                EffectivePowerMode = "MaxPerformance",
                ProcessPriorityClass = "High",
                ProcessPowerThrottlingMode =
                    PerformanceMeasurementContract.ProcessPowerThrottlingMode,
                GcServer = false,
                GcLatencyMode = "Interactive",
            };

        private static ScrollResult CreateScroll(GateValues values, bool candidate)
        {
            double[] uiP95 = candidate
                ? values.ScrollCandidateUiP95Milliseconds
                : values.ScrollReferenceUiP95Milliseconds;
            double[] uiP99 = candidate
                ? values.ScrollCandidateUiP99Milliseconds
                : values.ScrollReferenceUiP99Milliseconds;
            double[] frameP95 = candidate
                ? values.ScrollCandidateFrameP95Milliseconds
                : values.ScrollReferenceFrameP95Milliseconds;
            double[] allocationP95 = candidate
                ? values.ScrollCandidateAllocationP95Bytes
                : values.ScrollReferenceAllocationP95Bytes;
            long frequency = Stopwatch.Frequency;
            var trials = new List<ScrollTrialResult>(5);
            for (int index = 0; index < 5; index++)
            {
                long uiP95Ticks = MillisecondsToTicks(uiP95[index], frequency);
                long uiP99Ticks = MillisecondsToTicks(uiP99[index], frequency);
                long frameP95Ticks = MillisecondsToTicks(frameP95[index], frequency);
                long allocation = checked((long)allocationP95[index]);
                List<long> uiTicks = Enumerable.Repeat(uiP95Ticks, 2_280)
                    .Concat(Enumerable.Repeat(uiP99Ticks, 120))
                    .ToList();
                List<long> frameTicks = Enumerable.Repeat(frameP95Ticks, 2_377)
                    .Concat(Enumerable.Repeat(MillisecondsToTicks(20, frequency), 23))
                    .ToList();
                List<long> allocations = Enumerable.Repeat(allocation, 2_400).ToList();
                trials.Add(new ScrollTrialResult
                {
                    Ordinal = index,
                    FirstLogicalFrameId = 10_000L + index * 10_000_000L,
                    RequestedFrames = 2_400,
                    MeasuredFrameIntervals = 2_400,
                    FramesWithRendererWork = 2_400,
                    UiThreadWorkElapsedTicks = uiTicks,
                    FrameIntervalElapsedTicks = frameTicks,
                    RendererOwnedAllocatedBytesPerFrame = allocations,
                    UiThreadWorkP95Milliseconds = uiP95[index],
                    UiThreadWorkP99Milliseconds = uiP99[index],
                    FrameTimeP95Milliseconds = frameP95[index],
                    FramesOver16_7Percent = 23 * 100.0 / 2_400,
                    MaximumFrameStallMilliseconds = 20,
                    AllocatedBytesPerFrameP95 = allocation,
                    MaximumAllocatedBytesPerFrame = allocation,
                    RendererOwnedAllocatedBytesPerFrameP95 = allocationP95[index],
                    MaximumRendererOwnedAllocatedBytesPerFrame = allocation,
                    Complete = true,
                });
            }

            List<long> pooledUiTicks = trials
                .SelectMany(static trial => trial.UiThreadWorkElapsedTicks)
                .ToList();
            List<long> pooledFrameTicks = trials
                .SelectMany(static trial => trial.FrameIntervalElapsedTicks)
                .ToList();
            List<long> pooledAllocations = trials
                .SelectMany(static trial => trial.RendererOwnedAllocatedBytesPerFrame)
                .ToList();
            double millisecondsPerTick = 1_000.0 / frequency;
            double pooledUiP95 = PerformanceStatistics.Percentile(pooledUiTicks, 0.95) *
                millisecondsPerTick;
            double pooledUiP99 = PerformanceStatistics.Percentile(pooledUiTicks, 0.99) *
                millisecondsPerTick;
            double pooledFrameP95 = PerformanceStatistics.Percentile(pooledFrameTicks, 0.95) *
                millisecondsPerTick;
            double pooledFramesOver = pooledFrameTicks.Count(ticks =>
                ticks * millisecondsPerTick > 16.7) * 100.0 / pooledFrameTicks.Count;
            double pooledAllocationP95 = PerformanceStatistics.Percentile(pooledAllocations, 0.95);
            long maximumAllocation = pooledAllocations.Max();
            return new ScrollResult
            {
                Corpus = PerformanceDocumentFactory.WarmScrollStressCorpus,
                SourceUtf16Bytes = 1024 * 1024,
                StopwatchFrequency = frequency,
                RequestedFrames = 12_000,
                MeasuredFrameIntervals = 12_000,
                FramesWithRendererWork = 12_000,
                RegressionEstimator = PerformanceMeasurementContract.ScrollRegressionEstimator,
                Trials = trials,
                RegressionUiThreadWorkP95Milliseconds = PerformanceStatistics.HodgesLehmann(uiP95),
                RegressionUiThreadWorkP99Milliseconds = PerformanceStatistics.HodgesLehmann(uiP99),
                RegressionFrameTimeP95Milliseconds = PerformanceStatistics.HodgesLehmann(frameP95),
                RegressionRendererOwnedAllocatedBytesPerFrameP95 =
                    PerformanceStatistics.HodgesLehmann(allocationP95),
                UiThreadWorkP95Milliseconds = pooledUiP95,
                UiThreadWorkP99Milliseconds = pooledUiP99,
                FrameTimeP95Milliseconds = pooledFrameP95,
                FramesOver16_7Percent = pooledFramesOver,
                MaximumFrameStallMilliseconds = 20,
                AllocatedBytesPerFrameP95 = pooledAllocationP95,
                MaximumAllocatedBytesPerFrame = maximumAllocation,
                RendererOwnedAllocatedBytesPerFrameP95 = pooledAllocationP95,
                MaximumRendererOwnedAllocatedBytesPerFrame = maximumAllocation,
                Passed = true,
            };
        }

        private static long MillisecondsToTicks(double milliseconds, long frequency)
            => checked((long)Math.Round(
                milliseconds * frequency / 1_000.0,
                MidpointRounding.AwayFromZero));

        private static SourceLookupResult CreateLookup(GateValues values, bool candidate)
        {
            const long absoluteTicks = 5;
            long[] regressionTicks = candidate
                ? values.SourceLookupCandidateTrialBatchTicks
                : values.SourceLookupReferenceTrialBatchTicks;
            double nanosecondsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
            var trials = new List<SourceLookupTrialResult>(
                PerformanceMeasurementContract.SourceLookupRegressionTrialCount);
            for (int ordinal = 0; ordinal < regressionTicks.Length; ordinal++)
            {
                double nanoseconds = regressionTicks[ordinal] * nanosecondsPerTick /
                    PerformanceMeasurementContract.SourceLookupRegressionBatchSize;
                trials.Add(new SourceLookupTrialResult
                {
                    Ordinal = ordinal,
                    ObservationCount = PerformanceMeasurementContract.SourceLookupRegressionObservationCount,
                    OperationCount = PerformanceMeasurementContract.SourceLookupRegressionOperationCount,
                    SamplesElapsedTicks = Enumerable.Repeat(
                        regressionTicks[ordinal],
                        PerformanceMeasurementContract.SourceLookupRegressionObservationCount).ToList(),
                    SamplesNanosecondsPerLookup = Enumerable.Repeat(
                        nanoseconds,
                        PerformanceMeasurementContract.SourceLookupRegressionObservationCount).ToList(),
                    P95Nanoseconds = nanoseconds,
                    AllocatedBytes = 0,
                    Complete = true,
                });
            }

            return new SourceLookupResult
            {
                MappingCount = 100_000,
                QueryCount = 20_000,
                StopwatchFrequency = Stopwatch.Frequency,
                MeasurementThreadAffinityMask = "0x0000000000000001",
                MeasurementProcessorGroup = 0,
                MeasurementProcessorNumber = 0,
                MeasurementThreadPriority = "Highest",
                SamplesElapsedTicks = Enumerable.Repeat(absoluteTicks, 20_000).ToList(),
                P95Nanoseconds = absoluteTicks * nanosecondsPerTick,
                AllocatedBytes = 0,
                RegressionWarmupPasses = PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses,
                RegressionBatchSize = PerformanceMeasurementContract.SourceLookupRegressionBatchSize,
                RegressionObservationCount = PerformanceMeasurementContract.SourceLookupRegressionObservationCount,
                RegressionOperationCount = PerformanceMeasurementContract.SourceLookupRegressionOperationCount,
                RegressionEstimator = PerformanceMeasurementContract.SourceLookupRegressionEstimator,
                RegressionTrialCount = PerformanceMeasurementContract.SourceLookupRegressionTrialCount,
                RegressionTrials = trials,
                RegressionP95Nanoseconds = PerformanceStatistics.HodgesLehmann(
                    trials.Select(static trial => trial.P95Nanoseconds)),
                RegressionAllocatedBytes = trials.Sum(static trial => trial.AllocatedBytes),
                Passed = true,
            };
        }

        private static CancellationResult CreateCancellation(GateValues values, bool candidate)
        {
            double[] measurements = candidate
                ? values.CancellationCandidateTrialP95Milliseconds
                : values.CancellationReferenceTrialP95Milliseconds;
            double[] warmups = candidate
                ? values.CancellationCandidateWarmupMilliseconds
                : values.CancellationReferenceWarmupMilliseconds;
            var trials = new List<CancellationTrialResult>(
                PerformanceMeasurementContract.ReleaseCancellationTrials);
            for (int ordinal = 0; ordinal < PerformanceMeasurementContract.ReleaseCancellationTrials; ordinal++)
            {
                IReadOnlyList<CancellationMeasurementIteration> plan =
                    CancellationMeasurementPlan.CreateTrial(
                        ordinal,
                        PerformanceMeasurementContract.ReleaseCancellationIterations);
                trials.Add(new CancellationTrialResult
                {
                    Ordinal = ordinal,
                    WarmupSourceSeed = plan[0].SourceSeed,
                    WarmupElapsedMilliseconds = warmups[ordinal],
                    WarmupStaleCommits = 0,
                    FirstRecordedSourceSeed = plan[1].SourceSeed,
                    RequestedIterations = PerformanceMeasurementContract.ReleaseCancellationIterations,
                    ObservedCancellations = PerformanceMeasurementContract.ReleaseCancellationIterations,
                    SamplesMilliseconds = Enumerable.Repeat(
                        measurements[ordinal],
                        PerformanceMeasurementContract.ReleaseCancellationIterations).ToList(),
                    P95Milliseconds = measurements[ordinal],
                    StaleCommits = 0,
                    Complete = true,
                });
            }

            List<double> pooledSamples = trials
                .SelectMany(static trial => trial.SamplesMilliseconds)
                .ToList();
            return new CancellationResult
            {
                RequestedIterations = pooledSamples.Count,
                IterationsPerTrial = PerformanceMeasurementContract.ReleaseCancellationIterations,
                WarmupIterations = trials.Count,
                WarmupElapsedMilliseconds = PerformanceStatistics.Percentile(warmups, 0.95),
                WarmupStaleCommits = 0,
                ObservedCancellations = pooledSamples.Count,
                TrialStartPolicy = PerformanceMeasurementContract.CancellationTrialStartPolicy,
                RegressionEstimator = PerformanceMeasurementContract.CancellationRegressionEstimator,
                Trials = trials,
                SamplesMilliseconds = pooledSamples,
                P95Milliseconds = PerformanceStatistics.Percentile(pooledSamples, 0.95),
                RegressionP95Milliseconds = PerformanceStatistics.HodgesLehmann(measurements),
                P95BudgetMilliseconds = CancellationMeasurementPlan.AbsoluteBudgetMilliseconds,
                StaleCommits = 0,
                Passed = true,
            };
        }
    }

    private sealed class GateValues
    {
        public double[][] FirstViewportReferenceTrialP95Milliseconds { get; init; } = [];
        public double[][] FirstViewportCandidateTrialP95Milliseconds { get; init; } = [];
        public double[] ScrollReferenceUiP95Milliseconds { get; init; } = [];
        public double[] ScrollCandidateUiP95Milliseconds { get; init; } = [];
        public double[] ScrollReferenceUiP99Milliseconds { get; init; } = [];
        public double[] ScrollCandidateUiP99Milliseconds { get; init; } = [];
        public double[] ScrollReferenceFrameP95Milliseconds { get; init; } = [];
        public double[] ScrollCandidateFrameP95Milliseconds { get; init; } = [];
        public double[] ScrollReferenceAllocationP95Bytes { get; init; } = [];
        public double[] ScrollCandidateAllocationP95Bytes { get; init; } = [];
        public long[] SourceLookupReferenceTrialBatchTicks { get; init; } = [];
        public long[] SourceLookupCandidateTrialBatchTicks { get; init; } = [];
        public double[] CancellationReferenceTrialP95Milliseconds { get; init; } = [];
        public double[] CancellationCandidateTrialP95Milliseconds { get; init; } = [];
        public double[] CancellationReferenceWarmupMilliseconds { get; init; } = [];
        public double[] CancellationCandidateWarmupMilliseconds { get; init; } = [];
    }

    private readonly record struct GateResult(bool Passed, string Output);
}
