// Spans into the original markdown source text. Used for round-tripping
// rendered selections back to the exact markdown substring.
namespace MarkdownRenderer;

/// <summary>
/// A contiguous span in the original markdown source text.
/// </summary>
public readonly record struct SourceSpan(int Start, int Length)
{
    public int End => Start + Length;
    public bool IsEmpty => Length == 0;
    public static SourceSpan Empty => default;

    public bool Contains(int position) => position >= Start && position < End;

    public SourceSpan Union(SourceSpan other)
    {
        if (other.IsEmpty) return this;
        if (IsEmpty) return other;
        var s = System.Math.Min(Start, other.Start);
        var e = System.Math.Max(End, other.End);
        return new SourceSpan(s, e - s);
    }
}
