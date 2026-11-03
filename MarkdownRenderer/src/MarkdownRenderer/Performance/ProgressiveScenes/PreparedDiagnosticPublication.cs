using System.Collections.Immutable;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Performance.ProgressiveScenes;

/// <summary>A diagnostics snapshot computed off the UI thread for one publication version.</summary>
internal sealed class PreparedDiagnosticPublication(
    long version,
    ImmutableDictionary<string, ImmutableArray<MarkdownDiagnostic>> contributions,
    ImmutableList<MarkdownDiagnostic> diagnostics)
{
    internal long Version { get; } = version;
    internal ImmutableDictionary<string, ImmutableArray<MarkdownDiagnostic>> Contributions { get; } = contributions;
    internal ImmutableList<MarkdownDiagnostic> Diagnostics { get; } = diagnostics;
}
