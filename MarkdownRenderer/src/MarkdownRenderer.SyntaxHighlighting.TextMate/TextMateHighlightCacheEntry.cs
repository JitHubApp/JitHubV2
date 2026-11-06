namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>One retained highlight result and its estimated cache weight in bytes.</summary>
internal sealed record TextMateHighlightCacheEntry(
    TextMateHighlightKey Key,
    TextMateGrammarHighlightResult Result,
    long Weight);