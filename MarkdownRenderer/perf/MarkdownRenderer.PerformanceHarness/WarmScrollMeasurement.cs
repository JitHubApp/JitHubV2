using System.Diagnostics;
using MarkdownRenderer.Controls;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Measures warm 120 Hz scrolling of the 1 MiB scroll-stress corpus: frame
/// intervals, per-frame renderer UI work and allocations, and gen2
/// collections over the requested trials, after a prewarm pass that makes
/// lazy layout and realization resident.
/// </summary>
internal sealed class WarmScrollMeasurement
{
    private const int SourceBytes = 1024 * 1024;

    private readonly PerformanceOptions _options;
    private readonly PerformanceReport _report;
    private readonly PerformanceEventListener _events;
    private readonly MeasurementRendererHost _rendererHost;
    private readonly CancellationToken _measurementAbort;

    internal WarmScrollMeasurement(
        PerformanceOptions options,
        PerformanceReport report,
        PerformanceEventListener events,
        MeasurementRendererHost rendererHost,
        CancellationToken measurementAbort)
    {
        _options = options;
        _report = report;
        _events = events;
        _rendererHost = rendererHost;
        _measurementAbort = measurementAbort;
    }

    private MarkdownScrollView Renderer => _rendererHost.Renderer!;

    internal async Task<ScrollResult> MeasureAsync()
    {
        await LoadAndPrewarmAsync();

        int totalFrames = checked(_options.ScrollFrames * _options.ScrollTrials);
        var series = new ScrollFrameSeries(totalFrames, _options.ScrollFrames);
        var trials = new List<ScrollTrialResult>(_options.ScrollTrials);
        var frameIntervals = new List<double>();
        long maximumRealizedCodeActions = 0;
        long maximumOffscreenCodeActions = 0;
        int framesWithRendererWork = 0;
        int gen2Collections = 0;

        for (int trialOrdinal = 0; trialOrdinal < _options.ScrollTrials; trialOrdinal++)
        {
            await SettleTrialAsync(trialOrdinal);

            long firstFrameId = 10_000L + trialOrdinal * 10_000_000L;
            _events.PrepareFrameCapture(firstFrameId, _options.ScrollFrames);
            int gen2Before = GC.CollectionCount(2);
            var trialIntervals = new List<double>(_options.ScrollFrames);
            var trialIntervalElapsedTicks = new List<long>(_options.ScrollFrames);
            await _rendererHost.RunScrollFramesAsync(
                firstFrameId,
                _options.ScrollFrames,
                trialIntervals,
                trialIntervalElapsedTicks);
            int trialGen2Collections = GC.CollectionCount(2) - gen2Before;
            gen2Collections += trialGen2Collections;
            frameIntervals.AddRange(trialIntervals);
            IReadOnlyDictionary<long, FrameWork> workByFrame = _events.SnapshotFrames(
                firstFrameId,
                firstFrameId + _options.ScrollFrames - 1);

            ScrollTrialResult trial = series.RecordTrial(
                trialOrdinal,
                firstFrameId,
                workByFrame,
                trialIntervals,
                trialIntervalElapsedTicks,
                trialGen2Collections);
            framesWithRendererWork += trial.FramesWithRendererWork;
            maximumRealizedCodeActions = Math.Max(
                maximumRealizedCodeActions,
                trial.MaximumRealizedCodeActions);
            maximumOffscreenCodeActions = Math.Max(
                maximumOffscreenCodeActions,
                trial.MaximumOffscreenRealizedCodeActions);
            trials.Add(trial);
        }

        return CreateResult(
            series,
            trials,
            frameIntervals,
            totalFrames,
            framesWithRendererWork,
            maximumRealizedCodeActions,
            maximumOffscreenCodeActions,
            gen2Collections);
    }

    /// <summary>
    /// Loads the corpus and exercises the exact scroll path once before the
    /// "warm" trace, so lazy band layout, copy-button plans, and text
    /// resources are already resident. A warm-scroll gate must not
    /// accidentally benchmark first realization or count its background
    /// collections.
    /// </summary>
    private async Task LoadAndPrewarmAsync()
    {
        string source = PerformanceDocumentFactory.CreateWarmScrollStress(SourceBytes);
        MarkdownEngine engine = PerformanceEngineFactory.Create(SourceBytes);
        _ = await engine.ParseAsync(source, _measurementAbort);
        await _rendererHost.ReplaceRendererAsync(engine);
        long sequence = _events.CurrentSequence;
        Renderer.Markdown = source;
        _ = await _events.WaitForFirstViewportAsync(
            sequence,
            SourceBytes,
            MeasurementRendererHost.ViewportTimeout);
        await _rendererHost.WaitForCompositionFramesAsync(20);

        var warmupIntervals = new List<double>(_options.ScrollFrames);
        await _rendererHost.RunScrollFramesAsync(0, _options.ScrollFrames, warmupIntervals);
        Renderer.AutomationScrolling.SetVerticalPercent(0);
        await _rendererHost.WaitForScrollTopAsync("prewarmed scroll reset");
        await WaitForLazyLayoutAsync("prewarmed lazy-layout work to retire");
        await Renderer.DrainRetiredSnapshotsAsync();
        await _rendererHost.WaitForCompositionFramesAsync(8);
        await _rendererHost.ForceManagedCollectionAsync();
        await WaitForLazyLayoutAsync("post-GC lazy-layout work to retire");
        await Renderer.DrainRetiredSnapshotsAsync();
    }

