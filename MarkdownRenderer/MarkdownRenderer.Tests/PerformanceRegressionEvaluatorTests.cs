using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MarkdownRenderer.PerformanceHarness;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PerformanceRegressionEvaluatorTests
{
    private const int TargetViewportBytes = 100 * 1024;
    private const string TargetViewportMode =
        PerformanceMeasurementContract.FirstViewportCacheDisabledMode;

    [Fact]
    public void CompleteMachineEnvironmentSnapshotsAreAccepted()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("complete-machine");
        PerformanceReport report = CreateReport(artifacts.Hashes, "complete-machine");

        Assert.True(PerformanceReleaseEvidenceValidator.IsEnvironmentSnapshotComplete(report.Machine));
        Assert.True(PerformanceReleaseEvidenceValidator.IsEnvironmentSnapshotComplete(report.CompletionMachine));
        Assert.True(PerformanceReleaseEvidenceValidator.IsSameRunEnvironment(report.Machine, report.CompletionMachine));
        Assert.True(PerformanceReleaseEvidenceValidator.HasCompleteMeasurementPayload(report));
    }

    [Fact]
    public void HodgesLehmannUsesTheMedianOfAllWalshAverages()
    {
        double estimate = PerformanceStatistics.HodgesLehmann([1, 1, 1, 10, 10]);

        Assert.Equal(5.5, estimate);
    }

    [Fact]
    public void RobustDispersionUsesMadAroundTheSampleMedianAndHlForScale()
    {
        double dispersion = PerformanceStatistics.RobustDispersionPercent([1, 1, 2, 10, 10]);

        Assert.Equal(1.4826 / 5.5 * 100, dispersion, 10);
        Assert.True(dispersion > PerformanceMeasurementContract.FirstViewportDispersionBudgetPercent);
    }

    [Fact]
    public void CandidateBindsTheExactEligibleReferenceBytesAndUsesRobustEstimators()
    {
        string referencePath = NewTemporaryPath();
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("same-build");
        try
        {
            PerformanceReport reference = CreateReport(artifacts.Hashes, "same-build");
            byte[] referenceBytes = JsonSerializer.SerializeToUtf8Bytes(
                reference,
                PerformanceReport.JsonOptions);
            File.WriteAllBytes(referencePath, referenceBytes);

            RegressionResult result = RegressionEvaluator.Evaluate(
                CreateReport(artifacts.Hashes, "same-build"),
                CandidateOptions(referencePath));

            Assert.True(result.ReferenceEligible);
            Assert.True(result.MachineComparable);
            Assert.True(result.Passed);
            Assert.Equal(
                PerformanceMeasurementContract.RegressionDecisionPolicy,
                result.DecisionPolicy);
            Assert.Equal(11, result.ComparedLatencyMetrics);
            Assert.Equal(1, result.ComparedAllocationMetrics);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(referenceBytes)),
                result.ReferenceReportSha256);
            Assert.Contains(
                result.Comparisons,
                comparison => comparison.Metric ==
                    "scroll.uiThreadWorkP99Ms.hodgesLehmann");
            Assert.Contains(
                result.Comparisons,
                comparison => comparison.Metric ==
                    "sourceLookup.regressionP95Ns.hodgesLehmann");
            Assert.Contains(
                result.Comparisons,
                comparison => comparison.Metric == "cancellation.p95Ms.hodgesLehmann");
            Assert.DoesNotContain(
                result.Comparisons,
                comparison => comparison.Metric == "sourceLookup.p95Ns");
        }
        finally
        {
            File.Delete(referencePath);
        }
    }

    [Fact]
    public void CandidateRejectsASchema9Reference()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("schema-9");
        RegressionResult result = EvaluateCandidate(
            CreateReport(artifacts.Hashes, "schema-9", schemaVersion: 9),
            CreateReport(artifacts.Hashes, "schema-9"));

        Assert.False(result.ReferenceEligible);
        Assert.False(result.Passed);
    }

    [Fact]
    public void EqualBuildIdentityRequiresAllArtifactHashesToMatch()
    {
        using ArtifactDirectory referenceArtifacts = ArtifactDirectory.Create("reference");
        using ArtifactDirectory candidateArtifacts = ArtifactDirectory.Create("candidate");
        string sharedIdentity = candidateArtifacts.Identity;

        RegressionResult result = EvaluateCandidate(
            CreateReport(referenceArtifacts.Hashes, sharedIdentity),
            CreateReport(candidateArtifacts.Hashes, sharedIdentity));

        Assert.False(result.ReferenceEligible);
        Assert.False(result.Passed);
    }

    [Fact]
    public void DifferentBuildIdentitiesMayUseDifferentValidArtifactHashes()
    {
        using ArtifactDirectory referenceArtifacts = ArtifactDirectory.Create("reference");
        using ArtifactDirectory candidateArtifacts = ArtifactDirectory.Create("candidate");

        RegressionResult result = EvaluateCandidate(
            CreateReport(referenceArtifacts.Hashes, "accepted-reference"),
            CreateReport(candidateArtifacts.Hashes, "candidate-revision"));

        Assert.True(result.ReferenceEligible);
        Assert.True(result.Passed);
    }

    [Fact]
    public void DifferentDeploymentIdentitiesMayShareFiveExplicitArtifactHashes()
    {
        using ArtifactDirectory referenceArtifacts = ArtifactDirectory.Create("identical-artifacts");
        using ArtifactDirectory candidateArtifacts = ArtifactDirectory.Create("identical-artifacts");
        referenceArtifacts.WriteDependency("Markdig.dll", "reference dependency");
        candidateArtifacts.WriteDependency("Markdig.dll", "candidate dependency");
        PerformanceReport reference = CreateReport(referenceArtifacts.Hashes, "reference-revision");
        PerformanceReport candidate = CreateReport(candidateArtifacts.Hashes, "candidate-revision");

        Assert.True(PerformanceArtifactContract.HaveIdenticalHashes(
            reference.BuildArtifacts,
            candidate.BuildArtifacts));
        Assert.NotEqual(reference.BuildIdentity, candidate.BuildIdentity);
        RegressionResult result = EvaluateCandidate(reference, candidate);

        Assert.True(result.ReferenceEligible);
        Assert.True(result.Passed);
    }

    [Fact]
    public void CandidateRejectsRuntimeDependencyMutationAfterIdentityCapture()
    {
        using ArtifactDirectory referenceArtifacts = ArtifactDirectory.Create("reference");
        using ArtifactDirectory candidateArtifacts = ArtifactDirectory.Create("candidate");
        referenceArtifacts.WriteDependency("Markdig.dll", "reference dependency");
        candidateArtifacts.WriteDependency("Markdig.dll", "candidate dependency");
        PerformanceReport reference = CreateReport(referenceArtifacts.Hashes, "reference-revision");
        PerformanceReport candidate = CreateReport(candidateArtifacts.Hashes, "candidate-revision");
        candidateArtifacts.WriteDependency("Markdig.dll", "mutated after capture");

        RegressionResult result = EvaluateCandidate(reference, candidate);

        Assert.True(result.ReferenceEligible);
        Assert.False(result.MachineComparable);
        Assert.False(result.Passed);
    }

    [Fact]
    public void CrossRevisionReferenceUsesHashBoundStoredArtifactsWithoutRehashingOldPaths()
    {
        string referencePath = NewTemporaryPath();
        using ArtifactDirectory referenceArtifacts = ArtifactDirectory.Create("accepted-reference");
        using ArtifactDirectory candidateArtifacts = ArtifactDirectory.Create("candidate");
        try
        {
            File.WriteAllText(
                referencePath,
                JsonSerializer.Serialize(
                    CreateReport(referenceArtifacts.Hashes, "accepted-reference"),
                    PerformanceReport.JsonOptions));
            referenceArtifacts.DeleteFiles();

            RegressionResult result = RegressionEvaluator.Evaluate(
                CreateReport(candidateArtifacts.Hashes, "candidate-revision"),
                CandidateOptions(referencePath));

            Assert.True(result.ReferenceEligible);
            Assert.True(result.Passed);
        }
        finally
        {
            File.Delete(referencePath);
        }
    }

    [Fact]
    public void CandidateRejectsSameIdentityReferenceWithDifferentStoredArtifactHash()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("tampered");
        BuildArtifactHashes invalid = artifacts.WithExecutableHash(new string('0', 64));

        RegressionResult result = EvaluateCandidate(
            CreateReport(invalid, "tampered"),
            CreateReport(artifacts.Hashes, "tampered"));

        Assert.False(result.ReferenceEligible);
        Assert.False(result.Passed);
    }

    [Fact]
    public void CandidateRejectsMalformedStoredReferenceArtifactHash()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("malformed");
        BuildArtifactHashes invalid = artifacts.WithExecutableHash("not-a-sha256");

        RegressionResult result = EvaluateCandidate(
            CreateReport(invalid, "accepted-reference"),
            CreateReport(artifacts.Hashes, "candidate-revision"));

        Assert.False(result.ReferenceEligible);
        Assert.False(result.Passed);
    }

    [Fact]
    public void SameBuildIdentityAlsoBindsTheRuntimeConfigurationArtifactHash()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("runtimeconfig-identity");
        BuildArtifactHashes differentRuntimeConfig =
            artifacts.WithRuntimeConfigHash(new string('0', 64));

        RegressionResult result = EvaluateCandidate(
            CreateReport(differentRuntimeConfig, "runtimeconfig-identity"),
            CreateReport(artifacts.Hashes, "runtimeconfig-identity"));

        Assert.False(result.ReferenceEligible);
        Assert.False(result.Passed);
    }

    [Fact]
    public void CandidateRejectsAReferenceMeasuredOnADifferentLogicalProcessor()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("affinity");
        RegressionResult result = EvaluateCandidate(
            CreateReport(artifacts.Hashes, "affinity", mutation: "valid-affinity-two"),
            CreateReport(artifacts.Hashes, "affinity"));

        Assert.True(result.ReferenceEligible);
        Assert.False(result.MachineComparable);
        Assert.False(result.Passed);
    }

    [Fact]
    public void CandidateWithUnqualifiedObservedRefreshIsNotMachineComparable()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("refresh");
        RegressionResult result = EvaluateCandidate(
            CreateReport(artifacts.Hashes, "refresh"),
            CreateReport(artifacts.Hashes, "refresh", observedRefreshRateHz: 114));

        Assert.True(result.ReferenceEligible);
        Assert.False(result.MachineComparable);
        Assert.False(result.Passed);
    }

    [Fact]
    public void DifferentQualifiedObservedRefreshMeasurementsRemainComparable()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("refresh-outcome");
        RegressionResult result = EvaluateCandidate(
            CreateReport(artifacts.Hashes, "refresh-outcome", observedRefreshRateHz: 120),
            CreateReport(artifacts.Hashes, "refresh-outcome", observedRefreshRateHz: 121));

        Assert.True(result.ReferenceEligible);
        Assert.True(result.MachineComparable);
        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("machine-fingerprint-different")]
    [InlineData("os-description-different")]
    [InlineData("display-device-different")]
    [InlineData("gpu-identity-different")]
    [InlineData("gpu-driver-different")]
    [InlineData("display-gpu-identity-different")]
    [InlineData("display-gpu-driver-different")]
    [InlineData("power-scheme-different")]
    [InlineData("ac-power-mode-different")]
    [InlineData("dc-power-mode-different")]
    [InlineData("configured-power-modes-unsupported")]
    [InlineData("effective-power-mode-different")]
    [InlineData("effective-power-mode-unsupported")]
    [InlineData("power-source-different")]
    [InlineData("energy-saver-different")]
    [InlineData("priority-different")]
    [InlineData("gc-server-different")]
    [InlineData("gc-latency-different")]
    public void CandidateRejectsADifferentMeasurementEnvironment(string mutation)
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("machine-environment");
        RegressionResult result = EvaluateCandidate(
            CreateReport(artifacts.Hashes, "machine-environment"),
            CreateReport(artifacts.Hashes, "machine-environment", mutation: mutation));

        Assert.True(result.ReferenceEligible);
        Assert.False(result.MachineComparable);
        Assert.False(result.Passed);
    }

    [Fact]
    public void DifferentPerBootGpuLuidsRemainComparableWhenStableAdapterIdentityMatches()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("gpu-luid");
        RegressionResult result = EvaluateCandidate(
            CreateReport(artifacts.Hashes, "gpu-luid"),
            CreateReport(artifacts.Hashes, "gpu-luid", mutation: "gpu-luid-different"));

        Assert.True(result.ReferenceEligible);
        Assert.True(result.MachineComparable);
        Assert.True(result.Passed);
    }

    [Fact]
    public void CandidateRejectsUncontrolledProcessPowerThrottling()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("power-throttling");
        RegressionResult result = EvaluateCandidate(
            CreateReport(artifacts.Hashes, "power-throttling"),
            CreateReport(
                artifacts.Hashes,
                "power-throttling",
                mutation: "power-throttling-uncontrolled"));

        Assert.True(result.ReferenceEligible);
        Assert.False(result.MachineComparable);
        Assert.False(result.Passed);
    }

    [Fact]
    public void ConsistentlyUnsupportedConfiguredPowerModeApisRemainValid()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("unsupported-power-mode");
        RegressionResult result = EvaluateCandidate(
            CreateReport(
                artifacts.Hashes,
                "unsupported-power-mode",
                mutation: "configured-power-modes-unsupported"),
            CreateReport(
                artifacts.Hashes,
                "unsupported-power-mode",
                mutation: "configured-power-modes-unsupported"));

        Assert.True(result.ReferenceEligible);
        Assert.True(result.MachineComparable);
        Assert.True(result.Passed);
    }

    [Fact]
    public void UnsupportedEffectivePowerModeApiRemainsValidWhenBothReportsMatch()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("unsupported-effective-power");
        RegressionResult result = EvaluateCandidate(
            CreateReport(
                artifacts.Hashes,
                "unsupported-effective-power",
                mutation: "effective-power-mode-unsupported"),
            CreateReport(
                artifacts.Hashes,
                "unsupported-effective-power",
                mutation: "effective-power-mode-unsupported"));

        Assert.True(result.ReferenceEligible);
        Assert.True(result.MachineComparable);
        Assert.True(result.Passed);
    }

    [Fact]
    public void HybridFloorDominatesRelativeAllowanceAndIsInclusive()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("floor-inclusive");
        RegressionResult result = EvaluateCandidate(
            CreateReport(
                artifacts.Hashes,
                "floor-inclusive",
                targetViewportTrialP95Milliseconds: RepeatTrialValues(10)),
            CreateReport(
                artifacts.Hashes,
                "floor-inclusive",
                targetViewportTrialP95Milliseconds: RepeatTrialValues(15)));

        RegressionComparison comparison = FindTargetViewportComparison(result);
        Assert.Equal(10, comparison.Reference);
        Assert.Equal(15, comparison.Candidate);
        Assert.Equal(5, comparison.AbsoluteDelta);
        Assert.Equal(0.5, comparison.RelativeAllowance, 10);
        Assert.Equal(5, comparison.AbsoluteNoiseFloor);
        Assert.Equal(5, comparison.AllowedAbsoluteDelta);
        Assert.Equal(0, comparison.ReferenceDispersionPercent);
        Assert.Equal(0, comparison.CandidateDispersionPercent);
        Assert.Equal(
            PerformanceMeasurementContract.FirstViewportDispersionBudgetPercent,
            comparison.StabilityBudgetPercent);
        Assert.Equal("pass", comparison.Decision);
        Assert.True(comparison.Passed);
        Assert.True(result.Passed);
    }

    [Fact]
    public void RelativeAllowanceDominatesNoiseFloorAndIsInclusive()
    {
        const int sourceBytes = 10 * 1024 * 1024;
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("relative-inclusive");
        RegressionResult result = EvaluateCandidate(
            CreateReport(
                artifacts.Hashes,
                "relative-inclusive",
                targetViewportTrialP95Milliseconds: RepeatTrialValues(100),
                targetViewportBytes: sourceBytes),
            CreateReport(
                artifacts.Hashes,
                "relative-inclusive",
                targetViewportTrialP95Milliseconds: RepeatTrialValues(105),
                targetViewportBytes: sourceBytes));

        RegressionComparison comparison = FindViewportComparison(
            result,
            sourceBytes,
            PerformanceMeasurementContract.FirstViewportCacheDisabledMode);
        Assert.Equal(5, comparison.RelativeAllowance);
        Assert.Equal(3, comparison.AbsoluteNoiseFloor);
        Assert.Equal(5, comparison.AllowedAbsoluteDelta);
        Assert.Equal("pass", comparison.Decision);
        Assert.True(result.Passed);
    }

    [Fact]
    public void CandidateOneIncrementOverHybridAllowanceIsARegression()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("over-floor");
        RegressionResult result = EvaluateCandidate(
            CreateReport(
                artifacts.Hashes,
                "over-floor",
                targetViewportTrialP95Milliseconds: RepeatTrialValues(10)),
            CreateReport(
                artifacts.Hashes,
                "over-floor",
                targetViewportTrialP95Milliseconds: RepeatTrialValues(15.001)));

        RegressionComparison comparison = FindTargetViewportComparison(result);
        Assert.True(comparison.AbsoluteDelta > comparison.AllowedAbsoluteDelta);
        Assert.Equal("regression", comparison.Decision);
        Assert.False(comparison.Passed);
        Assert.False(result.Passed);
    }

    [Fact]
    public void ExcessiveTrialDispersionMakesComparisonInconclusive()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("dispersion");
        RegressionResult result = EvaluateCandidate(
            CreateReport(
                artifacts.Hashes,
                "dispersion",
                targetViewportTrialP95Milliseconds: RepeatTrialValues(1)),
            CreateReport(
                artifacts.Hashes,
                "dispersion",
                targetViewportTrialP95Milliseconds: [1, 1, 1, 10, 10, 10]));

        RegressionComparison comparison = FindTargetViewportComparison(result);
        Assert.Equal(5.5, comparison.Candidate);
        Assert.True(
            comparison.CandidateDispersionPercent > comparison.StabilityBudgetPercent);
        Assert.Equal("inconclusive", comparison.Decision);
        Assert.False(comparison.Passed);
        Assert.False(result.Passed);
    }

    [Fact]
    public void ZeroReferenceUsesFiniteSentinelAndCanPassAtTheAbsoluteFloor()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("zero-reference");
        RegressionResult result = EvaluateCandidate(
            CreateReport(
                artifacts.Hashes,
                "zero-reference",
                targetViewportTrialP95Milliseconds: RepeatTrialValues(0)),
            CreateReport(
                artifacts.Hashes,
                "zero-reference",
                targetViewportTrialP95Milliseconds: RepeatTrialValues(5)));

        RegressionComparison comparison = FindTargetViewportComparison(result);
        Assert.Equal(double.MaxValue, comparison.DeltaPercent);
        Assert.Equal(0, comparison.RelativeAllowance);
        Assert.Equal(5, comparison.AllowedAbsoluteDelta);
        Assert.Equal("pass", comparison.Decision);
        Assert.True(comparison.Passed);
        Assert.True(result.Passed);
    }

    [Fact]
    public void ZeroReferenceAllocationCanPassAtTheAbsoluteFloor()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("zero-allocation");
        RegressionResult result = EvaluateCandidate(
            CreateReport(
                artifacts.Hashes,
                "zero-allocation",
                scrollRendererOwnedAllocationBytes: 0),
            CreateReport(
                artifacts.Hashes,
                "zero-allocation",
                scrollRendererOwnedAllocationBytes:
                    (long)PerformanceMeasurementContract.RendererOwnedAllocationNoiseFloorBytes));

        RegressionComparison comparison = Assert.Single(
            result.Comparisons,
            item => item.Metric ==
                "scroll.rendererOwnedAllocatedBytesPerFrameP95.hodgesLehmann");
        Assert.Equal(double.MaxValue, comparison.DeltaPercent);
        Assert.Equal(0, comparison.RelativeAllowance);
        Assert.Equal(64, comparison.AbsoluteNoiseFloor);
        Assert.Equal(64, comparison.AllowedAbsoluteDelta);
        Assert.Equal("pass", comparison.Decision);
        Assert.True(result.Passed);
    }

    [Fact]
    public void StoredArtifactRoleRejectsAPathWithTheWrongFileName()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("aliased-path");
        BuildArtifactHashes aliased = new()
        {
            Executable = artifacts.Hashes.Executable,
            PerformanceHarnessDll = new BuildArtifactHash
            {
                FileName = artifacts.Hashes.PerformanceHarnessDll.FileName,
                Path = artifacts.Hashes.Executable.Path,
                Sha256 = artifacts.Hashes.Executable.Sha256,
            },
            MarkdownRendererDll = artifacts.Hashes.MarkdownRendererDll,
            MarkdownRendererCoreDll = artifacts.Hashes.MarkdownRendererCoreDll,
            RuntimeConfig = artifacts.Hashes.RuntimeConfig,
        };

        Assert.False(PerformanceArtifactContract.TryValidateStored(aliased, out _));
        Assert.False(PerformanceArtifactContract.TryValidateCurrentFiles(aliased, out _));
    }

    [Fact]
    public void CurrentArtifactValidationBindsRuntimeConfigurationBytes()
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("runtimeconfig-current-file");
        Assert.True(PerformanceArtifactContract.TryValidateCurrentFiles(artifacts.Hashes, out _));

        artifacts.MutateRuntimeConfig();

        Assert.False(PerformanceArtifactContract.TryValidateCurrentFiles(artifacts.Hashes, out _));
    }

    [Theory]
    [InlineData("lifecycle")]
    [InlineData("first-warmup")]
    [InlineData("first-trials")]
    [InlineData("cancellation-iterations-requirement")]
    [InlineData("cancellation-trials-requirement")]
    [InlineData("mapping")]
    [InlineData("absolute-query")]
    [InlineData("lookup-warmup")]
    [InlineData("lookup-trials")]
    [InlineData("lookup-batch")]
    [InlineData("lookup-observations")]
    [InlineData("lookup-operations")]
    [InlineData("actual-mapping")]
    [InlineData("actual-query")]
    [InlineData("actual-lookup-absolute-count")]
    [InlineData("actual-lookup-absolute-p95")]
    [InlineData("actual-lookup-allocated")]
    [InlineData("actual-lookup-warmup")]
    [InlineData("actual-lookup-estimator")]
    [InlineData("actual-lookup-trials")]
    [InlineData("actual-lookup-order")]
    [InlineData("actual-lookup-ticks")]
    [InlineData("actual-lookup-trial-p95")]
    [InlineData("actual-lookup-hl")]
    [InlineData("viewport-warmup-count")]
    [InlineData("viewport-cache")]
    [InlineData("viewport-engine-reuse")]
    [InlineData("viewport-trial-policy")]
    [InlineData("viewport-schedule-policy")]
    [InlineData("viewport-stationarity-policy")]
    [InlineData("viewport-affinity")]
    [InlineData("viewport-estimator")]
    [InlineData("viewport-trial-count")]
    [InlineData("viewport-trial-order")]
    [InlineData("viewport-global-order")]
    [InlineData("viewport-schedule-row")]
    [InlineData("viewport-schedule-position")]
    [InlineData("viewport-chronology")]
    [InlineData("viewport-timestamp-order")]
    [InlineData("viewport-before-report")]
    [InlineData("completed-before-last-viewport-trial")]
    [InlineData("viewport-settling")]
    [InlineData("viewport-priority")]
    [InlineData("viewport-cache-proof")]
    [InlineData("viewport-cache-counter")]
    [InlineData("viewport-cache-hit-counter")]
    [InlineData("viewport-gc")]
    [InlineData("viewport-gc-hierarchy")]
    [InlineData("viewport-allocation")]
    [InlineData("viewport-trial-p95")]
    [InlineData("viewport-flattened")]
    [InlineData("viewport-publication-count")]
    [InlineData("viewport-publication-settling")]
    [InlineData("viewport-publication-trial-maximum")]
    [InlineData("viewport-publication-flattened")]
    [InlineData("viewport-publication-aggregate-maximum")]
    [InlineData("viewport-publication-budget")]
    [InlineData("viewport-publication-overbudget")]
    [InlineData("viewport-regression")]
    [InlineData("viewport-dispersion")]
    [InlineData("viewport-dispersion-derived")]
    [InlineData("viewport-drift-series")]
    [InlineData("viewport-boundary-series")]
    [InlineData("viewport-residual-spike")]
    [InlineData("viewport-boundary-endpoint-spike")]
    [InlineData("viewport-slope")]
    [InlineData("viewport-projected-drift")]
    [InlineData("viewport-boundary-shift")]
    [InlineData("viewport-stationarity-allowance")]
    [InlineData("viewport-stationarity-evaluated")]
    [InlineData("viewport-stationarity-passed")]
    [InlineData("scroll-estimator")]
    [InlineData("scroll-trial-count")]
    [InlineData("scroll-trial-order")]
    [InlineData("scroll-first-frame")]
    [InlineData("scroll-requested-frames")]
    [InlineData("scroll-ui-raw-count")]
    [InlineData("scroll-ui-raw-negative")]
    [InlineData("scroll-frame-raw-count")]
    [InlineData("scroll-frame-raw-zero")]
    [InlineData("scroll-allocation-raw-count")]
    [InlineData("scroll-allocation-raw-negative")]
    [InlineData("scroll-trial-p95")]
    [InlineData("scroll-aggregate-p95")]
    [InlineData("scroll-regression")]
    [InlineData("observed-refresh-raw")]
    [InlineData("completion-display-changed")]
    [InlineData("completion-display-driver-changed")]
    [InlineData("completion-configured-power-mode-support-changed")]
    [InlineData("completion-effective-power-mode-changed")]
    [InlineData("completion-effective-power-mode-unsupported")]
    [InlineData("cancellation-policy")]
    [InlineData("cancellation-estimator")]
    [InlineData("cancellation-trial-count")]
    [InlineData("cancellation-trial-order")]
    [InlineData("cancellation-warmup-seed")]
    [InlineData("cancellation-recorded-seed")]
    [InlineData("cancellation-sample-count")]
    [InlineData("cancellation-trial-p95")]
    [InlineData("cancellation-flattened")]
    [InlineData("cancellation-regression")]
    [InlineData("cancellation-warmup-stale")]
    [InlineData("cancellation-stale")]
    [InlineData("cancellation-requested")]
    [InlineData("cancellation-observed")]
    [InlineData("regression-decision-policy")]
    [InlineData("runtime-policy")]
    [InlineData("runtime-tiering")]
    [InlineData("runtime-override")]
    [InlineData("runtime-passed")]
    [InlineData("extra-viewport")]
    [InlineData("provider")]
    [InlineData("build-identity")]
    [InlineData("started")]
    [InlineData("completed")]
    [InlineData("retained-count")]
    [InlineData("retained-budget")]
    [InlineData("lifecycle-result")]
    [InlineData("top-failure")]
    [InlineData("top-passed")]
    [InlineData("machine-fingerprint-missing")]
    [InlineData("display-driver-missing")]
    [InlineData("configured-power-mode-support-mixed")]
    [InlineData("effective-power-mode-invalid")]
    public void CandidateRejectsReferenceThatDoesNotMatchTheFrozenReleaseShape(string mutation)
    {
        using ArtifactDirectory artifacts = ArtifactDirectory.Create("reference-shape");
        PerformanceReport reference = CreateReport(
            artifacts.Hashes,
            "reference-shape",
            sampleRequirements: CreateRequirements(mutation),
            mutation: mutation);
        if (mutation == "extra-viewport")
        {
            reference.FirstUsableViewport.Add(new FirstViewportResult
            {
                Corpus = "extra",
                Mode = "extra",
                SourceUtf16Bytes = 42,
                P95Milliseconds = 1,
            });
        }

        RegressionResult result = EvaluateCandidate(
            reference,
            CreateReport(artifacts.Hashes, "reference-shape"));

        Assert.False(result.ReferenceEligible);
        Assert.False(result.Passed);
    }

    private static PerformanceOptions CandidateOptions(string referencePath)
        => PerformanceOptions.Parse(
        [
            "--output",
            NewTemporaryPath(),
            "--reference",
            referencePath,
        ]);

    private static RegressionResult EvaluateCandidate(
        PerformanceReport reference,
        PerformanceReport candidate)
    {
        string referencePath = NewTemporaryPath();
        try
        {
            File.WriteAllText(
                referencePath,
                JsonSerializer.Serialize(reference, PerformanceReport.JsonOptions));
            return RegressionEvaluator.Evaluate(candidate, CandidateOptions(referencePath));
        }
        finally
        {
            File.Delete(referencePath);
        }
    }

    private static RegressionComparison FindTargetViewportComparison(RegressionResult result)
        => FindViewportComparison(result, TargetViewportBytes, TargetViewportMode);

    private static RegressionComparison FindViewportComparison(
        RegressionResult result,
        int sourceBytes,
        string mode)
        => Assert.Single(
            result.Comparisons,
            comparison => comparison.Metric ==
                $"firstViewport.{mode}.{sourceBytes}.p95Ms.hodgesLehmann");

    private static double[] RepeatTrialValues(double value)
        => Enumerable.Repeat(
            value,
            PerformanceMeasurementContract.ReleaseFirstViewportTrials).ToArray();

    private static double NormalizeObservedRefreshRate(double requestedRefreshRateHz)
    {
        long intervalTicks = checked((long)Math.Round(
            Stopwatch.Frequency / requestedRefreshRateHz));
        return Stopwatch.Frequency / (double)intervalTicks;
    }

    private static MachineMetadata CreateCompletionMachine(
        MachineMetadata start,
        string? mutation)
        => new()
        {
            MachineInstanceSha256 = start.MachineInstanceSha256,
            Cpu = start.Cpu,
            LogicalProcessorCount = start.LogicalProcessorCount,
            OsDescription = start.OsDescription,
            OsVersion = start.OsVersion,
            OsArchitecture = start.OsArchitecture,
            ProcessArchitecture = start.ProcessArchitecture,
            FrameworkDescription = start.FrameworkDescription,
            DpiScale = start.DpiScale,
            ViewportWidthDips = start.ViewportWidthDips,
            ViewportHeightDips = start.ViewportHeightDips,
            DisplayDeviceName = mutation == "completion-display-changed"
                ? @"\\.\DISPLAY2"
                : start.DisplayDeviceName,
            ConfiguredRefreshRateHz = start.ConfiguredRefreshRateHz,
            GpuAdapterIdentitySha256 = start.GpuAdapterIdentitySha256,
            GpuAdapterLuid = start.GpuAdapterLuid,
            GpuDriverVersion = start.GpuDriverVersion,
            DisplayAdapterIdentitySha256 = start.DisplayAdapterIdentitySha256,
            DisplayAdapterLuid = start.DisplayAdapterLuid,
            DisplayAdapterDriverVersion = mutation == "completion-display-driver-changed"
                ? "9.9.9.9"
                : start.DisplayAdapterDriverVersion,
            ActivePowerSchemeGuid = start.ActivePowerSchemeGuid,
            UserConfiguredAcPowerModeGuid =
                mutation == "completion-configured-power-mode-support-changed"
                    ? "unsupported"
                    : start.UserConfiguredAcPowerModeGuid,
            UserConfiguredDcPowerModeGuid =
                mutation == "completion-configured-power-mode-support-changed"
                    ? "unsupported"
                    : start.UserConfiguredDcPowerModeGuid,
            EffectivePowerMode = mutation switch
            {
                "completion-effective-power-mode-changed" => "HighPerformance",
                "completion-effective-power-mode-unsupported" => "unsupported",
                _ => start.EffectivePowerMode,
            },
            PowerSource = start.PowerSource,
            EnergySaverState = start.EnergySaverState,
            ProcessPriorityClass = start.ProcessPriorityClass,
            ProcessPowerThrottlingMode = start.ProcessPowerThrottlingMode,
            GcServer = start.GcServer,
            GcLatencyMode = start.GcLatencyMode,
        };

    private static PerformanceReport CreateReport(
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
        double measuredObservedRefreshRateHz = NormalizeObservedRefreshRate(
            observedRefreshRateHz);
        var report = new PerformanceReport
        {
            SchemaVersion = schemaVersion,
            ProviderName = mutation == "provider"
                ? "unreviewed-provider"
                : PerformanceMeasurementContract.ProviderName,
            StartedUtc = mutation == "started"
                ? default
                : DateTimeOffset.Parse("2026-09-10T12:00:00Z"),
            CompletedUtc = mutation switch
            {
                "completed" => DateTimeOffset.Parse("2026-09-10T11:59:00Z"),
                "completed-before-last-viewport-trial" =>
                    DateTimeOffset.Parse("2026-09-10T12:00:00Z").AddMilliseconds(1),
                _ => DateTimeOffset.Parse("2026-09-10T12:01:00Z"),
            },
            IsReleaseEvidence = true,
            Passed = mutation != "top-passed",
            BuildIdentity = mutation == "build-identity"
                ? "unversioned-build-label"
                : PerformanceDeploymentIdentity.IsWellFormed(buildIdentity)
                    ? buildIdentity
                    : PerformanceDeploymentIdentity.CaptureForExecutable(
                        artifacts.Executable.Path),
            BuildArtifacts = artifacts,
            RuntimeConfiguration = CreateRuntimeConfiguration(mutation),
            Machine = new MachineMetadata
            {
                MachineInstanceSha256 = mutation switch
                {
                    "machine-fingerprint-missing" => string.Empty,
                    "machine-fingerprint-different" => new string('B', 64),
                    _ => new string('A', 64),
                },
                Cpu = "test-cpu",
                LogicalProcessorCount = 8,
                OsDescription = mutation == "os-description-different"
                    ? "different-test-os"
                    : "test-os",
                OsVersion = "10.0.26100",
                OsArchitecture = "X64",
                ProcessArchitecture = "X64",
                FrameworkDescription = ".NET test",
                DpiScale = 1,
                ViewportWidthDips = 1280,
                ViewportHeightDips = 720,
                DisplayDeviceName = mutation == "display-device-different"
                    ? @"\\.\DISPLAY2"
                    : @"\\.\DISPLAY1",
                ConfiguredRefreshRateHz = 120,
                ObservedRefreshRateHz = measuredObservedRefreshRateHz,
                IsAtLeast120Hz = measuredObservedRefreshRateHz >= 115,
                GpuAdapterIdentitySha256 = mutation == "gpu-identity-different"
                    ? new string('D', 64)
                    : new string('C', 64),
                GpuAdapterLuid = mutation == "gpu-luid-different"
                    ? "00000000:00000002"
                    : "00000000:00000001",
                GpuDriverVersion = mutation == "gpu-driver-different"
                    ? "2.0.0.0"
                    : "1.2.3.4",
                DisplayAdapterIdentitySha256 = mutation == "display-gpu-identity-different"
                    ? new string('F', 64)
                    : new string('E', 64),
                DisplayAdapterLuid = "00000000:00000003",
                DisplayAdapterDriverVersion = mutation switch
                {
                    "display-gpu-driver-different" => "5.0.0.0",
                    "display-driver-missing" => string.Empty,
                    _ => "4.3.2.1",
                },
                ActivePowerSchemeGuid = mutation == "power-scheme-different"
                    ? "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"
                    : "381b4222-f694-41f0-9685-ff5bb260df2e",
                UserConfiguredAcPowerModeGuid = mutation == "ac-power-mode-different"
                    ? "961cc777-2547-4f9d-8174-7d86181b8a7a"
                    : mutation is "configured-power-modes-unsupported" or
                        "configured-power-mode-support-mixed"
                        ? "unsupported"
                        : "00000000-0000-0000-0000-000000000000",
                UserConfiguredDcPowerModeGuid = mutation == "dc-power-mode-different"
                    ? "ded574b5-45a0-4f42-8737-46345c09c238"
                    : mutation == "configured-power-modes-unsupported"
                        ? "unsupported"
                        : "00000000-0000-0000-0000-000000000000",
                EffectivePowerMode = mutation switch
                {
                    "effective-power-mode-different" =>
                        "HighPerformance",
                    "effective-power-mode-unsupported" => "unsupported",
                    "effective-power-mode-invalid" => "not-a-mode",
                    _ => "Balanced",
                },
                PowerSource = mutation == "power-source-different" ? "Battery" : "AC",
                EnergySaverState = mutation == "energy-saver-different" ? "On" : "Off",
                ProcessPriorityClass = mutation == "priority-different" ? "High" : "Normal",
                ProcessPowerThrottlingMode = mutation == "power-throttling-uncontrolled"
                    ? "system-managed"
                    : PerformanceMeasurementContract.ProcessPowerThrottlingMode,
                GcServer = mutation == "gc-server-different",
                GcLatencyMode = mutation == "gc-latency-different"
                    ? "SustainedLowLatency"
                    : "Interactive",
            },
            SampleRequirements = sampleRequirements ?? new SampleRequirements(),
            Scroll = CreateScrollResult(
                mutation,
                scrollRendererOwnedAllocationBytes,
                mutation == "observed-refresh-raw" ? 119 : observedRefreshRateHz),
            SourceLookup = CreateSourceLookupResult(mutation),
            Cancellation = CreateCancellationResult(mutation),
            LifecyclePlateau = new LifecyclePlateauResult
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
            },
            Regression = new RegressionResult
            {
                Mode = "baseline",
                DecisionPolicy = mutation == "regression-decision-policy"
                    ? "relative-only"
                    : PerformanceMeasurementContract.RegressionDecisionPolicy,
                ReferenceEligible = true,
                MachineComparable = true,
                Passed = true,
            },
        };
        report.CompletionMachine = CreateCompletionMachine(report.Machine, mutation);

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
                    sourceBytes == TargetViewportBytes && mode == TargetViewportMode &&
                    mutation == "viewport-dispersion"
                        ? [1, 1, 1, 10, 10, 10]
                        : sourceBytes == TargetViewportBytes && mode == TargetViewportMode &&
                          mutation == "viewport-residual-spike"
                        ? [10, 10, 10, 10, 10, 20]
                        : sourceBytes == TargetViewportBytes && mode == TargetViewportMode &&
                          mutation == "viewport-boundary-endpoint-spike"
                        ? RepeatTrialValues(10)
                        : sourceBytes == TargetViewportBytes && mode == TargetViewportMode &&
                          mutation == "viewport-drift-series"
                        ? [50, 52, 54, 56, 58, 60]
                        : sourceBytes == targetViewportBytes && mode == targetViewportMode &&
                          targetViewportTrialP95Milliseconds is not null
                        ? targetViewportTrialP95Milliseconds
                        : RepeatTrialValues(1);
                report.FirstUsableViewport.Add(
                    CreateFirstViewportResult(
                        sourceBytes,
                        mode,
                        condition,
                        trialValues,
                        mutation));
                condition++;
            }

            report.RetainedMemory.Add(new RetainedMemoryResult
            {
                Corpus = PerformanceDocumentFactory.GetCorpusName(sourceBytes),
                SourceUtf16Bytes = sourceBytes,
                ManagedDeltaBytes = 0,
                PrivateDeltaBytes = 0,
                BudgetBytes = mutation == "retained-budget" && sourceBytes == 100 * 1024
                    ? 1
                    : checked(sourceBytes * 8L + 32L * 1024 * 1024),
                Passed = true,
            });
        }

        if (mutation == "retained-count")
            report.RetainedMemory.RemoveAt(report.RetainedMemory.Count - 1);
        if (mutation == "top-failure")
            report.Failures.Add("synthetic failure");

        return report;
    }

    private static FirstViewportResult CreateFirstViewportResult(
        int sourceBytes,
        string mode,
        int condition,
        IReadOnlyList<double> trialValues,
        string? mutation)
    {
        if (trialValues.Count != PerformanceMeasurementContract.ReleaseFirstViewportTrials)
            throw new ArgumentException("Exactly six trial values are required.", nameof(trialValues));

        bool mutate = sourceBytes == TargetViewportBytes &&
            (mode == TargetViewportMode ||
             (mode == PerformanceMeasurementContract.FirstViewportCacheHitMode &&
              mutation == "viewport-cache-hit-counter"));
        double warmupValue = mutation == "viewport-boundary-series" && mutate
            ? trialValues[0] + 20
            : trialValues[0];
        List<FirstViewportTrialResult> warmupTrials = Enumerable.Range(
                0,
                PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials)
            .Select(row => CreateFirstViewportTrial(
                mode,
                condition,
                row,
                warmup: true,
                warmupValue,
                mutate,
                mutation))
            .ToList();
        var trials = trialValues.Select((value, row) => CreateFirstViewportTrial(
                mode,
                condition,
                row,
                warmup: false,
                value,
                mutate,
                mutation))
            .ToList();
        if (mutate && mutation == "viewport-warmup-count")
            warmupTrials.RemoveAt(warmupTrials.Count - 1);
        if (mutate && mutation == "viewport-trial-count")
            trials.RemoveAt(trials.Count - 1);

        List<double> flattenedSamples = trials
            .SelectMany(static trial => trial.SamplesMilliseconds)
            .ToList();
        List<double> publicationSamples = trials
            .SelectMany(static trial => trial.PublicationSamplesMilliseconds)
            .ToList();
        if (mutate && mutation == "viewport-publication-flattened")
            publicationSamples[0] += 0.25;
        double publicationMaximum = publicationSamples.Max();
        if (mutate && mutation == "viewport-flattened")
            flattenedSamples[0] += 1;

        double pooledP95 = PerformanceStatistics.Percentile(flattenedSamples, 0.95);
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
        double allowance = Math.Max(
            PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
                mode,
                sourceBytes),
            Math.Abs(regressionP95) *
                PerformanceMeasurementContract.FirstViewportStationarityRelativeAllowancePercent /
                100d);
        bool stationarityPassed = double.IsFinite(dispersion) &&
            dispersion <= PerformanceMeasurementContract.FirstViewportDispersionBudgetPercent &&
            double.IsFinite(slope) &&
            double.IsFinite(projectedDrift) &&
            Math.Abs(projectedDrift) <= allowance &&
            double.IsFinite(boundaryShift) &&
            boundaryShift <= allowance;
        double budget = sourceBytes switch
        {
            100 * 1024 when mode ==
                PerformanceMeasurementContract.FirstViewportCacheHitMode => 75,
            100 * 1024 => 125,
            1024 * 1024 => 250,
            10 * 1024 * 1024 => 750,
            _ => throw new InvalidOperationException(),
        };
        return new FirstViewportResult
        {
            Corpus = PerformanceDocumentFactory.GetCorpusName(sourceBytes),
            Mode = mode,
            SourceUtf16Bytes = sourceBytes,
            ParseCacheBudgetBytes = mutation == "viewport-cache" && mutate
                ? 1
                : mode == PerformanceMeasurementContract.FirstViewportCacheHitMode
                    ? sourceBytes * 8L
                    : 0,
            EngineReuseMode = mutation == "viewport-engine-reuse" && mutate
                ? "per-sample"
                : PerformanceMeasurementContract.FirstViewportEngineReuseMode,
            TrialStartPolicy = mutation == "viewport-trial-policy" && mutate
                ? "none"
                : PerformanceMeasurementContract.FirstViewportTrialStartPolicy,
            SchedulePolicy = mutation == "viewport-schedule-policy" && mutate
                ? "none"
                : PerformanceMeasurementContract.FirstViewportSchedulePolicy,
            RegressionEstimator = mutation == "viewport-estimator" && mutate
                ? "median-of-five-trial-p95"
                : PerformanceMeasurementContract.FirstViewportRegressionEstimator,
            StationarityPolicy = mutation == "viewport-stationarity-policy" && mutate
                ? "none"
                : PerformanceMeasurementContract.FirstViewportStationarityPolicy,
            MeasurementThreadAffinityMask = mutation == "viewport-affinity" && mutate
                ? "0x0000000000000000"
                : "0x0000000000000001",
            MeasurementProcessorGroup = 0,
            MeasurementProcessorNumber = 0,
            MeasurementThreadPriority = mutation == "viewport-priority" && mutate
                ? "Highest"
                : "Normal",
            WarmupTrials = warmupTrials,
            Trials = trials,
            SamplesMilliseconds = flattenedSamples,
            P95Milliseconds = pooledP95,
            PublicationSamplesMilliseconds = publicationSamples,
            PublicationMaximumMilliseconds = mutate && mutation == "viewport-publication-aggregate-maximum"
                ? publicationMaximum + 0.25
                : publicationMaximum,
            PublicationBudgetMilliseconds =
                mutate && mutation == "viewport-publication-budget"
                    ? 3
                    : PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds,
            RegressionP95Milliseconds = mutation == "viewport-regression" && mutate
                ? regressionP95 + 1
                : regressionP95,
            RegressionDispersionPercent = mutation == "viewport-dispersion-derived" && mutate
                ? dispersion + 1
                : dispersion,
            TheilSenSlopeMillisecondsPerGlobalOrdinal = mutation == "viewport-slope" && mutate
                ? slope + 1
                : slope,
            ProjectedDriftMilliseconds = mutation == "viewport-projected-drift" && mutate
                ? projectedDrift + 1
                : projectedDrift,
            WarmupBoundaryShiftMilliseconds = mutation == "viewport-boundary-shift" && mutate
                ? boundaryShift + 1
                : boundaryShift,
            StationarityAllowanceMilliseconds = mutation == "viewport-stationarity-allowance" && mutate
                ? allowance + 1
                : allowance,
            StationarityEvaluated = !(mutation == "viewport-stationarity-evaluated" && mutate),
            StationarityPassed = mutation == "viewport-stationarity-passed" && mutate
                ? !stationarityPassed
                : stationarityPassed,
            BudgetMilliseconds = budget,
            Passed = pooledP95 <= budget && stationarityPassed &&
                publicationMaximum <=
                    PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds,
        };
    }

    private static FirstViewportTrialResult CreateFirstViewportTrial(
        string mode,
        int condition,
        int row,
        bool warmup,
        double value,
        bool mutate,
        string? mutation)
    {
        int position = GetFirstViewportSchedulePosition(condition, row);
        int globalOrdinal = checked(
            (warmup
                ? row
                : PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials + row) *
            PerformanceMeasurementContract.FirstViewportConditionCount +
            position);
        DateTimeOffset started = DateTimeOffset.Parse("2026-09-10T12:00:00Z")
            .AddMilliseconds(1 + (globalOrdinal * 2));
        bool mutateMeasuredFirst = mutate && !warmup && row == 0;
        bool mutateWarmupFirst = mutate && warmup && row == 0;
        if (mutate && warmup && mutation == "viewport-boundary-endpoint-spike")
        {
            value = row switch
            {
                1 => 0,
                2 => 20,
                _ => value,
            };
        }

        bool cacheHit = mode == PerformanceMeasurementContract.FirstViewportCacheHitMode;
        int presentations = PerformanceMeasurementContract.ReleaseFirstViewportIterations + 1;
        return new FirstViewportTrialResult
        {
            Ordinal = mutateMeasuredFirst && mutation == "viewport-trial-order" ? 1 : row,
            GlobalOrdinal = mutateMeasuredFirst && mutation == "viewport-global-order"
                ? 0
                : globalOrdinal,
            ScheduleRow = mutateMeasuredFirst && mutation == "viewport-schedule-row"
                ? row + 1
                : row,
            SchedulePosition = mutateMeasuredFirst && mutation == "viewport-schedule-position"
                ? (position + 1) % PerformanceMeasurementContract.FirstViewportConditionCount
                : position,
            StartedUtc = mutation switch
            {
                "viewport-chronology" when mutateMeasuredFirst =>
                    started.ToOffset(TimeSpan.FromHours(-7)),
                "viewport-before-report" when mutateWarmupFirst => started.AddMilliseconds(-2),
                _ => started,
            },
            CompletedUtc = mutateMeasuredFirst && mutation == "viewport-timestamp-order"
                ? started.AddMilliseconds(-1)
                : started.AddMilliseconds(1),
            SettlingElapsedMilliseconds = mutateMeasuredFirst && mutation == "viewport-settling"
                ? -1
                : 1,
            SettlingPublicationMilliseconds = mutateMeasuredFirst &&
                mutation == "viewport-publication-settling" ? -1 : 1,
            SamplesMilliseconds = Enumerable.Repeat(
                value,
                PerformanceMeasurementContract.ReleaseFirstViewportIterations).ToList(),
            PublicationSamplesMilliseconds = Enumerable.Repeat(
                mutateMeasuredFirst && mutation == "viewport-publication-overbudget" ? 2.1 : 1d,
                mutateMeasuredFirst && mutation == "viewport-publication-count"
                    ? PerformanceMeasurementContract.ReleaseFirstViewportIterations - 1
                    : PerformanceMeasurementContract.ReleaseFirstViewportIterations).ToList(),
            PublicationMaximumMilliseconds = mutateMeasuredFirst &&
                mutation == "viewport-publication-trial-maximum" ? 1.25 :
                mutateMeasuredFirst && mutation == "viewport-publication-overbudget" ? 2.1 : 1,
            P95Milliseconds = mutateMeasuredFirst && mutation == "viewport-trial-p95"
                ? value + 1
                : value,
            Gen0Collections = mutateMeasuredFirst && mutation == "viewport-gc" ? -1 : 0,
            Gen1Collections = mutateMeasuredFirst && mutation == "viewport-gc-hierarchy" ? 1 : 0,
            Gen2Collections = 0,
            ProcessAllocatedBytes = mutateMeasuredFirst && mutation == "viewport-allocation"
                ? -1
                : 0,
            CompletedParseCountBeforePrime = 0,
            CompletedParseCountAfterPrime = cacheHit ? 1 : 0,
            CompletedParseCountAfterTrial = cacheHit ? 1 : 0,
            SourceKeyHashCountBeforePrime = 0,
            SourceKeyHashCountAfterPrime = cacheHit ? 1 : 0,
            SourceKeyHashCountAfterTrial = mutateMeasuredFirst &&
                mutation is "viewport-cache-counter" or "viewport-cache-hit-counter"
                    ? 0
                    : cacheHit ? 1 : presentations,
            CacheProofPassed = !(mutateMeasuredFirst && mutation == "viewport-cache-proof"),
            Complete = true,
        };
    }

    private static int GetFirstViewportSchedulePosition(int condition, int row)
        => Enumerable.Range(0, PerformanceMeasurementContract.FirstViewportConditionCount)
            .Single(position =>
                PerformanceMeasurementContract.GetFirstViewportCondition(row, position) == condition);

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

    private static ScrollResult CreateScrollResult(
        string? mutation,
        long rendererOwnedAllocationBytes,
        double observedRefreshRateHz)
    {
        long stopwatchFrequency = Stopwatch.Frequency;
        long frameIntervalTicks = checked((long)Math.Round(
            stopwatchFrequency / observedRefreshRateHz));
        double frameIntervalMilliseconds = frameIntervalTicks * 1_000.0 / stopwatchFrequency;
        long uiThreadWorkTicks = checked((long)Math.Round(stopwatchFrequency / 1_000.0));
        double uiThreadWorkMilliseconds =
            uiThreadWorkTicks * 1_000.0 / stopwatchFrequency;
        int framesPerTrial = PerformanceMeasurementContract.ReleaseScrollFrames;
        int trialCount = PerformanceMeasurementContract.ReleaseScrollTrials;
        var trials = new List<ScrollTrialResult>(trialCount);

        for (int ordinal = 0; ordinal < trialCount; ordinal++)
        {
            var uiTicks = Enumerable.Repeat(uiThreadWorkTicks, framesPerTrial).ToList();
            var frameTicks = Enumerable.Repeat(frameIntervalTicks, framesPerTrial).ToList();
            var allocationBytes = Enumerable.Repeat(
                rendererOwnedAllocationBytes,
                framesPerTrial).ToList();
            if (ordinal == 0)
            {
                if (mutation == "scroll-ui-raw-count")
                    uiTicks.RemoveAt(uiTicks.Count - 1);
                if (mutation == "scroll-ui-raw-negative")
                    uiTicks[0] = -1;
                if (mutation == "scroll-frame-raw-count")
                    frameTicks.RemoveAt(frameTicks.Count - 1);
                if (mutation == "scroll-frame-raw-zero")
                    frameTicks[0] = 0;
                if (mutation == "scroll-allocation-raw-count")
                    allocationBytes.RemoveAt(allocationBytes.Count - 1);
                if (mutation == "scroll-allocation-raw-negative")
                    allocationBytes[0] = -1;
            }

            trials.Add(new ScrollTrialResult
            {
                Ordinal = mutation == "scroll-trial-order" && ordinal == 0 ? 1 : ordinal,
                FirstLogicalFrameId = mutation == "scroll-first-frame" && ordinal == 0
                    ? 0
                    : checked((long)ordinal * framesPerTrial + 1),
                RequestedFrames = mutation == "scroll-requested-frames" && ordinal == 0
                    ? framesPerTrial - 1
                    : framesPerTrial,
                MeasuredFrameIntervals = framesPerTrial,
                FramesWithRendererWork = framesPerTrial,
                UiThreadWorkElapsedTicks = uiTicks,
                FrameIntervalElapsedTicks = frameTicks,
                RendererOwnedAllocatedBytesPerFrame = allocationBytes,
                UiThreadWorkP95Milliseconds = mutation == "scroll-trial-p95" && ordinal == 0
                    ? uiThreadWorkMilliseconds + 1
                    : uiThreadWorkMilliseconds,
                UiThreadWorkP99Milliseconds = uiThreadWorkMilliseconds,
                FrameTimeP95Milliseconds = frameIntervalMilliseconds,
                FramesOver16_7Percent = 0,
                MaximumFrameStallMilliseconds = frameIntervalMilliseconds,
                RendererOwnedAllocatedBytesPerFrameP95 = rendererOwnedAllocationBytes,
                MaximumRendererOwnedAllocatedBytesPerFrame = rendererOwnedAllocationBytes,
                MaximumOffscreenRealizedCodeActions = 0,
                Gen2Collections = 0,
                Complete = true,
            });
        }

        if (mutation == "scroll-trial-count")
            trials.RemoveAt(trials.Count - 1);

        int totalFrames = checked(framesPerTrial * trialCount);
        return new ScrollResult
        {
            Corpus = PerformanceDocumentFactory.WarmScrollStressCorpus,
            SourceUtf16Bytes = 1024 * 1024,
            StopwatchFrequency = stopwatchFrequency,
            RequestedFrames = totalFrames,
            MeasuredFrameIntervals = totalFrames,
            FramesWithRendererWork = totalFrames,
            RegressionEstimator = mutation == "scroll-estimator"
                ? "median-of-five-trial-p95"
                : PerformanceMeasurementContract.ScrollRegressionEstimator,
            Trials = trials,
            RegressionUiThreadWorkP95Milliseconds = mutation == "scroll-regression"
                ? uiThreadWorkMilliseconds + 1
                : uiThreadWorkMilliseconds,
            RegressionUiThreadWorkP99Milliseconds = uiThreadWorkMilliseconds,
            RegressionFrameTimeP95Milliseconds = frameIntervalMilliseconds,
            RegressionRendererOwnedAllocatedBytesPerFrameP95 = rendererOwnedAllocationBytes,
            UiThreadWorkP95Milliseconds = mutation == "scroll-aggregate-p95"
                ? uiThreadWorkMilliseconds + 1
                : uiThreadWorkMilliseconds,
            UiThreadWorkP99Milliseconds = uiThreadWorkMilliseconds,
            FrameTimeP95Milliseconds = frameIntervalMilliseconds,
            FramesOver16_7Percent = 0,
            MaximumFrameStallMilliseconds = frameIntervalMilliseconds,
            RendererOwnedAllocatedBytesPerFrameP95 = rendererOwnedAllocationBytes,
            MaximumRendererOwnedAllocatedBytesPerFrame = rendererOwnedAllocationBytes,
            RendererOwnedLohAllocationPossible = false,
            MaximumOffscreenRealizedCodeActions = 0,
            Gen2Collections = 0,
            Passed = true,
        };
    }

    private static SourceLookupResult CreateSourceLookupResult(string? mutation)
    {
        long stopwatchFrequency = Stopwatch.Frequency;
        const long absoluteTicks = 100;
        const long regressionTicks = 4_096;
        double absoluteNanoseconds = absoluteTicks * 1_000_000_000.0 / stopwatchFrequency;
        double regressionNanoseconds = regressionTicks * 1_000_000_000.0 /
            stopwatchFrequency /
            PerformanceMeasurementContract.SourceLookupRegressionBatchSize;
        var absoluteSamples = Enumerable.Repeat(
            absoluteTicks,
            PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount).ToList();
        if (mutation == "actual-lookup-absolute-count")
            absoluteSamples.RemoveAt(absoluteSamples.Count - 1);

        var trials = Enumerable.Range(
                0,
                PerformanceMeasurementContract.SourceLookupRegressionTrialCount)
            .Select(ordinal => new SourceLookupTrialResult
            {
                Ordinal = mutation == "actual-lookup-order" && ordinal == 0 ? 1 : ordinal,
                ObservationCount = PerformanceMeasurementContract.SourceLookupRegressionObservationCount,
                OperationCount = PerformanceMeasurementContract.SourceLookupRegressionOperationCount,
                SamplesElapsedTicks = Enumerable.Repeat(
                    regressionTicks,
                    PerformanceMeasurementContract.SourceLookupRegressionObservationCount).ToList(),
                SamplesNanosecondsPerLookup = Enumerable.Repeat(
                    regressionNanoseconds,
                    PerformanceMeasurementContract.SourceLookupRegressionObservationCount).ToList(),
                P95Nanoseconds = mutation == "actual-lookup-trial-p95" && ordinal == 0
                    ? regressionNanoseconds + 1
                    : regressionNanoseconds,
                AllocatedBytes = 0,
                Complete = true,
            })
            .ToList();
        if (mutation == "actual-lookup-trials")
            trials.RemoveAt(trials.Count - 1);
        if (mutation == "actual-lookup-ticks")
            trials[0].SamplesNanosecondsPerLookup[0] = regressionNanoseconds + 1;

        return new SourceLookupResult
        {
            MappingCount = mutation == "actual-mapping" ? 99_999 :
                PerformanceMeasurementContract.SourceLookupMappingCount,
            QueryCount = mutation == "actual-query" ? 19_999 :
                PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount,
            StopwatchFrequency = stopwatchFrequency,
            MeasurementThreadAffinityMask = mutation == "valid-affinity-two"
                ? "0x0000000000000002"
                : "0x0000000000000001",
            MeasurementProcessorGroup = 0,
            MeasurementProcessorNumber = mutation == "valid-affinity-two" ? 1 : 0,
            MeasurementThreadPriority = "Highest",
            SamplesElapsedTicks = absoluteSamples,
            P95Nanoseconds = mutation == "actual-lookup-absolute-p95"
                ? absoluteNanoseconds + 1
                : absoluteNanoseconds,
            AllocatedBytes = mutation == "actual-lookup-allocated" ? 1 : 0,
            RegressionWarmupPasses = mutation == "actual-lookup-warmup" ? 7 :
                PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses,
            RegressionEstimator = mutation == "actual-lookup-estimator"
                ? "median-of-five-trial-p95"
                : PerformanceMeasurementContract.SourceLookupRegressionEstimator,
            RegressionTrialCount = PerformanceMeasurementContract.SourceLookupRegressionTrialCount,
            RegressionBatchSize = mutation == "actual-lookup-batch" ? 63 :
                PerformanceMeasurementContract.SourceLookupRegressionBatchSize,
            RegressionObservationCount = mutation == "actual-lookup-observations" ? 127 :
                PerformanceMeasurementContract.SourceLookupRegressionObservationCount,
            RegressionOperationCount = mutation == "actual-lookup-operations" ? 524_287 :
                PerformanceMeasurementContract.SourceLookupRegressionOperationCount,
            RegressionTrials = trials,
            RegressionP95Nanoseconds = mutation == "actual-lookup-hl"
                ? regressionNanoseconds + 1
                : regressionNanoseconds,
            RegressionAllocatedBytes = 0,
            Passed = true,
        };
    }

    private static CancellationResult CreateCancellationResult(string? mutation)
    {
        int iterations = PerformanceMeasurementContract.ReleaseCancellationIterations;
        int trialCount = PerformanceMeasurementContract.ReleaseCancellationTrials;
        var trials = new List<CancellationTrialResult>(trialCount);
        for (int ordinal = 0; ordinal < trialCount; ordinal++)
        {
            IReadOnlyList<CancellationMeasurementIteration> plan =
                CancellationMeasurementPlan.CreateTrial(ordinal, iterations);
            var samples = Enumerable.Repeat(1.0, iterations).ToList();
            if (mutation == "cancellation-sample-count" && ordinal == 0)
                samples.RemoveAt(samples.Count - 1);

            trials.Add(new CancellationTrialResult
            {
                Ordinal = mutation == "cancellation-trial-order" && ordinal == 0 ? 1 : ordinal,
                WarmupSourceSeed = mutation == "cancellation-warmup-seed" && ordinal == 0
                    ? plan[0].SourceSeed + 1
                    : plan[0].SourceSeed,
                WarmupElapsedMilliseconds = 1,
                WarmupStaleCommits = mutation == "cancellation-warmup-stale" && ordinal == 0
                    ? 1
                    : 0,
                FirstRecordedSourceSeed = mutation == "cancellation-recorded-seed" && ordinal == 0
                    ? plan[1].SourceSeed + 1
                    : plan[1].SourceSeed,
                RequestedIterations = iterations,
                ObservedCancellations = iterations,
                SamplesMilliseconds = samples,
                P95Milliseconds = mutation == "cancellation-trial-p95" && ordinal == 0
                    ? 2
                    : 1,
                StaleCommits = mutation == "cancellation-stale" && ordinal == 0 ? 1 : 0,
                Complete = true,
            });
        }

        if (mutation == "cancellation-trial-count")
            trials.RemoveAt(trials.Count - 1);
        List<double> flattenedSamples = trials
            .SelectMany(static trial => trial.SamplesMilliseconds)
            .ToList();
        if (mutation == "cancellation-flattened")
            flattenedSamples[0] = 2;

        return new CancellationResult
        {
            RequestedIterations = mutation == "cancellation-requested"
                ? iterations * trialCount - 1
                : iterations * trialCount,
            IterationsPerTrial = iterations,
            WarmupIterations = trialCount,
            WarmupElapsedMilliseconds = 1,
            WarmupStaleCommits = trials.Sum(static trial => trial.WarmupStaleCommits),
            ObservedCancellations = mutation == "cancellation-observed"
                ? iterations * trialCount - 1
                : iterations * trialCount,
            TrialStartPolicy = mutation == "cancellation-policy"
                ? "none"
                : PerformanceMeasurementContract.CancellationTrialStartPolicy,
            RegressionEstimator = mutation == "cancellation-estimator"
                ? "median-of-five-trial-p95"
                : PerformanceMeasurementContract.CancellationRegressionEstimator,
            Trials = trials,
            SamplesMilliseconds = flattenedSamples,
            P95Milliseconds = 1,
            RegressionP95Milliseconds = mutation == "cancellation-regression" ? 2 : 1,
            StaleCommits = trials.Sum(static trial => trial.StaleCommits),
            Passed = true,
        };
    }

    private static SampleRequirements CreateRequirements(string mutation)
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

    private static string NewTemporaryPath()
        => Path.Combine(
            Path.GetTempPath(),
            $"markdown-renderer-performance-{Guid.NewGuid():N}.json");

    private sealed class ArtifactDirectory : IDisposable
    {
        private ArtifactDirectory(string path, BuildArtifactHashes hashes)
        {
            Path = path;
            Hashes = hashes;
        }

        private string Path { get; }
        internal BuildArtifactHashes Hashes { get; }
        internal string Identity => PerformanceDeploymentIdentity.CaptureForExecutable(
            Hashes.Executable.Path);

        internal static ArtifactDirectory Create(string content)
        {
            string directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"markdown-renderer-artifacts-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            BuildArtifactHash Capture(string fileName)
            {
                string path = System.IO.Path.Combine(directory, fileName);
                File.WriteAllText(path, $"{content}:{fileName}");
                return new BuildArtifactHash
                {
                    FileName = fileName,
                    Path = path,
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                };
            }

            return new ArtifactDirectory(
                directory,
                new BuildArtifactHashes
                {
                    Executable = Capture("MarkdownRenderer.PerformanceHarness.exe"),
                    PerformanceHarnessDll = Capture("MarkdownRenderer.PerformanceHarness.dll"),
                    MarkdownRendererDll = Capture("MarkdownRenderer.dll"),
                    MarkdownRendererCoreDll = Capture("MarkdownRenderer.Core.dll"),
                    RuntimeConfig = Capture(
                        "MarkdownRenderer.PerformanceHarness.runtimeconfig.json"),
                });
        }

        internal BuildArtifactHashes WithExecutableHash(string sha256)
            => new()
            {
                Executable = new BuildArtifactHash
                {
                    FileName = Hashes.Executable.FileName,
                    Path = Hashes.Executable.Path,
                    Sha256 = sha256,
                },
                PerformanceHarnessDll = Hashes.PerformanceHarnessDll,
                MarkdownRendererDll = Hashes.MarkdownRendererDll,
                MarkdownRendererCoreDll = Hashes.MarkdownRendererCoreDll,
                RuntimeConfig = Hashes.RuntimeConfig,
            };

        internal BuildArtifactHashes WithRuntimeConfigHash(string sha256)
            => new()
            {
                Executable = Hashes.Executable,
                PerformanceHarnessDll = Hashes.PerformanceHarnessDll,
                MarkdownRendererDll = Hashes.MarkdownRendererDll,
                MarkdownRendererCoreDll = Hashes.MarkdownRendererCoreDll,
                RuntimeConfig = new BuildArtifactHash
                {
                    FileName = Hashes.RuntimeConfig.FileName,
                    Path = Hashes.RuntimeConfig.Path,
                    Sha256 = sha256,
                },
            };

        internal void DeleteFiles()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }

        internal void MutateRuntimeConfig()
            => File.AppendAllText(Hashes.RuntimeConfig.Path, ":mutated");

        internal void WriteDependency(string relativePath, string content)
        {
            string path = System.IO.Path.Combine(
                Path,
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
