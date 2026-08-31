using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;

namespace MarkdownRenderer.Parsing;

internal enum SafeHtmlTagKind
{
    Opening,
    Closing,
    SelfClosing,
    Comment,
    Declaration,
}

internal enum SafeHtmlAlignment
{
    Inherit,
    Left,
    Center,
    Right,
}

internal readonly record struct SafeHtmlLength(float Value, bool IsPercent)
{
    public float Resolve(float available) => IsPercent
        ? Math.Max(0, available) * Math.Clamp(Value, 0, 100) / 100f
        : Math.Max(0, Value);
}

internal abstract class SafeHtmlNode
{
    protected SafeHtmlNode(int sourceStart, int sourceLength)
    {
        SourceStart = Math.Max(0, sourceStart);
        SourceLength = Math.Max(0, sourceLength);
    }

    public int SourceStart { get; }

    public int SourceLength { get; }
}

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

internal sealed class SafeHtmlElement : SafeHtmlNode
{
    private readonly IReadOnlyDictionary<string, string> _attributes;
    private readonly List<SafeHtmlNode> _children = [];

    public SafeHtmlElement(
        string name,
        IReadOnlyDictionary<string, string>? attributes,
        int sourceStart,
        int sourceLength)
        : base(sourceStart, sourceLength)
    {
        Name = name ?? string.Empty;
        _attributes = attributes ?? EmptyAttributes;
    }

    private static IReadOnlyDictionary<string, string> EmptyAttributes { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Name { get; }

    public IReadOnlyDictionary<string, string> Attributes => _attributes;

    public IReadOnlyList<SafeHtmlNode> Children => _children;

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
}

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

    public SafeHtmlTagKind Kind { get; }

    public string Name { get; }

    public IReadOnlyDictionary<string, string> Attributes { get; }

    public int SourceStart { get; }

    public int SourceLength { get; }

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

internal static class SafeHtmlParser
{
    internal const int MaxInputLength = 4 * 1024 * 1024;
    internal const int MaxNodeCount = 20_000;
    internal const int MaxNestingDepth = 64;
    internal const int MaxAttributeCount = 32;
    internal const int MaxAttributeValueLength = 16 * 1024;
    private const int MaxTagLength = 64 * 1024;

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta",
        "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawContentElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "noscript", "template",
    };

    public static SafeHtmlDocument Parse(string? html)
    {
        string source = html ?? string.Empty;
        int parseLength = Math.Min(source.Length, MaxInputLength);
        bool truncated = source.Length > parseLength;
        var root = new SafeHtmlElement("#document", null, 0, parseLength);
        List<SafeHtmlElement> stack = [root];
        int nodeCount = 1;
        int position = 0;

        while (position < parseLength && nodeCount < MaxNodeCount)
        {
            int tagStart = source.IndexOf('<', position, parseLength - position);
            if (tagStart < 0)
            {
                AddText(source, position, parseLength - position, stack[^1], ref nodeCount);
                position = parseLength;
                break;
            }

            if (tagStart > position)
            {
                AddText(source, position, tagStart - position, stack[^1], ref nodeCount);
                if (nodeCount >= MaxNodeCount)
                {
                    break;
                }
            }

            if (!TryReadTag(source, tagStart, parseLength, out SafeHtmlTag tag, out int endExclusive))
            {
                AddText(source, tagStart, 1, stack[^1], ref nodeCount);
                position = tagStart + 1;
                continue;
            }

            position = endExclusive;
            if (tag.Kind is SafeHtmlTagKind.Comment or SafeHtmlTagKind.Declaration)
            {
                continue;
            }

            if (tag.Kind == SafeHtmlTagKind.Closing)
            {
                PopThroughMatchingElement(stack, tag.Name);
                continue;
            }

            var element = new SafeHtmlElement(
                tag.Name,
                tag.Attributes,
                tag.SourceStart,
                tag.SourceLength);
            stack[^1].Add(element);
            nodeCount++;

            if (tag.Kind == SafeHtmlTagKind.SelfClosing || VoidElements.Contains(tag.Name))
            {
                continue;
            }

            if (RawContentElements.Contains(tag.Name))
            {
                position = SkipRawContent(source, position, parseLength, tag.Name);
                continue;
            }

            if (stack.Count < MaxNestingDepth)
            {
                stack.Add(element);
            }
            else
            {
                truncated = true;
            }
        }

        if (position < parseLength || nodeCount >= MaxNodeCount)
        {
            truncated = true;
        }

        return new SafeHtmlDocument(root, truncated);
    }

