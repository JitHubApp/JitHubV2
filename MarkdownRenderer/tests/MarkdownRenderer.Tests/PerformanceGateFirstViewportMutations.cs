using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Gate fixture mutations of the first-usable-viewport populations, protocol
/// fields, per-trial and pooled statistics, publication samples and budgets.
/// The schedule, timestamp and stationarity mutations live in
/// <see cref="PerformanceGateFirstViewportScheduleMutations"/>.
/// </summary>
internal static class PerformanceGateFirstViewportMutations
{
    internal static PerformanceGateReportMutations Mutations { get; } = new()
    {
        Shared = new Dictionary<string, Action<JsonObject>>
        {
            ["first-viewport-required-warmup"] = static report =>
                report["sampleRequirements"]!["firstViewportWarmupTrialsRequired"] = 0,
            ["first-viewport-required-trials"] = static report =>
                report["sampleRequirements"]!["firstViewportTrialsRequired"] = 4,
            ["first-viewport-engine-reuse"] = static report =>
                Viewport(report)["engineReuseMode"] = "per-iteration",
            ["first-viewport-trial-policy"] = static report =>
                Viewport(report)["trialStartPolicy"] = "wrong-policy",
            ["first-viewport-regression-estimator"] = static report =>
                Viewport(report)["regressionEstimator"] = "wrong-estimator",
            ["first-viewport-trial-list-count"] = static report =>
                Viewport(report)["trials"]!.AsArray().RemoveAt(5),
            ["first-viewport-trial-ordinal"] = static report => Trial(report)["ordinal"] = 1,
            ["first-viewport-trial-sample-count"] = static report =>
                Trial(report)["samplesMilliseconds"]!.AsArray().RemoveAt(99),
            ["first-viewport-trial-p95"] = static report =>
                PerformanceGateJson.AddOne(Trial(report), "p95Milliseconds"),
            ["first-viewport-trial-complete"] = static report => Trial(report)["complete"] = false,
            ["first-viewport-concatenation"] = SwapPooledSamplesAcrossTrials,
            ["first-viewport-pooled-p95"] = static report =>
                PerformanceGateJson.AddOne(Viewport(report), "p95Milliseconds"),
            ["first-viewport-regression-p95"] = static report =>
                PerformanceGateJson.AddOne(Viewport(report), "regressionP95Milliseconds"),
            ["first-viewport-budget"] = static report => Viewport(report)["budgetMilliseconds"] = 124,
        },
        CandidateOnly = new Dictionary<string, Action<JsonObject>>
        {
            ["first-viewport-warmup"] = static report =>
                Viewport(report)["warmupTrials"]!.AsArray().RemoveAt(2),
            ["first-viewport-cache-disabled"] = static report =>
                Viewport(report)["parseCacheBudgetBytes"] = 1,
            ["first-viewport-cache-hit"] = static report =>
                report["firstUsableViewport"]![1]!["parseCacheBudgetBytes"] = 0,
            ["first-viewport-publication-sample-count"] = static report =>
                Trial(report)["publicationSamplesMilliseconds"]!.AsArray().RemoveAt(99),
            ["first-viewport-publication-trial-maximum"] = static report =>
                Trial(report)["publicationMaximumMilliseconds"] = 1.25,
            ["first-viewport-publication-concatenation"] = static report =>
                Viewport(report)["publicationSamplesMilliseconds"]![0] = 1.25,
            ["first-viewport-publication-aggregate-maximum"] = static report =>
                Viewport(report)["publicationMaximumMilliseconds"] = 1.25,
            ["first-viewport-publication-budget"] = static report =>
                Viewport(report)["publicationBudgetMilliseconds"] = 3,
            ["first-viewport-publication-overbudget"] = static report =>
            {
                JsonObject viewport = Viewport(report);
                JsonObject trial = Trial(report);
                trial["publicationSamplesMilliseconds"]![0] = 2.5;
                trial["publicationMaximumMilliseconds"] = 2.5;
                viewport["publicationSamplesMilliseconds"]![0] = 2.5;
                viewport["publicationMaximumMilliseconds"] = 2.5;
            },
        },
        ReferenceOnly = new Dictionary<string, Action<JsonObject>>
        {
            ["reference-first-viewport-protocol"] = static report =>
                Viewport(report)["schedulePolicy"] = "wrong-policy",
            ["reference-first-viewport-cache"] = static report =>
                report["firstUsableViewport"]![1]!["parseCacheBudgetBytes"] = 0,
            ["reference-extra-first-viewport"] = static report =>
            {
                JsonNode extra = Viewport(report).DeepClone();
                extra["mode"] = "extra";
                report["firstUsableViewport"]!.AsArray().Add(extra);
            },
        },
    };

    private static JsonObject Viewport(JsonObject report)
        => report["firstUsableViewport"]![0]!.AsObject();

    private static JsonObject Trial(JsonObject report)
        => Viewport(report)["trials"]![0]!.AsObject();

    // Swap the first sample of the first and second trials in the pooled
    // population, so the pool is no longer the trials' concatenation.
    private static void SwapPooledSamplesAcrossTrials(JsonObject report)
    {
        JsonArray samples = Viewport(report)["samplesMilliseconds"]!.AsArray();
        JsonNode first = samples[0]!.DeepClone();
        JsonNode firstOfSecondTrial = samples[100]!.DeepClone();
        samples[0] = firstOfSecondTrial;
        samples[100] = first;
    }
}
