using System.Diagnostics;
using Microsoft.UI.Xaml;
using MarkdownRenderer.Controls;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Measures the first usable viewport for each source size with the parse
/// cache disabled and hit, on a pinned UI thread, in the frozen Williams
/// schedule, and appends one <see cref="FirstViewportResult"/> per condition
/// to the report.
/// </summary>
internal sealed class FirstViewportMeasurement
{
    private readonly PerformanceOptions _options;
    private readonly PerformanceReport _report;
    private readonly PerformanceEventListener _events;
    private readonly MeasurementRendererHost _rendererHost;
    private readonly MonotonicUtcClock _utcClock;
    private readonly CancellationToken _measurementAbort;
    private readonly FirstViewportStageProfileRecorder _stageProfile;

    internal FirstViewportMeasurement(
        PerformanceOptions options,
        PerformanceReport report,
        PerformanceEventListener events,
        MeasurementRendererHost rendererHost,
        MonotonicUtcClock utcClock,
        CancellationToken measurementAbort,
        FirstViewportStageProfileRecorder stageProfile)
    {
        _options = options;
        _report = report;
        _events = events;
        _rendererHost = rendererHost;
        _utcClock = utcClock;
        _measurementAbort = measurementAbort;
        _stageProfile = stageProfile;
    }

    internal async Task MeasureAsync()
    {
        List<FirstViewportScenario> scenarios = CreateScenarios();
        using PinnedMeasurementThreadScope measurementThread =
            PinnedMeasurementThreadScope.Enter(ThreadPriority.Normal, "first-viewport");
        int globalOrdinal = 0;
        int requiredWarmupTrials = _report.SampleRequirements.FirstViewportWarmupTrialsRequired;
        globalOrdinal = await RunScheduleAsync(
            scenarios,
            requiredWarmupTrials,
            globalOrdinal,
            isWarmup: true);

        int requiredTrials = _report.SampleRequirements.FirstViewportTrialsRequired;
        _ = await RunScheduleAsync(
            scenarios,
            requiredTrials,
            globalOrdinal,
            isWarmup: false);

        foreach (FirstViewportScenario scenario in scenarios)
        {
            _report.FirstUsableViewport.Add(Summarize(
                scenario,
                measurementThread,
                requiredWarmupTrials,
                requiredTrials));
        }
    }

    private static List<FirstViewportScenario> CreateScenarios()
    {
        var scenarios = new List<FirstViewportScenario>(
            PerformanceMeasurementContract.FirstViewportConditionCount);
        foreach (int sourceBytes in MeasuredSourceSizes.FirstViewport)
        {
            string source = PerformanceDocumentFactory.Create(sourceBytes, sourceBytes);
            scenarios.Add(new FirstViewportScenario(
                scenarios.Count,
                sourceBytes,
                source,
                PerformanceMeasurementContract.FirstViewportCacheDisabledMode,
                parseCacheBudgetBytes: 0));
            scenarios.Add(new FirstViewportScenario(
                scenarios.Count,
                sourceBytes,
                source,
                PerformanceMeasurementContract.FirstViewportCacheHitMode,
                parseCacheBudgetBytes: Math.Max(0, sourceBytes * 8L)));
        }

        if (scenarios.Count != PerformanceMeasurementContract.FirstViewportConditionCount)
            throw new UnreachableException("The frozen first-viewport design requires six conditions.");
        return scenarios;
    }

    /// <summary>
    /// Runs <paramref name="rows"/> Williams-schedule rows over every condition
    /// and returns the next global trial ordinal.
    /// </summary>
    private async Task<int> RunScheduleAsync(
        List<FirstViewportScenario> scenarios,
        int rows,
        int globalOrdinal,
        bool isWarmup)
    {
        for (int row = 0; row < rows; row++)
        {
            for (int position = 0;
                 position < PerformanceMeasurementContract.FirstViewportConditionCount;
                 position++)
            {
                int condition = PerformanceMeasurementContract.GetFirstViewportCondition(row, position);
                FirstViewportScenario scenario = scenarios[condition];
                List<FirstViewportTrialResult> trials = isWarmup
                    ? scenario.WarmupTrials
                    : scenario.Trials;
                trials.Add(await RunTrialAsync(new FirstViewportTrialSlot(
                    scenario,
                    row,
                    globalOrdinal++,
                    row,
                    position,
                    isWarmup)));
            }
        }

        return globalOrdinal;
    }

