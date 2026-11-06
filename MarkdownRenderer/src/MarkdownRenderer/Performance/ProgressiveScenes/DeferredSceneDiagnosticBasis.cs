using System.Collections.Immutable;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Performance.ProgressiveScenes;

/// <summary>
/// The diagnostics state one producer replaces off the plan gate, captured
/// together with the publication version it claimed.
/// </summary>
internal readonly record struct DeferredSceneDiagnosticBasis(
    ImmutableDictionary<string, ImmutableArray<MarkdownDiagnostic>> Contributions,
    ImmutableList<MarkdownDiagnostic> Diagnostics,
    ImmutableArray<MarkdownDiagnostic> PreviousContribution,
    long Version);
