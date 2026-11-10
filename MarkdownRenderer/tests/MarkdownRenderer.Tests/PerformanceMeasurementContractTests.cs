using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Evidence;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Tests for the frozen measurement contract: release and quick population
/// sizes, the first-viewport schedule and noise floors, strict reference
/// evidence deserialization, harness options and output writing, and the quick
/// smoke validator.
/// </summary>
/// <remarks>
/// This file is longer than 500 lines because it covers the harness's whole
/// input and output contract (frozen populations and schedule, strict
/// reference evidence reading, option parsing, output writing and the quick
/// smoke validator), and its theories list 69 named cases as their own
/// [InlineData] lines so each one is reported and rerun on its own. The quick smoke mutations live in
/// QuickPerformanceSmokeMutations and the valid report in
/// QuickPerformanceReportFixture.
/// </remarks>
public sealed class PerformanceMeasurementContractTests
{
    [Theory]
    [InlineData(32d, false)]
    [InlineData(118.99d, false)]
    [InlineData(119d, true)]
    [InlineData(120d, true)]
    public void ReleasePreflightUsesTheExistingConfiguredRefreshThreshold(
        double configuredRefreshRateHz,
        bool expectedQualified)
    {
        Assert.Equal(
            expectedQualified,
            MachineEvidenceValidator.IsConfiguredRefreshRateQualified(
                configuredRefreshRateHz));
    }

    [Fact]
    public void ReleasePreflightRejectsNonFiniteConfiguredRefreshRates()
    {
        Assert.False(MachineEvidenceValidator.IsConfiguredRefreshRateQualified(double.NaN));
        Assert.False(MachineEvidenceValidator.IsConfiguredRefreshRateQualified(double.PositiveInfinity));
    }

    [Fact]
    public void ReleaseDefaultsUseStatisticallyMeaningfulPercentilePopulations()
    {
        PerformanceOptions options = PerformanceOptions.Parse(
        [
            "--output",
            NewTemporaryPath(),
            "--baseline",
        ]);

        Assert.Equal(12, PerformanceMeasurementContract.SchemaVersion);
        Assert.Equal("MarkdownRenderer-Performance", PerformanceMeasurementContract.ProviderName);
        Assert.Equal(100, options.FirstViewportIterations);
        Assert.Equal(2_400, options.ScrollFrames);
        Assert.Equal(5, options.ScrollTrials);
        Assert.Equal(100, options.LifecycleCycles);
        Assert.Equal(40, options.CancellationIterations);
        Assert.True(options.EstablishBaseline);
        Assert.False(options.Quick);

        var report = new PerformanceReport();
        Assert.Equal(12, report.SchemaVersion);
        Assert.Equal(2, PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds);
        Assert.Equal(100, report.SampleRequirements.FirstViewportIterationsRequired);
        Assert.Equal(6, report.SampleRequirements.FirstViewportTrialsRequired);
        Assert.Equal(3, report.SampleRequirements.FirstViewportWarmupTrialsRequired);
        Assert.Equal(2_400, report.SampleRequirements.ScrollFramesRequired);
        Assert.Equal(5, report.SampleRequirements.ScrollTrialsRequired);
        Assert.Equal(100, report.SampleRequirements.LifecycleCyclesRequired);
        Assert.Equal(40, report.SampleRequirements.CancellationIterationsPerTrialRequired);
        Assert.Equal(5, report.SampleRequirements.CancellationTrialsRequired);
        Assert.Equal(100_000, report.SampleRequirements.SourceLookupMappingCountRequired);
        Assert.Equal(20_000, report.SampleRequirements.SourceLookupAbsoluteQueryCountRequired);
        Assert.Equal(8, report.SampleRequirements.SourceLookupRegressionWarmupPassesRequired);
        Assert.Equal(5, report.SampleRequirements.SourceLookupRegressionTrialsRequired);
        Assert.Equal(4_096, report.SampleRequirements.SourceLookupRegressionBatchSizeRequired);
        Assert.Equal(128, report.SampleRequirements.SourceLookupRegressionObservationCountRequired);
        Assert.Equal(524_288, report.SampleRequirements.SourceLookupRegressionOperationCountRequired);
    }

