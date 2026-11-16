using System.Text;
using System.Xml;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal.Preparation;
using MarkdownRenderer.Svg.Resvg.Internal.Sources;

namespace MarkdownRenderer.Svg.Resvg.Internal.Preflight;

/// <summary>
/// The preflight's single streaming pass over an SVG source: reader setup,
/// node, element and attribute rules, embedded-resource inspection and the
/// metadata the result reports, accumulated in one <see cref="SvgPreflightScan"/>.
/// <see cref="SvgPreflight"/> owns the source snapshot, normalization and the
/// result; this class only reads the source it is given.
/// </summary>
internal static class SvgPreflightDocumentScanner
{
    // The outer document's own nesting level for embedded-resource checks.
    private const int OuterSvgDepth = 0;

    /// <summary>
    /// Parses the whole source once, enforcing every structural, element,
    /// attribute and embedded-resource rule, and records the metadata the
    /// result reports. The reader, its input and the inspection workspace are
    /// released on every exit.
    /// </summary>
    /// <param name="source">The private source snapshot.</param>
    /// <param name="options">The renderer ceilings.</param>
    /// <param name="inspectionWorkspaces">The renderer's reusable inspection workspaces, if any.</param>
    /// <param name="scan">Receives the totals and metadata.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    internal static void ScanDocument(
        SvgSourceBuffer source,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionWorkspacePool? inspectionWorkspaces,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken)
    {
        IDisposable? readerInput = null;
        XmlReader? reader = null;
        var resources = new SvgResourceInspectionContext(inspectionWorkspaces);
        char[]? attributeScratch = null;
        bool readNext = true;

        try
        {
            XmlReaderSettings settings = SvgPreflight.CreateReaderSettings(
                options.MaxSourceBytes, ignoreComments: true, asynchronous: false);
            reader = OpenReader(source, settings, ref readerInput, cancellationToken);
            while (true)
            {
                if (readNext && !reader.Read())
                    break;
                readNext = ScanNode(reader, options, resources, ref attributeScratch, ref scan, cancellationToken);
            }
        }
        catch (MarkdownSvgException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or FormatException or OverflowException or DecoderFallbackException)
        {
            throw SvgPreflightFailures.Unsupported("The SVG is malformed.", exception);
        }
        finally
        {
            try
            {
                reader?.Dispose();
            }
            finally
            {
                reader = null;
                try
                {
                    readerInput?.Dispose();
                }
                finally
                {
                    readerInput = null;
                    attributeScratch = null;
                    resources.Dispose();
                }
            }
        }
    }

    /// <summary>Creates the synchronous reader over a read lease of the source.</summary>
    /// <param name="source">The private source snapshot.</param>
    /// <param name="settings">The hardened reader settings.</param>
    /// <param name="readerInput">Receives the text reader or stream that owns the lease, as soon as it exists.</param>
    /// <param name="cancellationToken">Cancels reads.</param>
    /// <returns>The reader.</returns>
    private static XmlReader OpenReader(
        SvgSourceBuffer source,
        XmlReaderSettings settings,
        ref IDisposable? readerInput,
        CancellationToken cancellationToken)
    {
        SvgSourceReadLease? readerLease = source.AcquireReadLease();
        try
        {
            int utf8Offset = readerLease.GetSpanAt(0).StartsWith<byte>([0xef, 0xbb, 0xbf]) ? 3 : 0;
            if (readerLease.Length >= 4096 &&
                !SvgUtf8Normalization.HasNonUtf8EncodingPrefix(readerLease.GetSpanAt(0)) &&
                !readerLease.GetSpanAt(utf8Offset).StartsWith("<?xml"u8))
            {
                // The snapshot is already private memory. Decode directly
                // into XmlReader's character buffer without a second 4 KiB
                // byte buffer. The text reader owns the lease through parse.
                var textInput = new SvgUtf8TextReader(readerLease, utf8Offset, cancellationToken);
                readerLease = null;
                readerInput = textInput;
                return XmlReader.Create(textInput, settings);
            }
            else
            {
                // Preserve XmlReader's BOM/declaration sniffing for small
                // sources and non-UTF-8 encodings, while the stream retains
                // a lease over either array or native-backed storage.
                var stream = new SvgSourceStream(readerLease, cancellationToken);
                readerLease = null;
                readerInput = stream;
                return XmlReader.Create(stream, settings);
            }
        }
        finally
        {
            readerLease?.Dispose();
        }
    }

    /// <summary>Checks the reader's current node.</summary>
    /// <param name="reader">The reader, positioned on the node.</param>
    /// <param name="options">The renderer ceilings.</param>
    /// <param name="resources">The inspection context for embedded resources.</param>
    /// <param name="attributeScratch">The outer parser's attribute buffer, borrowed on first use.</param>
    /// <param name="scan">The running totals.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>False when the reader is already positioned on the next node.</returns>
    private static bool ScanNode(
        XmlReader reader,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref char[]? attributeScratch,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reader.Depth > options.MaxXmlDepth)
            throw SvgPreflightFailures.Resource("The SVG exceeds the XML-depth ceiling.");

        if (reader.NodeType == XmlNodeType.DocumentType)
        {
            throw SvgPreflightFailures.Unsupported("SVG DTDs and entities are forbidden.");
        }

        if (reader.NodeType == XmlNodeType.XmlDeclaration)
        {
            scan.DeclaredEncoding = reader.GetAttribute("encoding");
            return true;
        }

        if (reader.NodeType == XmlNodeType.ProcessingInstruction &&
            reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase))
        {
            throw SvgPreflightFailures.Unsupported("External SVG stylesheets are forbidden.");
        }

        if (reader.NodeType == XmlNodeType.EndElement &&
            reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase) &&
            reader.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal))
        {
            scan.SvgNesting--;
            return true;
        }

        if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
            XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
        {
            SvgPreflightStructure.ChargeStructuralPayload(
                reader.Value,
                options,
                ref scan.StructuralCost,
                "The SVG exceeds the weighted text-content ceiling.");
            return true;
        }

        if (reader.NodeType != XmlNodeType.Element)
            return true;

        return ScanElement(reader, options, resources, ref attributeScratch, ref scan, cancellationToken);
    }

    /// <summary>Checks one element, its attributes and any text content the scan consumes.</summary>
    /// <param name="reader">The reader, positioned on the element.</param>
    /// <param name="options">The renderer ceilings.</param>
    /// <param name="resources">The inspection context for embedded resources.</param>
    /// <param name="attributeScratch">The outer parser's attribute buffer, borrowed on first use.</param>
    /// <param name="scan">The running totals.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>False when the element's content was read and the reader is on the next node.</returns>
    private static bool ScanElement(
        XmlReader reader,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref char[]? attributeScratch,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken)
    {
        string name = reader.LocalName;
        if (!scan.SawRoot)
        {
            if (!name.Equals("svg", StringComparison.OrdinalIgnoreCase) ||
                !reader.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal))
            {
                throw SvgPreflightFailures.Unsupported("The document root is not an SVG element.");
            }
            scan.SawRoot = true;
        }

        if (SvgPreflight.ForbiddenElements.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw SvgPreflightFailures.Unsupported($"The static SVG subset does not allow <{name}>.");
        bool isSvgElement = reader.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal);
        if (isSvgElement && name.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            scan.SvgNesting++;
            if (scan.SvgNesting > options.MaxNestedSvgDepth)
                throw SvgPreflightFailures.Resource("The SVG exceeds the nested-SVG ceiling.");
        }

        // Native inspection uses local names regardless of namespace.
        // Preserve that conservative font-discovery parity for foreign
        // wrappers that still contain renderable text.
        scan.HasText |= name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("textPath", StringComparison.OrdinalIgnoreCase);
        bool isFilter = name.StartsWith("fe", StringComparison.OrdinalIgnoreCase);
        if (isFilter)
            scan.FilterPrimitives++;

        SvgPreflightStructure.ChargeStructural(
            1 + (isFilter ? 31 : 0),
            options,
            ref scan.StructuralCost,
            "The SVG exceeds the weighted structural ceiling.");
        if (reader.Depth == 0)
        {
            scan.Width = SvgPreflightStructure.ParseCssLength(reader.GetAttribute("width"));
            scan.Height = SvgPreflightStructure.ParseCssLength(reader.GetAttribute("height"));
        }

        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                ScanAttribute(reader, name, isSvgElement, options, resources, ref attributeScratch, ref scan, cancellationToken);
            }
            reader.MoveToElement();
        }

        if (TryScanElementContent(reader, name, isSvgElement, options, resources, ref scan, cancellationToken))
            return false;

        if (reader.IsEmptyElement && isSvgElement &&
            name.Equals("svg", StringComparison.OrdinalIgnoreCase))
            scan.SvgNesting--;
        return true;
    }

    /// <summary>
    /// Reads and checks the content of a non-empty style element, or of a
    /// top-level description, which the scan consumes whole.
    /// </summary>
    /// <param name="reader">The reader, positioned on the element.</param>
    /// <param name="name">The element's local name.</param>
    /// <param name="isSvgElement">Whether the element is in the SVG namespace.</param>
    /// <param name="options">The renderer ceilings.</param>
    /// <param name="resources">The inspection context for embedded resources.</param>
    /// <param name="scan">The running totals.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>True when the content was read and the reader is on the next node.</returns>
    private static bool TryScanElementContent(
        XmlReader reader,
        string name,
        bool isSvgElement,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken)
    {
        if (name.Equals("style", StringComparison.OrdinalIgnoreCase) && !reader.IsEmptyElement)
        {
            string css = reader.ReadElementContentAsString();
            SvgPreflightStructure.ChargeStructuralPayload(
                css,
                options,
                ref scan.StructuralCost,
                "The SVG exceeds the weighted CSS ceiling.");
            SvgPreflightCss.InspectCss(
                css,
                options,
                resources,
                ref scan,
                cancellationToken,
                OuterSvgDepth,
                trackThemeMetadata: isSvgElement);
            // ReadElementContentAsString leaves the reader on the
            // next node. Process that node before asking the reader to
            // advance again, or adjacent security-sensitive elements
            // and metadata would be silently skipped.
            return true;
        }

        if (reader.Depth == 1 && name.Equals("desc", StringComparison.OrdinalIgnoreCase) && !reader.IsEmptyElement)
        {
            string text = reader.ReadElementContentAsString();
            SvgPreflightStructure.ChargeStructuralPayload(
                text,
                options,
                ref scan.StructuralCost,
                "The SVG exceeds the weighted description ceiling.");
            scan.Description ??= text.Length <= 4096 ? text.Trim() : text[..4096].Trim();
            return true;
        }

        return false;
    }

    /// <summary>Charges and checks the attribute the reader is positioned on.</summary>
    /// <param name="reader">The reader, positioned on the attribute.</param>
    /// <param name="elementName">The owning element's local name.</param>
    /// <param name="isSvgElement">Whether the owning element is in the SVG namespace.</param>
    /// <param name="options">The renderer ceilings.</param>
    /// <param name="resources">The inspection context for embedded resources.</param>
    /// <param name="attributeScratch">The outer parser's attribute buffer, borrowed on first use.</param>
    /// <param name="scan">The running totals.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    private static void ScanAttribute(
        XmlReader reader,
        string elementName,
        bool isSvgElement,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref char[]? attributeScratch,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken)
    {
        string attributeName = reader.LocalName;
        SvgPreflightStructure.ChargeStructuralPayload(
            attributeName,
            options,
            ref scan.StructuralCost,
            "The SVG exceeds the weighted attribute ceiling.");
        // Only the outer parser borrows this destination.
        // Nested SVGs need independent scratch while the
        // referring attribute span is still being checked.
        attributeScratch ??= resources.GetAttributeScratch(checked(options.MaxSourceBytes + 2));
        ReadOnlySpan<char> rawValue = SvgPreflightStructure.ReadAttributeValue(
            reader, ref attributeScratch, options.MaxSourceBytes, cancellationToken);
        if (elementName.Equals("path", StringComparison.OrdinalIgnoreCase) &&
            reader.Name.Equals("d", StringComparison.Ordinal))
        {
            // Match GetAttribute("d")'s exact qualified name
            // and charge the untrimmed payload without a
            // second, potentially large path-data string.
            SvgPreflightStructure.ChargeStructural(SvgPreflightStructure.ComputePathComplexity(rawValue), options, ref scan.StructuralCost,
                "The SVG exceeds the weighted path-complexity ceiling.");
        }
        ReadOnlySpan<char> value = rawValue.Trim();
        SvgPreflightStructure.ChargeStructuralPayload(
            value,
            options,
            ref scan.StructuralCost,
            "The SVG exceeds the weighted attribute-value ceiling.");
        if (attributeName.Length > 2 && attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            throw SvgPreflightFailures.Unsupported("SVG event handlers are forbidden.");
        InspectAttributeContent(elementName, attributeName, value, isSvgElement, options, resources, ref scan, cancellationToken);
    }

    /// <summary>Records theme metadata and inspects any reference or CSS an attribute value carries.</summary>
    /// <param name="elementName">The owning element's local name.</param>
    /// <param name="attributeName">The attribute's local name.</param>
    /// <param name="value">The trimmed attribute value.</param>
    /// <param name="isSvgElement">Whether the owning element is in the SVG namespace.</param>
    /// <param name="options">The renderer ceilings.</param>
    /// <param name="resources">The inspection context for embedded resources.</param>
    /// <param name="scan">The running totals.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    private static void InspectAttributeContent(
        string elementName,
        string attributeName,
        ReadOnlySpan<char> value,
        bool isSvgElement,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref SvgPreflightScan scan,
        CancellationToken cancellationToken)
    {
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
                OuterSvgDepth);
        }
        // Only CSS-bearing values use CSS quoting rules. Treating
        // arbitrary metadata (for example an aria-label containing
        // an apostrophe) as a stylesheet caused valid SVGs to fail
        // with an unterminated-string diagnostic. Decode escapes
        // first so resource-bearing presentation attributes still
        // cannot hide url() or active CSS directives.
        if (SvgPreflightCss.MayContainCssResourceOrActiveContent(attributeName, value))
        {
            SvgPreflightCss.InspectCss(
                value.ToString(),
                options,
                resources,
                ref scan,
                cancellationToken,
                OuterSvgDepth,
                attributeName.Equals("style", StringComparison.OrdinalIgnoreCase),
                isSvgElement);
        }
    }
}
