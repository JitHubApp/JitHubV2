using System;

namespace MarkdownRenderer.Parsing;

internal abstract class SafeHtmlNode
{
    protected SafeHtmlNode(int sourceStart, int sourceLength)
    {
        SourceStart = Math.Max(0, sourceStart);
        SourceLength = Math.Max(0, sourceLength);
    }

    public int SourceStart { get; }

    public int SourceLength { get; }
}
