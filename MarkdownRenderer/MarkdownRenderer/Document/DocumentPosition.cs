using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Document;

public readonly record struct DocumentPosition(int BlockIndex, int InlineIndex, int CharacterOffset)
    : IComparable<DocumentPosition>
{
    public static readonly DocumentPosition Zero = new(0, 0, 0);

    public int CompareTo(DocumentPosition other)
    {
        var c = BlockIndex.CompareTo(other.BlockIndex);
        if (c != 0) return c;
        c = InlineIndex.CompareTo(other.InlineIndex);
        if (c != 0) return c;
        return CharacterOffset.CompareTo(other.CharacterOffset);
    }

    public static bool operator <(DocumentPosition a, DocumentPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(DocumentPosition a, DocumentPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(DocumentPosition a, DocumentPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(DocumentPosition a, DocumentPosition b) => a.CompareTo(b) >= 0;
}

public readonly record struct DocumentRange(DocumentPosition Start, DocumentPosition End)
{
    public bool IsEmpty => Start.CompareTo(End) == 0;
    public DocumentRange Normalized() => Start <= End ? this : new DocumentRange(End, Start);
    public static DocumentRange Empty => new(DocumentPosition.Zero, DocumentPosition.Zero);
}
