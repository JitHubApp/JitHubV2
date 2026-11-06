namespace MarkdownRenderer.Parsing;

/// <summary>
/// Renderer-internal copy of the optional pack's immutable resource ceilings.
/// Keeping this value in the base assembly avoids a dependency from the lean
/// renderer onto <c>MarkdownRenderer.Html</c>.
/// </summary>
internal readonly struct SafeHtmlParseLimits
{
    public SafeHtmlParseLimits(
        int MaxInputLength,
        int MaxNodeCount,
        int MaxNestingDepth,
        int MaxAttributeCount,
        int MaxAttributeValueLength,
        int MaxTagLength)
    {
        this.MaxInputLength = MaxInputLength;
        this.MaxNodeCount = MaxNodeCount;
        this.MaxNestingDepth = MaxNestingDepth;
        this.MaxAttributeCount = MaxAttributeCount;
        this.MaxAttributeValueLength = MaxAttributeValueLength;
        this.MaxTagLength = MaxTagLength;
    }

    public int MaxInputLength { get; init; }
    public int MaxNodeCount { get; init; }
    public int MaxNestingDepth { get; init; }
    public int MaxAttributeCount { get; init; }
    public int MaxAttributeValueLength { get; init; }
    public int MaxTagLength { get; init; }

    internal static SafeHtmlParseLimits Default { get; } = new(
        SafeHtmlParser.MaxInputLength,
        SafeHtmlParser.MaxNodeCount,
        SafeHtmlParser.MaxNestingDepth,
        SafeHtmlParser.MaxAttributeCount,
        SafeHtmlParser.MaxAttributeValueLength,
        SafeHtmlParser.MaxTagLength);
}
