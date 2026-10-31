using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>
/// Supplies TextMate grammar resources and performs tokenization for the lean
/// MarkdownRenderer TextMate integration package.
/// </summary>
/// <remarks>
/// The contract deliberately exposes no TextMateSharp types. Providers may use
/// TextMateSharp, another compatible engine, or application-owned grammars.
/// Implementations must perform expensive work away from the UI thread and
/// observe <see cref="CancellationToken"/> between lines or equivalent units of work.
/// </remarks>
public interface ITextMateGrammarProvider
{
    /// <summary>Revision used to invalidate cached highlighting results.</summary>
    int Revision => 0;

    /// <summary>
    /// Highlights a code block, returns an empty result when the language is not
    /// supported, or returns <see langword="null"/> when work is canceled.
    /// </summary>
    ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
        TextMateGrammarHighlightRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Input passed to an <see cref="ITextMateGrammarProvider"/>.</summary>
public sealed class TextMateGrammarHighlightRequest
{
    /// <summary>Creates a validated highlighting request.</summary>
    public TextMateGrammarHighlightRequest(
        string languageId,
        string code,
        TextMateGrammarThemeVariant themeVariant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageId);
        ArgumentNullException.ThrowIfNull(code);

        LanguageId = languageId;
        Code = code;
        ThemeVariant = themeVariant;
    }

    /// <summary>Normalized TextMate language identifier.</summary>
    public string LanguageId { get; }

    /// <summary>Code to tokenize.</summary>
    public string Code { get; }

    /// <summary>Requested editor theme family.</summary>
    public TextMateGrammarThemeVariant ThemeVariant { get; }
}

/// <summary>Theme family requested from a grammar provider.</summary>
public enum TextMateGrammarThemeVariant
{
    /// <summary>Light editor colors.</summary>
    Light,

    /// <summary>Dark editor colors.</summary>
    Dark,
}

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

/// <summary>Immutable output from a TextMate grammar provider.</summary>
public sealed class TextMateGrammarHighlightResult
{
    private static readonly IReadOnlyList<TextMateGrammarHighlightSpan> NoSpans =
        Array.Empty<TextMateGrammarHighlightSpan>();

    /// <summary>An empty successful result for unsupported languages.</summary>
    public static TextMateGrammarHighlightResult Empty { get; } = new(NoSpans);

    /// <summary>Creates an immutable highlighting result.</summary>
    public TextMateGrammarHighlightResult(IEnumerable<TextMateGrammarHighlightSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        Spans = spans is TextMateGrammarHighlightSpan[] array
            ? Array.AsReadOnly((TextMateGrammarHighlightSpan[])array.Clone())
            : Array.AsReadOnly(new List<TextMateGrammarHighlightSpan>(spans).ToArray());
    }

    /// <summary>Foreground spans in ascending source order.</summary>
    public IReadOnlyList<TextMateGrammarHighlightSpan> Spans { get; }
}
