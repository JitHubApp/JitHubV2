using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Gate fixture mutations of the lifecycle plateau and retained-memory
/// evidence.
/// </summary>
internal static class PerformanceGateLifecycleMutations
{
    internal static PerformanceGateReportMutations Mutations { get; } = new()
    {
        Shared = new Dictionary<string, Action<JsonObject>>
        {
            ["lifecycle-derived-growth"] = static report =>
                report["lifecyclePlateau"]!["checkpoint100ManagedBytes"] = 2,
            ["lifecycle-device-mode"] = static report =>
                report["lifecyclePlateau"]!["deviceResetMode"] = "wrong-mode",
            ["retained-corpus"] = static report => report["retainedMemory"]![0]!["corpus"] = "wrong-corpus",
        },
        CandidateOnly = new Dictionary<string, Action<JsonObject>>
        {
            ["negative-device-loss"] = static report =>
                report["lifecyclePlateau"]!["actualDeviceLossEvents"] = -1,
        },
        ReferenceOnly = new Dictionary<string, Action<JsonObject>>
        {
            ["reference-lifecycle-population"] = static report =>
                report["sampleRequirements"]!["lifecycleCyclesRequired"] = 99,
            ["reference-lifecycle-budget"] = static report =>
                report["lifecyclePlateau"]!["growthBudgetPercent"] = 6,
        },
    };
}