    /// <summary>
    /// Builds one condition's result from its warmup and measured trials.
    /// </summary>
    /// <remarks>
    /// This method stays whole because it is one flat mapping of the pooled
    /// samples and estimators onto the frozen FirstViewportResult schema; the
    /// population and stationarity checks it reads are separate methods.
    /// </remarks>
    private FirstViewportResult Summarize(
        FirstViewportScenario scenario,
        PinnedMeasurementThreadScope measurementThread,
        int requiredWarmupTrials,
        int requiredTrials)
    {
        List<double> samples = scenario.Trials
            .SelectMany(static trial => trial.SamplesMilliseconds)
            .ToList();
        List<double> publicationSamples = scenario.Trials
            .SelectMany(static trial => trial.PublicationSamplesMilliseconds)
            .ToList();
        double publicationMaximum = publicationSamples.Count == 0
            ? double.NaN
            : publicationSamples.Max();
        double p95 = PerformanceStatistics.Percentile(samples, 0.95);
        double regressionP95 = PerformanceStatistics.HodgesLehmann(
            scenario.Trials.Select(static trial => trial.P95Milliseconds));
        double regressionDispersion = PerformanceStatistics.RobustDispersionPercent(
            scenario.Trials.Select(static trial => trial.P95Milliseconds));
        FirstViewportStationarity stationarity = FirstViewportStationarity.Evaluate(
            scenario,
            _options.Quick,
            regressionP95,
            regressionDispersion);
        double budget = FirstViewportBudget.GetMilliseconds(
            scenario.SourceBytes,
            scenario.Mode);
        bool populationComplete =
            IsPopulationComplete(
                scenario,
                samples,
                publicationSamples,
                requiredWarmupTrials,
                requiredTrials) &&
            double.IsFinite(p95) &&
            double.IsFinite(publicationMaximum) &&
            double.IsFinite(regressionP95);

        return new FirstViewportResult
        {
            Corpus = PerformanceDocumentFactory.GetCorpusName(scenario.SourceBytes),
            Mode = scenario.Mode,
            SourceUtf16Bytes = scenario.SourceBytes,
            ParseCacheBudgetBytes = scenario.ParseCacheBudgetBytes,
            EngineReuseMode = PerformanceMeasurementContract.FirstViewportEngineReuseMode,
            TrialStartPolicy = PerformanceMeasurementContract.FirstViewportTrialStartPolicy,
            SchedulePolicy = _options.Quick
                ? "quick-first-williams-row-non-gating"
                : PerformanceMeasurementContract.FirstViewportSchedulePolicy,
            RegressionEstimator = _options.Quick
                ? "quick-single-trial-non-gating"
                : PerformanceMeasurementContract.FirstViewportRegressionEstimator,
            StationarityPolicy = _options.Quick
                ? "quick-not-evaluated"
                : PerformanceMeasurementContract.FirstViewportStationarityPolicy,
            MeasurementThreadAffinityMask = measurementThread.AffinityMask,
            MeasurementProcessorGroup = measurementThread.ProcessorGroup,
            MeasurementProcessorNumber = measurementThread.ProcessorNumber,
            MeasurementThreadPriority = measurementThread.ThreadPriority,
            WarmupTrials = scenario.WarmupTrials,
            Trials = scenario.Trials,
            SamplesMilliseconds = samples,
            P95Milliseconds = p95,
            PublicationSamplesMilliseconds = publicationSamples,
            PublicationMaximumMilliseconds = publicationMaximum,
            PublicationBudgetMilliseconds =
                PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds,
            RegressionP95Milliseconds = regressionP95,
            RegressionDispersionPercent = regressionDispersion,
            TheilSenSlopeMillisecondsPerGlobalOrdinal = stationarity.TheilSenSlope,
            ProjectedDriftMilliseconds = stationarity.ProjectedDriftMilliseconds,
            WarmupBoundaryShiftMilliseconds = stationarity.WarmupBoundaryShiftMilliseconds,
            StationarityAllowanceMilliseconds = stationarity.AllowanceMilliseconds,
            StationarityEvaluated = stationarity.Evaluated,
            StationarityPassed = stationarity.Passed,
            BudgetMilliseconds = budget,
            Passed = populationComplete &&
                     p95 <= budget &&
                     (_options.Quick || publicationMaximum <=
                         PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds) &&
                     (_options.Quick || stationarity.Passed),
        };
    }