    [Fact]
    public void QuickModePreservesSmallNonGatingSmokePopulation()
    {
        PerformanceOptions options = PerformanceOptions.Parse(
        [
            "--output",
            Path.Combine(Path.GetTempPath(), "markdown-renderer-performance-quick.json"),
            "--quick",
        ]);

        Assert.Equal(1, options.FirstViewportIterations);
        Assert.Equal(30, options.ScrollFrames);
        Assert.Equal(1, options.ScrollTrials);
        Assert.Equal(8, options.LifecycleCycles);
        Assert.Equal(1, options.CancellationIterations);
        Assert.True(options.Quick);
        Assert.False(options.EstablishBaseline);

        SampleRequirements requirements = SampleRequirements.For(options);
        Assert.Equal(1, requirements.FirstViewportIterationsRequired);
        Assert.Equal(1, requirements.FirstViewportTrialsRequired);
        Assert.Equal(1, requirements.FirstViewportWarmupTrialsRequired);
        Assert.Equal(30, requirements.ScrollFramesRequired);
        Assert.Equal(1, requirements.ScrollTrialsRequired);
        Assert.Equal(8, requirements.LifecycleCyclesRequired);
        Assert.Equal(1, requirements.CancellationIterationsPerTrialRequired);
        Assert.Equal(1, requirements.CancellationTrialsRequired);
    }

    [Fact]
    public void FrozenPopulationsMakeTailEstimatorsUseMultipleObservations()
    {
        Assert.Equal(
            95,
            PerformanceStatistics.Percentile(Enumerable.Range(1, 100).Select(static value => (double)value), 0.95));
        Assert.Equal(
            3,
            PerformanceStatistics.Percentile(new double[] { 1, 2, 3, 100, 200 }, 0.50));
        Assert.Equal(
            PerformanceMeasurementContract.SourceLookupRegressionOperationCount,
            PerformanceMeasurementContract.SourceLookupRegressionBatchSize *
            PerformanceMeasurementContract.SourceLookupRegressionObservationCount);
        Assert.Equal(5, PerformanceMeasurementContract.SourceLookupRegressionTrialCount);
        Assert.Equal(6, PerformanceMeasurementContract.ReleaseFirstViewportTrials);
        Assert.Equal(3, PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials);
        Assert.Equal(5, PerformanceMeasurementContract.ReleaseCancellationTrials);
        Assert.Equal(
            PerformanceMeasurementContract.SixTrialHodgesLehmannRegressionEstimator,
            PerformanceMeasurementContract.FirstViewportRegressionEstimator);
        Assert.Equal(
            PerformanceMeasurementContract.FiveTrialHodgesLehmannRegressionEstimator,
            PerformanceMeasurementContract.ScrollRegressionEstimator);
        Assert.Equal(
            PerformanceMeasurementContract.FiveTrialHodgesLehmannRegressionEstimator,
            PerformanceMeasurementContract.SourceLookupRegressionEstimator);
        Assert.Equal(
            PerformanceMeasurementContract.FiveTrialHodgesLehmannRegressionEstimator,
            PerformanceMeasurementContract.CancellationRegressionEstimator);
        Assert.Equal(
            3,
            PerformanceStatistics.HodgesLehmann(new double[] { 1, 2, 3, 4, 100 }));
        Assert.Equal(0, PerformanceStatistics.RobustDispersionPercent([4, 4, 4, 4, 4]));
    }

    [Fact]
    public void SchemaElevenFreezesTheSixConditionWilliamsSchedule()
    {
        Assert.Equal(
            new[] { 0, 1, 5, 2, 4, 3 },
            Enumerable.Range(0, PerformanceMeasurementContract.FirstViewportConditionCount)
                .Select(position =>
                    PerformanceMeasurementContract.GetFirstViewportCondition(0, position)));

        for (int row = 0; row < PerformanceMeasurementContract.ReleaseFirstViewportTrials; row++)
        {
            Assert.Equal(
                Enumerable.Range(0, PerformanceMeasurementContract.FirstViewportConditionCount),
                Enumerable.Range(0, PerformanceMeasurementContract.FirstViewportConditionCount)
                    .Select(position =>
                        PerformanceMeasurementContract.GetFirstViewportCondition(row, position))
                    .Order());
        }

        for (int position = 0;
             position < PerformanceMeasurementContract.FirstViewportConditionCount;
             position++)
        {
            Assert.Equal(
                Enumerable.Range(0, PerformanceMeasurementContract.FirstViewportConditionCount),
                Enumerable.Range(0, PerformanceMeasurementContract.ReleaseFirstViewportTrials)
                    .Select(row =>
                        PerformanceMeasurementContract.GetFirstViewportCondition(row, position))
                    .Order());
        }
    }

