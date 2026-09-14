using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using Windows.Graphics;
using WinRT.Interop;

namespace MarkdownRenderer.PerformanceHarness;

internal sealed class PerformanceWindow : Window
{
    private const double MeasurementWidthDips = 1100;
    private const double MeasurementHeightDips = 760;
    private const double ScrollResetToleranceDips = 0.5;
    private static readonly TimeSpan ViewportTimeout = TimeSpan.FromMinutes(3);
    private static readonly int[] FirstViewportSourceSizes =
    [
        100 * 1024,
        1024 * 1024,
        10 * 1024 * 1024,
    ];

    private readonly PerformanceOptions _options;
    private readonly PerformanceEventListener _events = new();
    private readonly Grid _host = new();
    private readonly MonotonicUtcClock _utcClock = new();
    private readonly PerformanceReport _report;
    private MarkdownScrollView? _renderer;
    private MarkdownEngine? _ownedRendererEngine;
    private int _renderFailures;
    private bool _started;
    private int _completionState;

    internal PerformanceWindow(PerformanceOptions options)
    {
        _options = options;
        Title = "MarkdownRenderer pinned-machine performance harness";
        AppWindow.Resize(new SizeInt32(1100, 760));
        Content = _host;
        _host.Loaded += OnHostLoaded;

        BuildArtifactHashes buildArtifacts = PerformanceEvidenceHashing.CaptureBuildArtifacts();
        _report = new PerformanceReport
        {
            StartedUtc = _utcClock.GetUtcNow(),
            IsReleaseEvidence = !options.Quick,
            BuildIdentity = MachineProbe.GetBuildIdentity(),
            BuildArtifacts = buildArtifacts,
            RuntimeConfiguration = PerformanceRuntimeContract.Capture(buildArtifacts.RuntimeConfig.Path),
            Machine = new MachineMetadata(),
            SampleRequirements = SampleRequirements.For(options),
        };
    }

