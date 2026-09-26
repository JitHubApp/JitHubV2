using MarkdownRenderer.PerformanceHarness;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PerformanceMeasurementContractTests
{
    [Fact]
    public void ReleaseDefaultsUseStatisticallyMeaningfulPercentilePopulations()
    {
        PerformanceOptions options = PerformanceOptions.Parse(
        [
            "--output",
            NewTemporaryPath(),
            "--baseline",
        ]);

        Assert.Equal(11, PerformanceMeasurementContract.SchemaVersion);
        Assert.Equal("MarkdownRenderer-Performance", PerformanceMeasurementContract.ProviderName);
        Assert.Equal(100, options.FirstViewportIterations);
        Assert.Equal(2_400, options.ScrollFrames);
        Assert.Equal(5, options.ScrollTrials);
        Assert.Equal(100, options.LifecycleCycles);
        Assert.Equal(40, options.CancellationIterations);
        Assert.True(options.EstablishBaseline);
        Assert.False(options.Quick);

        var report = new PerformanceReport();
        Assert.Equal(11, report.SchemaVersion);
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
            CreateValidQuickReport(),
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
        PerformanceReport report = CreateValidQuickReport();
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
            CreateValidQuickReport(),
            PerformanceReport.JsonOptions))!.AsObject();
        JsonObject scroll = json["scroll"]!.AsObject();
        JsonObject trial = scroll["trials"]![0]!.AsObject();
        switch (mutation)
        {
            case "aggregate-metric":
                scroll["uiThreadWorkP95Milliseconds"] = -1;
                break;
            case "aggregate-finite-mismatch":
                scroll["uiThreadWorkP95Milliseconds"] = 99;
                break;
            case "aggregate-regression":
                scroll["regressionUiThreadWorkP95Milliseconds"] = 99;
                break;
            case "allocation-scope":
                scroll["rawAllocationScope"] = string.Empty;
                break;
            case "trial-metric":
                trial["paintCallbackWorkP95Milliseconds"] = -1;
                break;
            case "trial-offscreen":
                trial["maximumOffscreenRealizedCodeActions"] = 1;
                break;
            case "aggregate-offscreen":
                scroll["maximumOffscreenRealizedCodeActions"] = 1;
                break;
            case "aggregate-gen2-mismatch":
                scroll["gen2Collections"] = 2;
                break;
            case "impossible-p99":
                scroll["uiThreadWorkP99Milliseconds"] = 99;
                scroll["regressionUiThreadWorkP99Milliseconds"] = 99;
                trial["uiThreadWorkP99Milliseconds"] = 99;
                break;
            case "impossible-max-stall":
                scroll["maximumFrameStallMilliseconds"] = 99;
                trial["maximumFrameStallMilliseconds"] = 99;
                break;
            case "impossible-frame-percent":
                scroll["framesOver16_7Percent"] = 101;
                trial["framesOver16_7Percent"] = 101;
                break;
            case "impossible-allocation-maximum":
                scroll["maximumRendererOwnedAllocatedBytesPerFrame"] = 99_999;
                trial["maximumRendererOwnedAllocatedBytesPerFrame"] = 99_999;
                break;
            case "raw-scroll-frequency":
                scroll["stopwatchFrequency"] = 0;
                break;
            case "raw-scroll-ui-tick":
                trial["uiThreadWorkElapsedTicks"]![0] = -1;
                break;
            case "raw-scroll-frame-count":
                trial["frameIntervalElapsedTicks"]!.AsArray().RemoveAt(0);
                break;
            case "raw-scroll-allocation":
                trial["rendererOwnedAllocatedBytesPerFrame"]![0] = -1;
                break;
            case "lifecycle-growth":
                json["lifecyclePlateau"]!["checkpoint100ManagedBytes"] = 2;
                break;
            case "runtime-policy":
                json["runtimeConfiguration"]!["policy"] = "uncontrolled";
                break;
            case "runtime-override":
                json["runtimeConfiguration"]!["overrideEnvironmentVariables"] =
                    new JsonArray("DOTNET_TieredCompilation");
                break;
            case "runtimeconfig-artifact":
                json["buildArtifacts"]!["runtimeConfig"]!["sha256"] = "not-a-sha256";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
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
            CreateValidQuickReport(),
            PerformanceReport.JsonOptions))!.AsObject();
        JsonObject viewport = json["firstUsableViewport"]![0]!.AsObject();
        JsonObject lookup = json["sourceLookup"]!.AsObject();
        JsonObject lookupTrial = lookup["regressionTrials"]![0]!.AsObject();
        JsonObject cancellation = json["cancellation"]!.AsObject();
        JsonObject cancellationTrial = cancellation["trials"]![0]!.AsObject();
        switch (mutation)
        {
            case "viewport-warmup-count":
                viewport["warmupTrials"]!.AsArray().Clear();
                break;
            case "viewport-cache":
                viewport["parseCacheBudgetBytes"] = 1;
                break;
            case "viewport-engine-reuse":
                viewport["engineReuseMode"] = "per-sample";
                break;
            case "viewport-trial-policy":
                viewport["trialStartPolicy"] = "none";
                break;
            case "viewport-schedule-policy":
                viewport["schedulePolicy"] = "none";
                break;
            case "viewport-stationarity-policy":
                viewport["stationarityPolicy"] = "none";
                break;
            case "viewport-affinity":
                viewport["measurementThreadAffinityMask"] = "0x0000000000000000";
                break;
            case "viewport-estimator":
                viewport["regressionEstimator"] = "pooled-p95";
                break;
            case "viewport-trial-count":
                viewport["trials"]!.AsArray().Clear();
                break;
            case "viewport-trial-order":
                viewport["trials"]![0]!["ordinal"] = 1;
                break;
            case "viewport-global-order":
                viewport["trials"]![0]!["globalOrdinal"] = 0;
                break;
            case "viewport-schedule-position":
                viewport["trials"]![0]!["schedulePosition"] = 5;
                break;
            case "viewport-chronology":
                viewport["trials"]![0]!["startedUtc"] = "2026-09-10T11:00:00-07:00";
                break;
            case "viewport-cache-proof":
                viewport["trials"]![0]!["cacheProofPassed"] = false;
                break;
            case "viewport-cache-counter":
                viewport["trials"]![0]!["sourceKeyHashCountAfterTrial"] = 0;
                break;
            case "viewport-gc":
                viewport["trials"]![0]!["gen0Collections"] = -1;
                break;
            case "viewport-allocation":
                viewport["trials"]![0]!["processAllocatedBytes"] = -1;
                break;
            case "viewport-trial-p95":
                viewport["trials"]![0]!["p95Milliseconds"] = 2;
                break;
            case "viewport-flattened-samples":
                viewport["samplesMilliseconds"]![0] = 2;
                viewport["p95Milliseconds"] = 2;
                viewport["regressionP95Milliseconds"] = 2;
                break;
            case "viewport-regression-estimate":
                viewport["regressionP95Milliseconds"] = 2;
                break;
            case "viewport-stationarity-derived":
                viewport["projectedDriftMilliseconds"] = 1;
                break;
            case "viewport-stationarity-flags":
                viewport["stationarityEvaluated"] = true;
                break;
            case "lookup-estimator":
                lookup["regressionEstimator"] = "single-trial";
                break;
            case "lookup-affinity":
                lookup["measurementThreadAffinityMask"] = "0x0000000000000000";
                break;
            case "lookup-trial-count":
                lookup["regressionTrialCount"] = 4;
                break;
            case "lookup-trial-order":
                lookupTrial["ordinal"] = 1;
                break;
            case "lookup-trial-tick-normalization":
                lookupTrial["samplesNanosecondsPerLookup"]![0] = 999;
                break;
            case "lookup-trial-p95":
                lookupTrial["p95Nanoseconds"] = 999;
                break;
            case "lookup-top-estimate":
                lookup["regressionP95Nanoseconds"] = 999;
                break;
            case "cancellation-estimator":
                cancellation["regressionEstimator"] = "single-trial";
                break;
            case "cancellation-trial-order":
                cancellationTrial["ordinal"] = 1;
                break;
            case "cancellation-seed":
                cancellationTrial["warmupSourceSeed"] = 123;
                break;
            case "cancellation-trial-p95":
                cancellationTrial["p95Milliseconds"] = 99;
                break;
            case "cancellation-flattened-samples":
                cancellation["samplesMilliseconds"]![0] = 99;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        PerformanceReport malformed = JsonSerializer.Deserialize<PerformanceReport>(
            json.ToJsonString(PerformanceReport.JsonOptions),
            PerformanceReport.JsonOptions)!;
        Assert.NotEmpty(QuickPerformanceSmokeValidator.Validate(malformed, 0, []));
    }

    private static PerformanceReport CreateValidQuickReport()
    {
        long stopwatchFrequency = System.Diagnostics.Stopwatch.Frequency;
        long elapsedTicks = Math.Max(1, stopwatchFrequency / 10);
        double elapsedMilliseconds = elapsedTicks * 1000.0 / stopwatchFrequency;
        const long rendererOwnedAllocatedBytes = 100_000;
        IReadOnlyList<CancellationMeasurementIteration> cancellationPlan =
            CancellationMeasurementPlan.CreateTrial(0, 1);
        var report = new PerformanceReport
        {
            StartedUtc = DateTimeOffset.Parse("2026-09-10T11:59:00Z"),
            CompletedUtc = DateTimeOffset.Parse("2026-09-10T12:01:00Z"),
            IsReleaseEvidence = false,
            BuildArtifacts = CreateStoredBuildArtifacts(),
            RuntimeConfiguration = CreateRuntimeConfiguration(),
            SampleRequirements = new SampleRequirements
            {
                FirstViewportIterationsRequired = 1,
                FirstViewportTrialsRequired = 1,
                FirstViewportWarmupTrialsRequired = 1,
                ScrollFramesRequired = 30,
                ScrollTrialsRequired = 1,
                LifecycleCyclesRequired = 8,
                CancellationIterationsPerTrialRequired = 1,
                CancellationTrialsRequired = 1,
            },
            Scroll = new ScrollResult
            {
                Corpus = PerformanceDocumentFactory.WarmScrollStressCorpus,
                SourceUtf16Bytes = 1024 * 1024,
                StopwatchFrequency = stopwatchFrequency,
                RequestedFrames = 30,
                MeasuredFrameIntervals = 30,
                FramesWithRendererWork = 30,
                RegressionEstimator = "quick-single-trial-non-gating",
                RegressionUiThreadWorkP95Milliseconds = elapsedMilliseconds,
                RegressionUiThreadWorkP99Milliseconds = elapsedMilliseconds,
                RegressionFrameTimeP95Milliseconds = elapsedMilliseconds,
                RegressionRendererOwnedAllocatedBytesPerFrameP95 = rendererOwnedAllocatedBytes,
                UiThreadWorkP95Milliseconds = elapsedMilliseconds,
                UiThreadWorkP99Milliseconds = elapsedMilliseconds,
                FrameTimeP95Milliseconds = elapsedMilliseconds,
                FramesOver16_7Percent = 100,
                MaximumFrameStallMilliseconds = elapsedMilliseconds,
                AllocatedBytesPerFrameP95 = rendererOwnedAllocatedBytes,
                MaximumAllocatedBytesPerFrame = rendererOwnedAllocatedBytes,
                RendererOwnedAllocatedBytesPerFrameP95 = rendererOwnedAllocatedBytes,
                MaximumRendererOwnedAllocatedBytesPerFrame = rendererOwnedAllocatedBytes,
                LohAllocationPossible = true,
                RendererOwnedLohAllocationPossible = true,
                Gen2Collections = 1,
                Trials =
                [
                    new ScrollTrialResult
                    {
                        Ordinal = 0,
                        FirstLogicalFrameId = 10_000,
                        RequestedFrames = 30,
                        MeasuredFrameIntervals = 30,
                        FramesWithRendererWork = 30,
                        UiThreadWorkElapsedTicks = Enumerable.Repeat(elapsedTicks, 30).ToList(),
                        FrameIntervalElapsedTicks = Enumerable.Repeat(elapsedTicks, 30).ToList(),
                        RendererOwnedAllocatedBytesPerFrame = Enumerable.Repeat(
                            rendererOwnedAllocatedBytes,
                            30).ToList(),
                        UiThreadWorkP95Milliseconds = elapsedMilliseconds,
                        UiThreadWorkP99Milliseconds = elapsedMilliseconds,
                        FrameTimeP95Milliseconds = elapsedMilliseconds,
                        FramesOver16_7Percent = 100,
                        MaximumFrameStallMilliseconds = elapsedMilliseconds,
                        AllocatedBytesPerFrameP95 = rendererOwnedAllocatedBytes,
                        MaximumAllocatedBytesPerFrame = rendererOwnedAllocatedBytes,
                        RendererOwnedAllocatedBytesPerFrameP95 = rendererOwnedAllocatedBytes,
                        MaximumRendererOwnedAllocatedBytesPerFrame = rendererOwnedAllocatedBytes,
                        Gen2Collections = 1,
                        Complete = true,
                    },
                ],
                Passed = false,
            },
            SourceLookup = CreateValidQuickLookup(),
            LifecyclePlateau = new LifecyclePlateauResult
            {
                RequiredCycles = 8,
                CompletedCycles = 8,
                Checkpoint80ManagedBytes = 1,
                Checkpoint100ManagedBytes = 1,
                Checkpoint80PrivateBytes = 1,
                Checkpoint100PrivateBytes = 1,
                RenderFailures = 0,
                Passed = false,
            },
            Cancellation = new CancellationResult
            {
                RequestedIterations = 1,
                IterationsPerTrial = 1,
                WarmupIterations = 1,
                WarmupElapsedMilliseconds = 100,
                ObservedCancellations = 1,
                TrialStartPolicy = PerformanceMeasurementContract.CancellationTrialStartPolicy,
                RegressionEstimator = "quick-single-trial-non-gating",
                Trials =
                [
                    new CancellationTrialResult
                    {
                        Ordinal = 0,
                        WarmupSourceSeed = cancellationPlan[0].SourceSeed,
                        WarmupElapsedMilliseconds = 100,
                        FirstRecordedSourceSeed = cancellationPlan[1].SourceSeed,
                        RequestedIterations = 1,
                        ObservedCancellations = 1,
                        SamplesMilliseconds = [100],
                        P95Milliseconds = 100,
                        Complete = true,
                    },
                ],
                SamplesMilliseconds = [100],
                P95Milliseconds = 100,
                RegressionP95Milliseconds = 100,
                Passed = false,
            },
            Regression = new RegressionResult
            {
                Mode = "quick-non-gating",
                Passed = false,
            },
        };
        int condition = 0;
        foreach (int sourceBytes in new[] { 100 * 1024, 1024 * 1024, 10 * 1024 * 1024 })
        {
            foreach (string mode in new[]
                     {
                         PerformanceMeasurementContract.FirstViewportCacheDisabledMode,
                         PerformanceMeasurementContract.FirstViewportCacheHitMode,
                     })
            {
                int position = GetFirstViewportSchedulePosition(condition, row: 0);
                FirstViewportTrialResult warmupTrial = CreateQuickViewportTrial(
                    mode,
                    position,
                    globalOrdinal: position);
                FirstViewportTrialResult measuredTrial = CreateQuickViewportTrial(
                    mode,
                    position,
                    globalOrdinal: PerformanceMeasurementContract.FirstViewportConditionCount +
                        position);
                double budget = sourceBytes switch
                {
                    100 * 1024 when mode ==
                        PerformanceMeasurementContract.FirstViewportCacheHitMode => 75,
                    100 * 1024 => 125,
                    1024 * 1024 => 250,
                    10 * 1024 * 1024 => 750,
                    _ => throw new InvalidOperationException(),
                };
                report.FirstUsableViewport.Add(new FirstViewportResult
                {
                    Corpus = PerformanceDocumentFactory.GetCorpusName(sourceBytes),
                    Mode = mode,
                    SourceUtf16Bytes = sourceBytes,
                    ParseCacheBudgetBytes = mode ==
                        PerformanceMeasurementContract.FirstViewportCacheHitMode
                            ? sourceBytes * 8L
                            : 0,
                    EngineReuseMode = PerformanceMeasurementContract.FirstViewportEngineReuseMode,
                    TrialStartPolicy = PerformanceMeasurementContract.FirstViewportTrialStartPolicy,
                    SchedulePolicy = "quick-first-williams-row-non-gating",
                    RegressionEstimator = "quick-single-trial-non-gating",
                    StationarityPolicy = "quick-not-evaluated",
                    MeasurementThreadAffinityMask = "0x0000000000000001",
                    MeasurementProcessorGroup = 0,
                    MeasurementProcessorNumber = 0,
                    MeasurementThreadPriority = "Normal",
                    WarmupTrials = [warmupTrial],
                    Trials = [measuredTrial],
                    SamplesMilliseconds = [1],
                    P95Milliseconds = 1,
                    PublicationSamplesMilliseconds = [1],
                    PublicationMaximumMilliseconds = 1,
                    PublicationBudgetMilliseconds =
                        PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds,
                    RegressionP95Milliseconds = 1,
                    RegressionDispersionPercent = 0,
                    TheilSenSlopeMillisecondsPerGlobalOrdinal = 0,
                    ProjectedDriftMilliseconds = 0,
                    WarmupBoundaryShiftMilliseconds = 0,
                    StationarityAllowanceMilliseconds =
                        PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
                            mode,
                            sourceBytes),
                    StationarityEvaluated = false,
                    StationarityPassed = false,
                    BudgetMilliseconds = budget,
                    Passed = true,
                });
                condition++;
            }
            report.RetainedMemory.Add(new RetainedMemoryResult
            {
                Corpus = PerformanceDocumentFactory.GetCorpusName(sourceBytes),
                SourceUtf16Bytes = sourceBytes,
                ManagedDeltaBytes = 1,
                PrivateDeltaBytes = 1,
                BudgetBytes = 1,
                Passed = false,
            });
        }
        return report;
    }

    private static FirstViewportTrialResult CreateQuickViewportTrial(
        string mode,
        int schedulePosition,
        int globalOrdinal)
    {
        bool cacheHit = mode == PerformanceMeasurementContract.FirstViewportCacheHitMode;
        DateTimeOffset started = DateTimeOffset.Parse("2026-09-10T12:00:00Z")
            .AddMilliseconds(globalOrdinal * 2);
        return new FirstViewportTrialResult
        {
            Ordinal = 0,
            GlobalOrdinal = globalOrdinal,
            ScheduleRow = 0,
            SchedulePosition = schedulePosition,
            StartedUtc = started,
            CompletedUtc = started.AddMilliseconds(1),
            SettlingElapsedMilliseconds = 1,
            SettlingPublicationMilliseconds = 1,
            SamplesMilliseconds = [1],
            PublicationSamplesMilliseconds = [1],
            PublicationMaximumMilliseconds = 1,
            P95Milliseconds = 1,
            Gen0Collections = 0,
            Gen1Collections = 0,
            Gen2Collections = 0,
            ProcessAllocatedBytes = 0,
            CompletedParseCountBeforePrime = 0,
            CompletedParseCountAfterPrime = cacheHit ? 1 : 0,
            CompletedParseCountAfterTrial = cacheHit ? 1 : 0,
            SourceKeyHashCountBeforePrime = 0,
            SourceKeyHashCountAfterPrime = cacheHit ? 1 : 0,
            SourceKeyHashCountAfterTrial = cacheHit ? 1 : 2,
            CacheProofPassed = true,
            Complete = true,
        };
    }

    private static int GetFirstViewportSchedulePosition(int condition, int row)
        => Enumerable.Range(0, PerformanceMeasurementContract.FirstViewportConditionCount)
            .Single(position =>
                PerformanceMeasurementContract.GetFirstViewportCondition(row, position) == condition);

    private static RuntimeConfigurationEvidence CreateRuntimeConfiguration()
        => new()
        {
            Policy = PerformanceMeasurementContract.RuntimeConfigurationPolicy,
            TieredCompilationEnabled = false,
            TieredPgoEnabled = false,
            ConcurrentGcEnabled = false,
            ReadyToRunEnabled = false,
            OverrideEnvironmentVariables = [],
            Passed = true,
        };

    private static BuildArtifactHashes CreateStoredBuildArtifacts()
    {
        const string sha256 =
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        string directory = Path.Combine(Path.GetTempPath(), "markdown-renderer-quick-contract");
        BuildArtifactHash Create(string fileName)
            => new()
            {
                FileName = fileName,
                Path = Path.Combine(directory, fileName),
                Sha256 = sha256,
            };

        return new BuildArtifactHashes
        {
            Executable = Create("MarkdownRenderer.PerformanceHarness.exe"),
            PerformanceHarnessDll = Create("MarkdownRenderer.PerformanceHarness.dll"),
            MarkdownRendererDll = Create("MarkdownRenderer.dll"),
            MarkdownRendererCoreDll = Create("MarkdownRenderer.Core.dll"),
            RuntimeConfig = Create("MarkdownRenderer.PerformanceHarness.runtimeconfig.json"),
        };
    }

    private static SourceLookupResult CreateValidQuickLookup()
    {
        long frequency = System.Diagnostics.Stopwatch.Frequency;
        const long absoluteTicks = 1;
        const long regressionTicks = 4_096;
        double absoluteNanoseconds = absoluteTicks * 1_000_000_000.0 / frequency;
        double regressionNanoseconds =
            regressionTicks * 1_000_000_000.0 / frequency /
            PerformanceMeasurementContract.SourceLookupRegressionBatchSize;
        List<SourceLookupTrialResult> trials = Enumerable.Range(
                0,
                PerformanceMeasurementContract.SourceLookupRegressionTrialCount)
            .Select(ordinal => new SourceLookupTrialResult
            {
                Ordinal = ordinal,
                ObservationCount = PerformanceMeasurementContract.SourceLookupRegressionObservationCount,
                OperationCount = PerformanceMeasurementContract.SourceLookupRegressionOperationCount,
                SamplesElapsedTicks = Enumerable.Repeat(
                    regressionTicks,
                    PerformanceMeasurementContract.SourceLookupRegressionObservationCount).ToList(),
                SamplesNanosecondsPerLookup = Enumerable.Repeat(
                    regressionNanoseconds,
                    PerformanceMeasurementContract.SourceLookupRegressionObservationCount).ToList(),
                P95Nanoseconds = regressionNanoseconds,
                AllocatedBytes = 0,
                Complete = true,
            })
            .ToList();
        return new SourceLookupResult
        {
            MappingCount = PerformanceMeasurementContract.SourceLookupMappingCount,
            QueryCount = PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount,
            StopwatchFrequency = frequency,
            MeasurementThreadAffinityMask = "0x0000000000000001",
            MeasurementProcessorGroup = 0,
            MeasurementProcessorNumber = 0,
            MeasurementThreadPriority = "Highest",
            SamplesElapsedTicks = Enumerable.Repeat(
                absoluteTicks,
                PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount).ToList(),
            P95Nanoseconds = absoluteNanoseconds,
            AllocatedBytes = 0,
            RegressionWarmupPasses =
                PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses,
            RegressionEstimator = PerformanceMeasurementContract.SourceLookupRegressionEstimator,
            RegressionTrialCount = PerformanceMeasurementContract.SourceLookupRegressionTrialCount,
            RegressionBatchSize = PerformanceMeasurementContract.SourceLookupRegressionBatchSize,
            RegressionObservationCount =
                PerformanceMeasurementContract.SourceLookupRegressionObservationCount,
            RegressionOperationCount =
                PerformanceMeasurementContract.SourceLookupRegressionOperationCount,
            RegressionTrials = trials,
            RegressionP95Nanoseconds = regressionNanoseconds,
            RegressionAllocatedBytes = 0,
            Passed = false,
        };
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
