using System.Globalization;

namespace MarkdownRenderer.Mermaid;

/// <summary>Native layout selection. ELK is represented so hosts receive a deterministic unsupported result.</summary>
public enum MermaidLayoutMode
{
    NativeDefault,
    Dagre,
    TidyTree,
    ForceDirected,
    Elk,
}
