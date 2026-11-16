using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests.PerformanceHarness.Core.Configuration;

/// <summary>
/// Named JSON mutations of a valid quick (non-gating) performance report that
/// the quick smoke validator must reject even though it applies no release
/// budgets: malformed scroll, lifecycle, runtime and artifact evidence, and a
/// broken repeatability protocol.
/// </summary>
internal static class QuickPerformanceSmokeMutations
{
    /// <summary>Malformed evidence that is not a budget decision.</summary>
    internal static IReadOnlyDictionary<string, Action<JsonObject>> NonBudget { get; } =
        new Dictionary<string, Action<JsonObject>>
        {
            ["aggregate-metric"] = static report => Scroll(report)["uiThreadWorkP95Milliseconds"] = -1,
            ["aggregate-finite-mismatch"] = static report => Scroll(report)["uiThreadWorkP95Milliseconds"] = 99,
            ["aggregate-regression"] = static report =>
                Scroll(report)["regressionUiThreadWorkP95Milliseconds"] = 99,
            ["allocation-scope"] = static report => Scroll(report)["rawAllocationScope"] = string.Empty,
            ["trial-metric"] = static report => ScrollTrial(report)["paintCallbackWorkP95Milliseconds"] = -1,
            ["trial-offscreen"] = static report =>
                ScrollTrial(report)["maximumOffscreenRealizedCodeActions"] = 1,
            ["aggregate-offscreen"] = static report =>
                Scroll(report)["maximumOffscreenRealizedCodeActions"] = 1,
            ["aggregate-gen2-mismatch"] = static report => Scroll(report)["gen2Collections"] = 2,
            ["impossible-p99"] = static report =>
            {
                Scroll(report)["uiThreadWorkP99Milliseconds"] = 99;
                Scroll(report)["regressionUiThreadWorkP99Milliseconds"] = 99;
                ScrollTrial(report)["uiThreadWorkP99Milliseconds"] = 99;
            },
            ["impossible-max-stall"] = static report =>
            {
                Scroll(report)["maximumFrameStallMilliseconds"] = 99;
                ScrollTrial(report)["maximumFrameStallMilliseconds"] = 99;
            },
            ["impossible-frame-percent"] = static report =>
            {
                Scroll(report)["framesOver16_7Percent"] = 101;
                ScrollTrial(report)["framesOver16_7Percent"] = 101;
            },
            ["impossible-allocation-maximum"] = static report =>
            {
                Scroll(report)["maximumRendererOwnedAllocatedBytesPerFrame"] = 99_999;
                ScrollTrial(report)["maximumRendererOwnedAllocatedBytesPerFrame"] = 99_999;
            },
            ["raw-scroll-frequency"] = static report => Scroll(report)["stopwatchFrequency"] = 0,
            ["raw-scroll-ui-tick"] = static report => ScrollTrial(report)["uiThreadWorkElapsedTicks"]![0] = -1,
            ["raw-scroll-frame-count"] = static report =>
                ScrollTrial(report)["frameIntervalElapsedTicks"]!.AsArray().RemoveAt(0),
            ["raw-scroll-allocation"] = static report =>
                ScrollTrial(report)["rendererOwnedAllocatedBytesPerFrame"]![0] = -1,
            ["lifecycle-growth"] = static report =>
                report["lifecyclePlateau"]!["checkpoint100ManagedBytes"] = 2,
            ["runtime-policy"] = static report => report["runtimeConfiguration"]!["policy"] = "uncontrolled",
            ["runtime-override"] = static report =>
                report["runtimeConfiguration"]!["overrideEnvironmentVariables"] =
                    new JsonArray("DOTNET_TieredCompilation"),
            ["runtimeconfig-artifact"] = static report =>
                report["buildArtifacts"]!["runtimeConfig"]!["sha256"] = "not-a-sha256",
        };