    /// <summary>
    /// Returns to the top, retires lazy-layout work and retired snapshots, and
    /// collects, so every trial starts from the same quiescent state.
    /// </summary>
    private async Task SettleTrialAsync(int trialOrdinal)
    {
        Renderer.AutomationScrolling.SetVerticalPercent(0);
        await _rendererHost.WaitForScrollTopAsync($"scroll trial {trialOrdinal} reset");
        await WaitForLazyLayoutAsync(
            $"scroll trial {trialOrdinal} reset lazy-layout work to retire");
        await Renderer.DrainRetiredSnapshotsAsync();
        await _rendererHost.WaitForCompositionFramesAsync(8);
        await _rendererHost.ForceManagedCollectionAsync();
        await WaitForLazyLayoutAsync(
            $"scroll trial {trialOrdinal} post-GC lazy-layout work to retire");
        await Renderer.DrainRetiredSnapshotsAsync();
        await _rendererHost.WaitForCompositionFramesAsync(4);
        await WaitForLazyLayoutAsync(
            $"scroll trial {trialOrdinal} final lazy-layout work to retire");
        await Renderer.DrainRetiredSnapshotsAsync();
    }

    private Task WaitForLazyLayoutAsync(string description)
        => _rendererHost.WaitUntilAsync(
            () => !Renderer.HasPendingLazyLayoutWork,
            MeasurementRendererHost.ViewportTimeout,
            description);

