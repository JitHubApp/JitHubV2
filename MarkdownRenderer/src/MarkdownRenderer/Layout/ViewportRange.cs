namespace MarkdownRenderer.Layout;

internal readonly struct ViewportRange(int Start, int End)
{
    internal readonly int Start = Start;
    internal readonly int End = End;
}