    private bool IsPopulationComplete(
        FirstViewportScenario scenario,
        List<double> samples,
        List<double> publicationSamples,
        int requiredWarmupTrials,
        int requiredTrials)
        => scenario.WarmupTrials.Count == requiredWarmupTrials &&
            scenario.WarmupTrials.All(static trial => trial.Complete) &&
            scenario.Trials.Count == requiredTrials &&
            scenario.Trials.All(static trial => trial.Complete) &&
            samples.Count == checked(_options.FirstViewportIterations * requiredTrials) &&
            publicationSamples.Count == checked(_options.FirstViewportIterations * requiredTrials) &&
            samples.All(static value => double.IsFinite(value) && value >= 0) &&
            publicationSamples.All(static value => double.IsFinite(value) && value >= 0);

    /// <summary>
    /// Runs one trial: primes the parse cache when the mode is a cache hit,
    /// presents one settling viewport, then the measured presentations.
    /// </summary>
    /// <remarks>
    /// The trial's allocation and collection counters bracket the settling
    /// and measured presentations in this frame. Moving the presentation loop
    /// into another async method would allocate that method's state machine
    /// inside the bracket and change the recorded ProcessAllocatedBytes, so
    /// only the evaluation after the bracket is a separate step.
    /// </remarks>
    private async Task<FirstViewportTrialResult> RunTrialAsync(FirstViewportTrialSlot slot)
    {
        FirstViewportScenario scenario = slot.Scenario;
        using MarkdownEngine engine = PerformanceEngineFactory.Create(
            scenario.SourceBytes,
            scenario.ParseCacheBudgetBytes);
        DateTimeOffset startedUtc = _utcClock.GetUtcNow();
        int completedBeforePrime = engine.CompletedParseCount;
        long hashesBeforePrime = engine.SourceKeyHashCount;
        if (scenario.Mode == PerformanceMeasurementContract.FirstViewportCacheHitMode)
        {
            _ = await engine.ParseAsync(scenario.Source, _measurementAbort);
            // Verify the exact same source identity resolves through the
            // completed-cache fast path before any measurement boundary.
            _ = await engine.ParseAsync(scenario.Source, _measurementAbort);
        }

        int completedAfterPrime = engine.CompletedParseCount;
        long hashesAfterPrime = engine.SourceKeyHashCount;
        await _rendererHost.ForceManagedCollectionAsync();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        FirstViewportPresentation settling = await PresentAsync(
            engine,
            scenario.Source,
            scenario.SourceBytes);
        _stageProfile.Record(slot, "settling", -1, settling);
        var samples = new List<double>(_options.FirstViewportIterations);
        var publicationSamples = new List<double>(_options.FirstViewportIterations);
        for (int iteration = 0; iteration < _options.FirstViewportIterations; iteration++)
        {
            FirstViewportPresentation presented = await PresentAsync(
                engine,
                scenario.Source,
                scenario.SourceBytes);
            _stageProfile.Record(slot, "sample", iteration, presented);
            samples.Add(presented.FirstViewport.ElapsedMilliseconds);
            publicationSamples.Add(presented.PublicationMilliseconds);
        }

        long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        int gen0Collections = GC.CollectionCount(0) - gen0Before;
        int gen1Collections = GC.CollectionCount(1) - gen1Before;
        int gen2Collections = GC.CollectionCount(2) - gen2Before;
        var parseCounters = new FirstViewportParseCounters(
            completedBeforePrime,
            hashesBeforePrime,
            completedAfterPrime,
            hashesAfterPrime,
            engine.CompletedParseCount,
            engine.SourceKeyHashCount);
        return CreateTrialResult(
            slot,
            startedUtc,
            settling,
            samples,
            publicationSamples,
            allocatedBytes,
            (gen0Collections, gen1Collections, gen2Collections),
            parseCounters);
    }