    private void OnHostLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
            return;
        _started = true;
        _ = RunGuardedAsync();
    }

    private async Task RunGuardedAsync()
    {
        Debug.WriteLine("[PerfHarness] run started");
        int exitCode;
        try
        {
            await NormalizeMeasurementWindowAsync();
            double dpiScale = _host.XamlRoot?.RasterizationScale ?? 0;
            IntPtr windowHandle = WindowNative.GetWindowHandle(this);
            _report.Machine = MachineProbe.Capture(
                windowHandle,
                CanvasDevice.GetSharedDevice(),
                dpiScale,
                _host.ActualWidth,
                _host.ActualHeight,
                configureProcessPowerThrottling: true);

            await WarmRuntimeAsync();
            Debug.WriteLine("[PerfHarness] runtime warm");
            _report.SourceLookup = MeasureSourceLookup();
            Debug.WriteLine("[PerfHarness] source lookup complete");
            await MeasureFirstUsableViewportsAsync();
            Debug.WriteLine("[PerfHarness] first viewport complete");
            _report.Scroll = await MeasureWarmScrollAsync();
            Debug.WriteLine("[PerfHarness] scroll complete");
            _report.Machine.ObservedRefreshRateHz =
                PerformanceReleaseEvidenceValidator.CalculateObservedRefreshRateHz(
                    _report.Scroll);
            _report.Machine.IsAtLeast120Hz =
                _report.Machine.ConfiguredRefreshRateHz >= 119 &&
                _report.Machine.ObservedRefreshRateHz >= Math.Max(
                    115,
                    _report.Machine.ConfiguredRefreshRateHz * 0.95);
            await MeasureRetainedMemoryAsync();
            Debug.WriteLine("[PerfHarness] retained memory complete");
            _report.LifecyclePlateau = await MeasureLifecyclePlateauAsync();
            Debug.WriteLine("[PerfHarness] lifecycle complete");
            _report.Cancellation = await MeasureCancellationAsync();
            Debug.WriteLine("[PerfHarness] cancellation complete");
            _report.CompletionMachine = MachineProbe.Capture(
                windowHandle,
                CanvasDevice.GetSharedDevice(),
                _host.XamlRoot?.RasterizationScale ?? 0,
                _host.ActualWidth,
                _host.ActualHeight,
                configureProcessPowerThrottling: false);
            // Freeze the serialized report interval before evaluating its
            // evidence. Every recorded trial must be bounded by this exact,
            // positive interval; do not mutate it after the verdict is formed.
            _report.CompletedUtc = _utcClock.GetUtcNow();
            _report.Regression = RegressionEvaluator.Evaluate(_report, _options);
            ValidateReport();
            exitCode = _report.Passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[PerfHarness] run failure: {exception}");
            _report.Failures.Add($"Harness failure: {exception}");
            _report.Passed = false;
            exitCode = 1;
        }

        if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0)
            return;

        if (_report.CompletedUtc == default)
        {
            // A failure before the normal freeze still needs an honest,
            // positive interval. Successful and post-freeze failure payloads
            // retain the exact timestamp that was evaluated above.
            _report.CompletedUtc = _utcClock.GetUtcNow();
        }
        try
        {
            await WriteReportAsync();
            Console.WriteLine($"Performance report: {_options.OutputPath}");
            Console.WriteLine(_options.Quick
                ? _report.Passed
                    ? "Quick performance smoke completed; release gates were not evaluated."
                    : "Quick performance smoke failed."
                : _report.Passed
                    ? "All release performance gates passed."
                    : "One or more release performance gates failed.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not write performance report: {exception}");
            exitCode = 2;
        }

        await DisposeRendererAsync();
        _events.Dispose();
        Environment.Exit(exitCode);
    }

    internal bool TryWriteFatalReport(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _completionState, 1, 0) != 0)
            return false;

        try
        {
            _report.Failures.Add($"Unhandled WinUI harness failure: {exception}");
            _report.Passed = false;
            _report.CompletedUtc = _utcClock.GetUtcNow();
            Program.WriteReport(_options.OutputPath, _report);
            return true;
        }
        catch (Exception writeException)
        {
            Debug.WriteLine($"[PerfHarness] could not write fatal report: {writeException}");
            return false;
        }
        finally
        {
            _ = DisposeRendererAsync();
            _events.Dispose();
        }
    }

    private Task WriteReportAsync()
        => PerformanceReportWriter.WriteNewAsync(_options.OutputPath, _report);

    private async Task WarmRuntimeAsync()
    {
        var engine = CreateEngine(100 * 1024);
        await ReplaceRendererAsync(engine);
        string source = PerformanceDocumentFactory.Create(100 * 1024, -1);
        long sequence = _events.CurrentSequence;
        _renderer!.Markdown = source;
        _ = await _events.WaitForFirstViewportAsync(sequence, 100 * 1024, ViewportTimeout);
        await WaitForCompositionFramesAsync(8);
        await DisposeRendererAsync();
        await ForceCollectionAsync();
    }

    private async Task NormalizeMeasurementWindowAsync()
    {
        double scale = _host.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(MeasurementWidthDips * scale),
            (int)Math.Round(MeasurementHeightDips * scale)));
        await WaitForCompositionFramesAsync(4);
    }

    private async Task MeasureFirstUsableViewportsAsync()
    {
        var scenarios = new List<FirstViewportScenario>(
            PerformanceMeasurementContract.FirstViewportConditionCount);
        foreach (int sourceBytes in FirstViewportSourceSizes)
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

        using PinnedMeasurementThreadScope measurementThread =
            PinnedMeasurementThreadScope.Enter(ThreadPriority.Normal, "first-viewport");
        int globalOrdinal = 0;
        int requiredWarmupTrials = _report.SampleRequirements.FirstViewportWarmupTrialsRequired;
        for (int row = 0; row < requiredWarmupTrials; row++)
        {
            for (int position = 0;
                 position < PerformanceMeasurementContract.FirstViewportConditionCount;
                 position++)
            {
                int condition = PerformanceMeasurementContract.GetFirstViewportCondition(row, position);
                FirstViewportScenario scenario = scenarios[condition];
                scenario.WarmupTrials.Add(await RunFirstViewportTrialAsync(
                    scenario,
                    row,
                    globalOrdinal++,
                    row,
                    position));
            }
        }

        int requiredTrials = _report.SampleRequirements.FirstViewportTrialsRequired;
        for (int row = 0; row < requiredTrials; row++)
        {
            for (int position = 0;
                 position < PerformanceMeasurementContract.FirstViewportConditionCount;
                 position++)
            {
                int condition = PerformanceMeasurementContract.GetFirstViewportCondition(row, position);
                FirstViewportScenario scenario = scenarios[condition];
                scenario.Trials.Add(await RunFirstViewportTrialAsync(
                    scenario,
                    row,
                    globalOrdinal++,
                    row,
                    position));
            }
        }

        foreach (FirstViewportScenario scenario in scenarios)
        {
            List<double> samples = scenario.Trials
                .SelectMany(static trial => trial.SamplesMilliseconds)
                .ToList();
            double p95 = PerformanceStatistics.Percentile(samples, 0.95);
            double regressionP95 = PerformanceStatistics.HodgesLehmann(
                scenario.Trials.Select(static trial => trial.P95Milliseconds));
            double regressionDispersion = PerformanceStatistics.RobustDispersionPercent(
                scenario.Trials.Select(static trial => trial.P95Milliseconds));
            double stationarityAllowance = Math.Max(
                PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
                    scenario.Mode,
                    scenario.SourceBytes),
                Math.Abs(regressionP95) *
                    PerformanceMeasurementContract.FirstViewportStationarityRelativeAllowancePercent /
                    100d);
            bool stationarityEvaluated = !_options.Quick;
            double theilSenSlope = stationarityEvaluated
                ? PerformanceStatistics.TheilSenSlope(
                    scenario.Trials.Select(static trial =>
                        (trial.GlobalOrdinal, trial.P95Milliseconds)))
                : 0;
            double projectedDrift = stationarityEvaluated
                ? PerformanceStatistics.ProjectedTheilSenDrift(
                    scenario.Trials.Select(static trial =>
                        (trial.GlobalOrdinal, trial.P95Milliseconds)))
                : 0;
            double warmupBoundaryShift = stationarityEvaluated
                ? Math.Abs(
                    PerformanceStatistics.TwoValueCenter(
                        scenario.WarmupTrials[^2].P95Milliseconds,
                        scenario.WarmupTrials[^1].P95Milliseconds) -
                    PerformanceStatistics.TwoValueCenter(
                        scenario.Trials[0].P95Milliseconds,
                        scenario.Trials[1].P95Milliseconds))
                : 0;
            bool residualEnvelopePassed = !stationarityEvaluated ||
                scenario.Trials.All(trial =>
                    Math.Abs(trial.P95Milliseconds - regressionP95) <=
                        stationarityAllowance) &&
                scenario.WarmupTrials.TakeLast(2)
                    .Concat(scenario.Trials.Take(2))
                    .All(trial =>
                        Math.Abs(trial.P95Milliseconds - regressionP95) <=
                            stationarityAllowance);
            bool stationarityPassed = stationarityEvaluated &&
                double.IsFinite(regressionDispersion) &&
                regressionDispersion <=
                    PerformanceMeasurementContract.FirstViewportDispersionBudgetPercent &&
                double.IsFinite(theilSenSlope) &&
                double.IsFinite(projectedDrift) &&
                Math.Abs(projectedDrift) <= stationarityAllowance &&
                double.IsFinite(warmupBoundaryShift) &&
                warmupBoundaryShift <= stationarityAllowance &&
                residualEnvelopePassed;
            double budget = GetFirstViewportBudgetMilliseconds(
                scenario.SourceBytes,
                scenario.Mode);
            bool populationComplete =
                scenario.WarmupTrials.Count == requiredWarmupTrials &&
                scenario.WarmupTrials.All(static trial => trial.Complete) &&
                scenario.Trials.Count == requiredTrials &&
                scenario.Trials.All(static trial => trial.Complete) &&
                samples.Count == checked(_options.FirstViewportIterations * requiredTrials) &&
                samples.All(static value => double.IsFinite(value) && value >= 0) &&
                double.IsFinite(p95) &&
                double.IsFinite(regressionP95);

            _report.FirstUsableViewport.Add(new FirstViewportResult
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
                RegressionP95Milliseconds = regressionP95,
                RegressionDispersionPercent = regressionDispersion,
                TheilSenSlopeMillisecondsPerGlobalOrdinal = theilSenSlope,
                ProjectedDriftMilliseconds = projectedDrift,
                WarmupBoundaryShiftMilliseconds = warmupBoundaryShift,
                StationarityAllowanceMilliseconds = stationarityAllowance,
                StationarityEvaluated = stationarityEvaluated,
                StationarityPassed = stationarityPassed,
                BudgetMilliseconds = budget,
                Passed = populationComplete &&
                         p95 <= budget &&
                         (_options.Quick || stationarityPassed),
            });
        }
    }

    private async Task<FirstViewportTrialResult> RunFirstViewportTrialAsync(
        FirstViewportScenario scenario,
        int ordinal,
        int globalOrdinal,
        int scheduleRow,
        int schedulePosition)
    {
        using MarkdownEngine engine = CreateEngine(
            scenario.SourceBytes,
            scenario.ParseCacheBudgetBytes);
        DateTimeOffset startedUtc = _utcClock.GetUtcNow();
        int completedBeforePrime = engine.CompletedParseCount;
        long hashesBeforePrime = engine.SourceKeyHashCount;
        if (scenario.Mode == PerformanceMeasurementContract.FirstViewportCacheHitMode)
        {
            _ = await engine.ParseAsync(scenario.Source);
            // Verify the exact same source identity resolves through the
            // completed-cache fast path before any measurement boundary.
            _ = await engine.ParseAsync(scenario.Source);
        }

        int completedAfterPrime = engine.CompletedParseCount;
        long hashesAfterPrime = engine.SourceKeyHashCount;
        await ForceManagedCollectionAsync();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        FirstViewportEvent settling = await PresentFirstViewportAsync(
            engine,
            scenario.Source,
            scenario.SourceBytes);
        var samples = new List<double>(_options.FirstViewportIterations);
        for (int iteration = 0; iteration < _options.FirstViewportIterations; iteration++)
        {
            FirstViewportEvent presented = await PresentFirstViewportAsync(
                engine,
                scenario.Source,
                scenario.SourceBytes);
            samples.Add(presented.ElapsedMilliseconds);
        }

        long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        int gen0Collections = GC.CollectionCount(0) - gen0Before;
        int gen1Collections = GC.CollectionCount(1) - gen1Before;
        int gen2Collections = GC.CollectionCount(2) - gen2Before;
        int completedAfterTrial = engine.CompletedParseCount;
        long hashesAfterTrial = engine.SourceKeyHashCount;
        int presentations = checked(_options.FirstViewportIterations + 1);
        bool cacheProofPassed = scenario.Mode switch
        {
            PerformanceMeasurementContract.FirstViewportCacheDisabledMode =>
                completedBeforePrime == 0 &&
                hashesBeforePrime == 0 &&
                completedAfterPrime == 0 &&
                hashesAfterPrime == 0 &&
                completedAfterTrial == 0 &&
                hashesAfterTrial == presentations,
            PerformanceMeasurementContract.FirstViewportCacheHitMode =>
                completedBeforePrime == 0 &&
                hashesBeforePrime == 0 &&
                completedAfterPrime == 1 &&
                hashesAfterPrime == 1 &&
                completedAfterTrial == 1 &&
                hashesAfterTrial == 1,
            _ => false,
        };
        double trialP95 = PerformanceStatistics.Percentile(samples, 0.95);
        DateTimeOffset completedUtc = _utcClock.GetUtcNow();
        bool complete = samples.Count == _options.FirstViewportIterations &&
            samples.All(static value => double.IsFinite(value) && value >= 0) &&
            double.IsFinite(settling.ElapsedMilliseconds) &&
            settling.ElapsedMilliseconds >= 0 &&
            double.IsFinite(trialP95) &&
            allocatedBytes >= 0 &&
            gen0Collections >= 0 &&
            gen1Collections >= 0 &&
            gen2Collections >= 0 &&
            gen0Collections >= gen1Collections &&
            gen1Collections >= gen2Collections &&
            completedUtc > startedUtc &&
            cacheProofPassed;

        return new FirstViewportTrialResult
        {
            Ordinal = ordinal,
            GlobalOrdinal = globalOrdinal,
            ScheduleRow = scheduleRow,
            SchedulePosition = schedulePosition,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            SettlingElapsedMilliseconds = settling.ElapsedMilliseconds,
            SamplesMilliseconds = samples,
            P95Milliseconds = trialP95,
            Gen0Collections = gen0Collections,
            Gen1Collections = gen1Collections,
            Gen2Collections = gen2Collections,
            ProcessAllocatedBytes = allocatedBytes,
            CompletedParseCountBeforePrime = completedBeforePrime,
            CompletedParseCountAfterPrime = completedAfterPrime,
            CompletedParseCountAfterTrial = completedAfterTrial,
            SourceKeyHashCountBeforePrime = hashesBeforePrime,
            SourceKeyHashCountAfterPrime = hashesAfterPrime,
            SourceKeyHashCountAfterTrial = hashesAfterTrial,
            CacheProofPassed = cacheProofPassed,
            Complete = complete,
        };
    }

    private static double GetFirstViewportBudgetMilliseconds(int sourceBytes, string mode)
        => sourceBytes switch
        {
            100 * 1024 when mode == PerformanceMeasurementContract.FirstViewportCacheHitMode => 75,
            100 * 1024 => 125,
            1024 * 1024 => 250,
            10 * 1024 * 1024 => 750,
            _ => throw new UnreachableException(),
        };

    private async Task<FirstViewportEvent> PresentFirstViewportAsync(
        MarkdownEngine engine,
        string source,
        int sourceBytes)
    {
        await DisposeRendererAsync();
        await WaitForCompositionFramesAsync(2);
        try
        {
            // Construct the control with its actual source. Initializing a blank
            // renderer first would interleave empty-source parses with the cache
            // condition and invalidate both the cache proof and the measurement.
            await WaitForCompositionFramesAsync(1);
            long sequence = _events.CurrentSequence;
            var renderer = new MarkdownScrollView
            {
                Engine = engine,
                Markdown = source,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            renderer.RenderFailed += OnRenderFailed;
            _renderer = renderer;
            _ownedRendererEngine = null;
            _host.Children.Add(renderer);
            FirstViewportEvent presented = await _events.WaitForFirstViewportAsync(
                sequence,
                sourceBytes,
                ViewportTimeout);
            await WaitForCompositionFramesAsync(2);
            return presented;
        }
        finally
        {
            await DisposeRendererAsync();
        }
    }

    private sealed class FirstViewportScenario(
        int condition,
        int sourceBytes,
        string source,
        string mode,
        long parseCacheBudgetBytes)
    {
        internal int Condition { get; } = condition;
        internal int SourceBytes { get; } = sourceBytes;
        internal string Source { get; } = source;
        internal string Mode { get; } = mode;
        internal long ParseCacheBudgetBytes { get; } = parseCacheBudgetBytes;
        internal List<FirstViewportTrialResult> WarmupTrials { get; } = [];
        internal List<FirstViewportTrialResult> Trials { get; } = [];
    }

    private readonly List<double> _lastFrameIntervalsMilliseconds = [];

    private async Task<ScrollResult> MeasureWarmScrollAsync()
    {
        const int sourceBytes = 1024 * 1024;
        string source = PerformanceDocumentFactory.CreateWarmScrollStress(sourceBytes);
        MarkdownEngine engine = CreateEngine(sourceBytes);
        _ = await engine.ParseAsync(source);
        await ReplaceRendererAsync(engine);
        long sequence = _events.CurrentSequence;
        _renderer!.Markdown = source;
        _ = await _events.WaitForFirstViewportAsync(sequence, sourceBytes, ViewportTimeout);
        await WaitForCompositionFramesAsync(20);

        // Exercise the exact path once before collecting the "warm" trace so
        // lazy band layout, copy-button plans, and text resources are already
        // resident. A warm-scroll gate must not accidentally benchmark first
        // realization or count its background collections.
        var warmupIntervals = new List<double>(_options.ScrollFrames);
        await RunScrollFramesAsync(0, _options.ScrollFrames, warmupIntervals);
        _renderer.SetAutomationVerticalScrollPercent(0);
        await WaitForScrollTopAsync("prewarmed scroll reset");
        await WaitUntilAsync(
            () => !_renderer.HasPendingLazyLayoutWork,
            ViewportTimeout,
            "prewarmed lazy-layout work to retire");
        await _renderer.DrainRetiredSnapshotsAsync();
        await WaitForCompositionFramesAsync(8);
        await ForceManagedCollectionAsync();
        await WaitUntilAsync(
            () => !_renderer.HasPendingLazyLayoutWork,
            ViewportTimeout,
            "post-GC lazy-layout work to retire");
        await _renderer.DrainRetiredSnapshotsAsync();

        int totalFrames = checked(_options.ScrollFrames * _options.ScrollTrials);
        var uiWork = new double[totalFrames];
        var allocations = new long[totalFrames];
        var scrollWork = new double[totalFrames];
        var paintWork = new double[totalFrames];
        var scrollAllocations = new long[totalFrames];
        var paintAllocations = new long[totalFrames];
        var lazyLayoutAllocations = new long[totalFrames];
        var adornerAllocations = new long[totalFrames];
        var realizationAllocations = new long[totalFrames];
        var highlightingAllocations = new long[totalFrames];
        var paintSchedulingAllocations = new long[totalFrames];
        var platformSessionAllocations = new long[totalFrames];
        var snapshotPaintAllocations = new long[totalFrames];
        var interactivePaintAllocations = new long[totalFrames];
        var inlinePaintAllocations = new long[totalFrames];
        var tablePaintAllocations = new long[totalFrames];
        var codePaintAllocations = new long[totalFrames];
        var otherPaintAllocations = new long[totalFrames];
        var rendererOwnedAllocations = new long[totalFrames];
        var platformProjectionAllocations = new long[totalFrames];
        var trials = new List<ScrollTrialResult>(_options.ScrollTrials);
        long maximumRealizedCodeActions = 0;
        long maximumOffscreenCodeActions = 0;
        int framesWithRendererWork = 0;
        int gen2Collections = 0;
        _lastFrameIntervalsMilliseconds.Clear();

        for (int trialOrdinal = 0; trialOrdinal < _options.ScrollTrials; trialOrdinal++)
        {
            _renderer.SetAutomationVerticalScrollPercent(0);
            await WaitForScrollTopAsync($"scroll trial {trialOrdinal} reset");
            await WaitUntilAsync(
                () => !_renderer.HasPendingLazyLayoutWork,
                ViewportTimeout,
                $"scroll trial {trialOrdinal} reset lazy-layout work to retire");
            await _renderer.DrainRetiredSnapshotsAsync();
            await WaitForCompositionFramesAsync(8);
            await ForceManagedCollectionAsync();
            await WaitUntilAsync(
                () => !_renderer.HasPendingLazyLayoutWork,
                ViewportTimeout,
                $"scroll trial {trialOrdinal} post-GC lazy-layout work to retire");
            await _renderer.DrainRetiredSnapshotsAsync();
            await WaitForCompositionFramesAsync(4);
            await WaitUntilAsync(
                () => !_renderer.HasPendingLazyLayoutWork,
                ViewportTimeout,
                $"scroll trial {trialOrdinal} final lazy-layout work to retire");
            await _renderer.DrainRetiredSnapshotsAsync();

            long firstFrameId = 10_000L + trialOrdinal * 10_000_000L;
            _events.PrepareFrameCapture(firstFrameId, _options.ScrollFrames);
            int gen2Before = GC.CollectionCount(2);
            var trialIntervals = new List<double>(_options.ScrollFrames);
            var trialIntervalElapsedTicks = new List<long>(_options.ScrollFrames);
            await RunScrollFramesAsync(
                firstFrameId,
                _options.ScrollFrames,
                trialIntervals,
                trialIntervalElapsedTicks);
            int trialGen2Collections = GC.CollectionCount(2) - gen2Before;
            gen2Collections += trialGen2Collections;
            _lastFrameIntervalsMilliseconds.AddRange(trialIntervals);
            IReadOnlyDictionary<long, FrameWork> workByFrame = _events.SnapshotFrames(
                firstFrameId,
                firstFrameId + _options.ScrollFrames - 1);

            int trialFramesWithRendererWork = 0;
            long trialMaximumRealizedCodeActions = 0;
            long trialMaximumOffscreenCodeActions = 0;
            var trialUiThreadWorkElapsedTicks = new long[_options.ScrollFrames];
            int aggregateOffset = trialOrdinal * _options.ScrollFrames;
            for (int index = 0; index < _options.ScrollFrames; index++)
            {
                int aggregateIndex = aggregateOffset + index;
                if (!workByFrame.TryGetValue(firstFrameId + index, out FrameWork? work))
                    continue;

                if (work.ScrollCallbacks > 0 || work.PaintCallbacks > 0)
                {
                    trialFramesWithRendererWork++;
                    framesWithRendererWork++;
                }
                trialUiThreadWorkElapsedTicks[index] = work.UiThreadWorkElapsedTicks;
                uiWork[aggregateIndex] = work.ElapsedMilliseconds;
                allocations[aggregateIndex] = work.AllocatedBytes;
                scrollWork[aggregateIndex] = work.ScrollElapsedMilliseconds;
                paintWork[aggregateIndex] = work.PaintElapsedMilliseconds;
                scrollAllocations[aggregateIndex] = work.ScrollAllocatedBytes;
                paintAllocations[aggregateIndex] = work.PaintAllocatedBytes;
                lazyLayoutAllocations[aggregateIndex] = work.ScrollLazyLayoutAllocatedBytes;
                adornerAllocations[aggregateIndex] = work.ScrollAdornerAllocatedBytes;
                realizationAllocations[aggregateIndex] = work.ScrollRealizationAllocatedBytes;
                highlightingAllocations[aggregateIndex] = work.ScrollHighlightingAllocatedBytes;
                paintSchedulingAllocations[aggregateIndex] = work.PaintSchedulingAllocatedBytes;
                platformSessionAllocations[aggregateIndex] = work.PaintPlatformSessionAllocatedBytes;
                snapshotPaintAllocations[aggregateIndex] = work.SnapshotPaintAllocatedBytes;
                interactivePaintAllocations[aggregateIndex] = work.InteractivePaintAllocatedBytes;
                inlinePaintAllocations[aggregateIndex] = work.InlinePaintAllocatedBytes;
                tablePaintAllocations[aggregateIndex] = work.TablePaintAllocatedBytes;
                codePaintAllocations[aggregateIndex] = work.CodePaintAllocatedBytes;
                otherPaintAllocations[aggregateIndex] = work.OtherPaintAllocatedBytes;
                long platformBytes =
                    work.ScrollAdornerAllocatedBytes +
                    work.ScrollRealizationAllocatedBytes +
                    work.PaintPlatformSessionAllocatedBytes +
                    work.SnapshotPaintAllocatedBytes +
                    work.InteractivePaintAllocatedBytes;
                platformProjectionAllocations[aggregateIndex] = Math.Max(0, platformBytes);
                rendererOwnedAllocations[aggregateIndex] = Math.Max(0, work.AllocatedBytes - platformBytes);
                trialMaximumRealizedCodeActions = Math.Max(
                    trialMaximumRealizedCodeActions,
                    work.MaximumRealizedCodeActions);
                trialMaximumOffscreenCodeActions = Math.Max(
                    trialMaximumOffscreenCodeActions,
                    work.MaximumOffscreenCodeActions);
            }

            maximumRealizedCodeActions = Math.Max(
                maximumRealizedCodeActions,
                trialMaximumRealizedCodeActions);
            maximumOffscreenCodeActions = Math.Max(
                maximumOffscreenCodeActions,
                trialMaximumOffscreenCodeActions);
            IEnumerable<double> TrialDoubles(double[] values) =>
                values.Skip(aggregateOffset).Take(_options.ScrollFrames);
            IEnumerable<long> TrialLongs(long[] values) =>
                values.Skip(aggregateOffset).Take(_options.ScrollFrames);
            double trialFramesOver16_7Percent = trialIntervals.Count == 0
                ? double.NaN
                : trialIntervals.Count(value => value > 16.7) * 100.0 / trialIntervals.Count;
            trials.Add(new ScrollTrialResult
            {
                Ordinal = trialOrdinal,
                FirstLogicalFrameId = firstFrameId,
                RequestedFrames = _options.ScrollFrames,
                MeasuredFrameIntervals = trialIntervals.Count,
                FramesWithRendererWork = trialFramesWithRendererWork,
                UiThreadWorkElapsedTicks = trialUiThreadWorkElapsedTicks.ToList(),
                FrameIntervalElapsedTicks = trialIntervalElapsedTicks,
                RendererOwnedAllocatedBytesPerFrame =
                    TrialLongs(rendererOwnedAllocations).ToList(),
                UiThreadWorkP95Milliseconds = PerformanceStatistics.Percentile(TrialDoubles(uiWork), 0.95),
                UiThreadWorkP99Milliseconds = PerformanceStatistics.Percentile(TrialDoubles(uiWork), 0.99),
                ScrollCallbackWorkP95Milliseconds = PerformanceStatistics.Percentile(TrialDoubles(scrollWork), 0.95),
                PaintCallbackWorkP95Milliseconds = PerformanceStatistics.Percentile(TrialDoubles(paintWork), 0.95),
                FrameTimeP95Milliseconds = PerformanceStatistics.Percentile(trialIntervals, 0.95),
                FramesOver16_7Percent = trialFramesOver16_7Percent,
                MaximumFrameStallMilliseconds = trialIntervals.DefaultIfEmpty(double.NaN).Max(),
                AllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(TrialLongs(allocations), 0.95),
                MaximumAllocatedBytesPerFrame = TrialLongs(allocations).DefaultIfEmpty(long.MaxValue).Max(),
                RendererOwnedAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(TrialLongs(rendererOwnedAllocations), 0.95),
                MaximumRendererOwnedAllocatedBytesPerFrame = TrialLongs(rendererOwnedAllocations).DefaultIfEmpty(long.MaxValue).Max(),
                PlatformProjectionAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(TrialLongs(platformProjectionAllocations), 0.95),
                MaximumPlatformProjectionAllocatedBytesPerFrame = TrialLongs(platformProjectionAllocations).DefaultIfEmpty(long.MaxValue).Max(),
                ScrollAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(TrialLongs(scrollAllocations), 0.95),
                PaintAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(TrialLongs(paintAllocations), 0.95),
                ScrollLazyLayoutAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(lazyLayoutAllocations), 0.95),
                ScrollAdornerAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(adornerAllocations), 0.95),
                ScrollRealizationAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(realizationAllocations), 0.95),
                ScrollHighlightingAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(highlightingAllocations), 0.95),
                PaintSchedulingAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(paintSchedulingAllocations), 0.95),
                PaintPlatformSessionAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(platformSessionAllocations), 0.95),
                SnapshotPaintAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(snapshotPaintAllocations), 0.95),
                InteractivePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(interactivePaintAllocations), 0.95),
                InlinePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(inlinePaintAllocations), 0.95),
                TablePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(tablePaintAllocations), 0.95),
                CodePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(codePaintAllocations), 0.95),
                OtherPaintAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(otherPaintAllocations), 0.95),
                MaximumRealizedCodeActions = trialMaximumRealizedCodeActions,
                MaximumOffscreenRealizedCodeActions = trialMaximumOffscreenCodeActions,
                Gen2Collections = trialGen2Collections,
                Complete = trialIntervals.Count == _options.ScrollFrames &&
                           trialIntervalElapsedTicks.Count == _options.ScrollFrames &&
                           trialFramesWithRendererWork == _options.ScrollFrames,
            });
        }

        double uiP95 = PerformanceStatistics.Percentile(uiWork, 0.95);
        double uiP99 = PerformanceStatistics.Percentile(uiWork, 0.99);
        double frameP95 = PerformanceStatistics.Percentile(_lastFrameIntervalsMilliseconds, 0.95);
        double over16_7Percent = _lastFrameIntervalsMilliseconds.Count == 0
            ? double.NaN
            : _lastFrameIntervalsMilliseconds.Count(value => value > 16.7) * 100.0 /
              _lastFrameIntervalsMilliseconds.Count;
        double maximumFrame = _lastFrameIntervalsMilliseconds.DefaultIfEmpty(double.NaN).Max();
        double allocationP95 = PerformanceStatistics.Percentile(allocations, 0.95);
        long maximumAllocation = allocations.DefaultIfEmpty(long.MaxValue).Max();
        bool lohPossible = maximumAllocation >= 85_000;
        double rendererOwnedAllocationP95 = PerformanceStatistics.Percentile(rendererOwnedAllocations, 0.95);
        long maximumRendererOwnedAllocation = rendererOwnedAllocations.DefaultIfEmpty(long.MaxValue).Max();
        double platformProjectionAllocationP95 = PerformanceStatistics.Percentile(platformProjectionAllocations, 0.95);
        long maximumPlatformProjectionAllocation = platformProjectionAllocations.DefaultIfEmpty(long.MaxValue).Max();
        bool rendererOwnedLohPossible = maximumRendererOwnedAllocation >= 85_000;
        bool sufficient =
            _options.ScrollFrames == _report.SampleRequirements.ScrollFramesRequired &&
            _options.ScrollTrials == _report.SampleRequirements.ScrollTrialsRequired &&
            trials.Count == _report.SampleRequirements.ScrollTrialsRequired &&
            trials.Select(static trial => trial.Ordinal).SequenceEqual(
                Enumerable.Range(0, _report.SampleRequirements.ScrollTrialsRequired)) &&
            trials.All(static trial => trial.Complete) &&
            _lastFrameIntervalsMilliseconds.Count == totalFrames &&
            framesWithRendererWork == totalFrames;

        double regressionUiP95 = PerformanceStatistics.HodgesLehmann(
            trials.Select(static trial => trial.UiThreadWorkP95Milliseconds));
        double regressionUiP99 = PerformanceStatistics.HodgesLehmann(
            trials.Select(static trial => trial.UiThreadWorkP99Milliseconds));
        double regressionFrameP95 = PerformanceStatistics.HodgesLehmann(
            trials.Select(static trial => trial.FrameTimeP95Milliseconds));
        double regressionRendererAllocationP95 = PerformanceStatistics.HodgesLehmann(
            trials.Select(static trial => trial.RendererOwnedAllocatedBytesPerFrameP95));
        bool regressionEvidenceStable =
            PerformanceStatistics.RobustDispersionPercent(
                trials.Select(static trial => trial.UiThreadWorkP95Milliseconds)) <=
                PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent &&
            PerformanceStatistics.RobustDispersionPercent(
                trials.Select(static trial => trial.UiThreadWorkP99Milliseconds)) <=
                PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent &&
            PerformanceStatistics.RobustDispersionPercent(
                trials.Select(static trial => trial.FrameTimeP95Milliseconds)) <=
                PerformanceMeasurementContract.ScrollFrameTimeDispersionBudgetPercent &&
            PerformanceStatistics.RobustDispersionPercent(
                trials.Select(static trial => trial.RendererOwnedAllocatedBytesPerFrameP95)) <=
                PerformanceMeasurementContract.AllocationDispersionBudgetPercent;

        return new ScrollResult
        {
            Corpus = PerformanceDocumentFactory.WarmScrollStressCorpus,
            SourceUtf16Bytes = sourceBytes,
            StopwatchFrequency = Stopwatch.Frequency,
            RequestedFrames = totalFrames,
            MeasuredFrameIntervals = _lastFrameIntervalsMilliseconds.Count,
            FramesWithRendererWork = framesWithRendererWork,
            RegressionEstimator = _options.Quick
                ? "quick-single-trial-non-gating"
                : PerformanceMeasurementContract.ScrollRegressionEstimator,
            Trials = trials,
            RegressionUiThreadWorkP95Milliseconds = regressionUiP95,
            RegressionUiThreadWorkP99Milliseconds = regressionUiP99,
            RegressionFrameTimeP95Milliseconds = regressionFrameP95,
            RegressionRendererOwnedAllocatedBytesPerFrameP95 = regressionRendererAllocationP95,
            UiThreadWorkP95Milliseconds = uiP95,
            UiThreadWorkP99Milliseconds = uiP99,
            ScrollCallbackWorkP95Milliseconds = PerformanceStatistics.Percentile(scrollWork, 0.95),
            PaintCallbackWorkP95Milliseconds = PerformanceStatistics.Percentile(paintWork, 0.95),
            FrameTimeP95Milliseconds = frameP95,
            FramesOver16_7Percent = over16_7Percent,
            MaximumFrameStallMilliseconds = maximumFrame,
            AllocatedBytesPerFrameP95 = allocationP95,
            MaximumAllocatedBytesPerFrame = maximumAllocation,
            RendererOwnedAllocatedBytesPerFrameP95 = rendererOwnedAllocationP95,
            MaximumRendererOwnedAllocatedBytesPerFrame = maximumRendererOwnedAllocation,
            PlatformProjectionAllocatedBytesPerFrameP95 = platformProjectionAllocationP95,
            MaximumPlatformProjectionAllocatedBytesPerFrame = maximumPlatformProjectionAllocation,
            ScrollAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(scrollAllocations, 0.95),
            PaintAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(paintAllocations, 0.95),
            ScrollLazyLayoutAllocatedBytesP95 = PerformanceStatistics.Percentile(lazyLayoutAllocations, 0.95),
            ScrollAdornerAllocatedBytesP95 = PerformanceStatistics.Percentile(adornerAllocations, 0.95),
            ScrollRealizationAllocatedBytesP95 = PerformanceStatistics.Percentile(realizationAllocations, 0.95),
            ScrollHighlightingAllocatedBytesP95 = PerformanceStatistics.Percentile(highlightingAllocations, 0.95),
            PaintSchedulingAllocatedBytesP95 = PerformanceStatistics.Percentile(paintSchedulingAllocations, 0.95),
            PaintPlatformSessionAllocatedBytesP95 = PerformanceStatistics.Percentile(platformSessionAllocations, 0.95),
            SnapshotPaintAllocatedBytesP95 = PerformanceStatistics.Percentile(snapshotPaintAllocations, 0.95),
            InteractivePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(interactivePaintAllocations, 0.95),
            InlinePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(inlinePaintAllocations, 0.95),
            TablePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(tablePaintAllocations, 0.95),
            CodePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(codePaintAllocations, 0.95),
            OtherPaintAllocatedBytesP95 = PerformanceStatistics.Percentile(otherPaintAllocations, 0.95),
            LohAllocationPossible = lohPossible,
            RendererOwnedLohAllocationPossible = rendererOwnedLohPossible,
            MaximumRealizedCodeActions = maximumRealizedCodeActions,
            MaximumOffscreenRealizedCodeActions = maximumOffscreenCodeActions,
            Gen2Collections = gen2Collections,
            Passed = sufficient &&
                     regressionEvidenceStable &&
                     uiP95 <= 4 &&
                     uiP99 <= 6 &&
                     frameP95 <=
                         PerformanceMeasurementContract.ScrollFrameTimeP95BudgetMilliseconds &&
                     over16_7Percent < 1 &&
                     maximumFrame <= 50 &&
                     maximumRendererOwnedAllocation <= 4096 &&
                     !rendererOwnedLohPossible &&
                     maximumOffscreenCodeActions == 0 &&
                     gen2Collections == 0,
        };
    }

    private async Task MeasureRetainedMemoryAsync()
    {
        foreach (int sourceBytes in FirstViewportSourceSizes)
        {
            await ReplaceRendererAsync(CreateEngine(sourceBytes));
            await ForceCollectionAsync();
            (long managedBefore, long privateBefore) = CaptureMemory();
            // Allocate the caller's exact source after the baseline so the
            // renderer-retained total includes the UTF-16 input itself.
            string source = PerformanceDocumentFactory.Create(sourceBytes, sourceBytes + 1);

            long sequence = _events.CurrentSequence;
            _renderer!.Markdown = source;
            _ = await _events.WaitForFirstViewportAsync(sequence, sourceBytes, ViewportTimeout);
            await WaitForCompositionFramesAsync(5);
            await ForceCollectionAsync();
            (long managedAfter, long privateAfter) = CaptureMemory();

            long managedDelta = Math.Max(0, managedAfter - managedBefore);
            long privateDelta = Math.Max(0, privateAfter - privateBefore);
            long budget = checked(sourceBytes * 8L + 32L * 1024 * 1024);
            _report.RetainedMemory.Add(new RetainedMemoryResult
            {
                Corpus = PerformanceDocumentFactory.GetCorpusName(sourceBytes),
                SourceUtf16Bytes = sourceBytes,
                ManagedDeltaBytes = managedDelta,
                PrivateDeltaBytes = privateDelta,
                BudgetBytes = budget,
                Passed = managedDelta <= budget && privateDelta <= budget,
            });
            await DisposeRendererAsync();
            await ForceCollectionAsync();
        }
    }

    private async Task<LifecyclePlateauResult> MeasureLifecyclePlateauAsync()
    {
        const int sourceBytes = 100 * 1024;
        string first = PerformanceDocumentFactory.Create(sourceBytes, 1001);
        string second = PerformanceDocumentFactory.Create(sourceBytes, 1002);
        await ReplaceRendererAsync(CreateEngine(sourceBytes));
        long initialSequence = _events.CurrentSequence;
        _renderer!.Markdown = first;
        _ = await _events.WaitForFirstViewportAsync(initialSequence, sourceBytes, ViewportTimeout);

        int checkpointCycle = Math.Max(1, (int)Math.Ceiling(_options.LifecycleCycles * 0.8));
        long checkpointManaged = 0;
        long checkpointPrivate = 0;
        int resets = 0;
        int failuresBefore = _renderFailures;

        for (int cycle = 1; cycle <= _options.LifecycleCycles; cycle++)
        {
            long sequence = _events.CurrentSequence;
            _renderer.Markdown = (cycle & 1) == 0 ? first : second;
            _renderer.Width = (cycle & 1) == 0 ? 980 : 940;
            _renderer.RequestedTheme = (cycle & 1) == 0 ? ElementTheme.Light : ElementTheme.Dark;

            if (cycle % 25 == 0)
            {
                // Release every snapshot-owned Win2D/DirectWrite object, trim
                // the shared device, and rebuild. Win2D exposes no supported
                // synthetic DXGI-loss trigger; physically induced device-loss
                // evidence remains a separate hardware-lab requirement.
                _renderer.PrepareForSyntheticDeviceReset();
                await _renderer.DrainRetiredSnapshotsAsync();
                CanvasDevice.GetSharedDevice().Trim();
                resets++;
                _renderer.RequestRebuild();
            }

            _ = await _events.WaitForFirstViewportAsync(sequence, sourceBytes, ViewportTimeout);
            if (cycle == checkpointCycle || cycle == _options.LifecycleCycles)
            {
                await WaitForCompositionFramesAsync(5);
                await ForceCollectionAsync();
                (long managed, long privateBytes) = CaptureMemory();
                if (cycle == checkpointCycle)
                {
                    checkpointManaged = managed;
                    checkpointPrivate = privateBytes;
                }
                if (cycle == _options.LifecycleCycles)
                {
                    double managedGrowth = PerformanceStatistics.GrowthPercent(managed, checkpointManaged);
                    double privateGrowth = PerformanceStatistics.GrowthPercent(privateBytes, checkpointPrivate);
                    double growth = Math.Max(managedGrowth, privateGrowth);
                    return new LifecyclePlateauResult
                    {
                        CompletedCycles = cycle,
                        RequiredCycles = _options.LifecycleCycles,
                        SyntheticDeviceResets = resets,
                        Checkpoint80ManagedBytes = checkpointManaged,
                        Checkpoint100ManagedBytes = managed,
                        Checkpoint80PrivateBytes = checkpointPrivate,
                        Checkpoint100PrivateBytes = privateBytes,
                        ManagedGrowthPercent = managedGrowth,
                        PrivateGrowthPercent = privateGrowth,
                        GrowthPercent = growth,
                        RenderFailures = _renderFailures - failuresBefore,
                        Passed = cycle >= _report.SampleRequirements.LifecycleCyclesRequired &&
                                 resets >= 4 &&
                                 growth <= 5 &&
                                 _renderFailures == failuresBefore,
                    };
                }
            }
        }

        throw new InvalidOperationException("Lifecycle plateau loop did not produce a final checkpoint.");
    }

    private async Task<CancellationResult> MeasureCancellationAsync()
    {
        const int supersededBytes = 10 * 1024 * 1024;
        int requiredTrials = _report.SampleRequirements.CancellationTrialsRequired;
        int totalRequestedIterations = checked(
            _options.CancellationIterations * requiredTrials);
        var samples = new List<double>(totalRequestedIterations);
        var trials = new List<CancellationTrialResult>(requiredTrials);
        var warmupSamples = new List<double>(requiredTrials);
        int warmupIterations = 0;
        int warmupStaleCommits = 0;
        int staleCommits = 0;

        for (int trialOrdinal = 0; trialOrdinal < requiredTrials; trialOrdinal++)
        {
            await ForceManagedCollectionAsync();
            IReadOnlyList<CancellationMeasurementIteration> plan =
                CancellationMeasurementPlan.CreateTrial(
                    trialOrdinal,
                    _options.CancellationIterations);
            var trialSamples = new List<double>(_options.CancellationIterations);
            double trialWarmupElapsedMilliseconds = double.NaN;
            int trialWarmupStaleCommits = 0;
            int trialStaleCommits = 0;

            foreach (CancellationMeasurementIteration iteration in plan)
            {
                (double elapsedMilliseconds, int iterationStaleCommits) =
                    await RunCancellationIterationAsync(supersededBytes, iteration);
                if (!iteration.IsRecorded)
                {
                    warmupIterations++;
                    trialWarmupElapsedMilliseconds = elapsedMilliseconds;
                    trialWarmupStaleCommits += iterationStaleCommits;
                    warmupSamples.Add(elapsedMilliseconds);
                    warmupStaleCommits += iterationStaleCommits;
                    continue;
                }

                trialSamples.Add(elapsedMilliseconds);
                samples.Add(elapsedMilliseconds);
                trialStaleCommits += iterationStaleCommits;
                staleCommits += iterationStaleCommits;
            }

            double trialP95 = PerformanceStatistics.Percentile(trialSamples, 0.95);
            trials.Add(new CancellationTrialResult
            {
                Ordinal = trialOrdinal,
                WarmupSourceSeed = plan[0].SourceSeed,
                WarmupElapsedMilliseconds = trialWarmupElapsedMilliseconds,
                WarmupStaleCommits = trialWarmupStaleCommits,
                FirstRecordedSourceSeed = plan[1].SourceSeed,
                RequestedIterations = _options.CancellationIterations,
                ObservedCancellations = trialSamples.Count,
                SamplesMilliseconds = trialSamples,
                P95Milliseconds = trialP95,
                StaleCommits = trialStaleCommits,
                Complete = double.IsFinite(trialWarmupElapsedMilliseconds) &&
                           trialWarmupElapsedMilliseconds >= 0 &&
                           trialWarmupStaleCommits >= 0 &&
                           trialSamples.Count == _options.CancellationIterations &&
                           trialSamples.All(
                                static value => double.IsFinite(value) && value >= 0) &&
                           double.IsFinite(trialP95) &&
                           trialP95 >= 0 &&
                           trialStaleCommits >= 0,
            });
        }

        double p95 = PerformanceStatistics.Percentile(samples, 0.95);
        double regressionP95 = PerformanceStatistics.HodgesLehmann(
            trials.Select(static trial => trial.P95Milliseconds));
        double regressionDispersion = PerformanceStatistics.RobustDispersionPercent(
            trials.Select(static trial => trial.P95Milliseconds));
        double warmupP95 = PerformanceStatistics.Percentile(warmupSamples, 0.95);
        return new CancellationResult
        {
            RequestedIterations = totalRequestedIterations,
            IterationsPerTrial = _options.CancellationIterations,
            WarmupIterations = warmupIterations,
            WarmupElapsedMilliseconds = warmupP95,
            WarmupStaleCommits = warmupStaleCommits,
            ObservedCancellations = samples.Count,
            TrialStartPolicy = PerformanceMeasurementContract.CancellationTrialStartPolicy,
            RegressionEstimator = _options.Quick
                ? "quick-single-trial-non-gating"
                : PerformanceMeasurementContract.CancellationRegressionEstimator,
            Trials = trials,
            SamplesMilliseconds = samples,
            P95Milliseconds = p95,
            RegressionP95Milliseconds = regressionP95,
            StaleCommits = staleCommits,
            Passed = warmupIterations == requiredTrials &&
                     warmupSamples.Count == requiredTrials &&
                      warmupStaleCommits == 0 &&
                      trials.Count == requiredTrials &&
                      trials.All(static trial =>
                          trial.Complete &&
                          CancellationMeasurementPlan.IsWarmupEvidencePassing(
                              CancellationMeasurementPlan.WarmupIterationsPerTrial,
                              trial.WarmupElapsedMilliseconds,
                              trial.WarmupStaleCommits) &&
                          trial.P95Milliseconds <=
                              CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
                          trial.StaleCommits == 0) &&
                     samples.Count == totalRequestedIterations &&
                     p95 <= CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
                     double.IsFinite(regressionP95) &&
                     double.IsFinite(regressionDispersion) &&
                     regressionDispersion <=
                         PerformanceMeasurementContract.CancellationDispersionBudgetPercent &&
                     staleCommits == 0,
        };
    }

    private async Task<(double ElapsedMilliseconds, int StaleCommits)>
        RunCancellationIterationAsync(
            int supersededBytes,
            CancellationMeasurementIteration iteration)
    {
        MarkdownEngine engine = CreateEngine(supersededBytes);
        await ReplaceRendererAsync(engine);
        string superseded = PerformanceDocumentFactory.Create(
            supersededBytes,
            iteration.SourceSeed);
        long sequence = _events.CurrentSequence;
        _renderer!.Markdown = superseded;
        PipelineEvent oldPipeline = await _events.WaitForPipelineAsync(
            sequence,
            supersededBytes,
            ViewportTimeout);

        string winner = $"# cancellation winner {iteration.WinnerOrdinal}\n";
        long winnerBytes = (long)winner.Length * sizeof(char);
        long winnerStartSequence = _events.CurrentSequence;
        _renderer.Markdown = winner;
        SupersessionEvent supersession = await _events.WaitForSupersessionAsync(
            winnerStartSequence,
            oldPipeline.Generation,
            ViewportTimeout);
        _ = await _events.WaitForPipelineAsync(
            winnerStartSequence,
            winnerBytes,
            ViewportTimeout);
        CancellationEvent cancellation = await _events.WaitForCancellationAsync(
            supersession.Sequence,
            oldPipeline.Generation,
            ViewportTimeout);
        _ = await _events.WaitForFirstViewportAsync(
            winnerStartSequence,
            winnerBytes,
            ViewportTimeout);

        await WaitUntilAsync(
            () => engine.ActiveParseCount == 0,
            ViewportTimeout,
            "abandoned parser worker to retire");
        await WaitForCompositionFramesAsync(2);
        int staleCommits = _events.FirstViewports.Count(item =>
            item.Generation == oldPipeline.Generation &&
            item.Sequence > winnerStartSequence);
        await DisposeRendererAsync();
        return (cancellation.ElapsedMilliseconds, staleCommits);
    }

    private static SourceLookupResult MeasureSourceLookup()
    {
        const int mappingCount = PerformanceMeasurementContract.SourceLookupMappingCount;
        const int queryCount = PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount;
        const int regressionWarmupPasses =
            PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses;
        const int regressionTrialCount =
            PerformanceMeasurementContract.SourceLookupRegressionTrialCount;
        const int regressionBatchSize =
            PerformanceMeasurementContract.SourceLookupRegressionBatchSize;
        const int regressionObservationCount =
            PerformanceMeasurementContract.SourceLookupRegressionObservationCount;
        const int regressionOperationCount =
            PerformanceMeasurementContract.SourceLookupRegressionOperationCount;
        var map = new MarkdownSourceMap(new string('x', mappingCount * 4));
        var queries = new DocumentRange[regressionOperationCount];
        for (int index = 0; index < mappingCount; index++)
            map.Add(index + 1, 0, 3, new SourceSpan(index * 4, 3));

        var random = new Random(0x4D_44);
        for (int index = 0; index < queries.Length; index++)
        {
            int block = random.Next(1, mappingCount + 1);
            queries[index] = new DocumentRange(
                new DocumentPosition(block, 0, 0),
                new DocumentPosition(block, 0, 3));
        }

        using PinnedMeasurementThreadScope measurementScope =
            PinnedMeasurementThreadScope.Enter(
                ThreadPriority.Highest,
                "source-lookup");
        int checksum = 0;
        for (int pass = 0; pass < regressionWarmupPasses; pass++)
        {
            for (int index = 0; index < queries.Length; index++)
            {
                if (map.TryMapRange(queries[index], out SourceSpan span))
                    checksum ^= span.Start;
            }
        }

        var elapsed = new long[queryCount];
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < queryCount; index++)
        {
            long started = Stopwatch.GetTimestamp();
            if (map.TryMapRange(queries[index], out SourceSpan span))
                checksum ^= span.Start;
            elapsed[index] = Stopwatch.GetTimestamp() - started;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        double nanosecondsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
        var regressionTrials = new List<SourceLookupTrialResult>(regressionTrialCount);
        long regressionAllocated = 0;
        for (int trialOrdinal = 0; trialOrdinal < regressionTrialCount; trialOrdinal++)
        {
            var trialElapsed = new long[regressionObservationCount];
            long trialAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int observation = 0; observation < regressionObservationCount; observation++)
            {
                int first = observation * regressionBatchSize;
                long started = Stopwatch.GetTimestamp();
                for (int offset = 0; offset < regressionBatchSize; offset++)
                {
                    if (map.TryMapRange(queries[first + offset], out SourceSpan span))
                        checksum ^= span.Start;
                }
                trialElapsed[observation] = Stopwatch.GetTimestamp() - started;
            }
            long trialAllocated =
                GC.GetAllocatedBytesForCurrentThread() - trialAllocatedBefore;
            regressionAllocated = checked(regressionAllocated + trialAllocated);
            double[] trialNanosecondsPerLookup = trialElapsed
                .Select(value => value * nanosecondsPerTick / regressionBatchSize)
                .ToArray();
            double trialP95 = PerformanceStatistics.Percentile(
                trialNanosecondsPerLookup,
                0.95);
            regressionTrials.Add(new SourceLookupTrialResult
            {
                Ordinal = trialOrdinal,
                ObservationCount = regressionObservationCount,
                OperationCount = regressionOperationCount,
                SamplesElapsedTicks = trialElapsed.ToList(),
                SamplesNanosecondsPerLookup = trialNanosecondsPerLookup.ToList(),
                P95Nanoseconds = trialP95,
                AllocatedBytes = trialAllocated,
                Complete = trialElapsed.All(static value => value > 0) &&
                           trialNanosecondsPerLookup.All(
                               static value => double.IsFinite(value) && value > 0) &&
                           trialAllocated == 0,
            });
        }
        GC.KeepAlive(checksum);

        double p95Nanoseconds =
            PerformanceStatistics.Percentile(elapsed, 0.95) * nanosecondsPerTick;
        double regressionP95Nanoseconds = PerformanceStatistics.HodgesLehmann(
            regressionTrials.Select(static trial => trial.P95Nanoseconds));
        double regressionDispersion = PerformanceStatistics.RobustDispersionPercent(
            regressionTrials.Select(static trial => trial.P95Nanoseconds));
        return new SourceLookupResult
        {
            MappingCount = mappingCount,
            QueryCount = queryCount,
            StopwatchFrequency = Stopwatch.Frequency,
            MeasurementThreadAffinityMask = measurementScope.AffinityMask,
            MeasurementProcessorGroup = measurementScope.ProcessorGroup,
            MeasurementProcessorNumber = measurementScope.ProcessorNumber,
            MeasurementThreadPriority = measurementScope.ThreadPriority,
            SamplesElapsedTicks = elapsed.ToList(),
            P95Nanoseconds = p95Nanoseconds,
            AllocatedBytes = allocated,
            RegressionWarmupPasses = regressionWarmupPasses,
            RegressionBatchSize = regressionBatchSize,
            RegressionObservationCount = regressionObservationCount,
            RegressionOperationCount = regressionOperationCount,
            RegressionEstimator = PerformanceMeasurementContract.SourceLookupRegressionEstimator,
            RegressionTrialCount = regressionTrialCount,
            RegressionTrials = regressionTrials,
            RegressionP95Nanoseconds = regressionP95Nanoseconds,
            RegressionAllocatedBytes = regressionAllocated,
            Passed = p95Nanoseconds <= 25_000 &&
                     allocated == 0 &&
                     regressionTrials.Count == regressionTrialCount &&
                     regressionTrials.All(static trial => trial.Complete) &&
                     double.IsFinite(regressionP95Nanoseconds) &&
                     regressionP95Nanoseconds > 0 &&
                     double.IsFinite(regressionDispersion) &&
                     regressionDispersion <=
                         PerformanceMeasurementContract.SourceLookupDispersionBudgetPercent &&
                     regressionAllocated == 0,
        };
    }

    private async Task ReplaceRendererAsync(
        MarkdownEngine engine,
        bool takeEngineOwnership = true)
    {
        await DisposeRendererAsync();
        await WaitForCompositionFramesAsync(2);

        long sequence = _events.CurrentSequence;
        var renderer = new MarkdownScrollView
        {
            Engine = engine,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        renderer.RenderFailed += OnRenderFailed;
        _renderer = renderer;
        _ownedRendererEngine = takeEngineOwnership ? engine : null;
        _host.Children.Add(renderer);
        _ = await _events.WaitForFirstViewportAsync(sequence, 0, ViewportTimeout);
    }

    private async Task DisposeRendererAsync()
    {
        MarkdownEngine? ownedEngine = _ownedRendererEngine;
        _ownedRendererEngine = null;
        if (_renderer is null)
        {
            ownedEngine?.Dispose();
            return;
        }

        MarkdownScrollView renderer = _renderer;
        renderer.RenderFailed -= OnRenderFailed;
        _host.Children.Remove(renderer);
        renderer.Dispose();
        _renderer = null;
        try
        {
            await renderer.DrainRetiredSnapshotsAsync();
        }
        finally
        {
            ownedEngine?.Dispose();
        }
    }

    private void OnRenderFailed(object? sender, MarkdownRenderFailedEventArgs e)
        => _renderFailures++;

    private async Task RunScrollFramesAsync(
        long firstFrameId,
        int frameCount,
        ICollection<double> intervalsMilliseconds,
        ICollection<long>? intervalElapsedTicks = null)
    {
        if (_renderer is null)
            throw new InvalidOperationException("Scroll trace requires a renderer.");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long previousTimestamp = 0;
        int frame = 0;
        double position = 0;
        double direction = 1;
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            if (previousTimestamp != 0)
            {
                long elapsedTicks = now - previousTimestamp;
                intervalElapsedTicks?.Add(elapsedTicks);
                intervalsMilliseconds.Add(elapsedTicks * 1000.0 / Stopwatch.Frequency);
            }

            if (frame >= frameCount)
            {
                CompositionTarget.Rendering -= handler;
                MarkdownPerformanceEventSource.SetLogicalFrameId(0);
                completion.TrySetResult();
                return;
            }

            previousTimestamp = now;
            MarkdownPerformanceEventSource.SetLogicalFrameId(firstFrameId + frame);
            if (!_renderer.TryGetAutomationScrollMetrics(out _, out double viewportHeight, out double extentHeight))
            {
                CompositionTarget.Rendering -= handler;
                completion.TrySetException(new InvalidOperationException("Renderer did not expose scroll metrics."));
                return;
            }

            double maximum = Math.Max(1, extentHeight - viewportHeight);
            position += direction * 96;
            if (position >= maximum)
            {
                position = maximum;
                direction = -1;
            }
            else if (position <= 0)
            {
                position = 0;
                direction = 1;
            }
            _renderer.SetAutomationVerticalScrollPercent(position * 100.0 / maximum);
            frame++;
        };

        CompositionTarget.Rendering += handler;
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
        await WaitForCompositionFramesAsync(4);
    }

    private Task WaitForScrollTopAsync(string operation)
        => WaitUntilAsync(
            () => _renderer is not null &&
                  _renderer.TryGetAutomationScrollMetrics(
                      out double verticalOffset,
                      out _,
                      out _) &&
                  double.IsFinite(verticalOffset) &&
                  Math.Abs(verticalOffset) <= ScrollResetToleranceDips,
            ViewportTimeout,
            $"{operation} to reach offset zero");

    private static async Task WaitForCompositionFramesAsync(int count)
    {
        for (int index = 0; index < count; index++)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<object>? handler = null;
            handler = (_, _) =>
            {
                CompositionTarget.Rendering -= handler;
                completion.TrySetResult();
            };
            CompositionTarget.Rendering += handler;
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) >= timeout)
                throw new TimeoutException($"Timed out waiting for {description}.");
            await Task.Delay(10);
        }
    }

    private static MarkdownEngine CreateEngine(
        int sourceBytes,
        long? parseCacheBudgetBytes = null)
        => new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseLimits(PerformanceParsePolicy.CreateLimits(sourceBytes))
            .WithParseCacheBudgetBytes(
                parseCacheBudgetBytes ?? Math.Max(0, sourceBytes * 8L))
            .Build();

    private static async Task ForceCollectionAsync()
    {
        await WaitForCompositionFramesAsync(3);
        try { CanvasDevice.GetSharedDevice().Trim(); } catch { }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static async Task ForceManagedCollectionAsync()
    {
        await WaitForCompositionFramesAsync(3);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        await WaitForCompositionFramesAsync(3);
    }

    private static (long Managed, long Private) CaptureMemory()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return (GC.GetTotalMemory(forceFullCollection: false), process.PrivateMemorySize64);
    }

    private void ValidateReport()
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

        if (!PerformanceReleaseEvidenceValidator.HasRequiredFirstViewportProtocol(
                _report.FirstUsableViewport))
        {
            _report.Failures.Add(
                "First-viewport cache-disabled/cache-hit evidence violated its Williams schedule, raw trial, pinned-thread, cache-proof, stationarity, or absolute-budget contract.");
        }

        if (!IsScrollResultCompleteAndPassing())
            _report.Failures.Add("Warm 120 Hz scroll frame/work/allocation/GC gate failed.");
        ValidateRetainedMemory();
        if (!IsLifecycleResultCompleteAndPassing())
            _report.Failures.Add("The 100-cycle retained-memory plateau gate failed.");
        if (!IsSourceLookupCompleteAndPassing())
            _report.Failures.Add("The 100,000-entry source-map lookup gate failed.");
        if (!IsCancellationCompleteAndPassing())
            _report.Failures.Add("The cancellation/stale-commit gate failed.");
        if (!IsRegressionCompleteAndPassing())
            _report.Failures.Add(
                "The same-machine hybrid >5% latency/>2% allocation regression gate failed, was noisy, or lacked a reference.");
        if (!PerformanceReleaseEvidenceValidator.HasQualifiedRefresh(_report.Machine))
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

        _report.Passed = _report.IsReleaseEvidence && _report.Failures.Count == 0;
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
                     _renderFailures,
                     _events.ProviderErrors))
        {
            _report.Failures.Add(failure);
        }
    }

    private void ValidateMachineMetadata()
    {
        if (!PerformanceReleaseEvidenceValidator.IsMachineMetadataComplete(_report.Machine))
        {
            _report.Failures.Add(
                "Machine fingerprint/GPU/driver/power/scheduler/GC/display metadata was incomplete or inconsistent.");
        }
        if (!PerformanceReleaseEvidenceValidator.IsEnvironmentSnapshotComplete(
                _report.CompletionMachine))
        {
            _report.Failures.Add(
                "Completion machine/environment metadata was incomplete or inconsistent.");
        }
        if (!PerformanceReleaseEvidenceValidator.IsSameRunEnvironment(
                _report.Machine,
                _report.CompletionMachine))
        {
            _report.Failures.Add(
                "The display, graphics, power, scheduler, GC, DPI, or viewport environment changed during measurement.");
        }
        if (!_options.Quick &&
            !PerformanceReleaseEvidenceValidator.IsReleasePowerEnvironment(_report.Machine))
        {
            _report.Failures.Add(
                "Release evidence requires AC power with energy saver disabled.");
        }
    }

    private bool IsScrollResultCompleteAndPassing()
    {
        ScrollResult scroll = _report.Scroll;
        int framesPerTrial = _report.SampleRequirements.ScrollFramesRequired;
        int requiredTrials = _report.SampleRequirements.ScrollTrialsRequired;
        int expectedTotalFrames = checked(framesPerTrial * requiredTrials);
        bool rawEvidenceValid = ScrollEvidenceValidator.TryValidate(
            scroll,
            framesPerTrial,
            requiredTrials,
            out _);
        bool trialsOrdered = scroll.Trials.Count == requiredTrials &&
                             scroll.Trials.Select(static trial => trial.Ordinal).SequenceEqual(
                                 Enumerable.Range(0, requiredTrials));
        bool trialsDisjoint = trialsOrdered;
        long previousLastFrameId = 0;
        foreach (ScrollTrialResult trial in scroll.Trials)
        {
            if (trial.FirstLogicalFrameId <= previousLastFrameId)
                trialsDisjoint = false;
            previousLastFrameId = trial.FirstLogicalFrameId + trial.RequestedFrames - 1L;
        }

        bool trialsComplete = trialsOrdered &&
            trialsDisjoint &&
            scroll.Trials.All(trial =>
                trial.RequestedFrames == framesPerTrial &&
                trial.MeasuredFrameIntervals == framesPerTrial &&
                trial.FramesWithRendererWork == framesPerTrial &&
                IsFiniteNonNegative(trial.UiThreadWorkP95Milliseconds) &&
                IsFiniteNonNegative(trial.UiThreadWorkP99Milliseconds) &&
                IsFiniteNonNegative(trial.ScrollCallbackWorkP95Milliseconds) &&
                IsFiniteNonNegative(trial.PaintCallbackWorkP95Milliseconds) &&
                IsFinitePositive(trial.FrameTimeP95Milliseconds) &&
                IsFiniteNonNegative(trial.FramesOver16_7Percent) &&
                IsFinitePositive(trial.MaximumFrameStallMilliseconds) &&
                IsFiniteNonNegative(trial.AllocatedBytesPerFrameP95) &&
                trial.MaximumAllocatedBytesPerFrame >= 0 &&
                IsFiniteNonNegative(trial.RendererOwnedAllocatedBytesPerFrameP95) &&
                trial.MaximumRendererOwnedAllocatedBytesPerFrame >= 0 &&
                IsFiniteNonNegative(trial.PlatformProjectionAllocatedBytesPerFrameP95) &&
                trial.MaximumPlatformProjectionAllocatedBytesPerFrame >= 0 &&
                IsFiniteNonNegative(trial.ScrollAllocatedBytesPerFrameP95) &&
                IsFiniteNonNegative(trial.PaintAllocatedBytesPerFrameP95) &&
                IsFiniteNonNegative(trial.ScrollLazyLayoutAllocatedBytesP95) &&
                IsFiniteNonNegative(trial.ScrollAdornerAllocatedBytesP95) &&
                IsFiniteNonNegative(trial.ScrollRealizationAllocatedBytesP95) &&
                IsFiniteNonNegative(trial.ScrollHighlightingAllocatedBytesP95) &&
                IsFiniteNonNegative(trial.PaintSchedulingAllocatedBytesP95) &&
                IsFiniteNonNegative(trial.PaintPlatformSessionAllocatedBytesP95) &&
                IsFiniteNonNegative(trial.SnapshotPaintAllocatedBytesP95) &&
                IsFiniteNonNegative(trial.InteractivePaintAllocatedBytesP95) &&
                IsFiniteNonNegative(trial.InlinePaintAllocatedBytesP95) &&
                IsFiniteNonNegative(trial.TablePaintAllocatedBytesP95) &&
                IsFiniteNonNegative(trial.CodePaintAllocatedBytesP95) &&
                IsFiniteNonNegative(trial.OtherPaintAllocatedBytesP95) &&
                trial.MaximumRealizedCodeActions >= 0 &&
                trial.MaximumOffscreenRealizedCodeActions == 0 &&
                trial.Gen2Collections == 0 &&
                trial.Complete);
        bool estimatorsValid =
            scroll.RegressionEstimator == PerformanceMeasurementContract.ScrollRegressionEstimator &&
            IsFiniteNonNegative(scroll.RegressionUiThreadWorkP95Milliseconds) &&
            IsFiniteNonNegative(scroll.RegressionUiThreadWorkP99Milliseconds) &&
            IsFinitePositive(scroll.RegressionFrameTimeP95Milliseconds) &&
            IsFiniteNonNegative(scroll.RegressionRendererOwnedAllocatedBytesPerFrameP95) &&
            NearlyEqual(
                scroll.RegressionUiThreadWorkP95Milliseconds,
                PerformanceStatistics.HodgesLehmann(
                    scroll.Trials.Select(static trial => trial.UiThreadWorkP95Milliseconds))) &&
            NearlyEqual(
                scroll.RegressionUiThreadWorkP99Milliseconds,
                PerformanceStatistics.HodgesLehmann(
                    scroll.Trials.Select(static trial => trial.UiThreadWorkP99Milliseconds))) &&
            NearlyEqual(
                scroll.RegressionFrameTimeP95Milliseconds,
                PerformanceStatistics.HodgesLehmann(
                    scroll.Trials.Select(static trial => trial.FrameTimeP95Milliseconds))) &&
            NearlyEqual(
                scroll.RegressionRendererOwnedAllocatedBytesPerFrameP95,
                PerformanceStatistics.HodgesLehmann(
                    scroll.Trials.Select(static trial => trial.RendererOwnedAllocatedBytesPerFrameP95))) &&
            PerformanceStatistics.RobustDispersionPercent(
                scroll.Trials.Select(static trial => trial.UiThreadWorkP95Milliseconds)) <=
                PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent &&
            PerformanceStatistics.RobustDispersionPercent(
                scroll.Trials.Select(static trial => trial.UiThreadWorkP99Milliseconds)) <=
                PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent &&
            PerformanceStatistics.RobustDispersionPercent(
                scroll.Trials.Select(static trial => trial.FrameTimeP95Milliseconds)) <=
                PerformanceMeasurementContract.ScrollFrameTimeDispersionBudgetPercent &&
            PerformanceStatistics.RobustDispersionPercent(
                scroll.Trials.Select(
                    static trial => trial.RendererOwnedAllocatedBytesPerFrameP95)) <=
                PerformanceMeasurementContract.AllocationDispersionBudgetPercent;
        bool metricsPresent =
            scroll.Corpus == PerformanceDocumentFactory.WarmScrollStressCorpus &&
            scroll.SourceUtf16Bytes == 1024 * 1024 &&
            scroll.StopwatchFrequency == Stopwatch.Frequency &&
            scroll.RequestedFrames == expectedTotalFrames &&
            scroll.MeasuredFrameIntervals == expectedTotalFrames &&
            scroll.FramesWithRendererWork == expectedTotalFrames &&
            rawEvidenceValid &&
            trialsComplete &&
            estimatorsValid &&
            IsFiniteNonNegative(scroll.UiThreadWorkP95Milliseconds) &&
            IsFiniteNonNegative(scroll.UiThreadWorkP99Milliseconds) &&
            IsFiniteNonNegative(scroll.ScrollCallbackWorkP95Milliseconds) &&
            IsFiniteNonNegative(scroll.PaintCallbackWorkP95Milliseconds) &&
            IsFinitePositive(scroll.FrameTimeP95Milliseconds) &&
            IsFiniteNonNegative(scroll.FramesOver16_7Percent) &&
            IsFinitePositive(scroll.MaximumFrameStallMilliseconds) &&
            IsFiniteNonNegative(scroll.AllocatedBytesPerFrameP95) &&
            scroll.MaximumAllocatedBytesPerFrame >= 0 &&
            IsFiniteNonNegative(scroll.RendererOwnedAllocatedBytesPerFrameP95) &&
            scroll.MaximumRendererOwnedAllocatedBytesPerFrame >= 0 &&
            IsFiniteNonNegative(scroll.PlatformProjectionAllocatedBytesPerFrameP95) &&
            scroll.MaximumPlatformProjectionAllocatedBytesPerFrame >= 0 &&
            scroll.AllocationGateBasis == "renderer-owned-managed" &&
            !string.IsNullOrWhiteSpace(scroll.RawAllocationScope) &&
            !string.IsNullOrWhiteSpace(scroll.PlatformProjectionAllocationScope) &&
            scroll.AllocationBudgetBytesPerFrame == 4096 &&
            scroll.UiThreadWorkP95BudgetMilliseconds == 4 &&
            scroll.UiThreadWorkP99BudgetMilliseconds == 6 &&
            scroll.FrameTimeP95BudgetMilliseconds ==
                PerformanceMeasurementContract.ScrollFrameTimeP95BudgetMilliseconds &&
            scroll.FramesOver16_7PercentBudget == 1 &&
            scroll.MaximumFrameStallBudgetMilliseconds == 50 &&
            scroll.MaximumRealizedCodeActions >= 0 &&
            scroll.MaximumOffscreenRealizedCodeActions >= 0 &&
            scroll.Gen2Collections >= 0;

        bool gatesPass =
            scroll.UiThreadWorkP95Milliseconds <= scroll.UiThreadWorkP95BudgetMilliseconds &&
            scroll.UiThreadWorkP99Milliseconds <= scroll.UiThreadWorkP99BudgetMilliseconds &&
            scroll.FrameTimeP95Milliseconds <= scroll.FrameTimeP95BudgetMilliseconds &&
            scroll.FramesOver16_7Percent < scroll.FramesOver16_7PercentBudget &&
            scroll.MaximumFrameStallMilliseconds <= scroll.MaximumFrameStallBudgetMilliseconds &&
            scroll.MaximumRendererOwnedAllocatedBytesPerFrame <= scroll.AllocationBudgetBytesPerFrame &&
            !scroll.RendererOwnedLohAllocationPossible &&
            scroll.MaximumOffscreenRealizedCodeActions == 0 &&
            scroll.Gen2Collections == 0;
        return metricsPresent && gatesPass && scroll.Passed;
    }

    private void ValidateRetainedMemory()
    {
        if (_report.RetainedMemory.Count != FirstViewportSourceSizes.Length)
            _report.Failures.Add("Retained-memory report did not contain exactly the three required source sizes.");

        foreach (int sourceBytes in FirstViewportSourceSizes)
        {
            RetainedMemoryResult[] matches = _report.RetainedMemory
                .Where(item => item.SourceUtf16Bytes == sourceBytes)
                .ToArray();
            long expectedBudget = checked(sourceBytes * 8L + 32L * 1024 * 1024);
            bool valid = matches.Length == 1 &&
                         matches[0].Corpus == PerformanceDocumentFactory.GetCorpusName(sourceBytes) &&
                         matches[0].ManagedDeltaBytes >= 0 &&
                         matches[0].PrivateDeltaBytes >= 0 &&
                         matches[0].BudgetBytes == expectedBudget &&
                         matches[0].ManagedDeltaBytes <= expectedBudget &&
                         matches[0].PrivateDeltaBytes <= expectedBudget &&
                         matches[0].Passed;
            if (!valid)
                _report.Failures.Add($"Retained-memory gate failed or lacked evidence for {sourceBytes:N0} UTF-16 bytes.");
        }
    }

    private bool IsLifecycleResultCompleteAndPassing()
    {
        LifecyclePlateauResult lifecycle = _report.LifecyclePlateau;
        double managedGrowth = PerformanceStatistics.GrowthPercent(
            lifecycle.Checkpoint100ManagedBytes,
            lifecycle.Checkpoint80ManagedBytes);
        double privateGrowth = PerformanceStatistics.GrowthPercent(
            lifecycle.Checkpoint100PrivateBytes,
            lifecycle.Checkpoint80PrivateBytes);
        return lifecycle.RequiredCycles == _report.SampleRequirements.LifecycleCyclesRequired &&
               lifecycle.CompletedCycles == lifecycle.RequiredCycles &&
               lifecycle.SyntheticDeviceResets == 4 &&
               lifecycle.SyntheticDeviceResets == lifecycle.CompletedCycles / 25 &&
               !string.IsNullOrWhiteSpace(lifecycle.DeviceResetMode) &&
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

    private bool IsSourceLookupCompleteAndPassing()
    {
        SourceLookupResult lookup = _report.SourceLookup;
        double nanosecondsPerTick = 1_000_000_000.0 / lookup.StopwatchFrequency;
        bool trialsOrdered = lookup.RegressionTrials.Count == lookup.RegressionTrialCount &&
                             lookup.RegressionTrials
                                 .Select(static trial => trial.Ordinal)
                                 .SequenceEqual(Enumerable.Range(0, lookup.RegressionTrialCount));
        bool trialsComplete = trialsOrdered && lookup.RegressionTrials.All(trial =>
            trial.ObservationCount == lookup.RegressionObservationCount &&
            trial.OperationCount == lookup.RegressionOperationCount &&
            trial.OperationCount == lookup.RegressionBatchSize * trial.ObservationCount &&
            trial.SamplesElapsedTicks.Count == trial.ObservationCount &&
            trial.SamplesNanosecondsPerLookup.Count == trial.ObservationCount &&
            trial.SamplesElapsedTicks.Zip(
                    trial.SamplesNanosecondsPerLookup,
                    (ticks, nanoseconds) =>
                        ticks > 0 &&
                        IsFinitePositive(nanoseconds) &&
                        NearlyEqual(
                            nanoseconds,
                            ticks * nanosecondsPerTick / lookup.RegressionBatchSize))
                .All(static matches => matches) &&
            IsFinitePositive(trial.P95Nanoseconds) &&
            NearlyEqual(
                trial.P95Nanoseconds,
                PerformanceStatistics.Percentile(
                    trial.SamplesNanosecondsPerLookup,
                    0.95)) &&
            trial.AllocatedBytes == 0 &&
            trial.Complete);
        long measuredRegressionAllocations = lookup.RegressionTrials.Sum(
            static trial => trial.AllocatedBytes);
        return lookup.MappingCount == _report.SampleRequirements.SourceLookupMappingCountRequired &&
               lookup.QueryCount == _report.SampleRequirements.SourceLookupAbsoluteQueryCountRequired &&
               lookup.StopwatchFrequency == Stopwatch.Frequency &&
               PinnedMeasurementThreadScope.IsEvidenceValid(
                   lookup.MeasurementThreadAffinityMask,
                   lookup.MeasurementProcessorGroup,
                   lookup.MeasurementProcessorNumber,
                   lookup.MeasurementThreadPriority,
                   ThreadPriority.Highest) &&
               lookup.SamplesElapsedTicks.Count == lookup.QueryCount &&
               lookup.SamplesElapsedTicks.All(static ticks => ticks >= 0) &&
               IsFinitePositive(lookup.P95Nanoseconds) &&
               NearlyEqual(
                   lookup.P95Nanoseconds,
                   PerformanceStatistics.Percentile(lookup.SamplesElapsedTicks, 0.95) *
                   nanosecondsPerTick) &&
               lookup.P95BudgetNanoseconds == 25_000 &&
               lookup.P95Nanoseconds <= lookup.P95BudgetNanoseconds &&
               lookup.AllocatedBytes == 0 &&
                lookup.RegressionWarmupPasses ==
                    _report.SampleRequirements.SourceLookupRegressionWarmupPassesRequired &&
                lookup.RegressionEstimator ==
                    PerformanceMeasurementContract.SourceLookupRegressionEstimator &&
                lookup.RegressionTrialCount ==
                    _report.SampleRequirements.SourceLookupRegressionTrialsRequired &&
                lookup.RegressionBatchSize ==
                   _report.SampleRequirements.SourceLookupRegressionBatchSizeRequired &&
               lookup.RegressionObservationCount ==
                   _report.SampleRequirements.SourceLookupRegressionObservationCountRequired &&
               lookup.RegressionOperationCount ==
                   _report.SampleRequirements.SourceLookupRegressionOperationCountRequired &&
                lookup.RegressionOperationCount ==
                    lookup.RegressionBatchSize * lookup.RegressionObservationCount &&
                trialsComplete &&
                IsFinitePositive(lookup.RegressionP95Nanoseconds) &&
                NearlyEqual(
                    lookup.RegressionP95Nanoseconds,
                    PerformanceStatistics.HodgesLehmann(
                        lookup.RegressionTrials.Select(
                            static trial => trial.P95Nanoseconds))) &&
                PerformanceStatistics.RobustDispersionPercent(
                    lookup.RegressionTrials.Select(static trial => trial.P95Nanoseconds)) <=
                    PerformanceMeasurementContract.SourceLookupDispersionBudgetPercent &&
                lookup.RegressionAllocatedBytes == measuredRegressionAllocations &&
                measuredRegressionAllocations == 0 &&
                lookup.Passed;
    }

    private bool IsCancellationCompleteAndPassing()
    {
        CancellationResult cancellation = _report.Cancellation;
        int trialsRequired = _report.SampleRequirements.CancellationTrialsRequired;
        int iterationsPerTrial =
            _report.SampleRequirements.CancellationIterationsPerTrialRequired;
        bool evidenceValid = CancellationEvidenceValidator.TryValidate(
            cancellation,
            iterationsPerTrial,
            trialsRequired,
            out CancellationEvidenceStatistics statistics);
        return evidenceValid &&
               cancellation.TrialStartPolicy ==
                   PerformanceMeasurementContract.CancellationTrialStartPolicy &&
               cancellation.RegressionEstimator ==
                   PerformanceMeasurementContract.CancellationRegressionEstimator &&
               statistics.WarmupP95Milliseconds <=
                   CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
               statistics.PooledP95Milliseconds <=
                   CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
               cancellation.Trials.All(trial =>
                   trial.WarmupElapsedMilliseconds <=
                       CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
                   trial.WarmupStaleCommits == 0 &&
                   trial.P95Milliseconds <=
                       CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
                   trial.StaleCommits == 0) &&
               statistics.RegressionDispersionPercent <=
                   PerformanceMeasurementContract.CancellationDispersionBudgetPercent &&
               cancellation.P95BudgetMilliseconds == CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
               cancellation.Passed;
    }

    private bool IsRegressionCompleteAndPassing()
    {
        RegressionResult regression = _report.Regression;
        if (_options.EstablishBaseline)
        {
            return regression.Mode == "baseline" &&
                   regression.DecisionPolicy ==
                       PerformanceMeasurementContract.RegressionDecisionPolicy &&
                   regression.LatencyRegressionBudgetPercent == 5 &&
                   regression.AllocationRegressionBudgetPercent == 2 &&
                   string.IsNullOrEmpty(regression.ReferencePath) &&
                   string.IsNullOrEmpty(regression.ReferenceReportSha256) &&
                   regression.ReferenceEligible &&
                   regression.MachineComparable &&
                   regression.Passed;
        }

        if (_options.Quick)
            return false;

        var expectedCandidates = new Dictionary<
            string,
            (double Candidate, double NoiseFloor, double StabilityBudget, string Kind,
                bool InternallyStationary)>(
                StringComparer.Ordinal);
        foreach (FirstViewportResult viewport in _report.FirstUsableViewport)
        {
            expectedCandidates.Add(
                $"firstViewport.{viewport.Mode}.{viewport.SourceUtf16Bytes}.p95Ms.hodgesLehmann",
                (
                    viewport.RegressionP95Milliseconds,
                    PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
                        viewport.Mode,
                        viewport.SourceUtf16Bytes),
                    PerformanceMeasurementContract.FirstViewportDispersionBudgetPercent,
                    "latency",
                    viewport.StationarityEvaluated && viewport.StationarityPassed));
        }
        expectedCandidates.Add(
            "scroll.uiThreadWorkP95Ms.hodgesLehmann",
            (
                _report.Scroll.RegressionUiThreadWorkP95Milliseconds,
                PerformanceMeasurementContract.ScrollUiThreadWorkP95NoiseFloorMilliseconds,
                PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent,
                "latency",
                true));
        expectedCandidates.Add(
            "scroll.uiThreadWorkP99Ms.hodgesLehmann",
            (
                _report.Scroll.RegressionUiThreadWorkP99Milliseconds,
                PerformanceMeasurementContract.ScrollUiThreadWorkP99NoiseFloorMilliseconds,
                PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent,
                "latency",
                true));
        expectedCandidates.Add(
            "scroll.frameTimeP95Ms.hodgesLehmann",
            (
                _report.Scroll.RegressionFrameTimeP95Milliseconds,
                PerformanceMeasurementContract.ScrollFrameTimeP95NoiseFloorMilliseconds,
                PerformanceMeasurementContract.ScrollFrameTimeDispersionBudgetPercent,
                "latency",
                true));
        expectedCandidates.Add(
            "sourceLookup.regressionP95Ns.hodgesLehmann",
            (
                _report.SourceLookup.RegressionP95Nanoseconds,
                PerformanceMeasurementContract.SourceLookupNoiseFloorNanoseconds,
                PerformanceMeasurementContract.SourceLookupDispersionBudgetPercent,
                "latency",
                true));
        expectedCandidates.Add(
            "cancellation.p95Ms.hodgesLehmann",
            (
                _report.Cancellation.RegressionP95Milliseconds,
                PerformanceMeasurementContract.CancellationNoiseFloorMilliseconds,
                PerformanceMeasurementContract.CancellationDispersionBudgetPercent,
                "latency",
                true));
        expectedCandidates.Add(
            "scroll.rendererOwnedAllocatedBytesPerFrameP95.hodgesLehmann",
            (
                _report.Scroll.RegressionRendererOwnedAllocatedBytesPerFrameP95,
                PerformanceMeasurementContract.RendererOwnedAllocationNoiseFloorBytes,
                PerformanceMeasurementContract.AllocationDispersionBudgetPercent,
                "allocation",
                true));
        bool comparisonsMatch = regression.Comparisons.Count == expectedCandidates.Count &&
            regression.Comparisons.Select(static comparison => comparison.Metric).Distinct().Count() ==
                regression.Comparisons.Count &&
            regression.Comparisons.All(comparison =>
            {
                if (!expectedCandidates.TryGetValue(comparison.Metric, out var expected))
                    return false;
                double expectedBudget = expected.Kind == "allocation" ? 2 : 5;
                double expectedAbsoluteDelta = comparison.Candidate - comparison.Reference;
                double expectedRelativeAllowance =
                    Math.Abs(comparison.Reference) * expectedBudget / 100;
                double expectedAllowedDelta = Math.Max(
                    expectedRelativeAllowance,
                    expected.NoiseFloor);
                bool expectedPass =
                    expected.InternallyStationary &&
                    comparison.ReferenceDispersionPercent <= expected.StabilityBudget &&
                    comparison.CandidateDispersionPercent <= expected.StabilityBudget &&
                    (expectedAbsoluteDelta <= expectedAllowedDelta ||
                     NearlyEqual(expectedAbsoluteDelta, expectedAllowedDelta));
                return comparison.Kind == expected.Kind &&
                       NearlyEqual(comparison.Candidate, expected.Candidate) &&
                       comparison.BudgetPercent == expectedBudget &&
                       NearlyEqual(comparison.AbsoluteDelta, expectedAbsoluteDelta) &&
                       NearlyEqual(
                           comparison.DeltaPercent,
                           PerformanceStatistics.DeltaPercent(
                               comparison.Candidate,
                               comparison.Reference)) &&
                       NearlyEqual(comparison.RelativeAllowance, expectedRelativeAllowance) &&
                       comparison.AbsoluteNoiseFloor == expected.NoiseFloor &&
                       NearlyEqual(comparison.AllowedAbsoluteDelta, expectedAllowedDelta) &&
                       comparison.StabilityBudgetPercent == expected.StabilityBudget &&
                        comparison.Passed == expectedPass &&
                        comparison.Decision == (expectedPass ? "pass" :
                            !expected.InternallyStationary ||
                            comparison.ReferenceDispersionPercent > expected.StabilityBudget ||
                            comparison.CandidateDispersionPercent > expected.StabilityBudget
                               ? "inconclusive"
                               : "regression");
            });
        bool referenceBound = _options.ReferencePath is not null &&
            string.Equals(regression.ReferencePath, _options.ReferencePath, StringComparison.Ordinal) &&
            IsSha256(regression.ReferenceReportSha256) &&
            string.Equals(
                regression.ReferenceReportSha256,
                PerformanceEvidenceHashing.ComputeFileSha256(_options.ReferencePath),
                StringComparison.OrdinalIgnoreCase);
        return regression.Mode == "candidate" &&
               regression.DecisionPolicy ==
                   PerformanceMeasurementContract.RegressionDecisionPolicy &&
               regression.LatencyRegressionBudgetPercent == 5 &&
               regression.AllocationRegressionBudgetPercent == 2 &&
               referenceBound &&
               regression.ReferenceEligible &&
               regression.MachineComparable &&
               regression.ComparedLatencyMetrics == 11 &&
               regression.ComparedAllocationMetrics == 1 &&
               comparisonsMatch &&
               regression.Comparisons.All(static comparison =>
                   !string.IsNullOrWhiteSpace(comparison.Metric) &&
                   (comparison.Kind == "latency" || comparison.Kind == "allocation") &&
                   double.IsFinite(comparison.Reference) &&
                   double.IsFinite(comparison.Candidate) &&
                   double.IsFinite(comparison.AbsoluteDelta) &&
                   double.IsFinite(comparison.DeltaPercent) &&
                   double.IsFinite(comparison.BudgetPercent) &&
                   double.IsFinite(comparison.RelativeAllowance) &&
                   double.IsFinite(comparison.AbsoluteNoiseFloor) &&
                   double.IsFinite(comparison.AllowedAbsoluteDelta) &&
                   double.IsFinite(comparison.ReferenceDispersionPercent) &&
                   double.IsFinite(comparison.CandidateDispersionPercent) &&
                   double.IsFinite(comparison.StabilityBudgetPercent) &&
                   comparison.Decision == "pass" &&
                   comparison.Passed) &&
               regression.Passed;
    }

    private static bool IsFinitePositive(double value)
        => double.IsFinite(value) && value > 0;

    private static bool IsFiniteNonNegative(double value)
        => double.IsFinite(value) && value >= 0;

    private static bool NearlyEqual(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right))
            return false;
        double scale = Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= scale * 1e-12;
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } &&
           value.All(static character =>
               character is >= '0' and <= '9' or
               >= 'a' and <= 'f' or
               >= 'A' and <= 'F');
}
