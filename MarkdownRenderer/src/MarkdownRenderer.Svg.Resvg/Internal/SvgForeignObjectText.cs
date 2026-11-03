using System.Globalization;
using System.Text;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Replaces a plain-text HTML <c>foreignObject</c> with an equivalent SVG text
/// element, or reports that the subtree cannot be represented statically.
/// </summary>
internal static class SvgForeignObjectText
{
    internal static bool TryCreatePlainTextReplacement(
        XmlDocument document,
        XmlElement foreignObject,
        CancellationToken cancellationToken,
        out XmlElement replacement)
    {
        replacement = null!;
        if (!SvgHtmlTextStyleRules.HasOnlySafeForeignObjectAttributes(foreignObject, cancellationToken) ||
            !TryReadLength(foreignObject.GetAttribute("x"), defaultValue: 0, out double x) ||
            !TryReadLength(foreignObject.GetAttribute("y"), defaultValue: 0, out double y) ||
            !TryReadForeignObjectExtent(
                document,
                foreignObject.GetAttribute("width"),
                true,
                cancellationToken,
                out double width) ||
            !TryReadForeignObjectExtent(
                document,
                foreignObject.GetAttribute("height"),
                false,
                cancellationToken,
                out double height) ||
            width <= 0 ||
            height <= 0 ||
            !TryExtractPlainHtmlText(foreignObject, cancellationToken, out string text))
        {
            return false;
        }

        Dictionary<string, string> style = SvgHtmlTextStyleRules.ParseStyle(foreignObject.GetAttribute("style"), cancellationToken);
        if (style.Keys.Any(static name => !SvgHtmlTextStyleRules.IsSupportedTextStyle(name)))
            return false;

        double fontSize = 16;
        if (style.TryGetValue("font-size", out string? fontSizeText) &&
            (!TryReadLength(fontSizeText, defaultValue: double.NaN, out fontSize) ||
                fontSize <= 0 ||
                fontSize > 4096))
        {
            return false;
        }

        XmlElement svgText = document.CreateElement("text", SvgContentRules.SvgNamespace);
        if (!TryApplyTextAlignment(svgText, style, width, ref x))
            return false;

        svgText.SetAttribute("x", FormatNumber(x));
        svgText.SetAttribute("y", FormatNumber(y + Math.Min(fontSize, height)));
        svgText.SetAttribute("font-size", FormatNumber(fontSize));
        if (!TryApplyPresentationStyle(svgText, style))
            return false;

        if (foreignObject.HasAttribute("transform"))
            svgText.SetAttribute("transform", foreignObject.GetAttribute("transform"));
        if (foreignObject.HasAttribute("opacity"))
            svgText.SetAttribute("opacity", foreignObject.GetAttribute("opacity"));

        svgText.InnerText = text;
        replacement = svgText;
        return true;
    }

    /// <summary>
    /// Maps the HTML <c>text-align</c> onto an SVG <c>text-anchor</c>,
    /// shifting <paramref name="x"/> to the anchor point.
    /// </summary>
    /// <param name="svgText">The replacement text element.</param>
    /// <param name="style">The foreignObject's parsed style.</param>
    /// <param name="width">The foreignObject's width.</param>
    /// <param name="x">The text origin, adjusted for the anchor.</param>
    /// <returns><see langword="false"/> for an unsupported alignment.</returns>
    private static bool TryApplyTextAlignment(
        XmlElement svgText,
        Dictionary<string, string> style,
        double width,
        ref double x)
    {
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

        return true;
    }

    /// <summary>
    /// Copies the supported, validated text presentation properties onto the
    /// replacement in a fixed order, and validates the line height it does
    /// not copy.
    /// </summary>
    /// <param name="svgText">The replacement text element.</param>
    /// <param name="style">The foreignObject's parsed style.</param>
    /// <returns><see langword="false"/> when any property is unsafe or out of range.</returns>
    private static bool TryApplyPresentationStyle(XmlElement svgText, Dictionary<string, string> style)
    {
        if (style.TryGetValue("color", out string? color))
        {
            if (!SvgHtmlTextStyleRules.IsSafeCssToken(color, 128))
                return false;
            svgText.SetAttribute("fill", color.Trim());
        }

        if (style.TryGetValue("font-family", out string? fontFamily))
        {
            if (!SvgHtmlTextStyleRules.IsSafeCssToken(fontFamily, 256))
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

        return true;
    }

    internal static bool TryReadForeignObjectExtent(
        XmlDocument document,
        string value,
        bool horizontal,
        CancellationToken cancellationToken,
        out double extent)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryReadLength(value, defaultValue: double.NaN, out extent))
            return true;

