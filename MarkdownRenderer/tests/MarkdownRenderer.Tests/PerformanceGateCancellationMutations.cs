using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Gate fixture mutations of the cancellation evidence: required populations,
/// protocol fields, per-trial warm-up and samples, pooled and regression
/// statistics, stale commits and the budget.
/// </summary>
internal static class PerformanceGateCancellationMutations
{
    internal static PerformanceGateReportMutations Mutations { get; } = new()
    {
        Shared = new Dictionary<string, Action<JsonObject>>
        {
            ["cancellation-required-iterations"] = static report =>
                report["sampleRequirements"]!["cancellationIterationsPerTrialRequired"] = 39,
            ["cancellation-required-trials"] = static report =>
                report["sampleRequirements"]!["cancellationTrialsRequired"] = 4,
            ["cancellation-iterations-per-trial"] = static report =>
                Cancellation(report)["iterationsPerTrial"] = 39,
            ["cancellation-total"] = static report => Cancellation(report)["requestedIterations"] = 199,
            ["cancellation-warmup-count"] = static report => Cancellation(report)["warmupIterations"] = 4,
            ["cancellation-observed"] = static report => Cancellation(report)["observedCancellations"] = 199,
            ["cancellation-policy"] = static report => Cancellation(report)["trialStartPolicy"] = "wrong-policy",
            ["cancellation-estimator"] = static report =>
                Cancellation(report)["regressionEstimator"] = "wrong-estimator",
            ["cancellation-trial-count"] = static report => Trials(report).RemoveAt(4),
            ["cancellation-trial-ordinal"] = static report => Trial(report)["ordinal"] = 1,
            ["cancellation-warmup-seed"] = static report => AddOneToInt32(Trial(report), "warmupSourceSeed"),
            ["cancellation-first-recorded-seed"] = static report =>
                AddOneToInt32(Trial(report), "firstRecordedSourceSeed"),
            ["cancellation-trial-requested"] = static report => Trial(report)["requestedIterations"] = 39,
            ["cancellation-trial-observed"] = static report => Trial(report)["observedCancellations"] = 39,
            ["cancellation-trial-sample-count"] = static report =>
                Trial(report)["samplesMilliseconds"]!.AsArray().RemoveAt(39),
            ["cancellation-trial-negative-sample"] = static report =>
                Trial(report)["samplesMilliseconds"]![0] = -1,
            ["cancellation-trial-warmup-negative"] = static report =>
                Trial(report)["warmupElapsedMilliseconds"] = -1,
            ["cancellation-trial-warmup-stale"] = static report => Trial(report)["warmupStaleCommits"] = 1,
            ["cancellation-trial-p95"] = static report =>
                PerformanceGateJson.AddOne(Trial(report), "p95Milliseconds"),
            ["cancellation-trial-stale"] = static report => Trial(report)["staleCommits"] = 1,
            ["cancellation-trial-complete"] = static report => Trial(report)["complete"] = false,
            ["cancellation-concatenation"] = static report =>
            {
                JsonArray samples = Cancellation(report)["samplesMilliseconds"]!.AsArray();
                JsonNode first = samples[0]!.DeepClone();
                samples[0] = samples[40]!.DeepClone();
                samples[40] = first;
            },
            ["cancellation-warmup-p95"] = static report =>
                PerformanceGateJson.AddOne(Cancellation(report), "warmupElapsedMilliseconds"),
            ["cancellation-warmup-stale"] = static report => Cancellation(report)["warmupStaleCommits"] = 1,
            ["cancellation-pooled-p95"] = static report =>
                PerformanceGateJson.AddOne(Cancellation(report), "p95Milliseconds"),
            ["cancellation-regression-p95"] = static report =>
                PerformanceGateJson.AddOne(Cancellation(report), "regressionP95Milliseconds"),
            ["cancellation-budget"] = static report => Cancellation(report)["p95BudgetMilliseconds"] = 15,
            ["cancellation-stale"] = static report => Cancellation(report)["staleCommits"] = 1,
            ["cancellation-report-passed"] = static report => Cancellation(report)["passed"] = false,
        },
    };

    private static JsonObject Cancellation(JsonObject report)
        => report["cancellation"]!.AsObject();

    private static JsonArray Trials(JsonObject report)
        => Cancellation(report)["trials"]!.AsArray();

    private static JsonObject Trial(JsonObject report)
        => Trials(report)[0]!.AsObject();

    private static void AddOneToInt32(JsonObject owner, string propertyName)
        => owner[propertyName] = owner[propertyName]!.GetValue<int>() + 1;
}
