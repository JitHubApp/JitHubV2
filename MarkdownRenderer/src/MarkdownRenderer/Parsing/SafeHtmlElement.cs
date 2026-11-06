using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Parsing;

internal sealed class SafeHtmlElement : SafeHtmlNode
{
    private readonly IReadOnlyDictionary<string, string> _attributes;
    private readonly List<SafeHtmlNode> _children = [];

    public SafeHtmlElement(
        string name,
        IReadOnlyDictionary<string, string>? attributes,
        int sourceStart,
        int sourceLength,
        string? rawOpeningTag = null)
        : base(sourceStart, sourceLength)
    {
        Name = name ?? string.Empty;
        _attributes = attributes ?? EmptyAttributes;
        RawOpeningTag = rawOpeningTag ?? string.Empty;
    }

    private static IReadOnlyDictionary<string, string> EmptyAttributes { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Name { get; }

    public IReadOnlyDictionary<string, string> Attributes => _attributes;

    public IReadOnlyList<SafeHtmlNode> Children => _children;

    public string RawOpeningTag { get; }

    public string RawClosingTag { get; private set; } = string.Empty;

    public int RawClosingStart { get; private set; }

    public string RawTrailingMarkup { get; private set; } = string.Empty;

    public int RawTrailingStart { get; private set; }

    public bool TryGetAttribute(string name, out string value)
    {
        if (_attributes.TryGetValue(name, out string? candidate))
        {
            value = candidate;
            return true;
        }

        value = string.Empty;
        return false;
    }

    internal void Add(SafeHtmlNode node) => _children.Add(node);

    internal void SetClosingTag(string value, int sourceStart)
    {
        RawClosingTag = value ?? string.Empty;
        RawClosingStart = Math.Max(0, sourceStart);
    }

    internal void SetTrailingMarkup(string value, int sourceStart)
    {
        RawTrailingMarkup = value ?? string.Empty;
        RawTrailingStart = Math.Max(0, sourceStart);
    }
}
