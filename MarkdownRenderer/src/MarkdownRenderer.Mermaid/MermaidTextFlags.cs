namespace MarkdownRenderer.Mermaid;

[Flags]
internal enum MermaidTextFlags : uint
{
    None = 0,
    Italic = 1 << 0,
    RightToLeft = 1 << 1,
    AnchorMiddle = 1 << 2,
    AnchorEnd = 1 << 3,
}
