using System.Diagnostics;
using static MarkdownRenderer.PerformanceHarness.EvidenceValueRules;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Validates the first-usable-viewport protocol: one result per size and
/// cache mode, the frozen Williams schedule across warm-up and measured
/// trials, one pinned measurement thread, and every summary statistic
/// recomputed from the serialized trial samples.
/// </summary>
public static class FirstViewportEvidenceValidator
{
    public static bool HasRequiredFirstViewportProtocol(
        IReadOnlyCollection<FirstViewportResult> results)
        => HasFirstViewportProtocol(
            results,
            PerformanceMeasurementContract.ReleaseFirstViewportIterations,
            PerformanceMeasurementContract.ReleaseFirstViewportWarmupTrials,
            PerformanceMeasurementContract.ReleaseFirstViewportTrials,
            quick: false);

    public static bool HasRequiredQuickFirstViewportProtocol(
        IReadOnlyCollection<FirstViewportResult> results)
        => HasFirstViewportProtocol(
            results,
            PerformanceMeasurementContract.QuickFirstViewportIterations,
            PerformanceMeasurementContract.QuickFirstViewportWarmupTrials,
            PerformanceMeasurementContract.QuickFirstViewportTrials,
            quick: true);

    public static bool HasSameFirstViewportExecutionEnvironment(
        IReadOnlyCollection<FirstViewportResult> candidate,
        IReadOnlyCollection<FirstViewportResult> reference)
    {
        if (!TryGetFirstViewportExecutionEnvironment(candidate, out var candidateEnvironment) ||
            !TryGetFirstViewportExecutionEnvironment(reference, out var referenceEnvironment))
        {
            return false;
        }

        return candidateEnvironment == referenceEnvironment;
    }

    public static bool AreFirstViewportTrialsWithinReportInterval(
        IReadOnlyCollection<FirstViewportResult> results,
        DateTimeOffset reportStartedUtc,
        DateTimeOffset reportCompletedUtc)
    {
        if (results is null ||
            reportStartedUtc == default ||
            reportStartedUtc.Offset != TimeSpan.Zero ||
            reportCompletedUtc.Offset != TimeSpan.Zero ||
            reportCompletedUtc <= reportStartedUtc)
        {
            return false;
        }

        return results.SelectMany(static result =>
                result.WarmupTrials.Concat(result.Trials))
            .All(trial =>
                trial.StartedUtc > reportStartedUtc &&
                trial.CompletedUtc < reportCompletedUtc);
    }

    private static bool HasFirstViewportProtocol(
        IReadOnlyCollection<FirstViewportResult> results,
        int iterationsPerTrial,
        int warmupTrialCount,
        int measuredTrialCount,
        bool quick)
    {
        if (results is null ||
            results.Count != PerformanceMeasurementContract.FirstViewportConditionCount ||
            iterationsPerTrial <= 0 ||
            warmupTrialCount <= 0 ||
            measuredTrialCount <= 0)
        {
            return false;
        }

        int[] requiredSizes = [100 * 1024, 1024 * 1024, 10 * 1024 * 1024];
        string[] requiredModes =
        [
            PerformanceMeasurementContract.FirstViewportCacheDisabledMode,
            PerformanceMeasurementContract.FirstViewportCacheHitMode,
        ];
        var chronologicalTrials = new List<(FirstViewportTrialResult Trial, int Condition)>(
            checked((warmupTrialCount + measuredTrialCount) *
                    PerformanceMeasurementContract.FirstViewportConditionCount));
        var shape = new ProtocolShape(
            iterationsPerTrial,
            warmupTrialCount,
            measuredTrialCount,
            quick);
        MeasurementThread? sharedThread = null;

        int condition = 0;
        foreach (int sourceBytes in requiredSizes)
        {
            foreach (string mode in requiredModes)
            {
                FirstViewportResult[] matches = results.Where(result =>
                        result.SourceUtf16Bytes == sourceBytes && result.Mode == mode)
                    .ToArray();
                if (matches.Length != 1 ||
                    !HasValidCondition(
                        matches[0],
                        sourceBytes,
                        mode,
                        condition,
                        shape,
                        ref sharedThread,
                        chronologicalTrials))
                {
                    return false;
                }

                condition++;
            }
        }

        return HasExactChronologicalFirstViewportSchedule(
            chronologicalTrials,
            warmupTrialCount,
            measuredTrialCount);
    }

