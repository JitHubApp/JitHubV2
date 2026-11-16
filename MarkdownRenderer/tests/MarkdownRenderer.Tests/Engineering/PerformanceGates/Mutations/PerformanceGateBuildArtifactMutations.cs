using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests.Engineering.PerformanceGates.Mutations;

/// <summary>
/// Gate fixture mutations of the build identity and the hashed build
/// artifacts the reports bind to.
/// </summary>
internal static class PerformanceGateBuildArtifactMutations
{
    internal static PerformanceGateReportMutations Mutations { get; } = new()
    {
        CandidateOnly = new Dictionary<string, Action<JsonObject>>
        {
            ["deployment-identity-format"] = static report =>
                report["buildIdentity"] = "not-a-deployment-identity",
            ["deployment-identity-lowercase"] = static report =>
                report["buildIdentity"] = report["buildIdentity"]!.GetValue<string>().ToLowerInvariant(),
        },
        ReferenceOnly = new Dictionary<string, Action<JsonObject>>
        {
            ["same-build-identity-critical-hash-mismatch"] = static report =>
                report["buildArtifacts"]!["executable"]!["sha256"] = new string('0', 64),
        },
        BothReports = new Dictionary<string, Action<JsonObject>>
        {
            // Alias the harness DLL entry onto the executable's path and hash.
            ["artifact-path-alias"] = static report =>
            {
                JsonObject artifacts = report["buildArtifacts"]!.AsObject();
                JsonObject executable = artifacts["executable"]!.AsObject();
                JsonObject harness = artifacts["performanceHarnessDll"]!.AsObject();
                harness["path"] = executable["path"]!.DeepClone();
                harness["sha256"] = executable["sha256"]!.DeepClone();
            },
        },
    };
}
