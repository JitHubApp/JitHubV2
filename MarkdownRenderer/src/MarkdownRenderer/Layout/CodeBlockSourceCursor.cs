namespace MarkdownRenderer.Layout;

/// <summary>
/// Running position while projecting rendered code-block lines onto parser source.
/// </summary>
internal struct CodeBlockSourceCursor
{
    internal int LineIndex;
    internal int RenderedOffset;
    internal string? ParserSource;
}