    [Fact]
    public void SchemaElevenFreezesHybridNoiseFloorsAndDispersionBudgets()
    {
        Assert.Equal(5, PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
            PerformanceMeasurementContract.FirstViewportCacheDisabledMode, 100 * 1024));
        Assert.Equal(3, PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
            PerformanceMeasurementContract.FirstViewportCacheHitMode, 100 * 1024));
        Assert.Equal(5, PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
            PerformanceMeasurementContract.FirstViewportCacheDisabledMode, 1024 * 1024));
        Assert.Equal(5, PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
            PerformanceMeasurementContract.FirstViewportCacheHitMode, 1024 * 1024));
        Assert.Equal(3, PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
            PerformanceMeasurementContract.FirstViewportCacheDisabledMode, 10 * 1024 * 1024));
        Assert.Equal(1, PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
            PerformanceMeasurementContract.FirstViewportCacheHitMode, 10 * 1024 * 1024));
        Assert.True(double.IsNaN(
            PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
                PerformanceMeasurementContract.FirstViewportCacheDisabledMode, 1)));
        Assert.Equal(0.05, PerformanceMeasurementContract.ScrollUiThreadWorkP95NoiseFloorMilliseconds);
        Assert.Equal(0.10, PerformanceMeasurementContract.ScrollUiThreadWorkP99NoiseFloorMilliseconds);
        Assert.Equal(2, PerformanceMeasurementContract.ScrollFrameTimeP95NoiseFloorMilliseconds);
        Assert.Equal(120d, PerformanceMeasurementContract.TargetRefreshRateHz);
        Assert.Equal(
            1_000d / 120d,
            PerformanceMeasurementContract.ScrollFrameTimeP95BudgetMilliseconds);
        Assert.Equal(30, PerformanceMeasurementContract.SourceLookupNoiseFloorNanoseconds);
        Assert.Equal(0.05, PerformanceMeasurementContract.CancellationNoiseFloorMilliseconds);
        Assert.Equal(64, PerformanceMeasurementContract.RendererOwnedAllocationNoiseFloorBytes);
        Assert.Equal(25, PerformanceMeasurementContract.FirstViewportDispersionBudgetPercent);
        Assert.Equal(25, PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent);
        Assert.Equal(35, PerformanceMeasurementContract.ScrollFrameTimeDispersionBudgetPercent);
        Assert.Equal(20, PerformanceMeasurementContract.SourceLookupDispersionBudgetPercent);
        Assert.Equal(25, PerformanceMeasurementContract.CancellationDispersionBudgetPercent);
        Assert.Equal(
            "max-relative-percent-or-absolute-noise-floor-with-robust-dispersion",
            PerformanceMeasurementContract.RegressionDecisionPolicy);
        Assert.Equal(
            "execution-speed=off;ignore-timer-resolution=off",
            PerformanceMeasurementContract.ProcessPowerThrottlingMode);
        Assert.Equal(0x1u, PerformanceMeasurementContract.ProcessPowerThrottlingExecutionSpeedMask);
        Assert.Equal(0x4u, PerformanceMeasurementContract.ProcessPowerThrottlingIgnoreTimerResolutionMask);
        Assert.Equal(0x5u, PerformanceMeasurementContract.ProcessPowerThrottlingControlMask);
    }

    [Fact]
    public void ReferenceEvidenceDeserializesAndHashesOneByteSnapshot()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"markdown-renderer-performance-{Guid.NewGuid():N}.json");
        byte[] originalBytes = JsonSerializer.SerializeToUtf8Bytes(
            new PerformanceReport { BuildIdentity = "original" },
            PerformanceReport.JsonOptions);
        byte[] replacementBytes = JsonSerializer.SerializeToUtf8Bytes(
            new PerformanceReport { BuildIdentity = "replacement" },
            PerformanceReport.JsonOptions);
        try
        {
            File.WriteAllBytes(path, originalBytes);
            PerformanceReportEvidence evidence = PerformanceReport.ReadEvidence(path);
            File.WriteAllBytes(path, replacementBytes);

            Assert.Equal("original", evidence.Report.BuildIdentity);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(originalBytes)), evidence.Sha256);
            Assert.NotEqual(
                Convert.ToHexString(SHA256.HashData(replacementBytes)),
                evidence.Sha256);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("providerName")]
    [InlineData("sampleRequirements")]
    [InlineData("buildArtifacts")]
    [InlineData("runtimeConfiguration")]
    [InlineData("machine")]
    [InlineData("measurementVisibility")]
    [InlineData("regression")]
    public void ReferenceEvidenceRejectsMissingRequiredTopLevelMembers(string member)
    {
        JsonObject root = JsonSerializer.SerializeToNode(
            new PerformanceReport(),
            PerformanceReport.JsonOptions)!.AsObject();
        Assert.True(root.Remove(member));

        AssertInvalidReferenceJson(root);
    }

    [Fact]
    public void ReferenceEvidenceRejectsMissingRequiredNestedMembers()
    {
        JsonObject root = JsonSerializer.SerializeToNode(
            new PerformanceReport(),
            PerformanceReport.JsonOptions)!.AsObject();
        JsonObject requirements = root["sampleRequirements"]!.AsObject();
        Assert.True(requirements.Remove("cancellationTrialsRequired"));

        AssertInvalidReferenceJson(root);
    }

    [Theory]
    [InlineData("buildArtifacts", "runtimeConfig")]
    [InlineData("runtimeConfiguration", "readyToRunEnabled")]
    [InlineData("sampleRequirements", "firstViewportWarmupTrialsRequired")]
    public void ReferenceEvidenceRejectsMissingSchemaTenNestedMembers(
        string section,
        string member)
    {
        JsonObject root = JsonSerializer.SerializeToNode(
            new PerformanceReport(),
            PerformanceReport.JsonOptions)!.AsObject();
        Assert.True(root[section]!.AsObject().Remove(member));

        AssertInvalidReferenceJson(root);
    }

    [Fact]
    public void ReferenceEvidenceRejectsExplicitNullForNonNullableSections()
    {
        JsonObject root = JsonSerializer.SerializeToNode(
            new PerformanceReport(),
            PerformanceReport.JsonOptions)!.AsObject();
        root["machine"] = null;

        AssertInvalidReferenceJson(root);
    }

    [Fact]
    public void ReferenceEvidenceRejectsNullObjectCollectionElements()
    {
        JsonObject root = JsonSerializer.SerializeToNode(
            new PerformanceReport(),
            PerformanceReport.JsonOptions)!.AsObject();
        root["firstUsableViewport"] = new JsonArray((JsonNode?)null);

        AssertInvalidReferenceJson(root);
    }

    [Fact]
    public void ReferenceEvidenceRejectsNullNestedTrialElements()
    {
        var report = new PerformanceReport();
        report.FirstUsableViewport.Add(new FirstViewportResult());
        JsonObject root = JsonSerializer.SerializeToNode(
            report,
            PerformanceReport.JsonOptions)!.AsObject();
        JsonObject viewport = root["firstUsableViewport"]![0]!.AsObject();
        viewport["trials"] = new JsonArray((JsonNode?)null);

        AssertInvalidReferenceJson(root);
    }

    [Fact]
    public void ReferenceEvidenceRejectsNullWarmupTrialElements()
    {
        var report = new PerformanceReport();
        report.FirstUsableViewport.Add(new FirstViewportResult());
        JsonObject root = JsonSerializer.SerializeToNode(
            report,
            PerformanceReport.JsonOptions)!.AsObject();
        JsonObject viewport = root["firstUsableViewport"]![0]!.AsObject();
        viewport["warmupTrials"] = new JsonArray((JsonNode?)null);

        AssertInvalidReferenceJson(root);
    }

    [Fact]
    public void ReferenceEvidenceRejectsUnknownMembers()
    {
        JsonObject root = JsonSerializer.SerializeToNode(
            new PerformanceReport(),
            PerformanceReport.JsonOptions)!.AsObject();
        root["futureUnreviewedEvidence"] = true;

        AssertInvalidReferenceJson(root);
    }

    [Fact]
    public void ReferenceEvidenceRejectsDuplicateMembers()
    {
        string json = JsonSerializer.Serialize(
            new PerformanceReport(),
            PerformanceReport.JsonOptions);
        int openingBrace = json.IndexOf('{');
        Assert.True(openingBrace >= 0);
        json = json.Insert(openingBrace + 1, "\n  \"schemaVersion\": 10,");

        string path = NewTemporaryPath();
        try
        {
            File.WriteAllText(path, json);
            Assert.Throws<InvalidDataException>(() => PerformanceReport.ReadEvidence(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OptionsRejectIncompatibleModesAndPopulationOverridesInEitherOrder()
    {
        string referencePath = NewTemporaryPath();
        try
        {
            File.WriteAllText(referencePath, "{}");
            Assert.Throws<ArgumentException>(() => PerformanceOptions.Parse(
            [
                "--output", NewTemporaryPath(), "--baseline", "--reference", referencePath,
            ]));
            Assert.Throws<ArgumentException>(() => PerformanceOptions.Parse(
            [
                "--output", NewTemporaryPath(), "--quick", "--baseline",
            ]));
            Assert.Throws<ArgumentException>(() => PerformanceOptions.Parse(
            [
                "--output", NewTemporaryPath(), "--quick", "--reference", referencePath,
            ]));
            Assert.Throws<ArgumentException>(() => PerformanceOptions.Parse(
            [
                "--output", NewTemporaryPath(), "--scroll-frames", "30", "--quick",
            ]));
            Assert.Throws<ArgumentException>(() => PerformanceOptions.Parse(
            [
                "--output", NewTemporaryPath(), "--quick", "--scroll-frames", "30",
            ]));
        }
        finally
        {
            File.Delete(referencePath);
        }
    }

    [Fact]
    public void OptionsRejectOutputReferenceAliasesAndMissingReferences()
    {
        string referencePath = NewTemporaryPath();
        try
        {
            File.WriteAllText(referencePath, "{}");
            Assert.Throws<ArgumentException>(() => PerformanceOptions.Parse(
            [
                "--output", referencePath.ToUpperInvariant(), "--reference", referencePath,
            ]));
            Assert.Throws<FileNotFoundException>(() => PerformanceOptions.Parse(
            [
                "--output", NewTemporaryPath(), "--reference", NewTemporaryPath(),
            ]));
        }
        finally
        {
            File.Delete(referencePath);
        }
    }

    [Fact]
    public void OptionsAndWriterNeverReuseOrOverwriteAnOutput()
    {
        string outputPath = NewTemporaryPath();
        try
        {
            File.WriteAllText(outputPath, "preserve-me");
            Assert.Throws<IOException>(() => PerformanceOptions.Parse(
            [
                "--output", outputPath, "--baseline",
            ]));
            Assert.Throws<IOException>(() =>
                PerformanceReportWriter.WriteNew(outputPath, new PerformanceReport()));
            Assert.Equal("preserve-me", File.ReadAllText(outputPath));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task AsyncWriterUsesTheSameCreateNewContract()
    {
        string outputPath = NewTemporaryPath();
        try
        {
            await PerformanceReportWriter.WriteNewAsync(outputPath, new PerformanceReport());
            await Assert.ThrowsAsync<IOException>(() =>
                PerformanceReportWriter.WriteNewAsync(outputPath, new PerformanceReport()));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void QuickSmokeValidatorAcceptsStructureWithoutApplyingReleaseBudgets()
    {
        JsonObject json = JsonSerializer.SerializeToNode(
            QuickPerformanceReportFixture.Create(),
            PerformanceReport.JsonOptions)!.AsObject();
        JsonObject viewport = json["firstUsableViewport"]![0]!.AsObject();
        viewport["trials"]![0]!["samplesMilliseconds"]![0] = 10_000;
        viewport["trials"]![0]!["p95Milliseconds"] = 10_000;
        viewport["samplesMilliseconds"]![0] = 10_000;
        viewport["p95Milliseconds"] = 10_000;
        viewport["regressionP95Milliseconds"] = 10_000;
        viewport["stationarityAllowanceMilliseconds"] = 500;
        viewport["passed"] = false;
        PerformanceReport report = JsonSerializer.Deserialize<PerformanceReport>(
            json.ToJsonString(PerformanceReport.JsonOptions),
            PerformanceReport.JsonOptions)!;

        Assert.Empty(QuickPerformanceSmokeValidator.Validate(report, 0, []));
    }

    [Fact]
    public void QuickSmokeValidatorRejectsStructuralProviderAndRenderFailures()
    {
        PerformanceReport report = QuickPerformanceReportFixture.Create();
        report.Scroll = new ScrollResult();

        IReadOnlyList<string> failures = QuickPerformanceSmokeValidator.Validate(
            report,
            renderFailures: 1,
            providerErrors: ["provider failed"]);

        Assert.Contains(failures, failure => failure.Contains("30-frame", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("renderer failure", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("ETW provider error", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("aggregate-metric")]
    [InlineData("aggregate-finite-mismatch")]
    [InlineData("aggregate-regression")]
    [InlineData("allocation-scope")]
    [InlineData("trial-metric")]
    [InlineData("trial-offscreen")]
    [InlineData("aggregate-offscreen")]
    [InlineData("aggregate-gen2-mismatch")]
    [InlineData("impossible-p99")]
    [InlineData("impossible-max-stall")]
    [InlineData("impossible-frame-percent")]
    [InlineData("impossible-allocation-maximum")]
    [InlineData("raw-scroll-frequency")]
    [InlineData("raw-scroll-ui-tick")]
    [InlineData("raw-scroll-frame-count")]
    [InlineData("raw-scroll-allocation")]
    [InlineData("lifecycle-growth")]
    [InlineData("runtime-policy")]
    [InlineData("runtime-override")]
    [InlineData("runtimeconfig-artifact")]
    public void QuickSmokeValidatorRejectsMalformedNonBudgetEvidence(string mutation)
    {
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(
            QuickPerformanceReportFixture.Create(),
            PerformanceReport.JsonOptions))!.AsObject();
        QuickPerformanceSmokeMutations.NonBudget[mutation](json);
        PerformanceReport malformed = JsonSerializer.Deserialize<PerformanceReport>(
            json.ToJsonString(PerformanceReport.JsonOptions),
            PerformanceReport.JsonOptions)!;

        Assert.NotEmpty(QuickPerformanceSmokeValidator.Validate(malformed, 0, []));
    }

    [Theory]
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
    [InlineData("viewport-schedule-position")]
    [InlineData("viewport-chronology")]
    [InlineData("viewport-cache-proof")]
    [InlineData("viewport-cache-counter")]
    [InlineData("viewport-gc")]
    [InlineData("viewport-allocation")]
    [InlineData("viewport-trial-p95")]
    [InlineData("viewport-flattened-samples")]
    [InlineData("viewport-regression-estimate")]
    [InlineData("viewport-stationarity-derived")]
    [InlineData("viewport-stationarity-flags")]
    [InlineData("lookup-estimator")]
    [InlineData("lookup-affinity")]
    [InlineData("lookup-trial-count")]
    [InlineData("lookup-trial-order")]
    [InlineData("lookup-trial-tick-normalization")]
    [InlineData("lookup-trial-p95")]
    [InlineData("lookup-top-estimate")]
    [InlineData("cancellation-estimator")]
    [InlineData("cancellation-trial-order")]
    [InlineData("cancellation-seed")]
    [InlineData("cancellation-trial-p95")]
    [InlineData("cancellation-flattened-samples")]
    public void QuickSmokeValidatorRejectsMalformedRepeatabilityProtocol(string mutation)
    {
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(
            QuickPerformanceReportFixture.Create(),
            PerformanceReport.JsonOptions))!.AsObject();
        QuickPerformanceSmokeMutations.Repeatability[mutation](json);
        PerformanceReport malformed = JsonSerializer.Deserialize<PerformanceReport>(
            json.ToJsonString(PerformanceReport.JsonOptions),
            PerformanceReport.JsonOptions)!;

        Assert.NotEmpty(QuickPerformanceSmokeValidator.Validate(malformed, 0, []));
    }

    private static void AssertInvalidReferenceJson(JsonNode root)
    {
        string path = NewTemporaryPath();
        try
        {
            File.WriteAllText(path, root.ToJsonString());
            Assert.Throws<InvalidDataException>(() => PerformanceReport.ReadEvidence(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string NewTemporaryPath()
        => Path.Combine(
            Path.GetTempPath(),
            $"markdown-renderer-performance-{Guid.NewGuid():N}.json");
}
