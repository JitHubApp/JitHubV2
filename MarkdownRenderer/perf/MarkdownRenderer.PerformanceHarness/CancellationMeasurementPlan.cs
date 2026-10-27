namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// Defines cancellation-scenario warmup and evidence iterations independently
/// from the event timestamps used by the measurement itself.
/// </summary>
internal static class CancellationMeasurementPlan
{
    internal const int WarmupIterationsPerTrial = 1;
    internal const int FirstRecordedSourceSeed = 5_000;
    internal const double AbsoluteBudgetMilliseconds = 16;

    /// <summary>
    /// Creates one trial-local, scenario-identical, unrecorded warmup followed
    /// by exactly the requested recorded iterations. Every trial receives
    /// globally distinct source seeds and winner ordinals. The warmup exercises
    /// JIT, EventSource, cancellation, and teardown paths without
    /// populating an input-specific cache for the evidence corpus.
    /// </summary>
    internal static IReadOnlyList<CancellationMeasurementIteration> CreateTrial(
        int trialOrdinal,
        int recordedIterations)
    {
        if (trialOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(trialOrdinal));
        if (recordedIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(recordedIterations));

        var plan = new CancellationMeasurementIteration[
            checked(WarmupIterationsPerTrial + recordedIterations)];
        int firstRecordedSourceSeed = checked(
            FirstRecordedSourceSeed + trialOrdinal * recordedIterations);
        plan[0] = new CancellationMeasurementIteration(
            IsRecorded: false,
            SourceSeed: checked(FirstRecordedSourceSeed - 1 - trialOrdinal),
            WinnerOrdinal: checked(int.MinValue + trialOrdinal));

        for (int recordedIndex = 0; recordedIndex < recordedIterations; recordedIndex++)
        {
            plan[WarmupIterationsPerTrial + recordedIndex] = new CancellationMeasurementIteration(
                IsRecorded: true,
                SourceSeed: checked(firstRecordedSourceSeed + recordedIndex),
                WinnerOrdinal: checked(trialOrdinal * recordedIterations + recordedIndex));
        }

        return plan;
    }

    /// <summary>
    /// Validates first-call safety separately from the recorded steady-state
    /// percentile. A warmup is not a regression sample, but it must still meet
    /// the same absolute cancellation and stale-publication requirements.
    /// </summary>
    internal static bool IsWarmupEvidencePassing(
        int warmupIterations,
        double elapsedMilliseconds,
        int staleCommits)
        => warmupIterations == WarmupIterationsPerTrial &&
           double.IsFinite(elapsedMilliseconds) &&
           elapsedMilliseconds >= 0 &&
           elapsedMilliseconds <= AbsoluteBudgetMilliseconds &&
           staleCommits == 0;
}

internal readonly record struct CancellationMeasurementIteration(
    bool IsRecorded,
    int SourceSeed,
    int WinnerOrdinal);
