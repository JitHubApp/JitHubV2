using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;


namespace MarkdownRenderer.Tests.TestSupport;

/// <summary>
/// The first-viewport values that test fixtures derive the same way the
/// harness does: the release budget per corpus and mode, the Williams-schedule
/// position of a condition, the warm-up boundary shift and the stationarity
/// allowance.
/// </summary>
internal static class PerformanceFirstViewportExpectations
{
    internal static double GetBudgetMilliseconds(int sourceBytes, string mode)
        => sourceBytes switch
        {
            100 * 1024 when mode == PerformanceMeasurementContract.FirstViewportCacheHitMode => 75,
            100 * 1024 => 125,
            1024 * 1024 => 250,
            10 * 1024 * 1024 => 750,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceBytes), sourceBytes, null),
        };

    internal static int GetSchedulePosition(int condition, int row)
        => Enumerable.Range(0, PerformanceMeasurementContract.FirstViewportConditionCount)
            .Single(position =>
                PerformanceMeasurementContract.GetFirstViewportCondition(row, position) == condition);

    internal static double CalculateWarmupBoundaryShift(
        IReadOnlyList<FirstViewportTrialResult> warmupTrials,
        IReadOnlyList<FirstViewportTrialResult> trials)
        => Math.Abs(
            PerformanceStatistics.TwoValueCenter(
                warmupTrials[^2].P95Milliseconds,
                warmupTrials[^1].P95Milliseconds) -
            PerformanceStatistics.TwoValueCenter(
                trials[0].P95Milliseconds,
                trials[1].P95Milliseconds));

    internal static double CalculateStationarityAllowance(
        string mode,
        int sourceBytes,
        double regressionP95)
        => Math.Max(
            PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
                mode,
                sourceBytes),
            Math.Abs(regressionP95) *
            PerformanceMeasurementContract.FirstViewportStationarityRelativeAllowancePercent /
            100d);
}
