using System.Text.Json.Nodes;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Gate fixture mutations of the warm-scroll evidence: protocol fields, trial
/// structure, raw per-frame populations, per-trial, pooled and regression
/// statistics, and budgets.
/// </summary>
internal static class PerformanceGateScrollMutations
{
    internal static PerformanceGateReportMutations Mutations { get; } = new()
    {
        Shared = new Dictionary<string, Action<JsonObject>>
        {
            ["scroll-frequency"] = static report =>
            {
                JsonObject scroll = Scroll(report);
                scroll["stopwatchFrequency"] = scroll["stopwatchFrequency"]!.GetValue<long>() + 1;
            },
            ["scroll-raw-scope"] = static report => Scroll(report)["rawAllocationScope"] = "wrong-scope",
            ["scroll-platform-scope"] = static report =>
                Scroll(report)["platformProjectionAllocationScope"] = "wrong-scope",
            ["scroll-trial-count"] = static report => Trials(report).RemoveAt(4),
            ["scroll-trial-ordinal"] = static report => Trial(report)["ordinal"] = 1,
            ["scroll-trial-frame-order"] = static report =>
                Trials(report)[1]!["firstLogicalFrameId"] =
                    Trial(report)["firstLogicalFrameId"]!.GetValue<long>() + 1,
            ["scroll-trial-population"] = static report => Trial(report)["requestedFrames"] = 2_399,
            ["scroll-raw-ui-count"] = static report =>
                Trial(report)["uiThreadWorkElapsedTicks"]!.AsArray().RemoveAt(2_399),
            ["scroll-raw-frame-count"] = static report =>
                Trial(report)["frameIntervalElapsedTicks"]!.AsArray().RemoveAt(2_399),
            ["scroll-raw-allocation-count"] = static report =>
                Trial(report)["rendererOwnedAllocatedBytesPerFrame"]!.AsArray().RemoveAt(2_399),
            ["scroll-raw-ui-negative"] = static report => Trial(report)["uiThreadWorkElapsedTicks"]![0] = -1,
            ["scroll-raw-frame-zero"] = static report => Trial(report)["frameIntervalElapsedTicks"]![0] = 0,
            ["scroll-raw-allocation-negative"] = static report =>
                Trial(report)["rendererOwnedAllocatedBytesPerFrame"]![0] = -1,
            ["scroll-trial-ui-p95"] = AddOneToTrial("uiThreadWorkP95Milliseconds"),
            ["scroll-trial-ui-p99"] = AddOneToTrial("uiThreadWorkP99Milliseconds"),
            ["scroll-trial-frame-p95"] = AddOneToTrial("frameTimeP95Milliseconds"),
            ["scroll-trial-frame-over"] = AddOneToTrial("framesOver16_7Percent"),
            ["scroll-trial-frame-max"] = AddOneToTrial("maximumFrameStallMilliseconds"),
            ["scroll-trial-allocation-p95"] = AddOneToTrial("rendererOwnedAllocatedBytesPerFrameP95"),
            ["scroll-trial-allocation-max"] = static report =>
                AddOneToInt64(Trial(report), "maximumRendererOwnedAllocatedBytesPerFrame"),
            ["scroll-trial-complete"] = static report => Trial(report)["complete"] = false,
            ["scroll-pooled-ui-p95"] = AddOneToScroll("uiThreadWorkP95Milliseconds"),
            ["scroll-pooled-ui-p99"] = AddOneToScroll("uiThreadWorkP99Milliseconds"),
            ["scroll-pooled-frame-p95"] = AddOneToScroll("frameTimeP95Milliseconds"),
            ["scroll-pooled-frame-over"] = AddOneToScroll("framesOver16_7Percent"),
            ["scroll-pooled-frame-max"] = AddOneToScroll("maximumFrameStallMilliseconds"),
            ["scroll-pooled-allocation-p95"] = AddOneToScroll("rendererOwnedAllocatedBytesPerFrameP95"),
            ["scroll-pooled-allocation-max"] = static report =>
                AddOneToInt64(Scroll(report), "maximumRendererOwnedAllocatedBytesPerFrame"),
            ["scroll-pooled-loh"] = static report =>
                Scroll(report)["rendererOwnedLohAllocationPossible"] = true,
            ["scroll-regression-ui-p95"] = AddOneToScroll("regressionUiThreadWorkP95Milliseconds"),
            ["scroll-regression-ui-p99"] = AddOneToScroll("regressionUiThreadWorkP99Milliseconds"),
            ["scroll-regression-frame-p95"] = AddOneToScroll("regressionFrameTimeP95Milliseconds"),
            ["scroll-regression-allocation-p95"] =
                AddOneToScroll("regressionRendererOwnedAllocatedBytesPerFrameP95"),
            ["scroll-report-passed"] = static report => Scroll(report)["passed"] = false,
            ["scroll-frame-budget"] = static report =>
                Scroll(report)["frameTimeP95BudgetMilliseconds"] =
                    PerformanceMeasurementContract.ScrollFrameTimeP95BudgetMilliseconds - 0.001,
            ["negative-first-frame"] = static report =>
            {
                JsonArray trials = Trials(report);
                for (int index = 0; index < trials.Count; index++)
                    trials[index]!["firstLogicalFrameId"] = -50_000_000L + index * 10_000_000L;
            },
        },
        CandidateOnly = new Dictionary<string, Action<JsonObject>>
        {
            ["median"] = AddOneToScroll("regressionUiThreadWorkP95Milliseconds"),
            ["exclusive-threshold"] = static report => Scroll(report)["framesOver16_7Percent"] = 1.0,
            ["scroll-budget"] = static report => Scroll(report)["uiThreadWorkP95BudgetMilliseconds"] = 3,
        },
    };

    private static JsonObject Scroll(JsonObject report)
        => report["scroll"]!.AsObject();

    private static JsonArray Trials(JsonObject report)
        => Scroll(report)["trials"]!.AsArray();

    private static JsonObject Trial(JsonObject report)
        => Trials(report)[0]!.AsObject();

    private static Action<JsonObject> AddOneToTrial(string propertyName)
        => report => PerformanceGateJson.AddOne(Trial(report), propertyName);

    private static Action<JsonObject> AddOneToScroll(string propertyName)
        => report => PerformanceGateJson.AddOne(Scroll(report), propertyName);

    private static void AddOneToInt64(JsonObject owner, string propertyName)
        => owner[propertyName] = owner[propertyName]!.GetValue<long>() + 1;
}
