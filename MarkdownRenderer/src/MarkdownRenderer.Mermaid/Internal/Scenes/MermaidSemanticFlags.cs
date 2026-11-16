namespace MarkdownRenderer.Mermaid.Internal.Scenes;

[Flags]
internal enum MermaidSemanticFlags : uint
{
    None = 0,
    Focusable = 1 << 0,
    Selectable = 1 << 1,
    Linked = 1 << 2,
    Decorative = 1 << 3,
}
