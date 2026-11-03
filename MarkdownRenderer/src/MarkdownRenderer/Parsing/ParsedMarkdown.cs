using Markdig.Syntax;

namespace MarkdownRenderer.Parsing;

internal sealed class ParsedMarkdown(string sourceText, MarkdownDocument document)
{
    internal readonly string SourceText = sourceText;
    internal readonly MarkdownDocument Document = document;
}
