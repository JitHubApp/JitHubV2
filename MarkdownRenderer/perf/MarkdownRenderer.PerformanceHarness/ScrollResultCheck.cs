using System.Diagnostics;
using static MarkdownRenderer.PerformanceHarness.EvidenceNumbers;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// The harness's self-check of its own warm-scroll result before a release
/// report is written: raw evidence, trial completeness, regression
/// estimators, frozen budgets, and the gates themselves.
/// </summary>
internal static class ScrollResultCheck
{
    internal static bool IsCompleteAndPassing(PerformanceReport report)
    {
        ScrollResult scroll = report.Scroll;
        int framesPerTrial = report.SampleRequirements.ScrollFramesRequired;
        int requiredTrials = report.SampleRequirements.ScrollTrialsRequired;
        int expectedTotalFrames = checked(framesPerTrial * requiredTrials);
        bool rawEvidenceValid = ScrollEvidenceValidator.TryValidate(
            scroll,
            framesPerTrial,
            requiredTrials,
            out _);
        bool metricsPresent =
            scroll.Corpus == PerformanceDocumentFactory.WarmScrollStressCorpus &&
            scroll.SourceUtf16Bytes == 1024 * 1024 &&
            scroll.StopwatchFrequency == Stopwatch.Frequency &&
            scroll.RequestedFrames == expectedTotalFrames &&
            scroll.MeasuredFrameIntervals == expectedTotalFrames &&
            scroll.FramesWithRendererWork == expectedTotalFrames &&
            rawEvidenceValid &&
            AreTrialsComplete(scroll, framesPerTrial, requiredTrials) &&
            AreEstimatorsValid(scroll) &&
            AreAggregatesPresent(scroll);
        return metricsPresent && DoGatesPass(scroll) && scroll.Passed;
    }

    private static bool AreTrialsComplete(
        ScrollResult scroll,
        int framesPerTrial,
        int requiredTrials)
    {
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

        return trialsOrdered &&
            trialsDisjoint &&
            scroll.Trials.All(trial => IsTrialComplete(trial, framesPerTrial));
    }

    private static bool IsTrialComplete(ScrollTrialResult trial, int framesPerTrial)
        => trial.RequestedFrames == framesPerTrial &&
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
           trial.Complete;

    private static bool AreEstimatorsValid(ScrollResult scroll)
        => scroll.RegressionEstimator == PerformanceMeasurementContract.ScrollRegressionEstimator &&
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

    private static bool AreAggregatesPresent(ScrollResult scroll)
        => IsFiniteNonNegative(scroll.UiThreadWorkP95Milliseconds) &&
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

    private static bool DoGatesPass(ScrollResult scroll)
        => scroll.UiThreadWorkP95Milliseconds <= scroll.UiThreadWorkP95BudgetMilliseconds &&
           scroll.UiThreadWorkP99Milliseconds <= scroll.UiThreadWorkP99BudgetMilliseconds &&
           scroll.FrameTimeP95Milliseconds <= scroll.FrameTimeP95BudgetMilliseconds &&
           scroll.FramesOver16_7Percent < scroll.FramesOver16_7PercentBudget &&
           scroll.MaximumFrameStallMilliseconds <= scroll.MaximumFrameStallBudgetMilliseconds &&
           scroll.MaximumRendererOwnedAllocatedBytesPerFrame <= scroll.AllocationBudgetBytesPerFrame &&
           !scroll.RendererOwnedLohAllocationPossible &&
           scroll.MaximumOffscreenRealizedCodeActions == 0 &&
           scroll.Gen2Collections == 0;
}
