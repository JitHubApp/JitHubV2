using System.Security.Cryptography;
using MarkdownRenderer.PerformanceHarness.Core.Deployment;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Gate fixture mutations that change files rather than one report's JSON
/// tree: deployment files, raw report bytes and duplicate JSON properties that
/// a JSON object model cannot express.
/// </summary>
internal static class PerformanceGateEvidenceMutations
{
    internal static IReadOnlyDictionary<string, Action<PerformanceGateEvidence>> All { get; } =
        new Dictionary<string, Action<PerformanceGateEvidence>>
        {
            ["deployment-unmanifested-file"] = static evidence => File.WriteAllText(
                Path.Combine(evidence.DeploymentDirectory, "unmanifested-runtime-dependency.dll"),
                "unmanifested"),
            ["artifact-file"] = static evidence =>
                File.AppendAllText(evidence.ExecutableArtifactPath, "tampered"),
            ["reference-bytes"] = static evidence => File.AppendAllText(evidence.BaselinePath, " "),
            ["runtimeconfig-content"] = ReplaceRuntimeConfigAndRebindBothReports,
            ["duplicate-json-property"] = static evidence => evidence.MutateCandidateText(
                static text => AddDuplicateProperty(text, "providerName", "providerName")),
            ["case-colliding-json-property"] = static evidence => evidence.MutateCandidateText(
                static text => AddDuplicateProperty(text, "providerName", "ProviderName")),
            ["reference-duplicate-json-property"] = static evidence => evidence.MutateReferenceText(
                static text => AddDuplicateProperty(text, "providerName", "providerName")),
            ["reference-case-colliding-json-property"] = static evidence => evidence.MutateReferenceText(
                static text => AddDuplicateProperty(text, "providerName", "ProviderName")),
        };

    private static void ReplaceRuntimeConfigAndRebindBothReports(PerformanceGateEvidence evidence)
    {
        string runtimeConfigPath = evidence.ReadCandidateReport()
            ["buildArtifacts"]!["runtimeConfig"]!["path"]!.GetValue<string>();
        File.WriteAllText(
            runtimeConfigPath,
            """
            {
              "runtimeOptions": {
                "configProperties": {
                  "System.Runtime.TieredCompilation": true,
                  "System.Runtime.TieredPGO": false,
                  "System.GC.Concurrent": false,
                  "System.Runtime.ReadyToRun": false
                }
              }
            }
            """);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(runtimeConfigPath)));
        evidence.MutateReference((baseline, candidate) =>
        {
            baseline["buildArtifacts"]!["runtimeConfig"]!["sha256"] = hash;
            candidate["buildArtifacts"]!["runtimeConfig"]!["sha256"] = hash;
            candidate["buildIdentity"] =
                PerformanceDeploymentIdentity.CaptureForExecutable(
                    evidence.ExecutableArtifactPath);
        });
    }

    private static string AddDuplicateProperty(
        string json,
        string existingName,
        string duplicateName)
    {
        string marker = $"\"{existingName}\":";
        string replacement = $"\"{duplicateName}\": \"duplicate\",{Environment.NewLine}  {marker}";
        string mutated = json.Replace(marker, replacement, StringComparison.Ordinal);
        Assert.NotEqual(json, mutated);
        return mutated;
    }
}
