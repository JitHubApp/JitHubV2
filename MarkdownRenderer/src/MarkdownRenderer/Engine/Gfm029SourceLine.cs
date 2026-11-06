namespace MarkdownRenderer;

/// <summary>One physical source line found by <see cref="Gfm029TableSourcePreparer"/>.</summary>
internal readonly struct Gfm029SourceLine(int start, int length)
{
    internal readonly int Start = start;

    internal readonly int Length = length;
}
