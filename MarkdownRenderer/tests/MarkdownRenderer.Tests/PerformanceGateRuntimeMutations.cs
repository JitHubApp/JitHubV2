using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Gate fixture mutations of the recorded runtime configuration (JIT tiering,
/// environment overrides and the runtime policy verdict).
/// </summary>
internal static class PerformanceGateRuntimeMutations
{
    internal static PerformanceGateReportMutations Mutations { get; } = new()
    {
        Shared = new Dictionary<string, Action<JsonObject>>
        {
            ["runtime-policy"] = static report => Runtime(report)["policy"] = "wrong-policy",
            ["runtime-tiering"] = static report => Runtime(report)["tieredCompilationEnabled"] = true,
            ["runtime-overrides"] = static report =>
                Runtime(report)["overrideEnvironmentVariables"]!.AsArray().Add("DOTNET_TieredPGO"),
            ["runtime-passed"] = static report => Runtime(report)["passed"] = false,
        },
    };

    private static JsonObject Runtime(JsonObject report)
        => report["runtimeConfiguration"]!.AsObject();
}
