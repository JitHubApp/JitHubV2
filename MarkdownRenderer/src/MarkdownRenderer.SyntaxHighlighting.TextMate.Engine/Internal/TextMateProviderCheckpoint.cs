namespace MarkdownRenderer.SyntaxHighlighting.TextMate.Engine.Internal;

internal enum TextMateProviderCheckpoint
{
    BeforeGrammarLoad,
    AfterGrammarLoad,
    BeforeLineTokenization,
    AfterLineTokenizationSlice,
    AfterLineTokenization,
}
