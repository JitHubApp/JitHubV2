using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests.Engineering.PerformanceGates.Mutations;

/// <summary>
/// Gate fixture mutations of the candidate's regression comparisons. Each one
/// changes the 100 KiB cache-disabled first-viewport comparison, or the
/// comparison list itself, so that it no longer matches what the gate
/// recomputes from both reports.
/// </summary>
internal static class PerformanceGateComparisonMutations
{
    private const string TargetMetric = "firstViewport.cache-disabled.102400.p95Ms.hodgesLehmann";

    internal static PerformanceGateReportMutations Mutations { get; } = new()
    {
        CandidateOnly = new Dictionary<string, Action<JsonObject>>
        {
            ["duplicate-comparison"] = static report =>
                Comparisons(report).Add(Target(report).DeepClone()),
            ["wrong-kind"] = static report => Target(report)["kind"] = "allocation",
            ["wrong-reference"] = static report => PerformanceGateJson.AddOne(Target(report), "reference"),
            ["wrong-candidate"] = static report => PerformanceGateJson.AddOne(Target(report), "candidate"),
            ["wrong-delta"] = static report =>
            {
                JsonObject comparison = Target(report);
                comparison["deltaPercent"] = comparison["deltaPercent"]!.GetValue<double>() - 1;
            },
            ["comparison-absolute-delta"] = static report =>
                PerformanceGateJson.AddOne(Target(report), "absoluteDelta"),
            ["comparison-budget"] = static report => Target(report)["budgetPercent"] = 4,
            ["comparison-relative-allowance"] = static report =>
                PerformanceGateJson.AddOne(Target(report), "relativeAllowance"),
            ["comparison-floor"] = static report => Target(report)["absoluteNoiseFloor"] = 4,
            ["comparison-allowed-delta"] = static report =>
                PerformanceGateJson.AddOne(Target(report), "allowedAbsoluteDelta"),
            ["comparison-reference-dispersion"] = static report =>
                PerformanceGateJson.AddOne(Target(report), "referenceDispersionPercent"),
            ["comparison-candidate-dispersion"] = static report =>
                PerformanceGateJson.AddOne(Target(report), "candidateDispersionPercent"),
            ["comparison-stability-budget"] = static report => Target(report)["stabilityBudgetPercent"] = 24,
            ["comparison-decision"] = static report => Target(report)["decision"] = "regression",
            ["comparison-passed"] = static report => Target(report)["passed"] = false,
            ["comparison-counts"] = static report => report["regression"]!["comparedLatencyMetrics"] = 10,
            ["first-viewport-comparison-name"] = static report =>
                Target(report)["metric"] = "firstViewport.cache-disabled.102400.p95Ms",
        },
    };

    private static JsonArray Comparisons(JsonObject report)
        => report["regression"]!["comparisons"]!.AsArray();

    private static JsonObject Target(JsonObject report)
        => Comparisons(report)
            .Select(static node => node!.AsObject())
            .Single(static comparison => comparison["metric"]!.GetValue<string>() == TargetMetric);
}
