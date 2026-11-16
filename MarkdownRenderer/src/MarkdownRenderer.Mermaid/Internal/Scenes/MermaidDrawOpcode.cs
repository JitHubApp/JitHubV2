namespace MarkdownRenderer.Mermaid.Internal.Scenes;

internal enum MermaidDrawOpcode : ushort
{
    GroupBegin = 1,
    GroupEnd = 2,
    ClipBegin = 3,
    ClipEnd = 4,
    FillPath = 5,
    StrokePath = 6,
    Line = 7,
    Rectangle = 8,
    Ellipse = 9,
    Text = 10,
}
