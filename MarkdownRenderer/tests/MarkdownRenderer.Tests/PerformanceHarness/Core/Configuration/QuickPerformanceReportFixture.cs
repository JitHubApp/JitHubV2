using System.Diagnostics;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Scenarios;
using MarkdownRenderer.Tests.TestSupport;

namespace MarkdownRenderer.Tests.PerformanceHarness.Core.Configuration;

/// <summary>
/// Builds a structurally valid quick (non-gating) performance report: one
/// trial per scenario on the first Williams-schedule row, a 30-frame scroll
/// and eight lifecycle cycles, with pass flags left false because quick runs
/// apply no release budgets.
/// </summary>
internal static class QuickPerformanceReportFixture
{
    private const string QuickRegressionEstimator = "quick-single-trial-non-gating";
    private const int QuickScrollFrames = 30;
    private const long RendererOwnedAllocatedBytes = 100_000;

    internal static PerformanceReport Create()
    {
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
                ScrollFramesRequired = QuickScrollFrames,
                ScrollTrialsRequired = 1,
                LifecycleCyclesRequired = 8,
                CancellationIterationsPerTrialRequired = 1,
                CancellationTrialsRequired = 1,
            },
            Scroll = CreateScroll(),
            SourceLookup = CreateLookup(),
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
            Cancellation = CreateCancellation(),
            Regression = new RegressionResult
            {
                Mode = "quick-non-gating",
                Passed = false,
            },
        };
        AddScenarioResults(report);
        return report;
    }

    private static void AddScenarioResults(PerformanceReport report)
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
                report.FirstUsableViewport.Add(CreateFirstViewport(sourceBytes, mode, condition));
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
    }

    private static ScrollResult CreateScroll()
    {
        long stopwatchFrequency = Stopwatch.Frequency;
        long elapsedTicks = Math.Max(1, stopwatchFrequency / 10);
        double elapsedMilliseconds = elapsedTicks * 1000.0 / stopwatchFrequency;
        return new ScrollResult
        {
            Corpus = PerformanceDocumentFactory.WarmScrollStressCorpus,
            SourceUtf16Bytes = 1024 * 1024,
            StopwatchFrequency = stopwatchFrequency,
            RequestedFrames = QuickScrollFrames,
            MeasuredFrameIntervals = QuickScrollFrames,
            FramesWithRendererWork = QuickScrollFrames,
            RegressionEstimator = QuickRegressionEstimator,
            RegressionUiThreadWorkP95Milliseconds = elapsedMilliseconds,
            RegressionUiThreadWorkP99Milliseconds = elapsedMilliseconds,
            RegressionFrameTimeP95Milliseconds = elapsedMilliseconds,
            RegressionRendererOwnedAllocatedBytesPerFrameP95 = RendererOwnedAllocatedBytes,
            UiThreadWorkP95Milliseconds = elapsedMilliseconds,
            UiThreadWorkP99Milliseconds = elapsedMilliseconds,
            FrameTimeP95Milliseconds = elapsedMilliseconds,
            FramesOver16_7Percent = 100,
            MaximumFrameStallMilliseconds = elapsedMilliseconds,
            AllocatedBytesPerFrameP95 = RendererOwnedAllocatedBytes,
            MaximumAllocatedBytesPerFrame = RendererOwnedAllocatedBytes,
            RendererOwnedAllocatedBytesPerFrameP95 = RendererOwnedAllocatedBytes,
            MaximumRendererOwnedAllocatedBytesPerFrame = RendererOwnedAllocatedBytes,
            LohAllocationPossible = true,
            RendererOwnedLohAllocationPossible = true,
            Gen2Collections = 1,
            Trials = [CreateScrollTrial(elapsedTicks, elapsedMilliseconds)],
            Passed = false,
        };
    }

    private static ScrollTrialResult CreateScrollTrial(long elapsedTicks, double elapsedMilliseconds)
        => new()
        {
            Ordinal = 0,
            FirstLogicalFrameId = 10_000,
            RequestedFrames = QuickScrollFrames,
            MeasuredFrameIntervals = QuickScrollFrames,
            FramesWithRendererWork = QuickScrollFrames,
            UiThreadWorkElapsedTicks = Enumerable.Repeat(elapsedTicks, QuickScrollFrames).ToList(),
            FrameIntervalElapsedTicks = Enumerable.Repeat(elapsedTicks, QuickScrollFrames).ToList(),
            RendererOwnedAllocatedBytesPerFrame = Enumerable.Repeat(
                RendererOwnedAllocatedBytes,
                QuickScrollFrames).ToList(),
            UiThreadWorkP95Milliseconds = elapsedMilliseconds,
            UiThreadWorkP99Milliseconds = elapsedMilliseconds,
            FrameTimeP95Milliseconds = elapsedMilliseconds,
            FramesOver16_7Percent = 100,
            MaximumFrameStallMilliseconds = elapsedMilliseconds,
            AllocatedBytesPerFrameP95 = RendererOwnedAllocatedBytes,
            MaximumAllocatedBytesPerFrame = RendererOwnedAllocatedBytes,
            RendererOwnedAllocatedBytesPerFrameP95 = RendererOwnedAllocatedBytes,
            MaximumRendererOwnedAllocatedBytesPerFrame = RendererOwnedAllocatedBytes,
            Gen2Collections = 1,
            Complete = true,
        };

    private static CancellationResult CreateCancellation()
    {
        IReadOnlyList<CancellationMeasurementIteration> cancellationPlan =
            CancellationMeasurementPlan.CreateTrial(0, 1);
        return new CancellationResult
        {
            RequestedIterations = 1,
            IterationsPerTrial = 1,
            WarmupIterations = 1,
            WarmupElapsedMilliseconds = 100,
            ObservedCancellations = 1,
            TrialStartPolicy = PerformanceMeasurementContract.CancellationTrialStartPolicy,
            RegressionEstimator = QuickRegressionEstimator,
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
        };
    }

    private static FirstViewportResult CreateFirstViewport(int sourceBytes, string mode, int condition)
    {
        int position = PerformanceFirstViewportExpectations.GetSchedulePosition(condition, row: 0);
        FirstViewportTrialResult warmupTrial = CreateViewportTrial(
            mode,
            position,
            globalOrdinal: position);
        FirstViewportTrialResult measuredTrial = CreateViewportTrial(
            mode,
            position,
            globalOrdinal: PerformanceMeasurementContract.FirstViewportConditionCount + position);
        return new FirstViewportResult
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
            RegressionEstimator = QuickRegressionEstimator,
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
            BudgetMilliseconds =
                PerformanceFirstViewportExpectations.GetBudgetMilliseconds(sourceBytes, mode),
            Passed = true,
        };
    }

    private static FirstViewportTrialResult CreateViewportTrial(
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
            RuntimeConfig = Create("MarkdownRenderer.PerformanceHarness.runtimeconfig.json"),
        };
    }

    private static SourceLookupResult CreateLookup()
    {
        long frequency = Stopwatch.Frequency;
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
}
