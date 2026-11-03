namespace MarkdownRenderer.Mermaid;

/// <summary>A declarative host-approved link or action. No executable callback crosses the native boundary.</summary>
internal readonly record struct MermaidSceneLink(
    int SemanticIndex,
    int TargetIndex,
    int ActionIndex,
    MermaidLinkFlags Flags);
