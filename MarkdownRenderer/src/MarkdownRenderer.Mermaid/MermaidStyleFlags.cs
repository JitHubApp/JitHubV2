namespace MarkdownRenderer.Mermaid;

[Flags]
internal enum MermaidStyleFlags : uint
{
    None = 0,
    NonScalingStroke = 1 << 0,
    RoundLineCap = 1 << 1,
    RoundLineJoin = 1 << 2,
}
