namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// The stationarity evidence for one first-viewport condition: the Theil-Sen
/// drift of its trial p95 values, the shift across the warmup boundary, and
/// whether every trial stayed inside the allowance around the regression
/// estimate. Quick runs do not evaluate stationarity.
/// </summary>
internal readonly record struct FirstViewportStationarity(
    double AllowanceMilliseconds,
    bool Evaluated,
    double TheilSenSlope,
    double ProjectedDriftMilliseconds,
    double WarmupBoundaryShiftMilliseconds,
    bool Passed)
{
    internal static FirstViewportStationarity Evaluate(
        FirstViewportScenario scenario,
        bool quick,
        double regressionP95,
        double regressionDispersion)
    {
        double stationarityAllowance = Math.Max(
            PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
                scenario.Mode,
                scenario.SourceBytes),
            Math.Abs(regressionP95) *
                PerformanceMeasurementContract.FirstViewportStationarityRelativeAllowancePercent /
                100d);
        bool stationarityEvaluated = !quick;
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
        return new FirstViewportStationarity(
            stationarityAllowance,
            stationarityEvaluated,
            theilSenSlope,
            projectedDrift,
            warmupBoundaryShift,
            stationarityPassed);
    }
}
