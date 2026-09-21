using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Converts declarative SVG animation into a deterministic static first frame.
/// The renderer remains a static-SVG renderer: animation nodes never cross the
/// host/worker boundary, and the normal security preflight validates the
/// resulting document before it is opened by the provider.
/// </summary>
internal static class SvgStaticSnapshot
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    public static byte[] Create(byte[] bytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length == 0 ||
            bytes.Length > SvgResourceBudget.MaxInputBytes ||
            !MayRequireStaticSnapshot(bytes))
        {
            return bytes;
        }

        try
        {
            var document = new XmlDocument
            {
                PreserveWhitespace = true,
                XmlResolver = null,
            };
            using MemoryStream input = new(bytes, writable: false);
            using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
            {
                Async = false,
                DtdProcessing = DtdProcessing.Parse,
                XmlResolver = null,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                MaxCharactersFromEntities = 1,
                MaxCharactersInDocument = SvgResourceBudget.MaxInputBytes,
            });
            document.Load(reader);

            List<XmlElement> animationElements = FindAnimationElements(
                document,
                cancellationToken);
            List<XmlElement> foreignObjects = FindForeignObjectElements(
                document,
                cancellationToken);
            List<XmlElement> styleElements = FindElements(
                document,
                "style",
                cancellationToken);
            List<XmlElement> inlineStyleElements = FindElementsWithAttribute(
                document,
                "style",
                cancellationToken);
            bool changed = false;

            foreach (XmlElement animation in animationElements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyInitialValue(animation);
                animation.ParentNode?.RemoveChild(animation);
                changed = true;
            }

            foreach (XmlElement foreignObject in foreignObjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasSwitchFallback(foreignObject))
                {
                    // Draw.io and similar exporters put an XHTML label first in
                    // <switch> and a native SVG <text> fallback second. The
                    // XHTML branch is executable web content and never crosses
                    // the renderer boundary; retaining the authored SVG branch
                    // gives resvg the same safe fallback a non-HTML user agent
                    // selects.
                    foreignObject.ParentNode?.RemoveChild(foreignObject);
                    changed = true;
                }
                else if (TryCreatePlainTextReplacement(document, foreignObject, out XmlElement replacement))
                {
                    foreignObject.ParentNode?.ReplaceChild(replacement, foreignObject);
                    changed = true;
                }
            }

            foreach (XmlElement style in styleElements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryCreateStaticStyleSheet(style.InnerText, out string sanitizedCss))
                {
                    style.InnerText = sanitizedCss;
                    changed = true;
                }
            }

            foreach (XmlElement element in inlineStyleElements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string inlineStyle = element.GetAttribute("style");
                if (!TryCreateStaticStyleSheet(inlineStyle, out string sanitizedCss))
                    continue;

                if (string.IsNullOrWhiteSpace(sanitizedCss))
                    element.RemoveAttribute("style");
                else
                    element.SetAttribute("style", sanitizedCss);
                changed = true;
            }

            if (!changed)
                return bytes;

            using MemoryStream output = new(capacity: Math.Min(bytes.Length, 1024 * 1024));
            using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = false,
                NewLineHandling = NewLineHandling.None,
                OmitXmlDeclaration = document.FirstChild?.NodeType != XmlNodeType.XmlDeclaration,
            }))
            {
                document.Save(writer);
            }

            return output.ToArray();
        }
        catch (XmlException)
        {
            // Preserve the original bytes so the authoritative preflight can
            // report its normal typed invalid-XML failure.
            return bytes;
        }
    }

    private static bool HasSwitchFallback(XmlElement foreignObject)
    {
        if (foreignObject.ParentNode is not XmlElement parent ||
            (!string.IsNullOrEmpty(parent.NamespaceURI) &&
                !parent.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal)) ||
            !parent.LocalName.Equals("switch", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (XmlNode sibling in parent.ChildNodes)
        {
            if (ReferenceEquals(sibling, foreignObject) || sibling is not XmlElement element)
                continue;
            if ((string.IsNullOrEmpty(element.NamespaceURI) ||
                    element.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal)) &&
                !element.LocalName.Equals("foreignObject", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<XmlElement> FindAnimationElements(
        XmlDocument document,
        CancellationToken cancellationToken)
    {
        var result = new List<XmlElement>();
        if (document.DocumentElement is not XmlElement root)
            return result;

        var pending = new Stack<XmlNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            XmlNode node = pending.Pop();
            if (node is XmlElement element && IsAnimationElement(element))
            {
                result.Add(element);
                continue;
            }

            for (XmlNode? child = node.LastChild; child is not null; child = child.PreviousSibling)
                pending.Push(child);
        }

        return result;
    }

    private static List<XmlElement> FindForeignObjectElements(
        XmlDocument document,
        CancellationToken cancellationToken)
    {
        var result = new List<XmlElement>();
        if (document.DocumentElement is not XmlElement root)
            return result;

        var pending = new Stack<XmlNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            XmlNode node = pending.Pop();
            if (node is XmlElement element &&
                (string.IsNullOrEmpty(element.NamespaceURI) ||
                    element.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal)) &&
                element.LocalName.Equals("foreignObject", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(element);
                continue;
            }

            for (XmlNode? child = node.LastChild; child is not null; child = child.PreviousSibling)
                pending.Push(child);
        }

        return result;
    }

    private static List<XmlElement> FindElements(
        XmlDocument document,
        string localName,
        CancellationToken cancellationToken)
    {
        var result = new List<XmlElement>();
        if (document.DocumentElement is not XmlElement root)
            return result;

        var pending = new Stack<XmlNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            XmlNode node = pending.Pop();
            if (node is XmlElement element &&
                (string.IsNullOrEmpty(element.NamespaceURI) ||
                    element.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal)) &&
                element.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(element);
            }

            for (XmlNode? child = node.LastChild; child is not null; child = child.PreviousSibling)
                pending.Push(child);
        }

        return result;
    }

    private static List<XmlElement> FindElementsWithAttribute(
        XmlDocument document,
        string attributeName,
        CancellationToken cancellationToken)
    {
        var result = new List<XmlElement>();
        if (document.DocumentElement is not XmlElement root)
            return result;

        var pending = new Stack<XmlNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            XmlNode node = pending.Pop();
            if (node is XmlElement element && element.HasAttribute(attributeName))
                result.Add(element);

            for (XmlNode? child = node.LastChild; child is not null; child = child.PreviousSibling)
                pending.Push(child);
        }

        return result;
    }

    private static bool TryCreateStaticStyleSheet(string css, out string sanitized)
    {
        var removals = new List<(int Start, int Length)>();
        int importCursor = 0;
        while (TryFindCssToken(css, "@import", importCursor, out int importStart))
        {
            if (!TryFindCssStatementEnd(css, importStart + "@import".Length, out int importEnd))
            {
                sanitized = css;
                return false;
            }

            removals.Add((importStart, importEnd - importStart));
            importCursor = importEnd;
        }

        foreach (string directive in new[] { "@font-face", "@keyframes", "@-webkit-keyframes" })
        {
            int cursor = 0;
            while (TryFindCssToken(css, directive, cursor, out int start))
            {
                int openingBrace = start + directive.Length;
                while (openingBrace < css.Length && css[openingBrace] != '{')
                    openingBrace++;
                if (openingBrace >= css.Length ||
                    !TryFindCssBlockEnd(css, openingBrace, out int end))
                {
                    sanitized = css;
                    return false;
                }

                removals.Add((start, end + 1 - start));
                cursor = end + 1;
            }
        }

        if (!TryFindMotionDeclarations(css, removals))
        {
            sanitized = css;
            return false;
        }

        if (removals.Count == 0)
        {
            sanitized = css;
            return false;
        }

        char[] result = css.ToCharArray();
        foreach ((int start, int length) in removals)
            Array.Fill(result, ' ', start, length);
        sanitized = new string(result);
        return true;
    }

    private static bool TryFindCssStatementEnd(string css, int start, out int end)
    {
        char quote = '\0';
        bool comment = false;
        int parentheses = 0;
        for (int cursor = start; cursor < css.Length; cursor++)
        {
            char current = css[cursor];
            if (comment)
            {
                if (current == '*' && cursor + 1 < css.Length && css[cursor + 1] == '/')
                {
                    comment = false;
                    cursor++;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (current == '\\' && cursor + 1 < css.Length)
                    cursor++;
                else if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current == '/' && cursor + 1 < css.Length && css[cursor + 1] == '*')
            {
                comment = true;
                cursor++;
                continue;
            }
            if (current is '\'' or '"')
                quote = current;
            else if (current == '(')
                parentheses++;
            else if (current == ')' && parentheses > 0)
                parentheses--;
            else if (current == ';' && parentheses == 0)
            {
                end = cursor + 1;
                return true;
            }
            else if (current == '{' && parentheses == 0)
            {
                end = -1;
                return false;
            }
        }

        end = -1;
        return false;
    }

    private static bool TryFindMotionDeclarations(
        string css,
        List<(int Start, int Length)> removals)
    {
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (TrySkipCssTrivia(css, ref cursor))
                continue;

            int nameStart = cursor;
            while (cursor < css.Length &&
                (char.IsAsciiLetter(css[cursor]) || css[cursor] is '-' or '_'))
            {
                cursor++;
            }
            if (cursor == nameStart)
            {
                cursor++;
                continue;
            }

            string property = css[nameStart..cursor];
            int separator = cursor;
            while (separator < css.Length && char.IsWhiteSpace(css[separator]))
                separator++;
            if (separator >= css.Length || css[separator] != ':')
                continue;

            bool isMotion = property.Equals("animation", StringComparison.OrdinalIgnoreCase) ||
                property.StartsWith("animation-", StringComparison.OrdinalIgnoreCase) ||
                property.Equals("transition", StringComparison.OrdinalIgnoreCase) ||
                property.StartsWith("transition-", StringComparison.OrdinalIgnoreCase);
            if (!isMotion)
            {
                cursor = separator + 1;
                continue;
            }

            int end = separator + 1;
            char quote = '\0';
            int parentheses = 0;
            bool encounteredNestedRule = false;
            while (end < css.Length)
            {
                char current = css[end];
                if (quote != '\0')
                {
                    if (current == '\\' && end + 1 < css.Length)
                        end += 2;
                    else
                    {
                        if (current == quote)
                            quote = '\0';
                        end++;
                    }
                    continue;
                }
                if (current is '\'' or '"')
                    quote = current;
                else if (current == '(')
                    parentheses++;
                else if (current == ')' && parentheses > 0)
                    parentheses--;
                else if (parentheses == 0 && current == '{')
                {
                    // A block opener before a declaration terminator means the
                    // token was part of a selector or an at-rule prelude (for
                    // example `.animation:hover { ... }`), not a property.
                    encounteredNestedRule = true;
                    break;
                }
                else if (parentheses == 0 && current is (';' or '}'))
                    break;
                end++;
            }
            if (quote != '\0' || parentheses != 0)
                return false;
            if (encounteredNestedRule)
            {
                cursor = end + 1;
                continue;
            }

            if (end < css.Length && css[end] == ';')
                end++;
            removals.Add((nameStart, end - nameStart));
            cursor = Math.Max(end, separator + 1);
        }

        return true;
    }

    private static bool TrySkipCssTrivia(string css, ref int cursor)
    {
        if (char.IsWhiteSpace(css[cursor]))
        {
            cursor++;
            return true;
        }
        if (cursor + 1 < css.Length && css[cursor] == '/' && css[cursor + 1] == '*')
        {
            int end = css.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
            cursor = end < 0 ? css.Length : end + 2;
            return true;
        }
        if (css[cursor] is '\'' or '"')
        {
            char quote = css[cursor++];
            while (cursor < css.Length && css[cursor] != quote)
                cursor += css[cursor] == '\\' && cursor + 1 < css.Length ? 2 : 1;
            if (cursor < css.Length)
                cursor++;
            return true;
        }
        return false;
    }

    private static bool TryFindCssToken(string css, string token, int start, out int index)
    {
        char quote = '\0';
        bool comment = false;
        for (int cursor = Math.Max(0, start); cursor < css.Length; cursor++)
        {
            char current = css[cursor];
            if (comment)
            {
                if (current == '*' && cursor + 1 < css.Length && css[cursor + 1] == '/')
                {
                    comment = false;
                    cursor++;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (current == '\\' && cursor + 1 < css.Length)
                    cursor++;
                else if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current == '/' && cursor + 1 < css.Length && css[cursor + 1] == '*')
            {
                comment = true;
                cursor++;
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }
            if (css.AsSpan(cursor).StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                index = cursor;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private static bool TryFindCssBlockEnd(string css, int openingBrace, out int end)
    {
        int depth = 0;
        char quote = '\0';
        bool comment = false;
        for (int cursor = openingBrace; cursor < css.Length; cursor++)
        {
            char current = css[cursor];
            if (comment)
            {
                if (current == '*' && cursor + 1 < css.Length && css[cursor + 1] == '/')
                {
                    comment = false;
                    cursor++;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (current == '\\' && cursor + 1 < css.Length)
                    cursor++;
                else if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current == '/' && cursor + 1 < css.Length && css[cursor + 1] == '*')
            {
                comment = true;
                cursor++;
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }
            if (current == '{')
                depth++;
            else if (current == '}' && --depth == 0)
            {
                end = cursor;
                return true;
            }
        }

        end = -1;
        return false;
    }

    private static bool TryCreatePlainTextReplacement(
        XmlDocument document,
        XmlElement foreignObject,
        out XmlElement replacement)
    {
        replacement = null!;
        if (!HasOnlySafeForeignObjectAttributes(foreignObject) ||
            !TryReadLength(foreignObject.GetAttribute("x"), defaultValue: 0, out double x) ||
            !TryReadLength(foreignObject.GetAttribute("y"), defaultValue: 0, out double y) ||
            !TryReadLength(foreignObject.GetAttribute("width"), defaultValue: double.NaN, out double width) ||
            !TryReadLength(foreignObject.GetAttribute("height"), defaultValue: double.NaN, out double height) ||
            width <= 0 ||
            height <= 0 ||
            !TryExtractPlainHtmlText(foreignObject, out string text))
        {
            return false;
        }

        Dictionary<string, string> style = ParseStyle(foreignObject.GetAttribute("style"));
        if (style.Keys.Any(static name => !IsSupportedTextStyle(name)))
            return false;

        double fontSize = 16;
        if (style.TryGetValue("font-size", out string? fontSizeText) &&
            (!TryReadLength(fontSizeText, defaultValue: double.NaN, out fontSize) ||
                fontSize <= 0 ||
                fontSize > 4096))
        {
            return false;
        }

        XmlElement svgText = document.CreateElement("text", SvgNamespace);
        string alignment = style.GetValueOrDefault("text-align", "start").Trim();
        if (alignment.Equals("center", StringComparison.OrdinalIgnoreCase))
        {
            x += width / 2;
            svgText.SetAttribute("text-anchor", "middle");
        }
        else if (alignment.Equals("right", StringComparison.OrdinalIgnoreCase) ||
            alignment.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            x += width;
            svgText.SetAttribute("text-anchor", "end");
        }
        else if (!alignment.Equals("left", StringComparison.OrdinalIgnoreCase) &&
            !alignment.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        svgText.SetAttribute("x", FormatNumber(x));
        svgText.SetAttribute("y", FormatNumber(y + Math.Min(fontSize, height)));
        svgText.SetAttribute("font-size", FormatNumber(fontSize));

        if (style.TryGetValue("color", out string? color))
        {
            if (!IsSafeCssToken(color, 128))
                return false;
            svgText.SetAttribute("fill", color.Trim());
        }

        if (style.TryGetValue("font-family", out string? fontFamily))
        {
            if (!IsSafeCssToken(fontFamily, 256))
                return false;
            svgText.SetAttribute("font-family", fontFamily.Trim());
        }

        if (style.TryGetValue("font-weight", out string? fontWeight))
        {
            fontWeight = fontWeight.Trim();
            if (!fontWeight.Equals("normal", StringComparison.OrdinalIgnoreCase) &&
                !fontWeight.Equals("bold", StringComparison.OrdinalIgnoreCase) &&
                (!int.TryParse(fontWeight, NumberStyles.None, CultureInfo.InvariantCulture, out int numericWeight) ||
                    numericWeight is < 1 or > 1000))
            {
                return false;
            }
            svgText.SetAttribute("font-weight", fontWeight);
        }

        if (style.TryGetValue("font-style", out string? fontStyle))
        {
            fontStyle = fontStyle.Trim();
            if (!fontStyle.Equals("normal", StringComparison.OrdinalIgnoreCase) &&
                !fontStyle.Equals("italic", StringComparison.OrdinalIgnoreCase) &&
                !fontStyle.Equals("oblique", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            svgText.SetAttribute("font-style", fontStyle);
        }

        if (style.TryGetValue("letter-spacing", out string? letterSpacing))
        {
            if (!TryReadLength(letterSpacing, defaultValue: double.NaN, out double spacing) ||
                Math.Abs(spacing) > 4096)
            {
                return false;
            }
            svgText.SetAttribute("letter-spacing", FormatNumber(spacing));
        }

        if (style.TryGetValue("line-height", out string? lineHeight) &&
            (!TryReadLength(lineHeight, defaultValue: double.NaN, out double parsedLineHeight) ||
                parsedLineHeight <= 0 ||
                parsedLineHeight > 4096))
        {
            return false;
        }

        if (foreignObject.HasAttribute("transform"))
            svgText.SetAttribute("transform", foreignObject.GetAttribute("transform"));
        if (foreignObject.HasAttribute("opacity"))
            svgText.SetAttribute("opacity", foreignObject.GetAttribute("opacity"));

        svgText.InnerText = text;
        replacement = svgText;
        return true;
    }

    private static bool HasOnlySafeForeignObjectAttributes(XmlElement foreignObject)
    {
        foreach (XmlAttribute attribute in foreignObject.Attributes)
        {
            if (attribute.NamespaceURI.Equals("http://www.w3.org/2000/xmlns/", StringComparison.Ordinal))
                continue;
            if (attribute.NamespaceURI.Length != 0 ||
                !IsSupportedForeignObjectAttribute(attribute.LocalName))
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

    private static bool IsSupportedForeignObjectAttribute(string name) =>
        name.Equals("x", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("y", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("width", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("height", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("style", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("transform", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("opacity", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("requiredFeatures", StringComparison.OrdinalIgnoreCase) ||
        // Generated Trendshift cards use this inert authoring hint.
        name.Equals("selection", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedTextStyle(string name) =>
        name.Equals("font-size", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("color", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("font-family", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("font-weight", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("font-style", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("text-align", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("letter-spacing", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("line-height", StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractPlainHtmlText(XmlElement foreignObject, out string text)
    {
        var builder = new StringBuilder();
        foreach (XmlNode child in foreignObject.ChildNodes)
        {
            if (!AppendPlainHtmlText(child, builder))
            {
                text = string.Empty;
                return false;
            }
        }

        text = CollapseWhitespace(builder.ToString());
        return text.Length is > 0 and <= 16_384;
    }

    private static bool AppendPlainHtmlText(XmlNode node, StringBuilder builder)
    {
        if (node.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
            XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
        {
            builder.Append(node.Value);
            return true;
        }

        if (node is not XmlElement element ||
            !element.NamespaceURI.Equals("http://www.w3.org/1999/xhtml", StringComparison.Ordinal) ||
            !IsSafeTextContainer(element.LocalName))
        {
            return false;
        }

        if (!HasOnlySafeHtmlTextAttributes(element))
            return false;

        bool addsBoundary = element.LocalName.Equals("div", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("p", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("tr", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase) ||
            (element.LocalName.Length == 2 &&
                (element.LocalName[0] is 'h' or 'H') &&
                element.LocalName[1] is >= '1' and <= '6');
        if (addsBoundary)
            builder.Append(' ');
        foreach (XmlNode child in element.ChildNodes)
        {
            if (!AppendPlainHtmlText(child, builder))
                return false;
        }
        if (addsBoundary)
            builder.Append(' ');
        return true;
    }

    private static bool IsSafeTextContainer(string localName) =>
        localName.Equals("div", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("span", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("p", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("br", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("strong", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("b", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("em", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("i", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("small", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("code", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("table", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("thead", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("tbody", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("tfoot", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("tr", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("td", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("th", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("ul", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("ol", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("li", StringComparison.OrdinalIgnoreCase) ||
        localName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";

    private static bool HasOnlySafeHtmlTextAttributes(XmlElement element)
    {
        foreach (XmlAttribute attribute in element.Attributes)
        {
            if (attribute.NamespaceURI.Equals("http://www.w3.org/2000/xmlns/", StringComparison.Ordinal))
                continue;
            if (attribute.NamespaceURI.Length != 0)
                return false;

            string value = attribute.Value.Trim();
            if (attribute.LocalName.Equals("class", StringComparison.OrdinalIgnoreCase))
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
                IsSafeDiscardedHtmlTextStyle(value))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsSafeDiscardedHtmlTextStyle(string value)
    {
        if (!IsSafeCssToken(value, 1024) || value.IndexOfAny(['{', '}', '@']) >= 0)
            return false;

        string[] declarations = value.Split(';', StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, string> style = ParseStyle(value);
        return declarations.Length > 0 && style.Count == declarations.Length &&
            style.All(static declaration =>
            (declaration.Key is "width" or "white-space" or "margin" or "font-size" or
                "color" or "font-family" or "font-weight" or "font-style" or
                "text-align" or "letter-spacing" or "line-height") &&
            IsSafeCssToken(declaration.Value, 256));
    }

    private static Dictionary<string, string> ParseStyle(string value)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string declaration in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = declaration.IndexOf(':');
            if (separator <= 0)
                continue;
            string name = declaration[..separator].Trim();
            string propertyValue = declaration[(separator + 1)..].Trim();
            if (name.Length > 0 && propertyValue.Length > 0)
                properties[name] = propertyValue;
        }
        return properties;
    }

    private static bool TryReadLength(string value, double defaultValue, out double result)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            result = defaultValue;
            return double.IsFinite(result);
        }
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("em", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^2].Trim();
        }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
            double.IsFinite(result);
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool IsSafeCssToken(string value, int maximumLength) =>
        value.Length <= maximumLength &&
        value.IndexOf("url", StringComparison.OrdinalIgnoreCase) < 0 &&
        value.IndexOfAny(['<', '>', '\r', '\n', '\0']) < 0;

    private static string CollapseWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(character);
        }
        return result.ToString();
    }

    private static bool IsAnimationElement(XmlElement element)
    {
        if (!string.IsNullOrEmpty(element.NamespaceURI) &&
            !element.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal))
        {
            return false;
        }

        return element.LocalName.Equals("animate", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("animateColor", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("animateMotion", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("animateTransform", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("set", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("discard", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyInitialValue(XmlElement animation)
    {
        if (animation.ParentNode is not XmlElement target || !BeginsImmediately(animation))
            return;

        string localName = animation.LocalName;
        if (localName.Equals("animateMotion", StringComparison.OrdinalIgnoreCase) ||
            localName.Equals("discard", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? initialValue = GetInitialValue(animation);
        if (string.IsNullOrWhiteSpace(initialValue))
            return;

        if (localName.Equals("animateTransform", StringComparison.OrdinalIgnoreCase))
        {
            string type = animation.GetAttribute("type").Trim();
            if (!IsSupportedTransformType(type))
                return;

            string transform = $"{type}({initialValue.Trim()})";
            if (animation.GetAttribute("additive").Equals("sum", StringComparison.OrdinalIgnoreCase) &&
                target.HasAttribute("transform"))
            {
                transform = $"{target.GetAttribute("transform").Trim()} {transform}";
            }

            target.SetAttribute("transform", transform);
            return;
        }

        string attributeName = animation.GetAttribute("attributeName").Trim();
        if (attributeName.Length == 0 ||
            attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
            attributeName.IndexOf(':') >= 0)
        {
            return;
        }

        target.SetAttribute(attributeName, initialValue.Trim());
    }

    private static string? GetInitialValue(XmlElement animation)
    {
        string values = animation.GetAttribute("values");
        if (!string.IsNullOrWhiteSpace(values))
        {
            string[] candidates = values.Split(';');
            if (candidates.Length == 0)
                return null;

            int selected = 0;
            string keyTimes = animation.GetAttribute("keyTimes");
            if (!string.IsNullOrWhiteSpace(keyTimes))
            {
                string[] times = keyTimes.Split(';');
                int count = Math.Min(times.Length, candidates.Length);
                for (int index = 0; index < count; index++)
                {
                    if (!double.TryParse(
                            times[index].Trim(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double keyTime) ||
                        keyTime > 0)
                    {
                        break;
                    }

                    // Multiple values at keyTime zero represent an immediate
                    // transition. The final zero-time value is the visible
                    // first frame (common in rotating README badge artwork).
                    selected = index;
                }
            }

            return candidates[selected];
        }

        string from = animation.GetAttribute("from");
        if (!string.IsNullOrWhiteSpace(from))
            return from;

        return animation.LocalName.Equals("set", StringComparison.OrdinalIgnoreCase)
            ? animation.GetAttribute("to")
            : null;
    }

    private static bool BeginsImmediately(XmlElement animation)
    {
        string begin = animation.GetAttribute("begin").Trim();
        if (begin.Length == 0)
            return true;

        int separator = begin.IndexOf(';');
        if (separator >= 0)
            begin = begin[..separator].Trim();
        if (begin.Equals("indefinite", StringComparison.OrdinalIgnoreCase) ||
            begin.IndexOfAny(['.', '+']) >= 0)
        {
            return false;
        }

        if (begin.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            begin = begin[..^2];
        else if (begin.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            begin = begin[..^1];

        return double.TryParse(
                begin,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double start) &&
            start <= 0;
    }

    private static bool IsSupportedTransformType(string value) =>
        value.Equals("translate", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("scale", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("rotate", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("skewX", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("skewY", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("matrix", StringComparison.OrdinalIgnoreCase);

    private static bool MayRequireStaticSnapshot(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> marker = "<animate"u8;
        ReadOnlySpan<byte> setMarker = "<set"u8;
        ReadOnlySpan<byte> discardMarker = "<discard"u8;
        ReadOnlySpan<byte> foreignObjectMarker = "<foreignObject"u8;
        ReadOnlySpan<byte> fontFaceMarker = "@font-face"u8;
        ReadOnlySpan<byte> importMarker = "@import"u8;
        ReadOnlySpan<byte> keyframesMarker = "@keyframes"u8;
        ReadOnlySpan<byte> webkitKeyframesMarker = "@-webkit-keyframes"u8;
        ReadOnlySpan<byte> animationMarker = "animation"u8;
        ReadOnlySpan<byte> transitionMarker = "transition"u8;
        for (int index = 0; index < bytes.Length; index++)
        {
            if (AsciiStartsWithIgnoreCase(bytes[index..], marker) ||
                AsciiStartsWithIgnoreCase(bytes[index..], setMarker) ||
                AsciiStartsWithIgnoreCase(bytes[index..], discardMarker) ||
                AsciiStartsWithIgnoreCase(bytes[index..], foreignObjectMarker) ||
                AsciiStartsWithIgnoreCase(bytes[index..], fontFaceMarker) ||
                AsciiStartsWithIgnoreCase(bytes[index..], importMarker) ||
                AsciiStartsWithIgnoreCase(bytes[index..], keyframesMarker) ||
                AsciiStartsWithIgnoreCase(bytes[index..], webkitKeyframesMarker) ||
                AsciiStartsWithIgnoreCase(bytes[index..], animationMarker) ||
                AsciiStartsWithIgnoreCase(bytes[index..], transitionMarker))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AsciiStartsWithIgnoreCase(
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> prefix)
    {
        if (value.Length < prefix.Length)
            return false;

        for (int index = 0; index < prefix.Length; index++)
        {
            byte actual = value[index];
            byte expected = prefix[index];
            if (actual is >= (byte)'A' and <= (byte)'Z')
                actual = (byte)(actual + ((byte)'a' - (byte)'A'));
            if (expected is >= (byte)'A' and <= (byte)'Z')
                expected = (byte)(expected + ((byte)'a' - (byte)'A'));
            if (actual != expected)
                return false;
        }

        return true;
    }
}
