namespace MarkdownRenderer.Mermaid.Internal.Scenes;

/// <summary>A device-independent viewport in Mermaid scene coordinates.</summary>
internal readonly record struct MermaidViewport(float X, float Y, float Width, float Height);
