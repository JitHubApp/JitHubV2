using System.Diagnostics;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Measures document-range to source-span lookups in a 100,000-entry source
/// map on a pinned, highest-priority thread: one timed pass of absolute
/// per-query samples, then batched regression trials with per-trial thread
/// CPU, cycle, collection and allocation evidence.
/// </summary>
internal sealed class SourceLookupMeasurement
{
    private const int MappingCount = PerformanceMeasurementContract.SourceLookupMappingCount;
    private const int QueryCount = PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount;
    private const int RegressionWarmupPasses =
        PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses;
    private const int RegressionTrialCount =
        PerformanceMeasurementContract.SourceLookupRegressionTrialCount;
    private const int RegressionBatchSize =
        PerformanceMeasurementContract.SourceLookupRegressionBatchSize;
    private const int RegressionObservationCount =
        PerformanceMeasurementContract.SourceLookupRegressionObservationCount;
    private const int RegressionOperationCount =
        PerformanceMeasurementContract.SourceLookupRegressionOperationCount;

    private readonly MonotonicUtcClock _utcClock;

    internal SourceLookupMeasurement(MonotonicUtcClock utcClock)
        => _utcClock = utcClock;

    /// <summary>
    /// Runs the warmup passes, the absolute pass and the regression trials.
    /// </summary>
    /// <remarks>
    /// This method stays whole because its timed loops must share one frame:
    /// the map, query array and checksum stay locals of this method, the
    /// pinned-thread scope brackets every pass, and no call, closure or
    /// allocation boundary is introduced between the paired timestamp,
    /// thread-CPU, cycle, collection and allocation reads around each timed
    /// region. Setup and result construction are separate steps.
    /// </remarks>
    internal SourceLookupResult Measure()
    {
        MarkdownSourceMap map = CreateSourceMap();
        DocumentRange[] queries = CreateQueries();

        using PinnedMeasurementThreadScope measurementScope =
            PinnedMeasurementThreadScope.Enter(
                ThreadPriority.Highest,
                "source-lookup");
        int checksum = 0;
        for (int pass = 0; pass < RegressionWarmupPasses; pass++)
        {
            for (int index = 0; index < queries.Length; index++)
            {
                if (map.TryMapRange(queries[index], out SourceSpan span))
                    checksum ^= span.Start;
            }
        }

        var elapsed = new long[QueryCount];
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < QueryCount; index++)
        {
            long started = Stopwatch.GetTimestamp();
            if (map.TryMapRange(queries[index], out SourceSpan span))
                checksum ^= span.Start;
            elapsed[index] = Stopwatch.GetTimestamp() - started;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        double nanosecondsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
        var regressionTrials = new List<SourceLookupTrialResult>(RegressionTrialCount);
        long regressionAllocated = 0;
        for (int trialOrdinal = 0; trialOrdinal < RegressionTrialCount; trialOrdinal++)
        {
            var trialElapsed = new long[RegressionObservationCount];
            DateTimeOffset trialStartedUtc = _utcClock.GetUtcNow();
            long trialStartedTimestamp = Stopwatch.GetTimestamp();
            long threadCpuStarted = ThreadCpuClock.ReadCurrentThreadTicks();
            bool hasThreadCyclesAtStart = measurementScope.TryGetThreadCycleCount(
                out ulong threadCyclesStarted);
            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);
            long trialAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int observation = 0; observation < RegressionObservationCount; observation++)
            {
                int first = observation * RegressionBatchSize;
                long started = Stopwatch.GetTimestamp();
                for (int offset = 0; offset < RegressionBatchSize; offset++)
                {
                    if (map.TryMapRange(queries[first + offset], out SourceSpan span))
                        checksum ^= span.Start;
                }
                trialElapsed[observation] = Stopwatch.GetTimestamp() - started;
            }
            long trialAllocated =
                GC.GetAllocatedBytesForCurrentThread() - trialAllocatedBefore;
            long trialCompletedTimestamp = Stopwatch.GetTimestamp();
            DateTimeOffset trialCompletedUtc = _utcClock.GetUtcNow();
            long threadCpuCompleted = ThreadCpuClock.ReadCurrentThreadTicks();
            bool hasThreadCyclesAtEnd = measurementScope.TryGetThreadCycleCount(
                out ulong threadCyclesCompleted);
            long threadCpuElapsed = threadCpuStarted >= 0 &&
                threadCpuCompleted >= threadCpuStarted
                    ? threadCpuCompleted - threadCpuStarted
                    : -1;
            ulong? threadCycleDelta = hasThreadCyclesAtStart &&
                hasThreadCyclesAtEnd &&
                threadCyclesCompleted >= threadCyclesStarted
                    ? threadCyclesCompleted - threadCyclesStarted
                    : null;
            int gen0Collections = GC.CollectionCount(0) - gen0Before;
            int gen1Collections = GC.CollectionCount(1) - gen1Before;
            int gen2Collections = GC.CollectionCount(2) - gen2Before;
            regressionAllocated = checked(regressionAllocated + trialAllocated);
            double[] trialNanosecondsPerLookup = trialElapsed
                .Select(value => value * nanosecondsPerTick / RegressionBatchSize)
                .ToArray();
            double trialP95 = PerformanceStatistics.Percentile(
                trialNanosecondsPerLookup,
                0.95);
            regressionTrials.Add(new SourceLookupTrialResult
            {
                Ordinal = trialOrdinal,
                ObservationCount = RegressionObservationCount,
                OperationCount = RegressionOperationCount,
                SamplesElapsedTicks = trialElapsed.ToList(),
                SamplesNanosecondsPerLookup = trialNanosecondsPerLookup.ToList(),
                P95Nanoseconds = trialP95,
                AllocatedBytes = trialAllocated,
                StartedUtc = trialStartedUtc,
                CompletedUtc = trialCompletedUtc,
                ElapsedStopwatchTicks = trialCompletedTimestamp - trialStartedTimestamp,
                ThreadCpuTime100NsTicks = threadCpuElapsed,
                ThreadCycleDelta = threadCycleDelta,
                Gen0Collections = gen0Collections,
                Gen1Collections = gen1Collections,
                Gen2Collections = gen2Collections,
                Complete = trialElapsed.All(static value => value > 0) &&
                           trialNanosecondsPerLookup.All(
                               static value => double.IsFinite(value) && value > 0) &&
                           trialAllocated == 0,
            });
        }
        GC.KeepAlive(checksum);

        return CreateResult(
            measurementScope,
            elapsed,
            allocated,
            nanosecondsPerTick,
            regressionTrials,
            regressionAllocated);
    }

    private static MarkdownSourceMap CreateSourceMap()
    {
        var map = new MarkdownSourceMap(new string('x', MappingCount * 4));
        for (int index = 0; index < MappingCount; index++)
            map.Add(index + 1, 0, 3, new SourceSpan(index * 4, 3));
        return map;
    }

    private static DocumentRange[] CreateQueries()
    {
        var queries = new DocumentRange[RegressionOperationCount];
        var random = new Random(0x4D_44);
        for (int index = 0; index < queries.Length; index++)
        {
            int block = random.Next(1, MappingCount + 1);
            queries[index] = new DocumentRange(
                new DocumentPosition(block, 0, 0),
                new DocumentPosition(block, 0, 3));
        }

        return queries;
    }

    private static SourceLookupResult CreateResult(
        PinnedMeasurementThreadScope measurementScope,
        long[] elapsed,
        long allocated,
        double nanosecondsPerTick,
        List<SourceLookupTrialResult> regressionTrials,
        long regressionAllocated)
    {
        double p95Nanoseconds =
            PerformanceStatistics.Percentile(elapsed, 0.95) * nanosecondsPerTick;
        double regressionP95Nanoseconds = PerformanceStatistics.HodgesLehmann(
            regressionTrials.Select(static trial => trial.P95Nanoseconds));
        double regressionDispersion = PerformanceStatistics.RobustDispersionPercent(
            regressionTrials.Select(static trial => trial.P95Nanoseconds));
        return new SourceLookupResult
        {
            MappingCount = MappingCount,
            QueryCount = QueryCount,
            StopwatchFrequency = Stopwatch.Frequency,
            MeasurementThreadAffinityMask = measurementScope.AffinityMask,
            MeasurementProcessorGroup = measurementScope.ProcessorGroup,
            MeasurementProcessorNumber = measurementScope.ProcessorNumber,
            MeasurementThreadPriority = measurementScope.ThreadPriority,
            SamplesElapsedTicks = elapsed.ToList(),
            P95Nanoseconds = p95Nanoseconds,
            AllocatedBytes = allocated,
            RegressionWarmupPasses = RegressionWarmupPasses,
            RegressionBatchSize = RegressionBatchSize,
            RegressionObservationCount = RegressionObservationCount,
            RegressionOperationCount = RegressionOperationCount,
            RegressionEstimator = PerformanceMeasurementContract.SourceLookupRegressionEstimator,
            RegressionTrialCount = RegressionTrialCount,
            RegressionTrials = regressionTrials,
            RegressionP95Nanoseconds = regressionP95Nanoseconds,
            RegressionAllocatedBytes = regressionAllocated,
            Passed = p95Nanoseconds <= 25_000 &&
                     allocated == 0 &&
                     regressionTrials.Count == RegressionTrialCount &&
                     regressionTrials.All(static trial => trial.Complete) &&
                     double.IsFinite(regressionP95Nanoseconds) &&
                     regressionP95Nanoseconds > 0 &&
                     double.IsFinite(regressionDispersion) &&
                     regressionDispersion <=
                         PerformanceMeasurementContract.SourceLookupDispersionBudgetPercent &&
                     regressionAllocated == 0,
        };
    }
}