    /// <summary>
    /// Builds the warm-scroll result from the aggregate frame series and the
    /// per-trial results.
    /// </summary>
    /// <remarks>
    /// This method stays whole because it is one flat mapping of the aggregate
    /// series onto the frozen ScrollResult schema; the few derived values it
    /// computes first are each read once by that mapping.
    /// </remarks>
    private ScrollResult CreateResult(
        ScrollFrameSeries series,
        List<ScrollTrialResult> trials,
        List<double> frameIntervals,
        int totalFrames,
        int framesWithRendererWork,
        long maximumRealizedCodeActions,
        long maximumOffscreenCodeActions,
        int gen2Collections)
    {
        double uiP95 = PerformanceStatistics.Percentile(series.UiWork, 0.95);
        double uiP99 = PerformanceStatistics.Percentile(series.UiWork, 0.99);
        double frameP95 = PerformanceStatistics.Percentile(frameIntervals, 0.95);
        double over16_7Percent = frameIntervals.Count == 0
            ? double.NaN
            : frameIntervals.Count(value => value > 16.7) * 100.0 /
              frameIntervals.Count;
        double maximumFrame = frameIntervals.DefaultIfEmpty(double.NaN).Max();
        long maximumAllocation = series.Allocations.DefaultIfEmpty(long.MaxValue).Max();
        long maximumRendererOwnedAllocation =
            series.RendererOwnedAllocations.DefaultIfEmpty(long.MaxValue).Max();
        bool rendererOwnedLohPossible = maximumRendererOwnedAllocation >= 85_000;
        bool sufficient = IsSampleSufficient(
            trials,
            frameIntervals,
            totalFrames,
            framesWithRendererWork);

        return new ScrollResult
        {
            Corpus = PerformanceDocumentFactory.WarmScrollStressCorpus,
            SourceUtf16Bytes = SourceBytes,
            StopwatchFrequency = Stopwatch.Frequency,
            RequestedFrames = totalFrames,
            MeasuredFrameIntervals = frameIntervals.Count,
            FramesWithRendererWork = framesWithRendererWork,
            RegressionEstimator = _options.Quick
                ? "quick-single-trial-non-gating"
                : PerformanceMeasurementContract.ScrollRegressionEstimator,
            Trials = trials,
            RegressionUiThreadWorkP95Milliseconds = PerformanceStatistics.HodgesLehmann(
                trials.Select(static trial => trial.UiThreadWorkP95Milliseconds)),
            RegressionUiThreadWorkP99Milliseconds = PerformanceStatistics.HodgesLehmann(
                trials.Select(static trial => trial.UiThreadWorkP99Milliseconds)),
            RegressionFrameTimeP95Milliseconds = PerformanceStatistics.HodgesLehmann(
                trials.Select(static trial => trial.FrameTimeP95Milliseconds)),
            RegressionRendererOwnedAllocatedBytesPerFrameP95 = PerformanceStatistics.HodgesLehmann(
                trials.Select(static trial => trial.RendererOwnedAllocatedBytesPerFrameP95)),
            UiThreadWorkP95Milliseconds = uiP95,
            UiThreadWorkP99Milliseconds = uiP99,
            ScrollCallbackWorkP95Milliseconds = PerformanceStatistics.Percentile(series.ScrollWork, 0.95),
            PaintCallbackWorkP95Milliseconds = PerformanceStatistics.Percentile(series.PaintWork, 0.95),
            FrameTimeP95Milliseconds = frameP95,
            FramesOver16_7Percent = over16_7Percent,
            MaximumFrameStallMilliseconds = maximumFrame,
            AllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(series.Allocations, 0.95),
            MaximumAllocatedBytesPerFrame = maximumAllocation,
            RendererOwnedAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(series.RendererOwnedAllocations, 0.95),
            MaximumRendererOwnedAllocatedBytesPerFrame = maximumRendererOwnedAllocation,
            PlatformProjectionAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(series.PlatformProjectionAllocations, 0.95),
            MaximumPlatformProjectionAllocatedBytesPerFrame =
                series.PlatformProjectionAllocations.DefaultIfEmpty(long.MaxValue).Max(),
            ScrollAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(series.ScrollAllocations, 0.95),
            PaintAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(series.PaintAllocations, 0.95),
            ScrollLazyLayoutAllocatedBytesP95 = PerformanceStatistics.Percentile(series.LazyLayoutAllocations, 0.95),
            ScrollAdornerAllocatedBytesP95 = PerformanceStatistics.Percentile(series.AdornerAllocations, 0.95),
            ScrollRealizationAllocatedBytesP95 = PerformanceStatistics.Percentile(series.RealizationAllocations, 0.95),
            ScrollHighlightingAllocatedBytesP95 = PerformanceStatistics.Percentile(series.HighlightingAllocations, 0.95),
            PaintSchedulingAllocatedBytesP95 = PerformanceStatistics.Percentile(series.PaintSchedulingAllocations, 0.95),
            PaintPlatformSessionAllocatedBytesP95 = PerformanceStatistics.Percentile(series.PlatformSessionAllocations, 0.95),
            SnapshotPaintAllocatedBytesP95 = PerformanceStatistics.Percentile(series.SnapshotPaintAllocations, 0.95),
            InteractivePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(series.InteractivePaintAllocations, 0.95),
            InlinePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(series.InlinePaintAllocations, 0.95),
            TablePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(series.TablePaintAllocations, 0.95),
            CodePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(series.CodePaintAllocations, 0.95),
            OtherPaintAllocatedBytesP95 = PerformanceStatistics.Percentile(series.OtherPaintAllocations, 0.95),
            LohAllocationPossible = maximumAllocation >= 85_000,
            RendererOwnedLohAllocationPossible = rendererOwnedLohPossible,
            MaximumRealizedCodeActions = maximumRealizedCodeActions,
            MaximumOffscreenRealizedCodeActions = maximumOffscreenCodeActions,
            Gen2Collections = gen2Collections,
            Passed = sufficient &&
                     IsRegressionEvidenceStable(trials) &&
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

    private bool IsSampleSufficient(
        List<ScrollTrialResult> trials,
        List<double> frameIntervals,
        int totalFrames,
        int framesWithRendererWork)
        => _options.ScrollFrames == _report.SampleRequirements.ScrollFramesRequired &&
           _options.ScrollTrials == _report.SampleRequirements.ScrollTrialsRequired &&
           trials.Count == _report.SampleRequirements.ScrollTrialsRequired &&
           trials.Select(static trial => trial.Ordinal).SequenceEqual(
               Enumerable.Range(0, _report.SampleRequirements.ScrollTrialsRequired)) &&
           trials.All(static trial => trial.Complete) &&
           frameIntervals.Count == totalFrames &&
           framesWithRendererWork == totalFrames;

    private static bool IsRegressionEvidenceStable(List<ScrollTrialResult> trials)
        => PerformanceStatistics.RobustDispersionPercent(
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
}
