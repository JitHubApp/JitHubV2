namespace MarkdownRenderer.Mermaid;

/// <summary>A flattened draw operation. Indices use -1 when a referenced item is absent.</summary>
internal readonly record struct MermaidDrawCommand(
    MermaidDrawOpcode Opcode,
    MermaidDrawFlags Flags,
    int StyleIndex,
    int SemanticIndex,
    int TextIndex,
    int GeometryStart,
    int GeometryCount,
    int FontFamilyIndex,
    MermaidTextFlags TextFlags);