    public static bool TryParseSingleTag(string? text, out SafeHtmlTag tag)
    {
        string source = text?.Trim() ?? string.Empty;
        if (!TryReadTag(source, 0, source.Length, out tag, out int endExclusive))
        {
            return false;
        }

        for (int index = endExclusive; index < source.Length; index++)
        {
            if (!char.IsWhiteSpace(source[index]))
            {
                tag = default;
                return false;
            }
        }

        return true;
    }

    public static bool TryParseTagSequence(string? html, out IReadOnlyList<SafeHtmlTag> tags)
    {
        string source = html ?? string.Empty;
        List<SafeHtmlTag> parsed = [];
        int position = 0;

        while (position < source.Length)
        {
            while (position < source.Length && char.IsWhiteSpace(source[position]))
            {
                position++;
            }

            if (position >= source.Length)
            {
                break;
            }

            if (!TryReadTag(source, position, source.Length, out SafeHtmlTag tag, out int endExclusive))
            {
                tags = Array.Empty<SafeHtmlTag>();
                return false;
            }

            if (tag.Kind is not (SafeHtmlTagKind.Comment or SafeHtmlTagKind.Declaration))
            {
                parsed.Add(tag);
            }

            position = endExclusive;
        }

        tags = parsed;
        return parsed.Count > 0;
    }

    public static IReadOnlyList<SafeHtmlTag> ParseTags(string? html)
    {
        string source = html ?? string.Empty;
        int parseLength = Math.Min(source.Length, MaxInputLength);
        List<SafeHtmlTag> parsed = [];
        int position = 0;
        while (position < parseLength && parsed.Count < MaxNodeCount)
        {
            int tagStart = source.IndexOf('<', position, parseLength - position);
            if (tagStart < 0)
            {
                break;
            }

            if (TryReadTag(source, tagStart, parseLength, out SafeHtmlTag tag, out int endExclusive))
            {
                if (tag.Kind is not (SafeHtmlTagKind.Comment or SafeHtmlTagKind.Declaration))
                {
                    parsed.Add(tag);
                }

                position = endExclusive;
            }
            else
            {
                position = tagStart + 1;
            }
        }

        return parsed;
    }

    public static SafeHtmlAlignment GetAlignment(SafeHtmlElement element) =>
        element.TryGetAttribute("align", out string value)
            ? ParseAlignment(value)
            : SafeHtmlAlignment.Inherit;

    public static SafeHtmlAlignment GetAlignment(SafeHtmlTag tag) =>
        tag.TryGetAttribute("align", out string value)
            ? ParseAlignment(value)
            : SafeHtmlAlignment.Inherit;

    public static bool TryGetLength(SafeHtmlElement element, string name, out SafeHtmlLength length)
    {
        if (element.TryGetAttribute(name, out string value) && TryParseLength(value, out length))
        {
            return true;
        }

        if (element.TryGetAttribute("style", out string style) &&
            TryGetStyleDeclaration(style, name, out value) &&
            TryParseLength(value, out length))
        {
            return true;
        }

        length = default;
        return false;
    }

    public static bool TryGetLength(SafeHtmlTag tag, string name, out SafeHtmlLength length)
    {
        if (tag.TryGetAttribute(name, out string value) && TryParseLength(value, out length))
        {
            return true;
        }

        if (tag.TryGetAttribute("style", out string style) &&
            TryGetStyleDeclaration(style, name, out value) &&
            TryParseLength(value, out length))
        {
            return true;
        }

        length = default;
        return false;
    }

    public static bool TryGetSafeLink(SafeHtmlElement element, out string url)
    {
        url = string.Empty;
        return element.TryGetAttribute("href", out string value) &&
            TryNormalizeUrl(value, isImage: false, out url);
    }

    public static bool TryGetSafeLink(SafeHtmlTag tag, out string url)
    {
        url = string.Empty;
        return tag.TryGetAttribute("href", out string value) &&
            TryNormalizeUrl(value, isImage: false, out url);
    }

    public static bool TryGetSafeImageSource(SafeHtmlElement element, out string source)
    {
        source = string.Empty;
        return element.TryGetAttribute("src", out string value) &&
            TryNormalizeUrl(value, isImage: true, out source);
    }

    public static bool TryGetSafeImageSource(SafeHtmlTag tag, out string source)
    {
        source = string.Empty;
        return tag.TryGetAttribute("src", out string value) &&
            TryNormalizeUrl(value, isImage: true, out source);
    }

    public static bool TryNormalizeImageSource(string value, out string source) =>
        TryNormalizeUrl(value, isImage: true, out source);

    public static bool IsSuppressedElement(string name) => RawContentElements.Contains(name);

