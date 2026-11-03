using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Mermaid;

/// <summary>
/// Identifies one converted paint style: an authored MMIR style restricted to a
/// draw opcode and tagged with its High Contrast fill and stroke roles.
/// </summary>
internal readonly record struct MermaidVectorStyleCacheKey(
    int StyleIndex,
    MermaidDrawOpcode Opcode,
    MarkdownVectorPaintRole HighContrastFillRole,
    MarkdownVectorPaintRole HighContrastStrokeRole);