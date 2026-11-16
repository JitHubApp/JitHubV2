using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using MarkdownRenderer.Images.Raster;
using MarkdownRenderer.Images.Svg;

namespace MarkdownRenderer.Svg.Resvg.Internal.Preparation;

/// <summary>
/// Performs a bounded, streaming security preflight before SVG bytes are sent to
/// an optional renderer. Embedded images are charged by unique decoded cost
/// instead of element count, so avatar mosaics remain valid while image bombs
/// fail closed.
/// </summary>
internal static class SvgResourceBudget
{
    public const int MaxInputBytes = 8 * 1024 * 1024;
    public const int MaxElements = 100_000;
    public const int MaxDepth = 128;
    public const int MaxAttributes = 800_000;
    public const int MaxFilterPrimitives = 4_096;
    public const int MaxTextNodes = 65_536;
    public const int MaxTextCharacters = 1024 * 1024;
    public const int MaxPathCharacters = 4 * 1024 * 1024;
    public const int MaxTransformCharacters = 1024 * 1024;
    public const int MaxNestedSvgDepth = 4;
    public const long MaxDecodedEmbeddedImageBytes = 96L * 1024 * 1024;
    public const long MaxEmbeddedCompressedBytes = 32L * 1024 * 1024;
    public const long MaxComplexityUnits = 100_000;
    public const double MaxDeclaredFontSize = 4096;
    public static readonly TimeSpan ValidationDeadline = TimeSpan.FromMilliseconds(750);

    public static SvgResourceBudgetResult Validate(byte[]? bytes, CancellationToken cancellationToken)
        => ValidateCore(bytes, cancellationToken, metadataCollector: null, out _);

    public static SvgResourceBudgetResult ValidateWithMetadata(
        byte[]? bytes,
        CancellationToken cancellationToken,
        out MarkdownSvgSourceInfo? sourceInfo)
        => ValidateCore(bytes, cancellationToken, new MarkdownSvgSourceInfoReader(cancellationToken), out sourceInfo);

    private static SvgResourceBudgetResult ValidateCore(
        byte[]? bytes,
        CancellationToken cancellationToken,
        MarkdownSvgSourceInfoReader? metadataCollector,
        out MarkdownSvgSourceInfo? sourceInfo)
    {
        sourceInfo = null;
        if (bytes is null || bytes.Length == 0)
            return SvgResourceBudgetResult.Reject("empty");
        if (bytes.Length > MaxInputBytes)
            return SvgResourceBudgetResult.Reject("input-bytes");

        var context = new SvgResourceBudgetValidationContext(Stopwatch.GetTimestamp(), cancellationToken);
        SvgResourceBudgetResult result = ValidateDocument(bytes, context, nestedDepth: 0, metadataCollector);
        if (result.Accepted && metadataCollector is not null)
        {
            try
            {
                // Keep the successful metadata path inside the same active
                // deadline, including bounded final string/geometry work.
                context.ThrowIfStopped();
                MarkdownSvgSourceInfo collected = metadataCollector.GetResult();
                context.ThrowIfStopped();
                sourceInfo = collected;
            }
            catch (SvgValidationDeadlineException)
            {
                return SvgResourceBudgetResult.Reject("validation-deadline");
            }
        }
        return result;
    }

    private static SvgResourceBudgetResult ValidateDocument(
        byte[] bytes,
        SvgResourceBudgetValidationContext context,
        int nestedDepth,
        MarkdownSvgSourceInfoReader? metadataCollector)
    {
        if (nestedDepth > MaxNestedSvgDepth)
            return SvgResourceBudgetResult.Reject("nested-svg-depth");
        if (bytes.Length == 0 || bytes.Length > MaxInputBytes)
            return SvgResourceBudgetResult.Reject("embedded-image-data");

        bool sawSvgRoot = false;
        int styleElementDepth = -1;

        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using XmlReader reader = XmlReader.Create(stream, CreateReaderSettings());

            while (reader.Read())
            {
                context.ThrowIfStopped();
                if (reader.Depth > MaxDepth)
                    return SvgResourceBudgetResult.Reject("element-depth");

                if (reader.NodeType == XmlNodeType.DocumentType)
                {
                    return SvgResourceBudgetResult.Reject("dtd-forbidden");
                }

                SvgResourceBudgetResult nodeResult = ValidateNode(
                    reader,
                    context,
                    nestedDepth,
                    metadataCollector,
                    ref sawSvgRoot,
                    ref styleElementDepth);
                if (!nodeResult.Accepted)
                    return nodeResult;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SvgValidationDeadlineException)
        {
            return SvgResourceBudgetResult.Reject("validation-deadline");
        }
        catch (XmlException)
        {
            return SvgResourceBudgetResult.Reject("invalid-xml");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FormatException)
        {
            return SvgResourceBudgetResult.Reject("parse-failure");
        }

        return sawSvgRoot
            ? SvgResourceBudgetResult.Success
            : SvgResourceBudgetResult.Reject("missing-root");
    }

