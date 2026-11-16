using System.Diagnostics;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Scenarios;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;
using static MarkdownRenderer.PerformanceHarness.Core.Evidence.EvidenceValueRules;

namespace MarkdownRenderer.PerformanceHarness.Core.Evidence;

/// <summary>
/// Checks warm-scroll release evidence: the recomputed scroll statistics,
/// the frozen allocation scopes and diagnostic populations, trial
/// dispersion, and the release budgets.
/// </summary>
internal static class ScrollReleaseEvidenceValidator
{
    private const string RawAllocationScope =
        "all managed allocations observed on the UI thread inside renderer scroll and paint callbacks";
    private const string AllocationGateBasis = "renderer-owned-managed";
    private const string PlatformProjectionAllocationScope =
        "managed projection allocations measured around WinUI and Win2D calls; retained separately from renderer-owned allocations";

    internal static bool HasCompleteReleaseEvidence(ScrollResult scroll)
    {
        if (scroll.RegressionEstimator != PerformanceMeasurementContract.ScrollRegressionEstimator ||
            !ScrollEvidenceValidator.TryValidate(
                scroll,
                PerformanceMeasurementContract.ReleaseScrollFrames,
                PerformanceMeasurementContract.ReleaseScrollTrials,
                out ScrollEvidenceStatistics statistics))
        {
            return false;
        }

        return HasValidDiagnostics(scroll) &&
               IsStableAndWithinBudgets(scroll, statistics);
    }

    private static bool HasValidDiagnostics(ScrollResult scroll)
        => scroll.Corpus == PerformanceDocumentFactory.WarmScrollStressCorpus &&
           scroll.SourceUtf16Bytes == 1024 * 1024 &&
           scroll.StopwatchFrequency == Stopwatch.Frequency &&
           scroll.RawAllocationScope == RawAllocationScope &&
           scroll.AllocationGateBasis == AllocationGateBasis &&
           scroll.PlatformProjectionAllocationScope == PlatformProjectionAllocationScope &&
           AllFiniteNonNegative(
               scroll.ScrollCallbackWorkP95Milliseconds,
               scroll.PaintCallbackWorkP95Milliseconds,
               scroll.AllocatedBytesPerFrameP95,
               scroll.PlatformProjectionAllocatedBytesPerFrameP95,
               scroll.ScrollAllocatedBytesPerFrameP95,
               scroll.PaintAllocatedBytesPerFrameP95,
               scroll.ScrollLazyLayoutAllocatedBytesP95,
               scroll.ScrollAdornerAllocatedBytesP95,
               scroll.ScrollRealizationAllocatedBytesP95,
               scroll.ScrollHighlightingAllocatedBytesP95,
               scroll.PaintSchedulingAllocatedBytesP95,
               scroll.PaintPlatformSessionAllocatedBytesP95,
               scroll.SnapshotPaintAllocatedBytesP95,
               scroll.InteractivePaintAllocatedBytesP95,
               scroll.InlinePaintAllocatedBytesP95,
               scroll.TablePaintAllocatedBytesP95,
               scroll.CodePaintAllocatedBytesP95,
               scroll.OtherPaintAllocatedBytesP95) &&
           scroll.MaximumAllocatedBytesPerFrame >= 0 &&
           scroll.MaximumPlatformProjectionAllocatedBytesPerFrame >= 0 &&
           scroll.MaximumRealizedCodeActions >= 0 &&
           scroll.MaximumOffscreenRealizedCodeActions == 0 &&
           scroll.Gen2Collections == 0 &&
           scroll.Trials.All(static trial =>
               AllFiniteNonNegative(
                   trial.ScrollCallbackWorkP95Milliseconds,
                   trial.PaintCallbackWorkP95Milliseconds,
                   trial.AllocatedBytesPerFrameP95,
                   trial.PlatformProjectionAllocatedBytesPerFrameP95,
                   trial.ScrollAllocatedBytesPerFrameP95,
                   trial.PaintAllocatedBytesPerFrameP95,
                   trial.ScrollLazyLayoutAllocatedBytesP95,
                   trial.ScrollAdornerAllocatedBytesP95,
                   trial.ScrollRealizationAllocatedBytesP95,
                   trial.ScrollHighlightingAllocatedBytesP95,
                   trial.PaintSchedulingAllocatedBytesP95,
                   trial.PaintPlatformSessionAllocatedBytesP95,
                   trial.SnapshotPaintAllocatedBytesP95,
                   trial.InteractivePaintAllocatedBytesP95,
                   trial.InlinePaintAllocatedBytesP95,
                   trial.TablePaintAllocatedBytesP95,
                   trial.CodePaintAllocatedBytesP95,
                   trial.OtherPaintAllocatedBytesP95) &&
               trial.MaximumAllocatedBytesPerFrame >= 0 &&
               trial.MaximumPlatformProjectionAllocatedBytesPerFrame >= 0 &&
               trial.MaximumRealizedCodeActions >= 0 &&
               trial.MaximumOffscreenRealizedCodeActions == 0 &&
               trial.Gen2Collections == 0);

    private static bool IsStableAndWithinBudgets(
        ScrollResult scroll,
        ScrollEvidenceStatistics statistics)
        => PerformanceStatistics.RobustDispersionPercent(
               statistics.Trials.Select(static trial => trial.UiThreadWorkP95Milliseconds)) <=
               PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent &&
           PerformanceStatistics.RobustDispersionPercent(
               statistics.Trials.Select(static trial => trial.UiThreadWorkP99Milliseconds)) <=
               PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent &&
           PerformanceStatistics.RobustDispersionPercent(
               statistics.Trials.Select(static trial => trial.FrameTimeP95Milliseconds)) <=
               PerformanceMeasurementContract.ScrollFrameTimeDispersionBudgetPercent &&
           PerformanceStatistics.RobustDispersionPercent(
               statistics.Trials.Select(
                   static trial => trial.RendererOwnedAllocatedBytesPerFrameP95)) <=
               PerformanceMeasurementContract.AllocationDispersionBudgetPercent &&
           scroll.UiThreadWorkP95BudgetMilliseconds == 4 &&
           scroll.UiThreadWorkP99BudgetMilliseconds == 6 &&
           scroll.FrameTimeP95BudgetMilliseconds ==
               PerformanceMeasurementContract.ScrollFrameTimeP95BudgetMilliseconds &&
           scroll.FramesOver16_7PercentBudget == 1 &&
           scroll.MaximumFrameStallBudgetMilliseconds == 50 &&
           scroll.AllocationBudgetBytesPerFrame == 4096 &&
           statistics.UiThreadWorkP95Milliseconds <=
               scroll.UiThreadWorkP95BudgetMilliseconds &&
           statistics.UiThreadWorkP99Milliseconds <=
               scroll.UiThreadWorkP99BudgetMilliseconds &&
           statistics.FrameTimeP95Milliseconds <=
               scroll.FrameTimeP95BudgetMilliseconds &&
           statistics.FramesOver16_7Percent < scroll.FramesOver16_7PercentBudget &&
           statistics.MaximumFrameStallMilliseconds <=
               scroll.MaximumFrameStallBudgetMilliseconds &&
           statistics.MaximumRendererOwnedAllocatedBytesPerFrame <=
               scroll.AllocationBudgetBytesPerFrame &&
           !statistics.RendererOwnedLohAllocationPossible &&
           scroll.Passed;
}
