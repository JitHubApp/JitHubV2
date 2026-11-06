namespace MarkdownRenderer.Mermaid;

[Flags]
internal enum MermaidLinkFlags : uint
{
    None = 0,
    External = 1 << 0,
    Action = 1 << 1,
}
