using System.Collections.ObjectModel;
using MarkdownRenderer.Mermaid.Internal.Scenes;

namespace MarkdownRenderer.Mermaid.Internal.Rendering;

/// <summary>The result of a native Mermaid request. A non-success result always carries the original code-block fallback.</summary>
internal sealed class MermaidRenderResult
{
    private readonly ReadOnlyCollection<MermaidDiagnostic> _diagnostics;

    internal MermaidRenderResult(
        MermaidRenderStatus status,
        MermaidScene? scene,
        IEnumerable<MermaidDiagnostic>? diagnostics,
        string originalSource)
    {
        Status = status;
        Scene = scene;
        _diagnostics = Array.AsReadOnly(diagnostics?.ToArray() ?? []);
        Fallback = new MermaidCodeBlockFallback("mermaid", originalSource);
    }

    public MermaidRenderStatus Status { get; }

    public MermaidScene? Scene { get; }

    public IReadOnlyList<MermaidDiagnostic> Diagnostics => _diagnostics;

    public MermaidCodeBlockFallback Fallback { get; }

    public bool ShouldUseFallback => Status != MermaidRenderStatus.Success || Scene is null;
}
