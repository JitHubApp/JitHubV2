namespace MarkdownRenderer.Math;

/// <summary>
/// A half-open range of UTF-16 code units in the original Markdown source.
/// </summary>
public readonly record struct MathSourceRange
{
    /// <summary>Creates a checked half-open UTF-16 source range.</summary>
    public MathSourceRange(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _ = checked(start + length);

        Start = start;
        Length = length;
    }

    /// <summary>Gets the inclusive UTF-16 start offset.</summary>
    public int Start { get; }

    /// <summary>Gets the number of UTF-16 code units in the range.</summary>
    public int Length { get; }

    /// <summary>Gets the exclusive UTF-16 end offset.</summary>
    public int End => Start + Length;

    /// <summary>Gets whether the range contains no code units.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Tests whether another half-open range is wholly contained.</summary>
    public bool Contains(MathSourceRange range) =>
        range.Start >= Start && range.End <= End;

    /// <summary>Creates a checked range from inclusive start and exclusive end offsets.</summary>
    public static MathSourceRange FromBounds(int start, int end)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "End must not precede start.");
        }

        return new MathSourceRange(start, checked(end - start));
    }

    /// <summary>Returns the represented span from the supplied original source.</summary>
    public ReadOnlySpan<char> Slice(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (End > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "The range extends beyond the supplied source.");
        }

        return source.AsSpan(Start, Length);
    }
}
