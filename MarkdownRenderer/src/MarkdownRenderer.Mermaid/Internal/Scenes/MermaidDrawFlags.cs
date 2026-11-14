namespace MarkdownRenderer.Mermaid.Internal.Scenes;

[Flags]
internal enum MermaidDrawFlags : ushort
{
    None = 0,
    EvenOddFill = 1 << 0,
    ClosedGeometry = 1 << 1,
    Decorative = 1 << 2,
}