    private static bool HasValidCondition(
        FirstViewportResult result,
        int sourceBytes,
        string mode,
        int condition,
        ProtocolShape shape,
        ref MeasurementThread? sharedThread,
        List<(FirstViewportTrialResult Trial, int Condition)> chronologicalTrials)
    {
        int measuredFirstGlobalOrdinal = checked(
            shape.WarmupTrialCount * PerformanceMeasurementContract.FirstViewportConditionCount);
        return HasSharedPinnedThread(result, ref sharedThread) &&
            HasConditionPolicy(result, sourceBytes, mode, shape) &&
            TryAddScheduledTrials(
                result.WarmupTrials,
                mode,
                shape.IterationsPerTrial,
                condition,
                firstGlobalOrdinal: 0,
                chronologicalTrials) &&
            TryAddScheduledTrials(
                result.Trials,
                mode,
                shape.IterationsPerTrial,
                condition,
                measuredFirstGlobalOrdinal,
                chronologicalTrials) &&
            FirstViewportSummaryEvidence.HasRecomputedSummary(
                result,
                sourceBytes,
                mode,
                shape.IterationsPerTrial,
                shape.MeasuredTrialCount,
                shape.Quick);
    }

    private static bool HasSharedPinnedThread(
        FirstViewportResult result,
        ref MeasurementThread? sharedThread)
    {
        if (!PinnedMeasurementThreadScope.IsEvidenceValid(
                result.MeasurementThreadAffinityMask,
                result.MeasurementProcessorGroup,
                result.MeasurementProcessorNumber,
                result.MeasurementThreadPriority,
                ThreadPriority.Normal))
        {
            return false;
        }

        var thread = new MeasurementThread(
            result.MeasurementThreadAffinityMask,
            result.MeasurementProcessorGroup,
            result.MeasurementProcessorNumber,
            result.MeasurementThreadPriority);
        sharedThread ??= thread;
        return sharedThread.Value == thread;
    }

    private static bool HasConditionPolicy(
        FirstViewportResult result,
        int sourceBytes,
        string mode,
        ProtocolShape shape)
    {
        bool quick = shape.Quick;
        long expectedCacheBudget =
            mode == PerformanceMeasurementContract.FirstViewportCacheHitMode
                ? Math.Max(0, sourceBytes * 8L)
                : 0;
        return result.Corpus == PerformanceDocumentFactory.GetCorpusName(sourceBytes) &&
            result.ParseCacheBudgetBytes == expectedCacheBudget &&
            result.EngineReuseMode ==
                PerformanceMeasurementContract.FirstViewportEngineReuseMode &&
            result.TrialStartPolicy ==
                PerformanceMeasurementContract.FirstViewportTrialStartPolicy &&
            result.SchedulePolicy == (quick
                ? "quick-first-williams-row-non-gating"
                : PerformanceMeasurementContract.FirstViewportSchedulePolicy) &&
            result.RegressionEstimator == (quick
                ? "quick-single-trial-non-gating"
                : PerformanceMeasurementContract.FirstViewportRegressionEstimator) &&
            result.StationarityPolicy == (quick
                ? "quick-not-evaluated"
                : PerformanceMeasurementContract.FirstViewportStationarityPolicy) &&
            result.WarmupTrials is not null &&
            result.Trials is not null &&
            result.SamplesMilliseconds is not null &&
            result.PublicationSamplesMilliseconds is not null &&
            result.WarmupTrials.Count == shape.WarmupTrialCount &&
            result.Trials.Count == shape.MeasuredTrialCount;
    }

