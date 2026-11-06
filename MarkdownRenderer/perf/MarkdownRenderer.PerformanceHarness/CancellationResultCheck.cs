namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// The harness's self-check of its own cancellation result before a release
/// report is written: the raw evidence, the frozen policies, and the absolute
/// and stale-commit gates for every trial.
/// </summary>
internal static class CancellationResultCheck
{
    internal static bool IsCompleteAndPassing(PerformanceReport report)
    {
        CancellationResult cancellation = report.Cancellation;
        int trialsRequired = report.SampleRequirements.CancellationTrialsRequired;
        int iterationsPerTrial =
            report.SampleRequirements.CancellationIterationsPerTrialRequired;
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
}