    /// <summary>
    /// Evaluates a finished trial's cache proof and completeness, stamps its
    /// completion time, and builds its result.
    /// </summary>
    private FirstViewportTrialResult CreateTrialResult(
        FirstViewportTrialSlot slot,
        DateTimeOffset startedUtc,
        FirstViewportPresentation settling,
        List<double> samples,
        List<double> publicationSamples,
        long allocatedBytes,
        (int Gen0, int Gen1, int Gen2) collections,
        FirstViewportParseCounters parseCounters)
    {
        int presentations = checked(_options.FirstViewportIterations + 1);
        bool cacheProofPassed = parseCounters.ProvesCacheMode(slot.Scenario.Mode, presentations);
        double trialP95 = PerformanceStatistics.Percentile(samples, 0.95);
        DateTimeOffset completedUtc = _utcClock.GetUtcNow();
        bool complete = IsTrialComplete(
            settling,
            samples,
            publicationSamples,
            trialP95,
            allocatedBytes,
            collections.Gen0,
            collections.Gen1,
            collections.Gen2) &&
            completedUtc > startedUtc &&
            cacheProofPassed;

        return new FirstViewportTrialResult
        {
            Ordinal = slot.Ordinal,
            GlobalOrdinal = slot.GlobalOrdinal,
            ScheduleRow = slot.ScheduleRow,
            SchedulePosition = slot.SchedulePosition,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            SettlingElapsedMilliseconds = settling.FirstViewport.ElapsedMilliseconds,
            SettlingPublicationMilliseconds = settling.PublicationMilliseconds,
            SamplesMilliseconds = samples,
            PublicationSamplesMilliseconds = publicationSamples,
            PublicationMaximumMilliseconds = publicationSamples.Count == 0
                ? double.NaN
                : publicationSamples.Max(),
            P95Milliseconds = trialP95,
            Gen0Collections = collections.Gen0,
            Gen1Collections = collections.Gen1,
            Gen2Collections = collections.Gen2,
            ProcessAllocatedBytes = allocatedBytes,
            CompletedParseCountBeforePrime = parseCounters.CompletedBeforePrime,
            CompletedParseCountAfterPrime = parseCounters.CompletedAfterPrime,
            CompletedParseCountAfterTrial = parseCounters.CompletedAfterTrial,
            SourceKeyHashCountBeforePrime = parseCounters.HashesBeforePrime,
            SourceKeyHashCountAfterPrime = parseCounters.HashesAfterPrime,
            SourceKeyHashCountAfterTrial = parseCounters.HashesAfterTrial,
            CacheProofPassed = cacheProofPassed,
            Complete = complete,
        };
    }

    private bool IsTrialComplete(
        FirstViewportPresentation settling,
        List<double> samples,
        List<double> publicationSamples,
        double trialP95,
        long allocatedBytes,
        int gen0Collections,
        int gen1Collections,
        int gen2Collections)
        => samples.Count == _options.FirstViewportIterations &&
            publicationSamples.Count == _options.FirstViewportIterations &&
            samples.All(static value => double.IsFinite(value) && value >= 0) &&
            publicationSamples.All(static value => double.IsFinite(value) && value >= 0) &&
            double.IsFinite(settling.FirstViewport.ElapsedMilliseconds) &&
            settling.FirstViewport.ElapsedMilliseconds >= 0 &&
            double.IsFinite(settling.PublicationMilliseconds) &&
            settling.PublicationMilliseconds >= 0 &&
            double.IsFinite(trialP95) &&
            allocatedBytes >= 0 &&
            gen0Collections >= 0 &&
            gen1Collections >= 0 &&
            gen2Collections >= 0 &&
            gen0Collections >= gen1Collections &&
            gen1Collections >= gen2Collections;

