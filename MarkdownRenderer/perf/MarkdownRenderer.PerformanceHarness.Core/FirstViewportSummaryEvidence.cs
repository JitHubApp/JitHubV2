using static MarkdownRenderer.PerformanceHarness.EvidenceValueRules;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Recomputes a first-viewport result's pooled and regression statistics,
/// publication maximum, stationarity and pass decision from its measured
/// trials, and checks that the serialized summary matches.
/// </summary>
internal static class FirstViewportSummaryEvidence
{
    internal static bool HasRecomputedSummary(
        FirstViewportResult result,
        int sourceBytes,
        string mode,
        int iterationsPerTrial,
        int measuredTrialCount,
        bool quick)
    {
        double expectedBudget = GetFirstViewportBudgetMilliseconds(sourceBytes, mode);
        double[] flattenedSamples = result.Trials
            .SelectMany(static trial => trial.SamplesMilliseconds)
            .ToArray();
        double[] publicationSamples = result.Trials
            .SelectMany(static trial => trial.PublicationSamplesMilliseconds)
            .ToArray();
        double publicationMaximum = publicationSamples.Length == 0
            ? double.NaN
            : publicationSamples.Max();
        double pooledP95 = PerformanceStatistics.Percentile(flattenedSamples, 0.95);
        double regressionP95 = PerformanceStatistics.HodgesLehmann(
            result.Trials.Select(static trial => trial.P95Milliseconds));
        Stationarity stationarity = RecomputeStationarity(
            result,
            sourceBytes,
            mode,
            regressionP95,
            quick);
        bool populationComplete =
            result.WarmupTrials.All(static trial => trial.Complete) &&
            result.Trials.All(static trial => trial.Complete) &&
            flattenedSamples.Length == checked(iterationsPerTrial * measuredTrialCount) &&
            publicationSamples.Length == checked(iterationsPerTrial * measuredTrialCount) &&
            flattenedSamples.All(IsFiniteNonNegative) &&
            publicationSamples.All(IsFiniteNonNegative) &&
            IsFiniteNonNegative(pooledP95) &&
            IsFiniteNonNegative(publicationMaximum) &&
            IsFiniteNonNegative(regressionP95);
        bool expectedPassed = populationComplete &&
            pooledP95 <= expectedBudget &&
            (quick || publicationMaximum <=
                PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds) &&
            (quick || stationarity.Passed);

        return result.SamplesMilliseconds.Count == flattenedSamples.Length &&
            result.SamplesMilliseconds.SequenceEqual(flattenedSamples) &&
            result.PublicationSamplesMilliseconds.Count == publicationSamples.Length &&
            result.PublicationSamplesMilliseconds.SequenceEqual(publicationSamples) &&
            NearlyEqual(result.PublicationMaximumMilliseconds, publicationMaximum) &&
            result.PublicationBudgetMilliseconds ==
                PerformanceMeasurementContract.UiPublicationMaximumBudgetMilliseconds &&
            NearlyEqual(result.P95Milliseconds, pooledP95) &&
            NearlyEqual(result.RegressionP95Milliseconds, regressionP95) &&
            NearlyEqual(result.RegressionDispersionPercent, stationarity.Dispersion) &&
            NearlyEqual(result.TheilSenSlopeMillisecondsPerGlobalOrdinal, stationarity.Slope) &&
            NearlyEqual(result.ProjectedDriftMilliseconds, stationarity.ProjectedDrift) &&
            NearlyEqual(result.WarmupBoundaryShiftMilliseconds, stationarity.BoundaryShift) &&
            NearlyEqual(result.StationarityAllowanceMilliseconds, stationarity.Allowance) &&
            result.StationarityEvaluated == !quick &&
            result.StationarityPassed == stationarity.Passed &&
            result.BudgetMilliseconds == expectedBudget &&
            result.Passed == expectedPassed &&
            (quick || expectedPassed);
    }

    private static Stationarity RecomputeStationarity(
        FirstViewportResult result,
        int sourceBytes,
        string mode,
        double regressionP95,
        bool quick)
    {
        double dispersion = PerformanceStatistics.RobustDispersionPercent(
            result.Trials.Select(static trial => trial.P95Milliseconds));
        double allowance = Math.Max(
            PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
                mode,
                sourceBytes),
            Math.Abs(regressionP95) *
                PerformanceMeasurementContract.FirstViewportStationarityRelativeAllowancePercent /
                100d);
        double slope = quick
            ? 0
            : PerformanceStatistics.TheilSenSlope(
                result.Trials.Select(static trial =>
                    (trial.GlobalOrdinal, trial.P95Milliseconds)));
        double projectedDrift = quick
            ? 0
            : PerformanceStatistics.ProjectedTheilSenDrift(
                result.Trials.Select(static trial =>
                    (trial.GlobalOrdinal, trial.P95Milliseconds)));
        double boundaryShift = quick
            ? 0
            : Math.Abs(
                PerformanceStatistics.TwoValueCenter(
                    result.WarmupTrials[^2].P95Milliseconds,
                    result.WarmupTrials[^1].P95Milliseconds) -
                PerformanceStatistics.TwoValueCenter(
                    result.Trials[0].P95Milliseconds,
                    result.Trials[1].P95Milliseconds));
        bool residualEnvelopePassed = quick ||
            result.Trials.All(trial =>
                Math.Abs(trial.P95Milliseconds - regressionP95) <= allowance) &&
            result.WarmupTrials.TakeLast(2)
                .Concat(result.Trials.Take(2))
                .All(trial =>
                    Math.Abs(trial.P95Milliseconds - regressionP95) <= allowance);
        bool passed = !quick &&
            double.IsFinite(dispersion) &&
            dispersion <= PerformanceMeasurementContract.FirstViewportDispersionBudgetPercent &&
            double.IsFinite(slope) &&
            double.IsFinite(projectedDrift) &&
            Math.Abs(projectedDrift) <= allowance &&
            double.IsFinite(boundaryShift) &&
            boundaryShift <= allowance &&
            residualEnvelopePassed;
        return new Stationarity(dispersion, allowance, slope, projectedDrift, boundaryShift, passed);
    }

    private static double GetFirstViewportBudgetMilliseconds(int sourceBytes, string mode)
        => sourceBytes switch
        {
            100 * 1024 when mode == PerformanceMeasurementContract.FirstViewportCacheHitMode => 75,
            100 * 1024 => 125,
            1024 * 1024 => 250,
            10 * 1024 * 1024 => 750,
            _ => double.NaN,
        };

    private readonly record struct Stationarity(
        double Dispersion,
        double Allowance,
        double Slope,
        double ProjectedDrift,
        double BoundaryShift,
        bool Passed);
}
