using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

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
