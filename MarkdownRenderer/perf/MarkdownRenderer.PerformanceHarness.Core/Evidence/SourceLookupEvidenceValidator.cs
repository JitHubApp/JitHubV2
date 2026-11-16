using System.Diagnostics;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Measurement;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;
using static MarkdownRenderer.PerformanceHarness.Core.Evidence.EvidenceValueRules;

namespace MarkdownRenderer.PerformanceHarness.Core.Evidence;

/// <summary>
/// Checks source-lookup evidence: the pinned measurement thread, the
/// absolute query population and budget, and every regression trial
/// recomputed from its serialized batch timings.
/// </summary>
public static class SourceLookupEvidenceValidator
{
    public static bool HasSameExecutionEnvironment(
        SourceLookupResult candidate,
        SourceLookupResult reference)
        => PinnedMeasurementThreadScope.IsEvidenceValid(
               candidate.MeasurementThreadAffinityMask,
               candidate.MeasurementProcessorGroup,
               candidate.MeasurementProcessorNumber,
               candidate.MeasurementThreadPriority,
               ThreadPriority.Highest) &&
           PinnedMeasurementThreadScope.IsEvidenceValid(
               reference.MeasurementThreadAffinityMask,
               reference.MeasurementProcessorGroup,
               reference.MeasurementProcessorNumber,
               reference.MeasurementThreadPriority,
               ThreadPriority.Highest) &&
           string.Equals(
               candidate.MeasurementThreadAffinityMask,
               reference.MeasurementThreadAffinityMask,
               StringComparison.Ordinal) &&
           candidate.MeasurementProcessorGroup == reference.MeasurementProcessorGroup &&
           candidate.MeasurementProcessorNumber == reference.MeasurementProcessorNumber &&
           string.Equals(
               candidate.MeasurementThreadPriority,
               reference.MeasurementThreadPriority,
               StringComparison.Ordinal);

    internal static bool HasCompleteReleaseEvidence(SourceLookupResult lookup)
    {
        if (!HasAbsoluteQueryEvidence(lookup))
            return false;

        double nanosecondsPerTick = 1_000_000_000.0 / lookup.StopwatchFrequency;
        foreach (SourceLookupTrialResult trial in lookup.RegressionTrials)
        {
            if (!IsCompleteRegressionTrial(trial, nanosecondsPerTick))
                return false;
        }

        return lookup.RegressionAllocatedBytes == 0 &&
               NearlyEqual(
                   lookup.RegressionP95Nanoseconds,
                   PerformanceStatistics.HodgesLehmann(
                       lookup.RegressionTrials.Select(
                           static trial => trial.P95Nanoseconds))) &&
               PerformanceStatistics.RobustDispersionPercent(
                   lookup.RegressionTrials.Select(static trial => trial.P95Nanoseconds)) <=
                   PerformanceMeasurementContract.SourceLookupDispersionBudgetPercent &&
               lookup.Passed;
    }

    private static bool HasAbsoluteQueryEvidence(SourceLookupResult lookup)
        => !(lookup.MappingCount != PerformanceMeasurementContract.SourceLookupMappingCount ||
            lookup.QueryCount != PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount ||
            lookup.StopwatchFrequency != Stopwatch.Frequency ||
            !PinnedMeasurementThreadScope.IsEvidenceValid(
                lookup.MeasurementThreadAffinityMask,
                lookup.MeasurementProcessorGroup,
                lookup.MeasurementProcessorNumber,
                lookup.MeasurementThreadPriority,
                ThreadPriority.Highest) ||
            lookup.SamplesElapsedTicks.Count !=
                PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount ||
            lookup.SamplesElapsedTicks.Any(static ticks => ticks < 0) ||
            !IsFinitePositive(lookup.P95Nanoseconds) ||
            !NearlyEqual(
                lookup.P95Nanoseconds,
                PerformanceStatistics.Percentile(lookup.SamplesElapsedTicks, 0.95) *
                (1_000_000_000.0 / lookup.StopwatchFrequency)) ||
            lookup.P95BudgetNanoseconds != 25_000 ||
            lookup.P95Nanoseconds > lookup.P95BudgetNanoseconds ||
            lookup.AllocatedBytes != 0 ||
            lookup.RegressionWarmupPasses !=
                PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses ||
            lookup.RegressionEstimator !=
                PerformanceMeasurementContract.SourceLookupRegressionEstimator ||
            lookup.RegressionTrialCount !=
                PerformanceMeasurementContract.SourceLookupRegressionTrialCount ||
            lookup.RegressionBatchSize !=
                PerformanceMeasurementContract.SourceLookupRegressionBatchSize ||
            lookup.RegressionObservationCount !=
                PerformanceMeasurementContract.SourceLookupRegressionObservationCount ||
            lookup.RegressionOperationCount !=
                PerformanceMeasurementContract.SourceLookupRegressionOperationCount ||
            lookup.RegressionTrials.Count !=
                PerformanceMeasurementContract.SourceLookupRegressionTrialCount ||
            !lookup.RegressionTrials.Select(static trial => trial.Ordinal).SequenceEqual(
                Enumerable.Range(
                    0,
                    PerformanceMeasurementContract.SourceLookupRegressionTrialCount)));

    private static bool IsCompleteRegressionTrial(
        SourceLookupTrialResult trial,
        double nanosecondsPerTick)
        => trial.ObservationCount ==
               PerformanceMeasurementContract.SourceLookupRegressionObservationCount &&
           trial.OperationCount ==
               PerformanceMeasurementContract.SourceLookupRegressionOperationCount &&
           trial.SamplesElapsedTicks.Count == trial.ObservationCount &&
           trial.SamplesNanosecondsPerLookup.Count == trial.ObservationCount &&
           trial.SamplesElapsedTicks.Zip(
                   trial.SamplesNanosecondsPerLookup,
                   (ticks, nanoseconds) =>
                       ticks > 0 &&
                       IsFinitePositive(nanoseconds) &&
                       NearlyEqual(
                           nanoseconds,
                           ticks * nanosecondsPerTick /
                           PerformanceMeasurementContract.SourceLookupRegressionBatchSize))
               .All(static matches => matches) &&
           NearlyEqual(
               trial.P95Nanoseconds,
               PerformanceStatistics.Percentile(
                   trial.SamplesNanosecondsPerLookup,
                   0.95)) &&
           trial.AllocatedBytes == 0 &&
           trial.Complete;
}
