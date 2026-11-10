using MarkdownRenderer.PerformanceHarness.Core.Configuration;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// The engine's completed-parse and source-key-hash counters read before the
/// cache prime, after it, and after a first-viewport trial. They prove which
/// cache path the trial measured.
/// </summary>
internal readonly record struct FirstViewportParseCounters(
    int CompletedBeforePrime,
    long HashesBeforePrime,
    int CompletedAfterPrime,
    long HashesAfterPrime,
    int CompletedAfterTrial,
    long HashesAfterTrial)
{
    /// <summary>
    /// True when the counters show the trial took exactly the path its mode
    /// names: no completed parse and one hash per presentation with the cache
    /// disabled, or one primed parse that every presentation reused on a hit.
    /// </summary>
    internal bool ProvesCacheMode(string mode, int presentations)
        => mode switch
        {
            PerformanceMeasurementContract.FirstViewportCacheDisabledMode =>
                CompletedBeforePrime == 0 &&
                HashesBeforePrime == 0 &&
                CompletedAfterPrime == 0 &&
                HashesAfterPrime == 0 &&
                CompletedAfterTrial == 0 &&
                HashesAfterTrial == presentations,
            PerformanceMeasurementContract.FirstViewportCacheHitMode =>
                CompletedBeforePrime == 0 &&
                HashesBeforePrime == 0 &&
                CompletedAfterPrime == 1 &&
                HashesAfterPrime == 1 &&
                CompletedAfterTrial == 1 &&
                HashesAfterTrial == 1,
            _ => false,
        };
}
