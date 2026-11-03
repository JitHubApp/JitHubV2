using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Validates <c>href</c> references: embedded raster payloads (media type, size,
/// pixels) and nested SVG documents, which are inspected recursively.
/// </summary>
internal static class SvgEmbeddedReferenceInspection
{
    internal static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    internal static void InspectReference(
        ReadOnlySpan<char> value,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref long embeddedBytes,
        ref long embeddedPixels,
        CancellationToken cancellationToken,
        int embeddedSvgDepth,
        ref bool hasText,
        ref bool usesCurrentColor,
        ref bool usesColorScheme)
    {
        if (value.IsEmpty || value[0] == '#')
            return;
        if (!value.StartsWith("data:".AsSpan(), StringComparison.OrdinalIgnoreCase))
            throw SvgPreflightFailures.Unsupported("Nested network and file references are forbidden.");

        int comma = value.IndexOf(',');
        if (comma <= 5)
            throw SvgPreflightFailures.Unsupported("An embedded image data URI is malformed.");
        ReadOnlySpan<char> header = value[5..comma];
        int parameterStart = header.IndexOf(';');
        ReadOnlySpan<char> declaredMediaType = (parameterStart < 0 ? header : header[..parameterStart]).Trim();
        // Small references are cheaper to decode than to maintain in the memo.
        // A declared nested SVG is never a useful memo candidate, even if the
        // raster sniffer later discovers a mislabeled PNG. Always inspect it.
        // Hash the complete XML-decoded URI, including its header and encoding;
        // payload-only or sampled keys could bypass validation of another URI.
        bool memoizeRaster = value.Length >= 1024 &&
            !declaredMediaType.Equals("image/svg+xml".AsSpan(), StringComparison.OrdinalIgnoreCase);
        SvgRasterReferenceKey referenceKey = default;
        if (memoizeRaster)
        {
            cancellationToken.ThrowIfCancellationRequested();
            referenceKey = SvgRasterReferenceKey.Create(value);
            cancellationToken.ThrowIfCancellationRequested();
            if (resources.IsRasterValidated(referenceKey))
                return;
        }
        // Small embedded rasters need only signature/dimension/hash inspection
        // here; their actual decode belongs to the worker. Keep those transient
        // bytes on this bounded stack, including when the MIME label is wrong.
        // Nested XML still obtains its own array for the synchronous reader.
        Span<byte> smallPayload = stackalloc byte[512];
        scoped ReadOnlySpan<byte> decoded = DecodeDataUriPayload(
            header,
            value[(comma + 1)..],
            smallPayload,
            options,
            cancellationToken,
            out byte[]? ownedPayload);
        string mediaType = ResolveEmbeddedMediaType(
            decoded, declaredMediaType, ref ownedPayload, options, cancellationToken);

        // Keep the complete digest in the per-inspection value-key set. A
        // byte[] digest plus a 64-character hex string per image needlessly
        // expands transient host memory on contributor grids. This keeps the
        // exact same SHA-256 deduplication without retaining either payload.
        SvgRasterReferenceKey identity = SvgRasterReferenceKey.Create(decoded);
        bool isSvg = mediaType == "image/svg+xml";
        int nestedSvgDepth = embeddedSvgDepth + 1;
        if (isSvg && nestedSvgDepth >= options.MaxNestedSvgDepth)
        {
            // The configured maximum includes the original document, matching
            // the worker's top-level reference budget (max depth minus one).
            throw SvgPreflightFailures.Resource("The SVG exceeds the nested-SVG ceiling.");
        }

        if (resources.AddResource(identity))
            ChargeNewResource(decoded, mediaType, isSvg, options, ref embeddedBytes, ref embeddedPixels);

        if (isSvg && !resources.IsNestedSvgValidatedAtDepth(identity, nestedSvgDepth))
        {
            InspectNestedSvgMetadata(
                ownedPayload!,
                options,
                resources,
                ref embeddedBytes,
                ref embeddedPixels,
                cancellationToken,
                nestedSvgDepth,
                ref hasText,
                ref usesCurrentColor,
                ref usesColorScheme);
            resources.MarkNestedSvgValidated(identity, nestedSvgDepth);
        }
        if (!isSvg && memoizeRaster)
            resources.RememberRaster(referenceKey);
    }

    /// <summary>
    /// Decodes a data URI payload, into <paramref name="smallPayload"/> when a
    /// base64 payload fits it and into a new array otherwise.
    /// </summary>
    /// <returns>The decoded bytes, which may alias <paramref name="smallPayload"/>.</returns>
    private static ReadOnlySpan<byte> DecodeDataUriPayload(
        ReadOnlySpan<char> header,
        ReadOnlySpan<char> payload,
        Span<byte> smallPayload,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken,
        out byte[]? ownedPayload)
    {
        ownedPayload = null;
        if (header.Contains(";base64".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            int length = SvgDataUriDecoder.GetBase64DecodedLength(
                payload, options.MaxEmbeddedImageBytes, cancellationToken);
            Span<byte> target = length <= smallPayload.Length
                ? smallPayload[..length]
                : (ownedPayload = new byte[length]);
            SvgDataUriDecoder.DecodeBase64Into(payload, target, cancellationToken);
            return target;
        }

        ownedPayload = SvgDataUriDecoder.DecodePercentEncoded(
            payload, options.MaxEmbeddedImageBytes, cancellationToken);
        return ownedPayload;
    }

    /// <summary>
    /// Identifies the embedded payload by its byte signature, falling back to
    /// the declared media type only for a payload that is plausibly SVG.
    /// </summary>
    private static string ResolveEmbeddedMediaType(
        ReadOnlySpan<byte> decoded,
        ReadOnlySpan<char> declaredMediaType,
        ref byte[]? ownedPayload,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken)
    {
        string? mediaType = SniffEmbeddedImageMediaType(decoded);
        if (mediaType is null)
        {
            ownedPayload ??= decoded.ToArray();
            mediaType = NormalizeEmbeddedImageMediaType(
                declaredMediaType, ownedPayload, options, cancellationToken);
        }
        if (mediaType is null)
            throw SvgPreflightFailures.Unsupported("The embedded image media type is unsupported.");
        return mediaType;
    }

    /// <summary>
    /// Charges a resource seen for the first time in this inspection against the
    /// decoded-byte ceiling and, for a raster, the decoded-pixel ceiling.
    /// </summary>
    private static void ChargeNewResource(
        ReadOnlySpan<byte> decoded,
        string mediaType,
        bool isSvg,
        ResvgMarkdownSvgRendererOptions options,
        ref long embeddedBytes,
        ref long embeddedPixels)
    {
        embeddedBytes = checked(embeddedBytes + decoded.Length);
        if (embeddedBytes > options.MaxEmbeddedImageBytes)
            throw SvgPreflightFailures.Resource("Embedded images exceed the decoded-byte ceiling.");

        if (!isSvg)
        {
            if (!TryReadImageDimensions(decoded, mediaType, out int imageWidth, out int imageHeight))
                throw SvgPreflightFailures.Unsupported("An embedded raster image is malformed or has no bounded dimensions.");
            embeddedPixels = checked(embeddedPixels + checked((long)imageWidth * imageHeight));
            if (embeddedPixels > options.MaxEmbeddedImagePixels)
                throw SvgPreflightFailures.Resource("Embedded images exceed the decoded-pixel ceiling.");
        }
    }

    internal static string? NormalizeEmbeddedImageMediaType(
        ReadOnlySpan<char> declaredMediaType,
        byte[] decoded,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken)
    {
        if (declaredMediaType.Equals("image/jpg".AsSpan(), StringComparison.OrdinalIgnoreCase))
            declaredMediaType = "image/jpeg";

        // The byte signature is authoritative. Several real contributor-card
        // generators label PNG avatars as image/gif; browsers sniff these and
        // render them, while trusting the declaration made our bounded dimension
        // parser reject otherwise valid content.
        string? sniffed = SniffEmbeddedImageMediaType(decoded);
        if (sniffed is not null)
            return sniffed;
        if (declaredMediaType.Equals("image/svg+xml".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
            LooksLikeSvg(decoded, options, cancellationToken))
        {
            return "image/svg+xml";
        }

        // Browsers tolerate missing and invalid data-URI media types when the
        // payload has an unambiguous raster signature. OpenCollective emits
        // contributor avatars as `data:false;base64,...`; accepting only
        // positively identified bounded raster formats preserves that useful
        // compatibility without turning arbitrary payloads into nested SVG.
        return null;
    }

    internal static string? SniffEmbeddedImageMediaType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(PngSignature))
            return "image/png";
        if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff)
            return "image/jpeg";
        if (data.Length >= 6 &&
            (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    internal static bool LooksLikeSvg(
        byte[] data,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken)
    {
        if (SvgUtf8Normalization.HasNonUtf8EncodingPrefix(data))
            throw SvgPreflightFailures.Unsupported("Nested SVG encodings other than UTF-8 are not supported.");

        using var stream = new MemoryStream(data, writable: false);
        using XmlReader reader = XmlReader.Create(
            stream,
            SvgPreflight.CreateReaderSettings(
                Math.Min(options.MaxSourceBytes, options.MaxEmbeddedImageBytes),
                ignoreComments: true,
                asynchronous: false));
        string? declaredEncoding = null;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Depth > options.MaxXmlDepth)
                throw SvgPreflightFailures.Resource("The embedded SVG exceeds the XML-depth ceiling.");
            if (reader.NodeType == XmlNodeType.XmlDeclaration)
            {
                declaredEncoding = reader.GetAttribute("encoding");
                continue;
            }
            if (reader.NodeType == XmlNodeType.DocumentType)
            {
                throw SvgPreflightFailures.Unsupported("Embedded SVG DTDs are forbidden.");
            }
            if (reader.NodeType == XmlNodeType.ProcessingInstruction &&
                reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase))
            {
                throw SvgPreflightFailures.Unsupported("External SVG stylesheets are forbidden.");
            }
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (!reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase) ||
                !reader.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal))
            {
                return false;
            }
            if (SvgUtf8Normalization.RequiresUtf8Normalization(data, declaredEncoding))
                throw SvgPreflightFailures.Unsupported("Nested SVG encodings other than UTF-8 are not supported.");
            // The worker remains authoritative for the complete nested tree.
            // Host sniffing needs only a bounded, secure prolog/root probe.
            return true;
        }

        return false;
    }

    internal static void InspectNestedSvgMetadata(
        byte[] source,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref long embeddedBytes,
        ref long embeddedPixels,
        CancellationToken cancellationToken,
        int embeddedSvgDepth,
        ref bool hasText,
        ref bool usesCurrentColor,
        ref bool usesColorScheme)
    {
        // The data URI was already decoded and charged by InspectReference. Read
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
                ref embeddedBytes,
                ref embeddedPixels,
                cancellationToken,
                embeddedSvgDepth,
                ref hasText,
                ref usesCurrentColor,
                ref usesColorScheme,
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
        ref long embeddedBytes,
        ref long embeddedPixels,
        CancellationToken cancellationToken,
        int embeddedSvgDepth,
        ref bool hasText,
        ref bool usesCurrentColor,
        ref bool usesColorScheme,
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
        hasText |= name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
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
                    ref embeddedBytes,
                    ref embeddedPixels,
                    cancellationToken,
                    embeddedSvgDepth,
                    ref hasText,
                    ref usesCurrentColor,
                    ref usesColorScheme,
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
                ref embeddedBytes,
                ref embeddedPixels,
                cancellationToken,
                embeddedSvgDepth,
                ref hasText,
                ref usesCurrentColor,
                ref usesColorScheme,
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
        ref long embeddedBytes,
        ref long embeddedPixels,
        CancellationToken cancellationToken,
        int embeddedSvgDepth,
        ref bool hasText,
        ref bool usesCurrentColor,
        ref bool usesColorScheme,
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
            usesColorScheme |= SvgColorSchemeUsage.UsesPrefersColorSchemeMediaCondition(SvgPreflightCss.DecodeCssEscapes(value.ToString()));
        }
        else if (isSvgElement && SvgColorSchemeUsage.IsCurrentColorPresentationAttribute(attributeName))
        {
            usesCurrentColor |= SvgPreflightCss.ContainsCssIdentifier(SvgPreflightCss.DecodeCssEscapes(value.ToString()), "currentColor");
        }

        if (attributeName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
            attributeName.Equals("src", StringComparison.OrdinalIgnoreCase))
        {
            InspectElementReference(
                elementName,
                attributeName,
                value,
                options,
                resources,
                ref embeddedBytes,
                ref embeddedPixels,
                cancellationToken,
                embeddedSvgDepth,
                ref hasText,
                ref usesCurrentColor,
                ref usesColorScheme);
        }
        if (SvgPreflightCss.MayContainCssResourceOrActiveContent(attributeName, value))
        {
            SvgPreflightCss.InspectCss(
                value.ToString(),
                options,
                resources,
                ref embeddedBytes,
                ref embeddedPixels,
                cancellationToken,
                embeddedSvgDepth,
                ref hasText,
                ref usesCurrentColor,
                ref usesColorScheme,
                attributeName.Equals("style", StringComparison.OrdinalIgnoreCase),
                isSvgElement);
        }
    }

    internal static void InspectElementReference(
        string elementName,
        string attributeName,
        ReadOnlySpan<char> value,
        ResvgMarkdownSvgRendererOptions options,
        SvgResourceInspectionContext resources,
        ref long embeddedBytes,
        ref long embeddedPixels,
        CancellationToken cancellationToken,
        int embeddedSvgDepth,
        ref bool hasText,
        ref bool usesCurrentColor,
        ref bool usesColorScheme)
    {
        // An SVG rendered as an image has no navigation surface. Keep authored
        // hyperlinks for fidelity, but do not treat their inert destinations as
        // subresources. Resource-bearing elements remain data-URI-only.
        if (attributeName.Equals("href", StringComparison.OrdinalIgnoreCase) &&
            elementName.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (value.Length == 0 || value[0] == '#')
            return;

        bool isEmbeddedImage =
            elementName.Equals("image", StringComparison.OrdinalIgnoreCase) ||
            elementName.Equals("feImage", StringComparison.OrdinalIgnoreCase);
        if (!isEmbeddedImage)
            throw SvgPreflightFailures.Unsupported("Nested network and file references are forbidden.");

        InspectReference(
            value,
            options,
            resources,
            ref embeddedBytes,
            ref embeddedPixels,
            cancellationToken,
            embeddedSvgDepth,
            ref hasText,
            ref usesCurrentColor,
            ref usesColorScheme);
    }

    internal static bool TryReadImageDimensions(ReadOnlySpan<byte> data, string mediaType, out int width, out int height)
    {
        width = height = 0;
        if (mediaType == "image/png" && data.Length >= 24 && data[..8].SequenceEqual(PngSignature))
        {
            width = ReadBigEndianInt32(data[16..20]);
            height = ReadBigEndianInt32(data[20..24]);
            return width > 0 && height > 0;
        }
        if (mediaType == "image/gif" && data.Length >= 10 &&
            (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
        {
            width = data[6] | data[7] << 8;
            height = data[8] | data[9] << 8;
            return width > 0 && height > 0;
        }
        if (mediaType == "image/webp" && data.Length >= 25 &&
            data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            if (data[12..16].SequenceEqual("VP8X"u8) && data.Length >= 30)
            {
                width = 1 + data[24] + (data[25] << 8) + (data[26] << 16);
                height = 1 + data[27] + (data[28] << 8) + (data[29] << 16);
                return width > 0 && height > 0;
            }
            if (data[12..16].SequenceEqual("VP8 "u8) && data.Length >= 30 &&
                data[23] == 0x9d && data[24] == 0x01 && data[25] == 0x2a)
            {
                width = (data[26] | data[27] << 8) & 0x3fff;
                height = (data[28] | data[29] << 8) & 0x3fff;
                return width > 0 && height > 0;
            }
            if (data[12..16].SequenceEqual("VP8L"u8) && data[20] == 0x2f)
            {
                width = 1 + data[21] + ((data[22] & 0x3f) << 8);
                height = 1 + (data[22] >> 6) + (data[23] << 2) + ((data[24] & 0x0f) << 10);
                return width > 0 && height > 0;
            }
            return false;
        }
        if (mediaType == "image/jpeg" && data.Length >= 4 && data[0] == 0xff && data[1] == 0xd8)
        {
            int index = 2;
            while (index + 8 < data.Length)
            {
                if (data[index] != 0xff)
                {
                    index++;
                    continue;
                }
                byte marker = data[index + 1];
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                {
                    height = (data[index + 5] << 8) | data[index + 6];
                    width = (data[index + 7] << 8) | data[index + 8];
                    return width > 0 && height > 0;
                }
                int segment = (data[index + 2] << 8) | data[index + 3];
                if (segment < 2)
                    return false;
                index += segment + 2;
            }
        }
        return false;
    }

    internal static int ReadBigEndianInt32(ReadOnlySpan<byte> value) =>
        (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];
}