    /// <summary>Creates the reader settings for one validation pass.</summary>
    private static XmlReaderSettings CreateReaderSettings() => new()
    {
        Async = false,
        // Parse only so a DocumentType node can be classified precisely.
        // The resolver is disabled and entity expansion is bounded; the
        // document is rejected as soon as the declaration is observed.
        DtdProcessing = DtdProcessing.Parse,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersFromEntities = 1,
        MaxCharactersInDocument = MaxInputBytes,
    };

    /// <summary>
    /// Validates the node under the reader once its depth is in range and it
    /// is not a document type declaration.
    /// </summary>
    private static SvgResourceBudgetResult ValidateNode(
        XmlReader reader,
        SvgResourceBudgetValidationContext context,
        int nestedDepth,
        MarkdownSvgSourceInfoReader? metadataCollector,
        ref bool sawSvgRoot,
        ref int styleElementDepth)
    {
        if (reader.NodeType == XmlNodeType.Element)
        {
            return ValidateElement(
                reader,
                context,
                nestedDepth,
                metadataCollector,
                ref sawSvgRoot,
                ref styleElementDepth);
        }

        if (reader.NodeType == XmlNodeType.EndElement)
        {
            metadataCollector?.ObserveEndElement(reader.Depth);
            if (styleElementDepth == reader.Depth &&
                reader.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                styleElementDepth = -1;
            }
        }
        else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
        {
            return ValidateText(reader, context, metadataCollector, styleElementDepth);
        }
        else if (reader.NodeType == XmlNodeType.Whitespace)
        {
            metadataCollector?.ObserveText(reader.Depth, reader.Value.AsSpan());
        }

        return SvgResourceBudgetResult.Success;
    }

    /// <summary>
    /// Validates one element and its attributes: the root check, element and
    /// complexity counts, active content, filter primitives and style tracking.
    /// </summary>
    private static SvgResourceBudgetResult ValidateElement(
        XmlReader reader,
        SvgResourceBudgetValidationContext context,
        int nestedDepth,
        MarkdownSvgSourceInfoReader? metadataCollector,
        ref bool sawSvgRoot,
        ref int styleElementDepth)
    {
        string elementName = reader.LocalName;
        int elementDepth = reader.Depth;
        bool isSvgElement = IsSvgNamespace(reader.NamespaceURI);
        if (!sawSvgRoot)
        {
            sawSvgRoot = elementName.Equals("svg", StringComparison.OrdinalIgnoreCase) &&
                isSvgElement;
            if (!sawSvgRoot)
                return SvgResourceBudgetResult.Reject("missing-root");
        }

        metadataCollector?.ObserveElement(
            elementDepth,
            elementName,
            reader.NamespaceURI,
            reader.IsEmptyElement);

        if (++context.ElementCount > MaxElements)
            return SvgResourceBudgetResult.Reject("element-count");
        if (++context.ComplexityUnits > MaxComplexityUnits)
            return SvgResourceBudgetResult.Reject("structural-complexity");

        if (isSvgElement && IsActiveContentElement(elementName))
            return SvgResourceBudgetResult.Reject("active-content");

        bool isFilterPrimitive = isSvgElement &&
            elementName.StartsWith("fe", StringComparison.OrdinalIgnoreCase);
        if (isFilterPrimitive)
        {
            if (!IsSupportedFilterPrimitive(elementName))
                return SvgResourceBudgetResult.Reject("unsupported-filter-primitive");
            if (++context.FilterPrimitiveCount > MaxFilterPrimitives)
                return SvgResourceBudgetResult.Reject("filter-complexity");
            context.ComplexityUnits += 8;
            if (context.ComplexityUnits > MaxComplexityUnits)
                return SvgResourceBudgetResult.Reject("structural-complexity");
        }

        if (isSvgElement && elementName.Equals("style", StringComparison.OrdinalIgnoreCase))
            styleElementDepth = reader.IsEmptyElement ? -1 : reader.Depth;

        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                SvgResourceBudgetResult attributeResult = ValidateAttribute(
                    reader,
                    elementName,
                    elementDepth,
                    context,
                    nestedDepth,
                    metadataCollector);
                if (!attributeResult.Accepted)
                    return attributeResult;
            }

