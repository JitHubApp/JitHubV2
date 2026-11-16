namespace MarkdownRenderer.Parsing.Internal.SafeHtml;

internal enum SafeHtmlTagKind
{
    Opening,
    Closing,
    SelfClosing,
    Comment,
    Declaration,
}