    /// <summary>A broken first-viewport, source-lookup or cancellation repeatability protocol.</summary>
    internal static IReadOnlyDictionary<string, Action<JsonObject>> Repeatability { get; } =
        new Dictionary<string, Action<JsonObject>>
        {
            ["viewport-warmup-count"] = static report => Viewport(report)["warmupTrials"]!.AsArray().Clear(),
            ["viewport-cache"] = static report => Viewport(report)["parseCacheBudgetBytes"] = 1,
            ["viewport-engine-reuse"] = static report => Viewport(report)["engineReuseMode"] = "per-sample",
            ["viewport-trial-policy"] = static report => Viewport(report)["trialStartPolicy"] = "none",
            ["viewport-schedule-policy"] = static report => Viewport(report)["schedulePolicy"] = "none",
            ["viewport-stationarity-policy"] = static report => Viewport(report)["stationarityPolicy"] = "none",
            ["viewport-affinity"] = static report =>
                Viewport(report)["measurementThreadAffinityMask"] = "0x0000000000000000",
            ["viewport-estimator"] = static report => Viewport(report)["regressionEstimator"] = "pooled-p95",
            ["viewport-trial-count"] = static report => Viewport(report)["trials"]!.AsArray().Clear(),
            ["viewport-trial-order"] = static report => ViewportTrial(report)["ordinal"] = 1,
            ["viewport-global-order"] = static report => ViewportTrial(report)["globalOrdinal"] = 0,
            ["viewport-schedule-position"] = static report => ViewportTrial(report)["schedulePosition"] = 5,
            ["viewport-chronology"] = static report =>
                ViewportTrial(report)["startedUtc"] = "2026-09-10T11:00:00-07:00",
            ["viewport-cache-proof"] = static report => ViewportTrial(report)["cacheProofPassed"] = false,
            ["viewport-cache-counter"] = static report =>
                ViewportTrial(report)["sourceKeyHashCountAfterTrial"] = 0,
            ["viewport-gc"] = static report => ViewportTrial(report)["gen0Collections"] = -1,
            ["viewport-allocation"] = static report => ViewportTrial(report)["processAllocatedBytes"] = -1,
            ["viewport-trial-p95"] = static report => ViewportTrial(report)["p95Milliseconds"] = 2,
            ["viewport-flattened-samples"] = static report =>
            {
                JsonObject viewport = Viewport(report);
                viewport["samplesMilliseconds"]![0] = 2;
                viewport["p95Milliseconds"] = 2;
                viewport["regressionP95Milliseconds"] = 2;
            },
            ["viewport-regression-estimate"] = static report =>
                Viewport(report)["regressionP95Milliseconds"] = 2,
            ["viewport-stationarity-derived"] = static report =>
                Viewport(report)["projectedDriftMilliseconds"] = 1,
            ["viewport-stationarity-flags"] = static report => Viewport(report)["stationarityEvaluated"] = true,
            ["lookup-estimator"] = static report => Lookup(report)["regressionEstimator"] = "single-trial",
            ["lookup-affinity"] = static report =>
                Lookup(report)["measurementThreadAffinityMask"] = "0x0000000000000000",
            ["lookup-trial-count"] = static report => Lookup(report)["regressionTrialCount"] = 4,
            ["lookup-trial-order"] = static report => LookupTrial(report)["ordinal"] = 1,
            ["lookup-trial-tick-normalization"] = static report =>
                LookupTrial(report)["samplesNanosecondsPerLookup"]![0] = 999,
            ["lookup-trial-p95"] = static report => LookupTrial(report)["p95Nanoseconds"] = 999,
            ["lookup-top-estimate"] = static report => Lookup(report)["regressionP95Nanoseconds"] = 999,
            ["cancellation-estimator"] = static report =>
                Cancellation(report)["regressionEstimator"] = "single-trial",
            ["cancellation-trial-order"] = static report => CancellationTrial(report)["ordinal"] = 1,
            ["cancellation-seed"] = static report => CancellationTrial(report)["warmupSourceSeed"] = 123,
            ["cancellation-trial-p95"] = static report => CancellationTrial(report)["p95Milliseconds"] = 99,
            ["cancellation-flattened-samples"] = static report =>
                Cancellation(report)["samplesMilliseconds"]![0] = 99,
        };

    private static JsonObject Scroll(JsonObject report) => report["scroll"]!.AsObject();

    private static JsonObject ScrollTrial(JsonObject report) => Scroll(report)["trials"]![0]!.AsObject();

    private static JsonObject Viewport(JsonObject report) => report["firstUsableViewport"]![0]!.AsObject();

    private static JsonObject ViewportTrial(JsonObject report) => Viewport(report)["trials"]![0]!.AsObject();

    private static JsonObject Lookup(JsonObject report) => report["sourceLookup"]!.AsObject();

    private static JsonObject LookupTrial(JsonObject report)
        => Lookup(report)["regressionTrials"]![0]!.AsObject();

    private static JsonObject Cancellation(JsonObject report) => report["cancellation"]!.AsObject();

    private static JsonObject CancellationTrial(JsonObject report)
        => Cancellation(report)["trials"]![0]!.AsObject();
}
