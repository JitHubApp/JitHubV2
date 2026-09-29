namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Recomputes cancellation evidence from its ordered trial populations. This
/// validator intentionally separates structural integrity from release budgets
/// so the one-sample quick smoke can validate shape without becoming a gate.
/// </summary>
internal static class CancellationEvidenceValidator
{
    internal static bool TryValidate(
        CancellationResult result,
        int iterationsPerTrial,
        int requiredTrials,
        out CancellationEvidenceStatistics statistics)
    {
        statistics = null!;
        if (iterationsPerTrial <= 0 || requiredTrials <= 0)
            return false;

        int totalIterations;
        try
        {
            totalIterations = checked(iterationsPerTrial * requiredTrials);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (result.IterationsPerTrial != iterationsPerTrial ||
            result.RequestedIterations != totalIterations ||
            result.ObservedCancellations != totalIterations ||
            result.WarmupIterations != requiredTrials ||
            result.Trials.Count != requiredTrials ||
            result.SamplesMilliseconds.Count != totalIterations)
        {
            return false;
        }

        var flattenedSamples = new List<double>(totalIterations);
        var trialP95Values = new List<double>(requiredTrials);
        var warmupSamples = new List<double>(requiredTrials);
        long warmupStaleCommits = 0;
        long staleCommits = 0;

        for (int ordinal = 0; ordinal < requiredTrials; ordinal++)
        {
            CancellationTrialResult trial = result.Trials[ordinal];
            IReadOnlyList<CancellationMeasurementIteration> expectedPlan;
            try
            {
                expectedPlan = CancellationMeasurementPlan.CreateTrial(
                    ordinal,
                    iterationsPerTrial);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }

            if (trial.Ordinal != ordinal ||
                trial.WarmupSourceSeed != expectedPlan[0].SourceSeed ||
                trial.FirstRecordedSourceSeed != expectedPlan[1].SourceSeed ||
                trial.RequestedIterations != iterationsPerTrial ||
                trial.ObservedCancellations != iterationsPerTrial ||
                trial.SamplesMilliseconds.Count != iterationsPerTrial ||
                !double.IsFinite(trial.WarmupElapsedMilliseconds) ||
                trial.WarmupElapsedMilliseconds < 0 ||
                trial.WarmupStaleCommits < 0 ||
                trial.SamplesMilliseconds.Any(
                    static value => !double.IsFinite(value) || value < 0) ||
                !double.IsFinite(trial.P95Milliseconds) ||
                trial.P95Milliseconds < 0 ||
                !NearlyEqual(
                    trial.P95Milliseconds,
                    PerformanceStatistics.Percentile(trial.SamplesMilliseconds, 0.95)) ||
                trial.StaleCommits < 0 ||
                !trial.Complete)
            {
                return false;
            }

            warmupSamples.Add(trial.WarmupElapsedMilliseconds);
            warmupStaleCommits += trial.WarmupStaleCommits;
            flattenedSamples.AddRange(trial.SamplesMilliseconds);
            trialP95Values.Add(trial.P95Milliseconds);
            staleCommits += trial.StaleCommits;
        }

        double warmupP95 = PerformanceStatistics.Percentile(warmupSamples, 0.95);
        double pooledP95 = PerformanceStatistics.Percentile(flattenedSamples, 0.95);
        double regressionP95 = PerformanceStatistics.HodgesLehmann(trialP95Values);
        double regressionDispersion =
            PerformanceStatistics.RobustDispersionPercent(trialP95Values);
        if (!result.SamplesMilliseconds.SequenceEqual(flattenedSamples) ||
            result.WarmupStaleCommits != warmupStaleCommits ||
            result.StaleCommits != staleCommits ||
            !NearlyEqual(result.WarmupElapsedMilliseconds, warmupP95) ||
            !NearlyEqual(result.P95Milliseconds, pooledP95) ||
            !NearlyEqual(result.RegressionP95Milliseconds, regressionP95))
        {
            return false;
        }

        statistics = new CancellationEvidenceStatistics(
            warmupP95,
            pooledP95,
            regressionP95,
            regressionDispersion,
            (int)warmupStaleCommits,
            (int)staleCommits);
        return true;
    }

    private static bool NearlyEqual(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right))
            return false;
        double scale = Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= scale * 1e-12;
    }
}

internal sealed record CancellationEvidenceStatistics(
    double WarmupP95Milliseconds,
    double PooledP95Milliseconds,
    double RegressionP95Milliseconds,
    double RegressionDispersionPercent,
    int WarmupStaleCommits,
    int StaleCommits);
