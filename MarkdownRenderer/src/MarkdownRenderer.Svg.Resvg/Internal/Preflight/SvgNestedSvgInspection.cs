using System.Xml;
using MarkdownRenderer.Svg.Resvg.Internal.Preparation;

namespace MarkdownRenderer.Svg.Resvg.Internal.Preflight;

/// <summary>
/// Inspects an embedded SVG image's own document for the metadata and
/// references the outer preflight must account for, charging the references
/// it finds back through <see cref="SvgEmbeddedReferenceInspection"/> at the
/// nested document's depth.
/// </summary>
internal static class SvgNestedSvgInspection
{
    internal static void InspectNestedSvgMetadata(
        byte[] source,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken,
        int embeddedSvgDepth)
    {
        // The data URI was already decoded and charged by SvgEmbeddedReferenceInspection.InspectReference. Read
        // that same byte[] directly so nested metadata inspection adds no source
        // copy or DOM-sized representation.
        using var stream = new MemoryStream(source, writable: false);
        using XmlReader reader = XmlReader.Create(
            stream,
            SvgPreflight.CreateReaderSettings(
                Math.Min(options.MaxSourceBytes, options.MaxEmbeddedImageBytes),
                ignoreComments: true,
                asynchronous: false));

        bool sawRoot = false;
        bool readNext = true;
        int svgNesting = 0;
        char[]? attributeScratch = null;
        while (true)
        {
            if (readNext && !reader.Read())
                break;
            readNext = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Depth > options.MaxXmlDepth)
                throw SvgPreflightFailures.Resource("The embedded SVG exceeds the XML-depth ceiling.");

            if (reader.NodeType == XmlNodeType.DocumentType)
                throw SvgPreflightFailures.Unsupported("Embedded SVG DTDs are forbidden.");
            if (reader.NodeType == XmlNodeType.ProcessingInstruction &&
                reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase))
            {
                throw SvgPreflightFailures.Unsupported("External SVG stylesheets are forbidden.");
            }
            if (reader.NodeType == XmlNodeType.EndElement &&
                reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase) &&
                reader.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal))
            {
                svgNesting--;
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            readNext = InspectNestedElement(
                reader,
                options,
                resources,
                ref scan,
                cancellationToken,
                embeddedSvgDepth,
                ref sawRoot,
                ref svgNesting,
                ref attributeScratch);
        }

        if (!sawRoot)
            throw SvgPreflightFailures.Unsupported("The embedded SVG has no document root.");
    }

    /// <summary>
    /// Inspects one nested-SVG element: the root check, the active-content
    /// policy, nested-SVG depth, text metadata, attributes and style content.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the reader already stands on the next node
    /// because a style element's content was consumed.
    /// </returns>
    private static bool InspectNestedElement(
        XmlReader reader,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken,
        int embeddedSvgDepth,
        ref bool sawRoot,
        ref int svgNesting,
        ref char[]? attributeScratch)
    {
        string name = reader.LocalName;
        bool isSvgElement = reader.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal);
        if (!sawRoot)
        {
            if (!isSvgElement || !name.Equals("svg", StringComparison.OrdinalIgnoreCase))
                throw SvgPreflightFailures.Unsupported("The embedded document root is not an SVG element.");
            sawRoot = true;
        }

        // OpenAsync is a public provider boundary, so its recursive walk
        // must enforce the same active-content policy as the outer SVG.
        // ImageBox normally performs an earlier admission pass, but a
        // caller may invoke the provider directly with nested SVG bytes.
        if (SvgPreflight.ForbiddenElements.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw SvgPreflightFailures.Unsupported($"The static SVG subset does not allow <{name}>.");

        if (isSvgElement && name.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            svgNesting++;
            if (svgNesting > options.MaxNestedSvgDepth)
                throw SvgPreflightFailures.Resource("The SVG exceeds the nested-SVG ceiling.");
        }
        // The worker's text metadata is local-name based, including foreign
        // namespace descendants encountered while inspecting nested bytes.
        scan.HasText |= name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("textPath", StringComparison.OrdinalIgnoreCase);

        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                InspectNestedAttribute(
                    reader,
                    name,
                    isSvgElement,
                    options,
                    resources,
                    ref scan,
                    cancellationToken,
                    embeddedSvgDepth,
                    ref attributeScratch);
            }
            reader.MoveToElement();
        }

        if (name.Equals("style", StringComparison.OrdinalIgnoreCase) &&
            !reader.IsEmptyElement)
        {
            string css = reader.ReadElementContentAsString();
            SvgPreflightCss.InspectCss(
                css,
                options,
                resources,
                ref scan,
                cancellationToken,
                embeddedSvgDepth,
                trackThemeMetadata: isSvgElement);
            // ReadElementContentAsString advances to the first node after the
            // element; process it before asking the reader to advance again.
            return false;
        }

        if (reader.IsEmptyElement && isSvgElement &&
            name.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            svgNesting--;
        }
        return true;
    }

    /// <summary>
    /// Inspects the nested-SVG attribute under the reader: event handlers,
    /// theme metadata, references and CSS resources.
    /// </summary>
    private static void InspectNestedAttribute(
        XmlReader reader,
        string elementName,
        bool isSvgElement,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken,
        int embeddedSvgDepth,
        ref char[]? attributeScratch)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string attributeName = reader.LocalName;
        ReadOnlySpan<char> value = SvgPreflightStructure.ReadAttributeValue(
            reader, ref attributeScratch, options.MaxSourceBytes, cancellationToken).Trim();
        if (attributeName.Length > 2 &&
            attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
        {
            throw SvgPreflightFailures.Unsupported("SVG event handlers are forbidden.");
        }

        if (isSvgElement && attributeName.Equals("media", StringComparison.OrdinalIgnoreCase))
        {
            scan.UsesColorScheme |= SvgColorSchemeUsage.UsesPrefersColorSchemeMediaCondition(SvgPreflightCss.DecodeCssEscapes(value.ToString()));
        }
        else if (isSvgElement && SvgColorSchemeUsage.IsCurrentColorPresentationAttribute(attributeName))
        {
            scan.UsesCurrentColor |= SvgPreflightCss.ContainsCssIdentifier(SvgPreflightCss.DecodeCssEscapes(value.ToString()), "currentColor");
        }

        if (attributeName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
            attributeName.Equals("src", StringComparison.OrdinalIgnoreCase))
        {
            SvgEmbeddedReferenceInspection.InspectElementReference(
                elementName,
                attributeName,
                value,
                options,
                resources,
                ref scan,
                cancellationToken,
                embeddedSvgDepth);
        }
        if (SvgPreflightCss.MayContainCssResourceOrActiveContent(attributeName, value))
        {
            SvgPreflightCss.InspectCss(
                value.ToString(),
                options,
                resources,
                ref scan,
                cancellationToken,
                embeddedSvgDepth,
                attributeName.Equals("style", StringComparison.OrdinalIgnoreCase),
                isSvgElement);
        }
    }
}
