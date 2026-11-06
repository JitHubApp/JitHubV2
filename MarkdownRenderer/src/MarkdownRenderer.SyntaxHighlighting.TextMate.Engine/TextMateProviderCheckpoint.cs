namespace MarkdownRenderer.SyntaxHighlighting.TextMate.Engine;

internal enum TextMateProviderCheckpoint
{
    BeforeGrammarLoad,
    AfterGrammarLoad,
    BeforeLineTokenization,
    AfterLineTokenizationSlice,
    AfterLineTokenization,
}
