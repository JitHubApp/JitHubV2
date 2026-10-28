using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Document;

/// <summary>
/// Logical position in the rendered markdown document.
/// </summary>
/// <param name="BlockIndex">One-based layout block index.</param>
/// <param name="InlineIndex">Zero-based inline run index within the block.</param>
/// <param name="CharacterOffset">Zero-based character offset within the inline run.</param>
public readonly record struct DocumentPosition(int BlockIndex, int InlineIndex, int CharacterOffset)
    : IComparable<DocumentPosition>
{
    /// <summary>Gets the zero position.</summary>
    public static readonly DocumentPosition Zero = new(0, 0, 0);

    /// <inheritdoc />
    public int CompareTo(DocumentPosition other)
    {
        var c = BlockIndex.CompareTo(other.BlockIndex);
        if (c != 0) return c;
        c = InlineIndex.CompareTo(other.InlineIndex);
        if (c != 0) return c;
        return CharacterOffset.CompareTo(other.CharacterOffset);
    }

    /// <summary>Returns true when <paramref name="a"/> is before <paramref name="b"/>.</summary>
    public static bool operator <(DocumentPosition a, DocumentPosition b) => a.CompareTo(b) < 0;

    /// <summary>Returns true when <paramref name="a"/> is after <paramref name="b"/>.</summary>
    public static bool operator >(DocumentPosition a, DocumentPosition b) => a.CompareTo(b) > 0;

    /// <summary>Returns true when <paramref name="a"/> is before or equal to <paramref name="b"/>.</summary>
    public static bool operator <=(DocumentPosition a, DocumentPosition b) => a.CompareTo(b) <= 0;

    /// <summary>Returns true when <paramref name="a"/> is after or equal to <paramref name="b"/>.</summary>
    public static bool operator >=(DocumentPosition a, DocumentPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// Logical selection range in the rendered markdown document.
/// </summary>
/// <param name="Start">Range start position.</param>
/// <param name="End">Range end position.</param>
public readonly record struct DocumentRange(DocumentPosition Start, DocumentPosition End)
{
    /// <summary>Gets whether the range contains no characters.</summary>
    public bool IsEmpty => Start.CompareTo(End) == 0;

    /// <summary>Returns the range with start and end sorted in document order.</summary>
    public DocumentRange Normalized() => Start <= End ? this : new DocumentRange(End, Start);

    /// <summary>Gets an empty range.</summary>
    public static DocumentRange Empty => new(DocumentPosition.Zero, DocumentPosition.Zero);
}
