using System.Globalization;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal.StaticSnapshots;

/// <summary>
/// The attribute and inline-style allow-lists that a <c>foreignObject</c> text
/// subtree must satisfy before it is replaced by SVG text.
/// </summary>
internal static class SvgHtmlTextStyleRules
{
    internal static bool HasOnlySafeForeignObjectAttributes(
        XmlElement foreignObject,
        CancellationToken cancellationToken)
    {
        foreach (XmlAttribute attribute in foreignObject.Attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.NamespaceURI.Equals("http://www.w3.org/2000/xmlns/", StringComparison.Ordinal))
                continue;
            if (attribute.NamespaceURI.Length != 0 ||
                !IsSupportedForeignObjectAttribute(attribute.LocalName))
            {
                return false;
            }

            if ((attribute.LocalName.Equals("class", StringComparison.OrdinalIgnoreCase) ||
                    attribute.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase)) &&
                !IsSafeCssToken(attribute.Value.Trim(), 512))
            {
                return false;
            }

            if (attribute.LocalName.Equals("selection", StringComparison.OrdinalIgnoreCase) &&
                !bool.TryParse(attribute.Value, out _))
            {
                return false;
            }

            if (attribute.LocalName.Equals("requiredFeatures", StringComparison.OrdinalIgnoreCase) &&
                !attribute.Value.Equals(
                    "http://www.w3.org/TR/SVG11/feature#Extensibility",
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsSupportedForeignObjectAttribute(string name) =>
        name.Equals("x", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("y", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("width", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("height", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("style", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("transform", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("opacity", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("class", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("requiredFeatures", StringComparison.OrdinalIgnoreCase) ||
        // Generated Trendshift cards use this inert authoring hint.
        name.Equals("selection", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSupportedTextStyle(string name) =>
        name.Equals("font-size", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("color", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("font-family", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("font-weight", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("font-style", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("text-align", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("letter-spacing", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("line-height", StringComparison.OrdinalIgnoreCase);

    internal static bool HasOnlySafeHtmlTextAttributes(
        XmlElement element,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int charactersUntilCancellationCheck = SvgCssScanner.CssCancellationCheckInterval;
        foreach (XmlAttribute attribute in element.Attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.NamespaceURI.Equals("http://www.w3.org/2000/xmlns/", StringComparison.Ordinal))
                continue;
            if (attribute.NamespaceURI.Length != 0)
                return false;

            ReadOnlySpan<char> attributeValue = attribute.Value.AsSpan();
            int valueStart = 0;
            int valueEnd = attributeValue.Length;
            TrimStyleRange(
                attributeValue,
                ref valueStart,
                ref valueEnd,
                ref charactersUntilCancellationCheck,
                cancellationToken);
            ReadOnlySpan<char> value = attributeValue[valueStart..valueEnd];
            if (attribute.LocalName.Equals("class", StringComparison.OrdinalIgnoreCase) ||
                attribute.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSafeCssToken(value, 512))
                    return false;
                continue;
            }
            if (attribute.LocalName.Equals("align", StringComparison.OrdinalIgnoreCase))
            {
                if (!value.Equals("left", StringComparison.OrdinalIgnoreCase) &&
                    !value.Equals("right", StringComparison.OrdinalIgnoreCase) &&
                    !value.Equals("center", StringComparison.OrdinalIgnoreCase))
                    return false;
                continue;
            }
            if (attribute.LocalName.Equals("colspan", StringComparison.OrdinalIgnoreCase) ||
                attribute.LocalName.Equals("rowspan", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int span) ||
                    span is < 1 or > 64)
                {
                    return false;
                }
                continue;
            }
            if (attribute.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase) &&
                IsSafeDiscardedHtmlTextStyle(value, cancellationToken, ref charactersUntilCancellationCheck))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    internal static bool IsSafeDiscardedHtmlTextStyle(
        ReadOnlySpan<char> value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int charactersUntilCancellationCheck = SvgCssScanner.CssCancellationCheckInterval;
        return IsSafeDiscardedHtmlTextStyle(value, cancellationToken, ref charactersUntilCancellationCheck);
    }

    internal static bool IsSafeDiscardedHtmlTextStyle(
        ReadOnlySpan<char> value,
        CancellationToken cancellationToken,
        ref int charactersUntilCancellationCheck)
    {
        if (!IsSafeCssToken(value, 1024) ||
            ContainsHtmlStyleForbiddenCharacter(value, cancellationToken, ref charactersUntilCancellationCheck))
        {
            return false;
        }

        int declarationCount = 0;
        uint seenProperties = 0;
        int declarationStart = 0;
        while (declarationStart <= value.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int declarationEnd = declarationStart;
            int separator = -1;
            while (declarationEnd < value.Length && value[declarationEnd] != ';')
            {
                SvgCssScanner.CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                if (separator < 0 && value[declarationEnd] == ':')
                    separator = declarationEnd;
                declarationEnd++;
            }

            if (declarationEnd > declarationStart)
            {
                if (separator <= declarationStart)
                    return false;

                int nameStart = declarationStart;
                int nameEnd = separator;
                TrimStyleRange(value, ref nameStart, ref nameEnd,
                    ref charactersUntilCancellationCheck, cancellationToken);
                int valueStart = separator + 1;
                int valueEnd = declarationEnd;
                TrimStyleRange(value, ref valueStart, ref valueEnd,
                    ref charactersUntilCancellationCheck, cancellationToken);
                if (nameStart == nameEnd || valueStart == valueEnd)
                    return false;

                int propertyIndex = GetSafeHtmlStyleIndex(value[nameStart..nameEnd]);
                if (propertyIndex < 0)
                    return false;

                uint propertyMask = 1u << propertyIndex;
                if ((seenProperties & propertyMask) != 0 ||
                    !IsSafeCssToken(value[valueStart..valueEnd], 256))
                {
                    return false;
                }

                seenProperties |= propertyMask;
                declarationCount++;
            }

            if (declarationEnd == value.Length)
                break;
            declarationStart = declarationEnd + 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return declarationCount > 0;
    }

    internal static Dictionary<string, string> ParseStyle(string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Span<int> valueStarts = stackalloc int[8];
        Span<int> valueLengths = stackalloc int[8];
        uint foundProperties = 0;
        bool hasUnsupportedProperty = false;
        int charactersUntilCancellationCheck = SvgCssScanner.CssCancellationCheckInterval;
        ReadOnlySpan<char> source = value.AsSpan();
        int declarationStart = 0;
        while (declarationStart <= source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int declarationEnd = declarationStart;
            int separator = -1;
            while (declarationEnd < source.Length && source[declarationEnd] != ';')
            {
                SvgCssScanner.CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                if (separator < 0 && source[declarationEnd] == ':')
                    separator = declarationEnd;
                declarationEnd++;
            }

            if (declarationEnd > declarationStart && separator > declarationStart)
            {
                int nameStart = declarationStart;
                int nameEnd = separator;
                TrimStyleRange(source, ref nameStart, ref nameEnd,
                    ref charactersUntilCancellationCheck, cancellationToken);
                int propertyValueStart = separator + 1;
                int propertyValueEnd = declarationEnd;
                TrimStyleRange(source, ref propertyValueStart, ref propertyValueEnd,
                    ref charactersUntilCancellationCheck, cancellationToken);

                if (nameStart < nameEnd && propertyValueStart < propertyValueEnd)
                {
                    int propertyIndex = GetSupportedTextStyleIndex(source[nameStart..nameEnd]);
                    if (propertyIndex < 0)
                    {
                        // Every unsupported text property rejects conversion.
                        // One static marker preserves that signal without
                        // allocating names or values for attacker-sized lists.
                        hasUnsupportedProperty = true;
                    }
                    else
                    {
                        valueStarts[propertyIndex] = propertyValueStart;
                        valueLengths[propertyIndex] = propertyValueEnd - propertyValueStart;
                        foundProperties |= 1u << propertyIndex;
                    }
                }
            }

            if (declarationEnd == source.Length)
                break;
            declarationStart = declarationEnd + 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (hasUnsupportedProperty)
        {
            // The only caller rejects as soon as it observes an unsupported
            // key, so do not materialize even the final supported values for
            // an input that cannot be admitted for replacement.
            properties.Add("\u0001unsupported-text-style", string.Empty);
            return properties;
        }

        for (int propertyIndex = 0; propertyIndex < 8; propertyIndex++)
        {
            if ((foundProperties & (1u << propertyIndex)) == 0)
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            properties.Add(
                GetSupportedTextStyleName(propertyIndex),
                source.Slice(valueStarts[propertyIndex], valueLengths[propertyIndex]).ToString());
        }
        return properties;
    }

    internal static void TrimStyleRange(
        ReadOnlySpan<char> value,
        ref int start,
        ref int end,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken)
    {
        while (start < end && char.IsWhiteSpace(value[start]))
        {
            SvgCssScanner.CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            start++;
        }
        while (end > start && char.IsWhiteSpace(value[end - 1]))
        {
            SvgCssScanner.CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            end--;
        }
    }

    internal static bool ContainsHtmlStyleForbiddenCharacter(
        ReadOnlySpan<char> value,
        CancellationToken cancellationToken,
        ref int charactersUntilCancellationCheck)
    {
        for (int index = 0; index < value.Length; index++)
        {
            SvgCssScanner.CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            if (value[index] is '{' or '}' or '@')
                return true;
        }
        return false;
    }

    internal static int GetSupportedTextStyleIndex(ReadOnlySpan<char> name)
    {
        if (name.Equals("font-size", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals("color", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Equals("font-family", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Equals("font-weight", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Equals("font-style", StringComparison.OrdinalIgnoreCase)) return 4;
        if (name.Equals("text-align", StringComparison.OrdinalIgnoreCase)) return 5;
        if (name.Equals("letter-spacing", StringComparison.OrdinalIgnoreCase)) return 6;
        if (name.Equals("line-height", StringComparison.OrdinalIgnoreCase)) return 7;
        return -1;
    }

    internal static string GetSupportedTextStyleName(int propertyIndex) => propertyIndex switch
    {
        0 => "font-size",
        1 => "color",
        2 => "font-family",
        3 => "font-weight",
        4 => "font-style",
        5 => "text-align",
        6 => "letter-spacing",
        7 => "line-height",
        _ => throw new ArgumentOutOfRangeException(nameof(propertyIndex)),
    };

    internal static int GetSafeHtmlStyleIndex(ReadOnlySpan<char> name)
    {
        if (name.Equals("width", StringComparison.Ordinal)) return 0;
        if (name.Equals("white-space", StringComparison.Ordinal)) return 1;
        if (name.Equals("margin", StringComparison.Ordinal)) return 2;
        if (name.Equals("font-size", StringComparison.Ordinal)) return 3;
        if (name.Equals("color", StringComparison.Ordinal)) return 4;
        if (name.Equals("font-family", StringComparison.Ordinal)) return 5;
        if (name.Equals("font-weight", StringComparison.Ordinal)) return 6;
        if (name.Equals("font-style", StringComparison.Ordinal)) return 7;
        if (name.Equals("text-align", StringComparison.Ordinal)) return 8;
        if (name.Equals("letter-spacing", StringComparison.Ordinal)) return 9;
        if (name.Equals("line-height", StringComparison.Ordinal)) return 10;
        return -1;
    }

    internal static bool IsSafeCssToken(string value, int maximumLength) =>
        value.Length <= maximumLength &&
        value.IndexOf("url", StringComparison.OrdinalIgnoreCase) < 0 &&
        value.IndexOfAny(['<', '>', '\r', '\n', '\0']) < 0;

    internal static bool IsSafeCssToken(ReadOnlySpan<char> value, int maximumLength)
    {
        if (value.Length > maximumLength ||
            value.IndexOf("url", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is '<' or '>' or '\r' or '\n' or '\0')
                return false;
        }
        return true;
    }
}
