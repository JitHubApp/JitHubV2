using System.Collections.Generic;
using MarkdownRenderer.Document;

namespace MarkdownRenderer.Engine.Internal;

/// <summary>One admitted entry of <see cref="MarkdownCompletedParseCache"/>.</summary>
internal sealed class MarkdownCompletedParse
{
    internal MarkdownCompletedParse(
        MarkdownParseSourceKey key,
        MarkdownDocument document,
        long weightBytes,
        long parseGeneration)
    {
        Key = key;
        Document = document;
        WeightBytes = weightBytes;
        ParseGeneration = parseGeneration;
    }

    internal readonly MarkdownDocument Document;

    internal readonly MarkdownParseSourceKey Key;

    internal readonly long ParseGeneration;

    internal LinkedListNode<MarkdownCompletedParse> RecencyNode { get; set; } = null!;

    internal readonly long WeightBytes;

    internal bool WasObserved { get; set; }
}
