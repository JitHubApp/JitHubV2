using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests.Engineering.PerformanceGates.Mutations;

/// <summary>
/// One area's named JSON mutations of a gate fixture report. The
/// <see cref="PerformanceGateMutationCatalog"/> decides which report each table
/// is applied to.
/// </summary>
internal sealed class PerformanceGateReportMutations
{
    private static readonly IReadOnlyDictionary<string, Action<JsonObject>> None =
        new Dictionary<string, Action<JsonObject>>();

    /// <summary>
    /// Mutations named by their candidate name. Each one also exists as
    /// "reference-" plus that name, which makes the same change to the
    /// baseline (reference) report instead.
    /// </summary>
    public IReadOnlyDictionary<string, Action<JsonObject>> Shared { get; init; } = None;

    /// <summary>Mutations of the candidate report only.</summary>
    public IReadOnlyDictionary<string, Action<JsonObject>> CandidateOnly { get; init; } = None;

    /// <summary>Mutations of the baseline (reference) report only, under their full name.</summary>
    public IReadOnlyDictionary<string, Action<JsonObject>> ReferenceOnly { get; init; } = None;

    /// <summary>Mutations that make the same change to both reports.</summary>
    public IReadOnlyDictionary<string, Action<JsonObject>> BothReports { get; init; } = None;
}