        value = value.Trim();
        if (!value.EndsWith('%') ||
            !double.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double percentage) ||
            percentage is <= 0 or > 100 ||
            !SvgViewportExtent.TryReadSvgViewportExtent(document.DocumentElement, horizontal, cancellationToken, out double viewport))
        {
            extent = double.NaN;
            return false;
        }

        extent = viewport * percentage / 100;
        return double.IsFinite(extent) && extent > 0;
    }

    internal static bool TryExtractPlainHtmlText(
        XmlElement foreignObject,
        CancellationToken cancellationToken,
        out string text)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accumulator = new CollapsedTextAccumulator(cancellationToken);
        if (!TryAccumulatePlainHtmlText(foreignObject, accumulator, cancellationToken))
        {
            text = string.Empty;
            return false;
        }

        text = accumulator.ToString();
        return text.Length > 0;
    }

    /// <summary>
    /// Walks the foreignObject's subtree in document order, appending its
    /// collapsed text and rejecting anything that is not plain HTML text.
    /// </summary>
    /// <param name="foreignObject">The foreignObject being replaced.</param>
    /// <param name="accumulator">Receives the collapsed text.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns><see langword="false"/> when the subtree is not representable as plain text.</returns>
    private static bool TryAccumulatePlainHtmlText(
        XmlElement foreignObject,
        CollapsedTextAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        XmlNode? current = foreignObject.FirstChild;
        while (current is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                if (!accumulator.Append(current.Value))
                    return false;
                current = AdvanceAfterPlainTextSubtree(current, foreignObject, accumulator);
                if (accumulator.IsTooLong)
                    return false;
                continue;
            }

            if (current is not XmlElement element)
                return false;

            if (element.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal))
            {
                if (!IsSafeDiscardedSvgChild(element, cancellationToken))
                    return false;

                current = AdvanceAfterPlainTextSubtree(current, foreignObject, accumulator);
                if (accumulator.IsTooLong)
                    return false;
                continue;
            }

            if (!element.NamespaceURI.Equals("http://www.w3.org/1999/xhtml", StringComparison.Ordinal) ||
                !IsSafeTextContainer(element.LocalName) ||
                !SvgHtmlTextStyleRules.HasOnlySafeHtmlTextAttributes(element, cancellationToken))
            {
                return false;
            }

            if (AddsPlainTextBoundary(element.LocalName) && !accumulator.AppendBoundary())
                return false;

            if (element.FirstChild is { } firstChild)
            {
                current = firstChild;
                continue;
            }

            current = AdvanceAfterPlainTextSubtree(element, foreignObject, accumulator);
            if (accumulator.IsTooLong)
                return false;
        }

        return true;
    }

    /// <summary>
    /// CSS inside an SVG image's foreignObject is inert once the entire HTML
    /// branch is replaced by native SVG text. Accept only a stylesheet whose
    /// XML children are text, discard it rather than forwarding CSS or
    /// executable web content.
    /// </summary>
    /// <param name="element">An SVG-namespace child of the HTML branch.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="true"/> when the element can be discarded safely.</returns>
    private static bool IsSafeDiscardedSvgChild(XmlElement element, CancellationToken cancellationToken) =>
        element.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase)
            ? HasOnlyTextChildren(element, cancellationToken)
            : IsSafeDiscardedSvgGraphic(element, cancellationToken);

    internal static bool AddsPlainTextBoundary(string localName) =>
        localName.Equals("div", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("p", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("br", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("tr", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("li", StringComparison.OrdinalIgnoreCase) ||
        (localName.Length == 2 &&
            (localName[0] is 'h' or 'H') &&
            localName[1] is >= '1' and <= '6');

    internal static XmlNode? AdvanceAfterPlainTextSubtree(
        XmlNode completedNode,
        XmlElement foreignObject,
        CollapsedTextAccumulator accumulator)
    {
        XmlNode? current = completedNode;
        while (current is not null && !ReferenceEquals(current, foreignObject))
        {
            accumulator.ThrowIfCancellationRequested();
            if (current is XmlElement element &&
                element.NamespaceURI.Equals("http://www.w3.org/1999/xhtml", StringComparison.Ordinal) &&
                AddsPlainTextBoundary(element.LocalName) &&
                !accumulator.AppendBoundary())
            {
                return null;
            }

            if (current.NextSibling is { } nextSibling)
                return nextSibling;
            current = current.ParentNode;
        }

        return null;
    }

    internal static bool HasOnlyTextChildren(XmlElement element, CancellationToken cancellationToken)
    {
        foreach (XmlNode child in element.ChildNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.NodeType is not (
                    XmlNodeType.Text or
                    XmlNodeType.CDATA or
                    XmlNodeType.Whitespace or
                    XmlNodeType.SignificantWhitespace))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsSafeDiscardedSvgGraphic(
        XmlElement root,
        CancellationToken cancellationToken)
    {
        XmlNode current = root;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current is XmlElement element)
            {
                if ((!ReferenceEquals(element, root) &&
                        !element.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal)) ||
                    !IsSafeDiscardedSvgGraphicName(element.LocalName))
                {
                    return false;
                }

                foreach (XmlAttribute attribute in element.Attributes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (attribute.NamespaceURI.Equals("http://www.w3.org/2000/xmlns/", StringComparison.Ordinal))
                        continue;
                    if (attribute.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                        attribute.LocalName is "href" or "src" ||
                        attribute.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                if (current.FirstChild is { } firstChild)
                {
                    current = firstChild;
                    continue;
                }
            }
            else if (current.NodeType is not (
                         XmlNodeType.Text or XmlNodeType.CDATA or
                         XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace))
            {
                return false;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReferenceEquals(current, root))
                    return true;
                if (current.NextSibling is { } nextSibling)
                {
                    current = nextSibling;
                    break;
                }

                current = current.ParentNode!;
            }
        }
    }

    internal static bool IsSafeDiscardedSvgGraphicName(string localName) => localName is
        "svg" or "g" or "defs" or "mask" or "path" or "rect" or
        "circle" or "ellipse" or "line" or "polyline" or "polygon" or
        "title" or "desc";

    internal static string GetInnerTextIterative(XmlNode root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (root.FirstChild is { NextSibling: null } onlyChild &&
            onlyChild.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return onlyChild.Value ?? string.Empty;
        }

        var builder = new StringBuilder();
        XmlNode? current = root.FirstChild;
        while (current is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                AppendStringWithCancellation(builder, current.Value, cancellationToken);
            }

            if (current.FirstChild is { } firstChild)
            {
                current = firstChild;
                continue;
            }

            current = AdvanceAfterSubtree(current, root, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return builder.ToString();
    }

    internal static XmlNode? AdvanceAfterSubtree(
        XmlNode completedNode,
        XmlNode root,
        CancellationToken cancellationToken)
    {
        XmlNode? current = completedNode;
        while (current is not null && !ReferenceEquals(current, root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.NextSibling is { } nextSibling)
                return nextSibling;
            current = current.ParentNode;
        }

        return null;
    }

    internal static void AppendStringWithCancellation(
        StringBuilder builder,
        string? value,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(value))
            return;

        for (int offset = 0; offset < value.Length; offset += SvgCssScanner.CssCancellationCheckInterval)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(SvgCssScanner.CssCancellationCheckInterval, value.Length - offset);
            builder.Append(value, offset, count);
        }
    }

    internal static bool IsSafeTextContainer(string localName) =>
        localName.Equals("div", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("section", StringComparison.OrdinalIgnoreCase) ||
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

    internal static bool TryReadLength(string value, double defaultValue, out double result)
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

    internal static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
