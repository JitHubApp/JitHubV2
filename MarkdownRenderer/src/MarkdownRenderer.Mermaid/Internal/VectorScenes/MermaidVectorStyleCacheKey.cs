using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Mermaid.Internal.Scenes;

namespace MarkdownRenderer.Mermaid.Internal.VectorScenes;

/// <summary>
/// Identifies one converted paint style: an authored MMIR style restricted to a
/// draw opcode and tagged with its High Contrast fill and stroke roles.
/// </summary>
internal readonly record struct MermaidVectorStyleCacheKey(
    int StyleIndex,
    MermaidDrawOpcode Opcode,
    MarkdownVectorPaintRole HighContrastFillRole,
    MarkdownVectorPaintRole HighContrastStrokeRole);