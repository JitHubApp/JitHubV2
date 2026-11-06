using System;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>One foreground-color span returned by a grammar provider.</summary>
public readonly record struct TextMateGrammarHighlightSpan
{
    /// <summary>Creates a validated, half-open UTF-16 token span.</summary>
    public TextMateGrammarHighlightSpan(int start, int length, uint foregroundArgb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        Start = start;
        Length = length;
        ForegroundArgb = foregroundArgb;
    }

    /// <summary>Zero-based UTF-16 start offset.</summary>
    public int Start { get; }

    /// <summary>UTF-16 length.</summary>
    public int Length { get; }

    /// <summary>Token foreground encoded as 0xAARRGGBB.</summary>
    public uint ForegroundArgb { get; }
}
