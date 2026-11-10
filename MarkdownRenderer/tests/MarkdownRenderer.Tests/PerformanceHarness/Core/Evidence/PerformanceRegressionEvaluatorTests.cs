using System.Security.Cryptography;
using System.Text.Json;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Deployment;
using MarkdownRenderer.PerformanceHarness.Core.Evidence;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;
using MarkdownRenderer.Tests.TestSupport;
using Xunit;
using static MarkdownRenderer.Tests.PerformanceHarness.Core.Evidence.PerformanceRegressionReportFixture;

namespace MarkdownRenderer.Tests.PerformanceHarness.Core.Evidence;

/// <summary>
/// Tests for the regression evaluator and release evidence validator: robust
/// statistics, reference eligibility, artifact and deployment identity,
/// machine comparability and per-metric regression decisions.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it covers every decision one
/// RegressionEvaluator.Evaluate call makes, all through the same candidate
/// evaluation helpers, and its reference-shape and measurement-environment
/// theories list 142 named mutations as their own [InlineData] cases so each
/// one is reported and rerun on its own. The reports are built by
/// PerformanceRegressionReportFixture and its scenario fixtures.
/// </remarks>
public sealed class PerformanceRegressionEvaluatorTests
{
    [Fact]
    public void CompleteMachineEnvironmentSnapshotsAreAccepted()
    {
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("complete-machine");
        PerformanceReport report = CreateReport(artifacts.Hashes, "complete-machine");

        Assert.True(MachineEvidenceValidator.IsEnvironmentSnapshotComplete(report.Machine));
        Assert.True(MachineEvidenceValidator.IsEnvironmentSnapshotComplete(report.CompletionMachine));
        Assert.True(MachineEvidenceValidator.IsSameRunEnvironment(report.Machine, report.CompletionMachine));
        Assert.True(PerformanceReleaseEvidenceValidator.HasCompleteMeasurementPayload(report));
    }

    [Fact]
    public void ReleasePayloadRejectsAnOccludedMeasurementInterval()
    {
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("occluded-measurement");
        PerformanceReport report = CreateReport(artifacts.Hashes, "occluded-measurement");
        MeasurementVisibilityEvidence valid = report.MeasurementVisibility;
        MeasurementVisibilitySample[] samples = [.. valid.Samples];
        MeasurementVisibilitySample sample = samples[4];
        samples[4] = new MeasurementVisibilitySample
        {
            ElapsedTicks = sample.ElapsedTicks,
            Source = sample.Source,
            CaptureSucceeded = sample.CaptureSucceeded,
            TargetWasForeground = sample.TargetWasForeground,
            TargetWasVisible = sample.TargetWasVisible,
            TargetWasMinimized = sample.TargetWasMinimized,
            TargetWasCloaked = sample.TargetWasCloaked,
            TargetWasWithinWorkArea = sample.TargetWasWithinWorkArea,
            TargetWasUnoccluded = false,
        };
        report.MeasurementVisibility = PerformanceVisibilityFixture.CreatePassing(
            report.StartedUtc,
            samples: samples,
            intervalOffset: valid.StartedUtc - report.StartedUtc,
            duration: TimeSpan.FromSeconds(
                (double)valid.DurationTicks / valid.StopwatchFrequency));

        Assert.False(PerformanceReleaseEvidenceValidator.HasCompleteMeasurementPayload(report));
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("same-build");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("schema-9");
        RegressionResult result = EvaluateCandidate(
            CreateReport(artifacts.Hashes, "schema-9", schemaVersion: 9),
            CreateReport(artifacts.Hashes, "schema-9"));

        Assert.False(result.ReferenceEligible);
        Assert.False(result.Passed);
    }

    [Fact]
    public void EqualBuildIdentityRequiresAllArtifactHashesToMatch()
    {
        using PerformanceArtifactDirectory referenceArtifacts = PerformanceArtifactDirectory.Create("reference");
        using PerformanceArtifactDirectory candidateArtifacts = PerformanceArtifactDirectory.Create("candidate");
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
        using PerformanceArtifactDirectory referenceArtifacts = PerformanceArtifactDirectory.Create("reference");
        using PerformanceArtifactDirectory candidateArtifacts = PerformanceArtifactDirectory.Create("candidate");

        RegressionResult result = EvaluateCandidate(
            CreateReport(referenceArtifacts.Hashes, "accepted-reference"),
            CreateReport(candidateArtifacts.Hashes, "candidate-revision"));

        Assert.True(result.ReferenceEligible);
        Assert.True(result.Passed);
    }

    [Fact]
    public void DifferentDeploymentIdentitiesMayShareFiveExplicitArtifactHashes()
    {
        using PerformanceArtifactDirectory referenceArtifacts = PerformanceArtifactDirectory.Create("identical-artifacts");
        using PerformanceArtifactDirectory candidateArtifacts = PerformanceArtifactDirectory.Create("identical-artifacts");
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
        using PerformanceArtifactDirectory referenceArtifacts = PerformanceArtifactDirectory.Create("reference");
        using PerformanceArtifactDirectory candidateArtifacts = PerformanceArtifactDirectory.Create("candidate");
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
        using PerformanceArtifactDirectory referenceArtifacts = PerformanceArtifactDirectory.Create("accepted-reference");
        using PerformanceArtifactDirectory candidateArtifacts = PerformanceArtifactDirectory.Create("candidate");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("tampered");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("malformed");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("runtimeconfig-identity");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("affinity");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("refresh");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("refresh-outcome");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("machine-environment");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("gpu-luid");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("power-throttling");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("unsupported-power-mode");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("unsupported-effective-power");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("floor-inclusive");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("relative-inclusive");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("over-floor");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("dispersion");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("zero-reference");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("zero-allocation");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("aliased-path");
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
            RuntimeConfig = artifacts.Hashes.RuntimeConfig,
        };

        Assert.False(PerformanceArtifactContract.TryValidateStored(aliased, out _));
        Assert.False(PerformanceArtifactContract.TryValidateCurrentFiles(aliased, out _));
    }

    [Fact]
    public void CurrentArtifactValidationBindsRuntimeConfigurationBytes()
    {
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("runtimeconfig-current-file");
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
        using PerformanceArtifactDirectory artifacts = PerformanceArtifactDirectory.Create("reference-shape");
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

    private static string NewTemporaryPath()
        => Path.Combine(
            Path.GetTempPath(),
            $"markdown-renderer-performance-{Guid.NewGuid():N}.json");
}