    private static bool TryAddScheduledTrials(
        IReadOnlyList<FirstViewportTrialResult> trials,
        string mode,
        int iterationsPerTrial,
        int condition,
        int firstGlobalOrdinal,
        List<(FirstViewportTrialResult Trial, int Condition)> chronologicalTrials)
    {
        for (int row = 0; row < trials.Count; row++)
        {
            int position = GetFirstViewportSchedulePosition(condition, row);
            int globalOrdinal = checked(
                firstGlobalOrdinal +
                row * PerformanceMeasurementContract.FirstViewportConditionCount +
                position);
            FirstViewportTrialResult trial = trials[row];
            if (!HasValidFirstViewportTrial(
                    trial,
                    mode,
                    iterationsPerTrial,
                    row,
                    globalOrdinal,
                    row,
                    position))
            {
                return false;
            }
            chronologicalTrials.Add((trial, condition));
        }

        return true;
    }

    private static bool HasValidFirstViewportTrial(
        FirstViewportTrialResult trial,
        string mode,
        int iterationsPerTrial,
        int ordinal,
        int globalOrdinal,
        int scheduleRow,
        int schedulePosition)
    {
        if (trial is null || trial.SamplesMilliseconds is null ||
            trial.PublicationSamplesMilliseconds is null)
            return false;

        int presentations = checked(iterationsPerTrial + 1);
        bool cacheProof = mode switch
        {
            PerformanceMeasurementContract.FirstViewportCacheDisabledMode =>
                trial.CompletedParseCountBeforePrime == 0 &&
                trial.SourceKeyHashCountBeforePrime == 0 &&
                trial.CompletedParseCountAfterPrime == 0 &&
                trial.SourceKeyHashCountAfterPrime == 0 &&
                trial.CompletedParseCountAfterTrial == 0 &&
                trial.SourceKeyHashCountAfterTrial == presentations,
            PerformanceMeasurementContract.FirstViewportCacheHitMode =>
                trial.CompletedParseCountBeforePrime == 0 &&
                trial.SourceKeyHashCountBeforePrime == 0 &&
                trial.CompletedParseCountAfterPrime == 1 &&
                trial.SourceKeyHashCountAfterPrime == 1 &&
                trial.CompletedParseCountAfterTrial == 1 &&
                trial.SourceKeyHashCountAfterTrial == 1,
            _ => false,
        };
        double p95 = PerformanceStatistics.Percentile(trial.SamplesMilliseconds, 0.95);
        bool complete =
            trial.SamplesMilliseconds.Count == iterationsPerTrial &&
            trial.SamplesMilliseconds.All(IsFiniteNonNegative) &&
            trial.PublicationSamplesMilliseconds.Count == iterationsPerTrial &&
            trial.PublicationSamplesMilliseconds.All(IsFiniteNonNegative) &&
            IsFiniteNonNegative(trial.SettlingPublicationMilliseconds) &&
            NearlyEqual(
                trial.PublicationMaximumMilliseconds,
                trial.PublicationSamplesMilliseconds.Count == 0
                    ? double.NaN
                    : trial.PublicationSamplesMilliseconds.Max()) &&
            IsFiniteNonNegative(trial.SettlingElapsedMilliseconds) &&
            IsFiniteNonNegative(p95) &&
            trial.StartedUtc != default &&
            trial.CompletedUtc > trial.StartedUtc &&
            trial.StartedUtc.Offset == TimeSpan.Zero &&
            trial.CompletedUtc.Offset == TimeSpan.Zero &&
            trial.Gen0Collections >= 0 &&
            trial.Gen1Collections >= 0 &&
            trial.Gen2Collections >= 0 &&
            trial.Gen0Collections >= trial.Gen1Collections &&
            trial.Gen1Collections >= trial.Gen2Collections &&
            trial.ProcessAllocatedBytes >= 0 &&
            cacheProof;

        return trial.Ordinal == ordinal &&
               trial.GlobalOrdinal == globalOrdinal &&
               trial.ScheduleRow == scheduleRow &&
               trial.SchedulePosition == schedulePosition &&
               NearlyEqual(trial.P95Milliseconds, p95) &&
               trial.CacheProofPassed == cacheProof &&
               trial.Complete == complete &&
               complete;
    }

