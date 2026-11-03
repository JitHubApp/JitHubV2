namespace MarkdownRenderer.Parsing;

internal enum SafeHtmlTagKind
{
    Opening,
    Closing,
    SelfClosing,
    Comment,
    Declaration,
}
