using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Every named evidence mutation the external performance-gate fixture tests
/// apply. Each area's <see cref="PerformanceGateReportMutations"/> table is
/// routed to the candidate report, the baseline (reference) report or both;
/// a baseline change always rebinds the candidate to the new reference bytes.
/// Building the catalog fails on a duplicate name, so no two areas can claim
/// the same mutation.
/// </summary>
internal static class PerformanceGateMutationCatalog
{
    private const string ReferencePrefix = "reference-";

    private static readonly FrozenDictionary<string, Action<PerformanceGateEvidence>> Mutations = Build();

    internal static void Apply(PerformanceGateEvidence evidence, string mutation)
    {
        if (!Mutations.TryGetValue(mutation, out Action<PerformanceGateEvidence>? apply))
            throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        apply(evidence);
    }

    private static FrozenDictionary<string, Action<PerformanceGateEvidence>> Build()
    {
        var mutations = new Dictionary<string, Action<PerformanceGateEvidence>>(StringComparer.Ordinal);
        foreach ((string name, Action<PerformanceGateEvidence> apply) in PerformanceGateEvidenceMutations.All)
            mutations.Add(name, apply);

        PerformanceGateReportMutations[] areas =
        [
            PerformanceGateReportShapeMutations.Mutations,
            PerformanceGateBuildArtifactMutations.Mutations,
            PerformanceGateMachineMutations.Mutations,
            PerformanceGateCompletionMachineMutations.Mutations,
            PerformanceGateRuntimeMutations.Mutations,
            PerformanceGateFirstViewportMutations.Mutations,
            PerformanceGateFirstViewportScheduleMutations.Mutations,
            PerformanceGateSourceLookupMutations.Mutations,
            PerformanceGateComparisonMutations.Mutations,
            PerformanceGateScrollMutations.Mutations,
            PerformanceGateCancellationMutations.Mutations,
            PerformanceGateLifecycleMutations.Mutations,
        ];
        foreach (PerformanceGateReportMutations area in areas)
            AddArea(mutations, area);

        return mutations.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void AddArea(
        Dictionary<string, Action<PerformanceGateEvidence>> mutations,
        PerformanceGateReportMutations area)
    {
        foreach ((string name, Action<JsonObject> mutate) in area.Shared)
        {
            mutations.Add(name, evidence => evidence.MutateCandidate(mutate));
            mutations.Add(ReferencePrefix + name, evidence => evidence.MutateReference(
                (baseline, _) => mutate(baseline)));
        }
        foreach ((string name, Action<JsonObject> mutate) in area.CandidateOnly)
            mutations.Add(name, evidence => evidence.MutateCandidate(mutate));
        foreach ((string name, Action<JsonObject> mutate) in area.ReferenceOnly)
        {
            mutations.Add(name, evidence => evidence.MutateReference(
                (baseline, _) => mutate(baseline)));
        }
        foreach ((string name, Action<JsonObject> mutate) in area.BothReports)
        {
            mutations.Add(name, evidence => evidence.MutateReference(
                (baseline, candidate) =>
                {
                    mutate(baseline);
                    mutate(candidate);
                }));
        }
    }
}
