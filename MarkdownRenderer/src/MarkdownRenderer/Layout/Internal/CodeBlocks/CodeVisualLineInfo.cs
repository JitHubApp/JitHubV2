namespace MarkdownRenderer.Layout.Internal.CodeBlocks;

/// <summary>One wrapped visual row of a code block and the logical line it belongs to.</summary>
internal readonly struct CodeVisualLineInfo(
    int logicalLineIndex,
    double top,
    double height)
{
    public readonly int LogicalLineIndex = logicalLineIndex;
    public readonly double Top = top;
    public readonly double Height = height;
}