    /// <summary>
    /// Constructs a renderer with its source, waits for its first viewport,
    /// and disposes it.
    /// </summary>
    /// <remarks>
    /// This method stays whole because it is one ordered measured sequence:
    /// the stage-profile counters are read immediately before the control is
    /// constructed and immediately after its first-viewport event, and the
    /// order of those reads defines the scope the sidecar documents.
    /// </remarks>
    private async Task<FirstViewportPresentation> PresentAsync(
        MarkdownEngine engine,
        string source,
        int sourceBytes)
    {
        await _rendererHost.DisposeRendererAsync();
        await _rendererHost.WaitForCompositionFramesAsync(2);
        try
        {
            // Construct the control with its actual source. Initializing a blank
            // renderer first would interleave empty-source parses with the cache
            // condition and invalidate both the cache proof and the measurement.
            await _rendererHost.WaitForCompositionFramesAsync(1);
            bool captureStageProfile = _stageProfile.Captures(sourceBytes);
            long processAllocatedBefore = captureStageProfile
                ? GC.GetTotalAllocatedBytes(precise: true)
                : 0;
            long uiThreadAllocatedBefore = captureStageProfile
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0;
            int uiThreadBefore = captureStageProfile
                ? Environment.CurrentManagedThreadId
                : 0;
            long sequence = _events.CurrentSequence;
            if (captureStageProfile)
                _events.BeginStageProfilePresentation(sequence, sourceBytes);
            // The sidecar is explicitly diagnostic-only and quick-run-only.
            // Enable stage counters before Markdown starts the initial build;
            // regular quick/release measurements retain the uninstrumented path.
            var renderer = new MarkdownScrollView(new MarkdownRendererDiagnosticOptions(captureStageProfile))
            {
                Engine = engine,
                Markdown = source,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            _rendererHost.AttachRenderer(renderer);
            FirstViewportEvent presented = await _events.WaitForFirstViewportAsync(
                sequence,
                sourceBytes,
                MeasurementRendererHost.ViewportTimeout);
            MarkdownPipelineTimingSnapshot timing = renderer.GetLastPipelineTimingSnapshot();
            if (timing.Generation != presented.Generation ||
                timing.SourceUtf16Bytes != sourceBytes ||
                timing.PublicationStartedTimestamp <= 0 ||
                timing.PublicationEndedTimestamp < timing.PublicationStartedTimestamp)
            {
                throw new InvalidDataException(
                    "The first-viewport event did not match a complete UI publication.");
            }
            double publicationMilliseconds = Stopwatch.GetElapsedTime(
                timing.PublicationStartedTimestamp,
                timing.PublicationEndedTimestamp).TotalMilliseconds;
            FirstViewportStageProfileThreadMetrics? threadMetrics = null;
            if (captureStageProfile &&
                _events.TryConsumeStageProfileMetrics(presented, out FirstViewportStageProfileThreadMetrics captured))
            {
                threadMetrics = captured;
            }
            long processAllocatedBytes = captureStageProfile
                ? Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - processAllocatedBefore)
                : 0;
            long? uiThreadAllocatedBytes = captureStageProfile &&
                Environment.CurrentManagedThreadId == uiThreadBefore
                    ? Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - uiThreadAllocatedBefore)
                    : null;
            FirstViewportStageProfileMeasurements? stageProfileMeasurements = captureStageProfile
                ? new FirstViewportStageProfileMeasurements(
                    timing,
                    processAllocatedBytes,
                    uiThreadAllocatedBytes,
                    threadMetrics)
                : null;
            await _rendererHost.WaitForCompositionFramesAsync(2);
            return new FirstViewportPresentation(
                presented,
                publicationMilliseconds,
                stageProfileMeasurements);
        }
        finally
        {
            await _rendererHost.DisposeRendererAsync();
        }
    }
}
