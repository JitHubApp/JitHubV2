using System.Net;

namespace MarkdownRenderer.Parsing;

internal sealed class SafeHtmlText : SafeHtmlNode
{
    public SafeHtmlText(string rawText, int sourceStart, int sourceLength)
        : base(sourceStart, sourceLength)
    {
        RawText = rawText ?? string.Empty;
    }

    public string RawText { get; }

    public string DecodedText => WebUtility.HtmlDecode(RawText) ?? string.Empty;
}