    private static bool HasExactChronologicalFirstViewportSchedule(
        IReadOnlyCollection<(FirstViewportTrialResult Trial, int Condition)> observations,
        int warmupTrialCount,
        int measuredTrialCount)
    {
        int conditionCount = PerformanceMeasurementContract.FirstViewportConditionCount;
        int warmupPopulation = checked(warmupTrialCount * conditionCount);
        int expectedPopulation = checked((warmupTrialCount + measuredTrialCount) * conditionCount);
        if (observations.Count != expectedPopulation)
            return false;

        (FirstViewportTrialResult Trial, int Condition)[] ordered = observations
            .OrderBy(static observation => observation.Trial.GlobalOrdinal)
            .ToArray();
        for (int globalOrdinal = 0; globalOrdinal < ordered.Length; globalOrdinal++)
        {
            (FirstViewportTrialResult trial, int condition) = ordered[globalOrdinal];
            bool warmup = globalOrdinal < warmupPopulation;
            int phaseOrdinal = warmup ? globalOrdinal : globalOrdinal - warmupPopulation;
            int row = phaseOrdinal / conditionCount;
            int position = phaseOrdinal % conditionCount;
            if (trial.GlobalOrdinal != globalOrdinal ||
                trial.Ordinal != row ||
                trial.ScheduleRow != row ||
                trial.SchedulePosition != position ||
                condition != PerformanceMeasurementContract.GetFirstViewportCondition(row, position) ||
                (globalOrdinal > 0 &&
                 trial.StartedUtc <= ordered[globalOrdinal - 1].Trial.CompletedUtc))
            {
                return false;
            }
        }

        return true;
    }

    private static int GetFirstViewportSchedulePosition(int condition, int row)
    {
        for (int position = 0;
             position < PerformanceMeasurementContract.FirstViewportConditionCount;
             position++)
        {
            if (PerformanceMeasurementContract.GetFirstViewportCondition(row, position) == condition)
                return position;
        }

        throw new UnreachableException("The frozen Williams row omitted a first-viewport condition.");
    }

    private static bool TryGetFirstViewportExecutionEnvironment(
        IReadOnlyCollection<FirstViewportResult> results,
        out (string AffinityMask, int ProcessorGroup, int ProcessorNumber, string Priority) environment)
    {
        environment = default;
        if (results is null ||
            results.Count != PerformanceMeasurementContract.FirstViewportConditionCount)
        {
            return false;
        }

        FirstViewportResult first = results.First();
        environment = (
            first.MeasurementThreadAffinityMask,
            first.MeasurementProcessorGroup,
            first.MeasurementProcessorNumber,
            first.MeasurementThreadPriority);
        var expected = environment;
        return PinnedMeasurementThreadScope.IsEvidenceValid(
                   environment.AffinityMask,
                   environment.ProcessorGroup,
                   environment.ProcessorNumber,
                   environment.Priority,
                   ThreadPriority.Normal) &&
               results.All(result =>
                   result.MeasurementThreadAffinityMask == expected.AffinityMask &&
                   result.MeasurementProcessorGroup == expected.ProcessorGroup &&
                   result.MeasurementProcessorNumber == expected.ProcessorNumber &&
                   result.MeasurementThreadPriority == expected.Priority);
    }

    private readonly record struct ProtocolShape(
        int IterationsPerTrial,
        int WarmupTrialCount,
        int MeasuredTrialCount,
        bool Quick);

    private readonly record struct MeasurementThread(
        string AffinityMask,
        int ProcessorGroup,
        int ProcessorNumber,
        string Priority);
}
