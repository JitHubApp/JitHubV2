using MarkdownRenderer.PerformanceHarness.Core.Events;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;

namespace MarkdownRenderer.PerformanceHarness.Measurements;

/// <summary>
/// The per-frame renderer work and allocation series of a warm-scroll run,
/// indexed by the frame's position across all trials. It records each
/// trial's frames from the event ledger and summarizes one trial's slice.
/// </summary>
internal sealed class ScrollFrameSeries
{
    private readonly int _framesPerTrial;

    internal ScrollFrameSeries(int totalFrames, int framesPerTrial)
    {
        _framesPerTrial = framesPerTrial;
        UiWork = new double[totalFrames];
        Allocations = new long[totalFrames];
        ScrollWork = new double[totalFrames];
        PaintWork = new double[totalFrames];
        ScrollAllocations = new long[totalFrames];
        PaintAllocations = new long[totalFrames];
        LazyLayoutAllocations = new long[totalFrames];
        AdornerAllocations = new long[totalFrames];
        RealizationAllocations = new long[totalFrames];
        HighlightingAllocations = new long[totalFrames];
        PaintSchedulingAllocations = new long[totalFrames];
        PlatformSessionAllocations = new long[totalFrames];
        SnapshotPaintAllocations = new long[totalFrames];
        InteractivePaintAllocations = new long[totalFrames];
        InlinePaintAllocations = new long[totalFrames];
        TablePaintAllocations = new long[totalFrames];
        CodePaintAllocations = new long[totalFrames];
        OtherPaintAllocations = new long[totalFrames];
        RendererOwnedAllocations = new long[totalFrames];
        PlatformProjectionAllocations = new long[totalFrames];
    }

    internal double[] UiWork { get; }
    internal long[] Allocations { get; }
    internal double[] ScrollWork { get; }
    internal double[] PaintWork { get; }
    internal long[] ScrollAllocations { get; }
    internal long[] PaintAllocations { get; }
    internal long[] LazyLayoutAllocations { get; }
    internal long[] AdornerAllocations { get; }
    internal long[] RealizationAllocations { get; }
    internal long[] HighlightingAllocations { get; }
    internal long[] PaintSchedulingAllocations { get; }
    internal long[] PlatformSessionAllocations { get; }
    internal long[] SnapshotPaintAllocations { get; }
    internal long[] InteractivePaintAllocations { get; }
    internal long[] InlinePaintAllocations { get; }
    internal long[] TablePaintAllocations { get; }
    internal long[] CodePaintAllocations { get; }
    internal long[] OtherPaintAllocations { get; }
    internal long[] RendererOwnedAllocations { get; }
    internal long[] PlatformProjectionAllocations { get; }

