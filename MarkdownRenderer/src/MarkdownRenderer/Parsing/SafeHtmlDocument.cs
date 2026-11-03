namespace MarkdownRenderer.Parsing;

internal sealed class SafeHtmlDocument
{
    public SafeHtmlDocument(SafeHtmlElement root, bool isTruncated)
    {
        Root = root;
        IsTruncated = isTruncated;
    }

    public SafeHtmlElement Root { get; }

    public bool IsTruncated { get; }
}
