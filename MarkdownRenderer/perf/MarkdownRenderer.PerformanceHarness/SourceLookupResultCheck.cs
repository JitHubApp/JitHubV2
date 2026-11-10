using System.Diagnostics;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Measurement;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;
using static MarkdownRenderer.PerformanceHarness.EvidenceNumbers;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// The harness's self-check of its own source-lookup result before a release
/// report is written: the pinned-thread evidence, the absolute samples and
/// their p95, and every regression trial recomputed from its raw ticks.
/// </summary>
internal static class SourceLookupResultCheck
{
    internal static bool IsCompleteAndPassing(PerformanceReport report)
    {
        SourceLookupResult lookup = report.SourceLookup;
        SampleRequirements requirements = report.SampleRequirements;
        double nanosecondsPerTick = 1_000_000_000.0 / lookup.StopwatchFrequency;
        bool trialsComplete = AreTrialsComplete(lookup, nanosecondsPerTick);
        long measuredRegressionAllocations = lookup.RegressionTrials.Sum(
            static trial => trial.AllocatedBytes);
        return lookup.MappingCount == requirements.SourceLookupMappingCountRequired &&
               lookup.QueryCount == requirements.SourceLookupAbsoluteQueryCountRequired &&
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
                   requirements.SourceLookupRegressionWarmupPassesRequired &&
               lookup.RegressionEstimator ==
                   PerformanceMeasurementContract.SourceLookupRegressionEstimator &&
               lookup.RegressionTrialCount ==
                   requirements.SourceLookupRegressionTrialsRequired &&
               lookup.RegressionBatchSize ==
                   requirements.SourceLookupRegressionBatchSizeRequired &&
               lookup.RegressionObservationCount ==
                   requirements.SourceLookupRegressionObservationCountRequired &&
               lookup.RegressionOperationCount ==
                   requirements.SourceLookupRegressionOperationCountRequired &&
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

    private static bool AreTrialsComplete(SourceLookupResult lookup, double nanosecondsPerTick)
    {
        bool trialsOrdered = lookup.RegressionTrials.Count == lookup.RegressionTrialCount &&
                             lookup.RegressionTrials
                                 .Select(static trial => trial.Ordinal)
                                 .SequenceEqual(Enumerable.Range(0, lookup.RegressionTrialCount));
        return trialsOrdered && lookup.RegressionTrials.All(trial =>
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
    }
}
