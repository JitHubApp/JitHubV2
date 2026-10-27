// Spans into the original markdown source text. Used for round-tripping
// rendered selections back to the exact markdown substring.
namespace MarkdownRenderer;

/// <summary>
/// A contiguous span in the original markdown source text.
/// </summary>
public readonly record struct SourceSpan(int Start, int Length)
{
    /// <summary>Gets the first source offset after this span.</summary>
    public int End => Start + Length;

    /// <summary>Gets whether this span has zero length.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Gets an empty source span.</summary>
    public static SourceSpan Empty => default;

    /// <summary>Returns true when <paramref name="position"/> is inside this span.</summary>
    public bool Contains(int position) => position >= Start && position < End;

    /// <summary>Returns the smallest span that contains this span and <paramref name="other"/>.</summary>
    public SourceSpan Union(SourceSpan other)
    {
        if (other.IsEmpty) return this;
        if (IsEmpty) return other;
        var s = System.Math.Min(Start, other.Start);
        var e = System.Math.Max(End, other.End);
        return new SourceSpan(s, e - s);
    }
}
