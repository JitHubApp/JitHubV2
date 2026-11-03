namespace MarkdownRenderer.Mermaid;

/// <summary>A mapping from a native source ID to a public UTF-16 source range.</summary>
internal readonly record struct MermaidSceneSourceMapping(
    uint SourceId,
    MermaidSourceRange SourceRange,
    int SemanticIndex);