    /// <summary>
    /// Records one trial's frames from the ledger snapshot and returns the
    /// trial's result. A frame missing from the ledger keeps zero work and is
    /// not counted as a frame with renderer work.
    /// </summary>
    internal ScrollTrialResult RecordTrial(
        int trialOrdinal,
        long firstFrameId,
        IReadOnlyDictionary<long, FrameWork> workByFrame,
        List<double> trialIntervals,
        List<long> trialIntervalElapsedTicks,
        int trialGen2Collections)
    {
        int aggregateOffset = trialOrdinal * _framesPerTrial;
        (int trialFramesWithRendererWork,
            long trialMaximumRealizedCodeActions,
            long trialMaximumOffscreenCodeActions,
            long[] trialUiThreadWorkElapsedTicks) =
            RecordTrialFrames(aggregateOffset, firstFrameId, workByFrame);

        IEnumerable<double> TrialDoubles(double[] values) =>
            values.Skip(aggregateOffset).Take(_framesPerTrial);
        IEnumerable<long> TrialLongs(long[] values) =>
            values.Skip(aggregateOffset).Take(_framesPerTrial);
        double trialFramesOver16_7Percent = trialIntervals.Count == 0
            ? double.NaN
            : trialIntervals.Count(value => value > 16.7) * 100.0 / trialIntervals.Count;
        return new ScrollTrialResult
        {
            Ordinal = trialOrdinal,
            FirstLogicalFrameId = firstFrameId,
            RequestedFrames = _framesPerTrial,
            MeasuredFrameIntervals = trialIntervals.Count,
            FramesWithRendererWork = trialFramesWithRendererWork,
            UiThreadWorkElapsedTicks = trialUiThreadWorkElapsedTicks.ToList(),
            FrameIntervalElapsedTicks = trialIntervalElapsedTicks,
            RendererOwnedAllocatedBytesPerFrame =
                TrialLongs(RendererOwnedAllocations).ToList(),
            UiThreadWorkP95Milliseconds = PerformanceStatistics.Percentile(TrialDoubles(UiWork), 0.95),
            UiThreadWorkP99Milliseconds = PerformanceStatistics.Percentile(TrialDoubles(UiWork), 0.99),
            ScrollCallbackWorkP95Milliseconds = PerformanceStatistics.Percentile(TrialDoubles(ScrollWork), 0.95),
            PaintCallbackWorkP95Milliseconds = PerformanceStatistics.Percentile(TrialDoubles(PaintWork), 0.95),
            FrameTimeP95Milliseconds = PerformanceStatistics.Percentile(trialIntervals, 0.95),
            FramesOver16_7Percent = trialFramesOver16_7Percent,
            MaximumFrameStallMilliseconds = trialIntervals.DefaultIfEmpty(double.NaN).Max(),
            AllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(TrialLongs(Allocations), 0.95),
            MaximumAllocatedBytesPerFrame = TrialLongs(Allocations).DefaultIfEmpty(long.MaxValue).Max(),
            RendererOwnedAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(TrialLongs(RendererOwnedAllocations), 0.95),
            MaximumRendererOwnedAllocatedBytesPerFrame = TrialLongs(RendererOwnedAllocations).DefaultIfEmpty(long.MaxValue).Max(),
            PlatformProjectionAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(TrialLongs(PlatformProjectionAllocations), 0.95),
            MaximumPlatformProjectionAllocatedBytesPerFrame = TrialLongs(PlatformProjectionAllocations).DefaultIfEmpty(long.MaxValue).Max(),
            ScrollAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(TrialLongs(ScrollAllocations), 0.95),
            PaintAllocatedBytesPerFrameP95 = PerformanceStatistics.Percentile(TrialLongs(PaintAllocations), 0.95),
            ScrollLazyLayoutAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(LazyLayoutAllocations), 0.95),
            ScrollAdornerAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(AdornerAllocations), 0.95),
            ScrollRealizationAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(RealizationAllocations), 0.95),
            ScrollHighlightingAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(HighlightingAllocations), 0.95),
            PaintSchedulingAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(PaintSchedulingAllocations), 0.95),
            PaintPlatformSessionAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(PlatformSessionAllocations), 0.95),
            SnapshotPaintAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(SnapshotPaintAllocations), 0.95),
            InteractivePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(InteractivePaintAllocations), 0.95),
            InlinePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(InlinePaintAllocations), 0.95),
            TablePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(TablePaintAllocations), 0.95),
            CodePaintAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(CodePaintAllocations), 0.95),
            OtherPaintAllocatedBytesP95 = PerformanceStatistics.Percentile(TrialLongs(OtherPaintAllocations), 0.95),
            MaximumRealizedCodeActions = trialMaximumRealizedCodeActions,
            MaximumOffscreenRealizedCodeActions = trialMaximumOffscreenCodeActions,
            Gen2Collections = trialGen2Collections,
            Complete = trialIntervals.Count == _framesPerTrial &&
                       trialIntervalElapsedTicks.Count == _framesPerTrial &&
                       trialFramesWithRendererWork == _framesPerTrial,
        };
    }

    private (int FramesWithRendererWork,
        long MaximumRealizedCodeActions,
        long MaximumOffscreenCodeActions,
        long[] UiThreadWorkElapsedTicks) RecordTrialFrames(
            int aggregateOffset,
            long firstFrameId,
            IReadOnlyDictionary<long, FrameWork> workByFrame)
    {
        int framesWithRendererWork = 0;
        long maximumRealizedCodeActions = 0;
        long maximumOffscreenCodeActions = 0;
        var uiThreadWorkElapsedTicks = new long[_framesPerTrial];
        for (int index = 0; index < _framesPerTrial; index++)
        {
            if (!workByFrame.TryGetValue(firstFrameId + index, out FrameWork? work))
                continue;

            if (work.ScrollCallbacks > 0 || work.PaintCallbacks > 0)
                framesWithRendererWork++;
            uiThreadWorkElapsedTicks[index] = work.UiThreadWorkElapsedTicks;
            RecordFrame(aggregateOffset + index, work);
            maximumRealizedCodeActions = Math.Max(
                maximumRealizedCodeActions,
                work.MaximumRealizedCodeActions);
            maximumOffscreenCodeActions = Math.Max(
                maximumOffscreenCodeActions,
                work.MaximumOffscreenCodeActions);
        }

        return (
            framesWithRendererWork,
            maximumRealizedCodeActions,
            maximumOffscreenCodeActions,
            uiThreadWorkElapsedTicks);
    }

    private void RecordFrame(int aggregateIndex, FrameWork work)
    {
        UiWork[aggregateIndex] = work.ElapsedMilliseconds;
        Allocations[aggregateIndex] = work.AllocatedBytes;
        ScrollWork[aggregateIndex] = work.ScrollElapsedMilliseconds;
        PaintWork[aggregateIndex] = work.PaintElapsedMilliseconds;
        ScrollAllocations[aggregateIndex] = work.ScrollAllocatedBytes;
        PaintAllocations[aggregateIndex] = work.PaintAllocatedBytes;
        LazyLayoutAllocations[aggregateIndex] = work.ScrollLazyLayoutAllocatedBytes;
        AdornerAllocations[aggregateIndex] = work.ScrollAdornerAllocatedBytes;
        RealizationAllocations[aggregateIndex] = work.ScrollRealizationAllocatedBytes;
        HighlightingAllocations[aggregateIndex] = work.ScrollHighlightingAllocatedBytes;
        PaintSchedulingAllocations[aggregateIndex] = work.PaintSchedulingAllocatedBytes;
        PlatformSessionAllocations[aggregateIndex] = work.PaintPlatformSessionAllocatedBytes;
        SnapshotPaintAllocations[aggregateIndex] = work.SnapshotPaintAllocatedBytes;
        InteractivePaintAllocations[aggregateIndex] = work.InteractivePaintAllocatedBytes;
        InlinePaintAllocations[aggregateIndex] = work.InlinePaintAllocatedBytes;
        TablePaintAllocations[aggregateIndex] = work.TablePaintAllocatedBytes;
        CodePaintAllocations[aggregateIndex] = work.CodePaintAllocatedBytes;
        OtherPaintAllocations[aggregateIndex] = work.OtherPaintAllocatedBytes;
        long platformBytes =
            work.ScrollAdornerAllocatedBytes +
            work.ScrollRealizationAllocatedBytes +
            work.PaintPlatformSessionAllocatedBytes +
            work.SnapshotPaintAllocatedBytes +
            work.InteractivePaintAllocatedBytes;
        PlatformProjectionAllocations[aggregateIndex] = Math.Max(0, platformBytes);
        RendererOwnedAllocations[aggregateIndex] = Math.Max(0, work.AllocatedBytes - platformBytes);
    }
}
