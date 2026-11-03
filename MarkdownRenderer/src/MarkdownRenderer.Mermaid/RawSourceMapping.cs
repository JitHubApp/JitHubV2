namespace MarkdownRenderer.Mermaid;

/// <summary>A native source mapping in UTF-8 offsets, before translation to UTF-16.</summary>
internal readonly record struct RawSourceMapping(uint SourceId, int Utf8Start, int Utf8Length, uint SemanticIndex);