            reader.MoveToElement();
        }

        return SvgResourceBudgetResult.Success;
    }

    /// <summary>
    /// Validates the attribute under the reader: the attribute count, event
    /// handlers, path, transform and font-size budgets, inline CSS, and
    /// references.
    /// </summary>
    private static SvgResourceBudgetResult ValidateAttribute(
        XmlReader reader,
        string elementName,
        int elementDepth,
        SvgResourceBudgetValidationContext context,
        int nestedDepth,
        MarkdownSvgSourceInfoReader? metadataCollector)
    {
        context.ThrowIfStopped();
        if (++context.AttributeCount > MaxAttributes)
            return SvgResourceBudgetResult.Reject("attribute-count");

        string name = reader.LocalName;
        string value = reader.Value;
        metadataCollector?.ObserveAttribute(
            elementDepth,
            name,
            reader.NamespaceURI,
            value);
        if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            return SvgResourceBudgetResult.Reject("event-handler");

        if (name.Equals("d", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("points", StringComparison.OrdinalIgnoreCase))
        {
            context.PathCharacters += value.Length;
            context.ComplexityUnits += (value.Length + 63L) / 64L;
            if (context.PathCharacters > MaxPathCharacters ||
                context.ComplexityUnits > MaxComplexityUnits)
            {
                return SvgResourceBudgetResult.Reject("path-complexity");
            }
        }
        else if (name.Equals("transform", StringComparison.OrdinalIgnoreCase))
        {
            context.TransformCharacters += value.Length;
            if (context.TransformCharacters > MaxTransformCharacters)
                return SvgResourceBudgetResult.Reject("transform-complexity");
        }
        else if (name.Equals("font-size", StringComparison.OrdinalIgnoreCase) &&
            TryReadLeadingNumber(value, out double fontSize) &&
            Math.Abs(fontSize) > MaxDeclaredFontSize)
        {
            return SvgResourceBudgetResult.Reject("font-size");
        }

        if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            SvgResourceBudgetResult styleResult = SvgResourceBudgetCss.ValidateCss(value);
            if (!styleResult.Accepted)
                return styleResult;
        }

        if (name.Equals("href", StringComparison.OrdinalIgnoreCase))
        {
            SvgResourceBudgetResult hrefResult = ValidateHref(
                elementName,
                value,
                context,
                nestedDepth);
            if (!hrefResult.Accepted)
                return hrefResult;
        }
        else if (SvgResourceBudgetCss.ContainsCssUrl(value))
        {
            SvgResourceBudgetResult urlResult = SvgResourceBudgetCss.ValidateCssUrls(value);
            if (!urlResult.Accepted)
                return urlResult;
        }

        return SvgResourceBudgetResult.Success;
    }

    /// <summary>Charges one text node and validates it as CSS inside a style element.</summary>
    private static SvgResourceBudgetResult ValidateText(
        XmlReader reader,
        SvgResourceBudgetValidationContext context,
        MarkdownSvgSourceInfoReader? metadataCollector,
        int styleElementDepth)
    {
        if (++context.TextNodeCount > MaxTextNodes)
            return SvgResourceBudgetResult.Reject("text-node-count");
        context.TextCharacters += reader.Value.Length;
        if (context.TextCharacters > MaxTextCharacters)
            return SvgResourceBudgetResult.Reject("text-length");

        metadataCollector?.ObserveText(reader.Depth, reader.Value.AsSpan());

        if (styleElementDepth >= 0)
        {
            SvgResourceBudgetResult styleResult = SvgResourceBudgetCss.ValidateCss(reader.Value);
            if (!styleResult.Accepted)
                return styleResult;
        }

        return SvgResourceBudgetResult.Success;
    }

    private static SvgResourceBudgetResult ValidateHref(
        string elementName,
        string value,
        SvgResourceBudgetValidationContext context,
        int nestedDepth)
    {
        value = value.Trim();
        if (value.Length == 0 || value[0] == '#')
            return SvgResourceBudgetResult.Success;

        bool permitsEmbeddedImage =
            elementName.Equals("image", StringComparison.OrdinalIgnoreCase) ||
            elementName.Equals("feImage", StringComparison.OrdinalIgnoreCase);
        if (!permitsEmbeddedImage)
        {
            // Hyperlinks are inert when an SVG is rendered as an image. Other
            // href-bearing elements can resolve another document and are denied.
            return elementName.Equals("a", StringComparison.OrdinalIgnoreCase)
                ? SvgResourceBudgetResult.Success
                : SvgResourceBudgetResult.Reject("external-resource");
        }

        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return SvgResourceBudgetResult.Reject("external-image-reference");
        if (!SvgResourceBudgetDataUri.TryDecodeDataUri(value, out string mediaType, out byte[] payload))
            return SvgResourceBudgetResult.Reject("invalid-embedded-image");

        string hash = Convert.ToHexString(SHA256.HashData(payload));
        if (!context.SeenEmbeddedResources.Add(hash))
            return SvgResourceBudgetResult.Success;

        context.EmbeddedCompressedBytes += payload.LongLength;
        if (context.EmbeddedCompressedBytes > MaxEmbeddedCompressedBytes)
            return SvgResourceBudgetResult.Reject("embedded-image-data");

        if (mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            context.ComplexityUnits += Math.Max(1L, payload.LongLength / 256L);
            if (context.ComplexityUnits > MaxComplexityUnits)
                return SvgResourceBudgetResult.Reject("structural-complexity");
            return ValidateDocument(payload, context, nestedDepth + 1, metadataCollector: null);
        }

        MarkdownRasterImageInspection raster = MarkdownRasterImageInspection.Inspect(payload);
        if (!raster.IsAccepted || raster.FrameCount != 1 ||
            !SvgResourceBudgetDataUri.IsSupportedEmbeddedRasterType(mediaType, raster.Format))
        {
            return SvgResourceBudgetResult.Reject("embedded-image-format");
        }

        context.DecodedEmbeddedImageBytes += raster.DecodedBytes;
        context.ComplexityUnits += Math.Max(1L, raster.DecodedBytes / 4096L);
        return context.DecodedEmbeddedImageBytes > MaxDecodedEmbeddedImageBytes ||
            context.ComplexityUnits > MaxComplexityUnits
            ? SvgResourceBudgetResult.Reject("embedded-image-data")
            : SvgResourceBudgetResult.Success;
    }

    private static bool IsSvgNamespace(string? namespaceUri) =>
        string.IsNullOrEmpty(namespaceUri) ||
        string.Equals(namespaceUri, SvgContentRules.SvgNamespace, StringComparison.Ordinal);

    private static bool IsActiveContentElement(string localName) =>
        localName.Equals("script", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("foreignObject", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("animate", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("animateMotion", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("animateTransform", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("set", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("discard", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("video", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedFilterPrimitive(string localName) =>
        localName.Equals("feBlend", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feColorMatrix", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feComponentTransfer", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feComposite", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feConvolveMatrix", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feDiffuseLighting", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feDisplacementMap", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feDistantLight", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feDropShadow", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feFlood", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feFuncA", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feFuncB", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feFuncG", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feFuncR", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feGaussianBlur", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feImage", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feMerge", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feMergeNode", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feMorphology", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feOffset", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("fePointLight", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feSpecularLighting", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feSpotLight", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feTile", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("feTurbulence", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadLeadingNumber(string? value, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        ReadOnlySpan<char> span = value.AsSpan().Trim();
        int length = 0;
        while (length < span.Length &&
            (char.IsDigit(span[length]) || span[length] is '.' or '-' or '+' or 'e' or 'E'))
        {
            length++;
        }

        return length > 0 && double.TryParse(
            span[..length],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result);
    }
}
