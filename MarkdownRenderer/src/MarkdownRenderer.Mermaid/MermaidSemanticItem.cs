namespace MarkdownRenderer.Mermaid;

/// <summary>Accessible diagram semantics independent of UI Automation or any drawing backend.</summary>
internal readonly record struct MermaidSemanticItem(
    uint SourceId,
    MermaidSemanticRole Role,
    int NameIndex,
    int DescriptionIndex,
    int ParentIndex,
    int SourceMappingIndex,
    MermaidSemanticFlags Flags);
