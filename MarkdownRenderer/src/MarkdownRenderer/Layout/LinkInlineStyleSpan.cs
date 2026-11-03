using System;

namespace MarkdownRenderer.Layout;

internal readonly struct LinkInlineStyleSpan
{
    internal LinkInlineStyleSpan(int start, int length, LinkInlineStylePath path)
    {
        Start = start;
        Length = length;
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    internal int Start { get; }
    internal int Length { get; }
    internal LinkInlineStylePath Path { get; }
}
