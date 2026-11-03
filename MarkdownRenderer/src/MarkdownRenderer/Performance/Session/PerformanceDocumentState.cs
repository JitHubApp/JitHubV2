namespace MarkdownRenderer.Performance;

/// <summary>The scope a control's current document uses, with the inputs that keep it reusable.</summary>
internal sealed class PerformanceDocumentState(
    string source,
    int registryRevision,
    MarkdownPerformanceDocumentScope scope)
{
    internal string Source { get; } = source;
    internal int RegistryRevision { get; } = registryRevision;
    internal MarkdownPerformanceDocumentScope Scope { get; } = scope;
    internal bool PrefetchStarted { get; set; }
}