    public static string CollapseWhitespace(string? text)
    {
        string value = WebUtility.HtmlDecode(text ?? string.Empty) ?? string.Empty;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[value.Length];
        int written = 0;
        bool pendingWhitespace = false;
        foreach (char character in value)
        {
            if (character == '\u00A0')
            {
                if (pendingWhitespace && written > 0)
                {
                    buffer[written++] = ' ';
                }

                pendingWhitespace = false;
                buffer[written++] = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = true;
                continue;
            }

            if (pendingWhitespace && written > 0)
            {
                buffer[written++] = ' ';
            }

            pendingWhitespace = false;
            buffer[written++] = character;
        }

        return new string(buffer, 0, written);
    }

    private static void AddText(
        string source,
        int start,
        int length,
        SafeHtmlElement parent,
        ref int nodeCount)
    {
        if (length <= 0 || nodeCount >= MaxNodeCount)
        {
            return;
        }

        parent.Add(new SafeHtmlText(source.Substring(start, length), start, length));
        nodeCount++;
    }

    private static void PopThroughMatchingElement(List<SafeHtmlElement> stack, string name)
    {
        for (int index = stack.Count - 1; index > 0; index--)
        {
            if (!stack[index].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            stack.RemoveRange(index, stack.Count - index);
            return;
        }
    }

    private static int SkipRawContent(string source, int position, int parseLength, string name)
    {
        string closingPrefix = $"</{name}";
        int closingStart = source.IndexOf(closingPrefix, position, parseLength - position, StringComparison.OrdinalIgnoreCase);
        if (closingStart < 0)
        {
            return parseLength;
        }

        int close = source.IndexOf('>', closingStart + closingPrefix.Length, parseLength - closingStart - closingPrefix.Length);
        return close < 0 ? parseLength : close + 1;
    }

    private static bool TryReadTag(
        string source,
        int start,
        int limit,
        out SafeHtmlTag tag,
        out int endExclusive)
    {
        tag = default;
        endExclusive = start;
        if (start < 0 || start >= limit || source[start] != '<')
        {
            return false;
        }

        if (StartsWith(source, start, limit, "<!--"))
        {
            int close = source.IndexOf("-->", start + 4, limit - start - 4, StringComparison.Ordinal);
            endExclusive = close < 0 ? limit : close + 3;
            tag = new SafeHtmlTag(
                SafeHtmlTagKind.Comment,
                string.Empty,
                EmptyTagAttributes,
                start,
                endExclusive - start);
            return true;
        }

        int closeIndex = FindTagClose(source, start + 1, limit);
        if (closeIndex < 0)
        {
            return false;
        }

        endExclusive = closeIndex + 1;
        int cursor = start + 1;
        SkipWhitespace(source, ref cursor, closeIndex);
        if (cursor >= closeIndex)
        {
            return false;
        }

        if (source[cursor] is '!' or '?')
        {
            tag = new SafeHtmlTag(
                SafeHtmlTagKind.Declaration,
                string.Empty,
                EmptyTagAttributes,
                start,
                endExclusive - start);
            return true;
        }

        bool closing = source[cursor] == '/';
        if (closing)
        {
            cursor++;
            SkipWhitespace(source, ref cursor, closeIndex);
        }

        int nameStart = cursor;
        while (cursor < closeIndex && IsNameCharacter(source[cursor]))
        {
            cursor++;
        }

        if (cursor == nameStart)
        {
            return false;
        }

        string name = source.Substring(nameStart, cursor - nameStart).ToLowerInvariant();
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool selfClosing = false;

        if (!closing)
        {
            ParseAttributes(source, ref cursor, closeIndex, attributes, out selfClosing);
        }

        tag = new SafeHtmlTag(
            closing
                ? SafeHtmlTagKind.Closing
                : selfClosing || VoidElements.Contains(name)
                    ? SafeHtmlTagKind.SelfClosing
                    : SafeHtmlTagKind.Opening,
            name,
            attributes,
            start,
            endExclusive - start);
        return true;
    }

    private static IReadOnlyDictionary<string, string> EmptyTagAttributes { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static int FindTagClose(string source, int start, int limit)
    {
        char quote = '\0';
        int max = Math.Min(limit, start + MaxTagLength);
        for (int index = start; index < max; index++)
        {
            char character = source[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static void ParseAttributes(
        string source,
        ref int cursor,
        int closeIndex,
        Dictionary<string, string> attributes,
        out bool selfClosing)
    {
        selfClosing = false;
        while (cursor < closeIndex)
        {
            SkipWhitespace(source, ref cursor, closeIndex);
            if (cursor >= closeIndex)
            {
                return;
            }

            if (source[cursor] == '/')
            {
                selfClosing = true;
                cursor++;
                continue;
            }

            int nameStart = cursor;
            while (cursor < closeIndex && IsAttributeNameCharacter(source[cursor]))
            {
                cursor++;
            }

            if (cursor == nameStart)
            {
                cursor++;
                continue;
            }

            string name = source.Substring(nameStart, cursor - nameStart).ToLowerInvariant();
            SkipWhitespace(source, ref cursor, closeIndex);
            string value = string.Empty;
            if (cursor < closeIndex && source[cursor] == '=')
            {
                cursor++;
                SkipWhitespace(source, ref cursor, closeIndex);
                value = ReadAttributeValue(source, ref cursor, closeIndex);
            }

            if (attributes.Count < MaxAttributeCount && !attributes.ContainsKey(name))
            {
                attributes[name] = WebUtility.HtmlDecode(value) ?? string.Empty;
            }
        }
    }

    private static string ReadAttributeValue(string source, ref int cursor, int closeIndex)
    {
        if (cursor >= closeIndex)
        {
            return string.Empty;
        }

        int valueStart;
        int valueLength;
        if (source[cursor] is '\'' or '"')
        {
            char quote = source[cursor++];
            valueStart = cursor;
            while (cursor < closeIndex && source[cursor] != quote)
            {
                cursor++;
            }

            valueLength = cursor - valueStart;
            if (cursor < closeIndex)
            {
                cursor++;
            }
        }
        else
        {
            valueStart = cursor;
            while (cursor < closeIndex &&
                   !char.IsWhiteSpace(source[cursor]) &&
                   source[cursor] != '>' &&
                   !(source[cursor] == '/' && cursor + 1 == closeIndex))
            {
                cursor++;
            }

            valueLength = cursor - valueStart;
        }

        valueLength = Math.Min(valueLength, MaxAttributeValueLength);
        return valueLength <= 0 ? string.Empty : source.Substring(valueStart, valueLength);
    }

    private static void SkipWhitespace(string source, ref int cursor, int limit)
    {
        while (cursor < limit && char.IsWhiteSpace(source[cursor]))
        {
            cursor++;
        }
    }

    private static bool IsNameCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or ':';

    private static bool IsAttributeNameCharacter(char character) =>
        IsNameCharacter(character) || character is '_' or '.';

    private static bool StartsWith(string source, int start, int limit, string value) =>
        start + value.Length <= limit &&
        source.AsSpan(start, value.Length).Equals(value.AsSpan(), StringComparison.Ordinal);

    private static SafeHtmlAlignment ParseAlignment(string value) => value.Trim().ToLowerInvariant() switch
    {
        "left" => SafeHtmlAlignment.Left,
        "center" or "middle" => SafeHtmlAlignment.Center,
        "right" => SafeHtmlAlignment.Right,
        _ => SafeHtmlAlignment.Inherit,
    };

    private static bool TryParseLength(string value, out SafeHtmlLength length)
    {
        string candidate = value.Trim();
        bool percent = candidate.EndsWith('%');
        if (percent)
        {
            candidate = candidate[..^1].Trim();
        }
        else if (candidate.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^2].Trim();
        }

        if (!float.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
            !float.IsFinite(parsed) ||
            parsed <= 0)
        {
            length = default;
            return false;
        }

        length = new SafeHtmlLength(percent ? Math.Min(parsed, 100) : Math.Min(parsed, 8192), percent);
        return true;
    }

    private static bool TryGetStyleDeclaration(string style, string name, out string value)
    {
        value = string.Empty;
        ReadOnlySpan<char> remaining = style.AsSpan();
        while (!remaining.IsEmpty)
        {
            int separator = remaining.IndexOf(';');
            ReadOnlySpan<char> declaration = separator < 0 ? remaining : remaining[..separator];
            remaining = separator < 0 ? ReadOnlySpan<char>.Empty : remaining[(separator + 1)..];
            int colon = declaration.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (!declaration[..colon].Trim().Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = declaration[(colon + 1)..].Trim().ToString();
            return true;
        }

        return false;
    }

    private static bool TryNormalizeUrl(string value, bool isImage, out string normalized)
    {
        normalized = (WebUtility.HtmlDecode(value) ?? string.Empty).Trim();
        if (normalized.Length == 0 || normalized.Length > MaxAttributeValueLength)
        {
            normalized = string.Empty;
            return false;
        }

        foreach (char character in normalized)
        {
            if (char.IsControl(character))
            {
                normalized = string.Empty;
                return false;
            }
        }

        if (!Uri.TryCreate(normalized, UriKind.RelativeOrAbsolute, out Uri? uri) || !uri.IsAbsoluteUri)
        {
            return true;
        }

        bool allowed = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            (!isImage && uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase)) ||
            (isImage && uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase) &&
             normalized.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase));
        if (allowed)
        {
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}
