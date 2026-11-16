using System.Collections.Generic;

namespace MarkdownRenderer.Parsing.Internal.SafeHtml;

internal readonly struct SafeHtmlTag
{
    public SafeHtmlTag(
        SafeHtmlTagKind kind,
        string name,
        IReadOnlyDictionary<string, string> attributes,
        int sourceStart,
        int sourceLength)
    {
        Kind = kind;
        Name = name;
        Attributes = attributes;
        SourceStart = sourceStart;
        SourceLength = sourceLength;
    }

    public readonly SafeHtmlTagKind Kind;

    public readonly string Name;

    public readonly IReadOnlyDictionary<string, string> Attributes;

    public readonly int SourceStart;

    public readonly int SourceLength;

    public bool TryGetAttribute(string name, out string value)
    {
        if (Attributes.TryGetValue(name, out string? candidate))
        {
            value = candidate;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
