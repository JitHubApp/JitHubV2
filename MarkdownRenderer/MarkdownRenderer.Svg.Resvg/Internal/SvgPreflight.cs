using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed record SvgPreflightResult(
    byte[] Source,
    byte[] Hash,
    MarkdownSvgDocumentInfo Info,
    int StructuralCost,
    long EmbeddedBytes,
    long EmbeddedPixels,
    int FilterPrimitives);

internal static class SvgPreflight
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly string[] ForbiddenElements =
    [
        "script", "foreignobject", "animate", "animatemotion", "animatetransform",
        "animatecolor", "set", "discard",
    ];

    public static Task<SvgPreflightResult> InspectAsync(
        MarkdownSvgOpenRequest request,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReadOnlyMemory<byte> input = request.SanitizedSvgBytes;
        if (input.IsEmpty)
            throw Unsupported("The SVG source is empty.");
        if (input.Length > options.MaxSourceBytes)
            throw Resource("The SVG source exceeds the configured byte ceiling.");

        byte[] source = input.ToArray();
        return Task.Run(
            () => InspectCoreAsync(source, options, cancellationToken),
            cancellationToken);
    }

    private static async Task<SvgPreflightResult> InspectCoreAsync(
        byte[] source,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(source, writable: false);
        var settings = new XmlReaderSettings
        {
            Async = true,
            // Legacy static SVGs commonly carry the SVG 1.1 external DOCTYPE.
            // Keep it inert by disabling resolution and reject internal subsets
            // explicitly while preserving those otherwise-valid documents.
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = false,
            MaxCharactersInDocument = options.MaxSourceBytes,
            MaxCharactersFromEntities = 1,
        };

        using XmlReader reader = XmlReader.Create(stream, settings);
        int structuralCost = 0;
        int svgNesting = 0;
        int filterPrimitives = 0;
        bool sawRoot = false;
        bool hasText = false;
        bool usesCurrentColor = false;
        bool usesColorScheme = false;
        double? width = null;
        double? height = null;
        string? description = null;
        long embeddedBytes = 0;
        long embeddedPixels = 0;
        var resources = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Depth > options.MaxXmlDepth)
                    throw Resource("The SVG exceeds the XML-depth ceiling.");

                if (reader.NodeType == XmlNodeType.DocumentType)
                {
                    if (!string.IsNullOrWhiteSpace(reader.Value))
                        throw Unsupported("SVG DTD internal subsets are forbidden.");
                    continue;
                }

                if (reader.NodeType == XmlNodeType.EndElement &&
                    reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                {
                    svgNesting--;
                    continue;
                }

                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                    XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    ChargeStructuralPayload(
                        reader.Value,
                        options,
                        ref structuralCost,
                        "The SVG exceeds the weighted text-content ceiling.");
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                string name = reader.LocalName;
                if (!sawRoot)
                {
                    if (!name.Equals("svg", StringComparison.OrdinalIgnoreCase))
                        throw Unsupported("The document root is not an SVG element.");
                    sawRoot = true;
                }

                if (ForbiddenElements.Contains(name, StringComparer.OrdinalIgnoreCase))
                    throw Unsupported($"The static SVG subset does not allow <{name}>.");
                if (name.Equals("svg", StringComparison.OrdinalIgnoreCase))
                {
                    svgNesting++;
                    if (svgNesting > options.MaxNestedSvgDepth)
                        throw Resource("The SVG exceeds the nested-SVG ceiling.");
                }

                hasText |= name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("textPath", StringComparison.OrdinalIgnoreCase);
                bool isFilter = name.StartsWith("fe", StringComparison.OrdinalIgnoreCase);
                if (isFilter)
                    filterPrimitives++;

                ChargeStructural(
                    1 + (isFilter ? 31 : 0),
                    options,
                    ref structuralCost,
                    "The SVG exceeds the weighted structural ceiling.");
                string? pathData = reader.GetAttribute("d");
                if (name.Equals("path", StringComparison.OrdinalIgnoreCase) && pathData is not null)
                {
                    ChargeStructural(
                        ComputePathComplexity(pathData),
                        options,
                        ref structuralCost,
                        "The SVG exceeds the weighted path-complexity ceiling.");
                }

                if (reader.Depth == 0)
                {
                    width = ParseCssLength(reader.GetAttribute("width"));
                    height = ParseCssLength(reader.GetAttribute("height"));
                }

                if (reader.HasAttributes)
                {
                    while (reader.MoveToNextAttribute())
                    {
                        string attributeName = reader.LocalName;
                        string value = reader.Value.Trim();
                        ChargeStructuralPayload(
                            attributeName,
                            options,
                            ref structuralCost,
                            "The SVG exceeds the weighted attribute ceiling.");
                        ChargeStructuralPayload(
                            value,
                            options,
                            ref structuralCost,
                            "The SVG exceeds the weighted attribute-value ceiling.");
                        if (attributeName.Length > 2 && attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                            throw Unsupported("SVG event handlers are forbidden.");
                        usesCurrentColor |= value.Contains("currentColor", StringComparison.OrdinalIgnoreCase);
                        usesColorScheme |= value.Contains("prefers-color-scheme", StringComparison.OrdinalIgnoreCase);
                        if (attributeName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                            attributeName.Equals("src", StringComparison.OrdinalIgnoreCase))
                        {
                            InspectElementReference(
                                name,
                                attributeName,
                                value,
                                options,
                                resources,
                                ref embeddedBytes,
                                ref embeddedPixels);
                        }
                        // Only CSS-bearing values use CSS quoting rules. Treating
                        // arbitrary metadata (for example an aria-label containing
                        // an apostrophe) as a stylesheet caused valid SVGs to fail
                        // with an unterminated-string diagnostic. Decode escapes
                        // first so resource-bearing presentation attributes still
                        // cannot hide url() or active CSS directives.
                        if (MayContainCssResourceOrActiveContent(attributeName, value))
                        {
                            InspectCss(
                                value,
                                options,
                                resources,
                                ref embeddedBytes,
                                ref embeddedPixels);
                        }
                    }
                    reader.MoveToElement();
                }

                if (name.Equals("style", StringComparison.OrdinalIgnoreCase) && !reader.IsEmptyElement)
                {
                    string css = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    ChargeStructuralPayload(
                        css,
                        options,
                        ref structuralCost,
                        "The SVG exceeds the weighted CSS ceiling.");
                    usesCurrentColor |= css.Contains("currentColor", StringComparison.OrdinalIgnoreCase);
                    usesColorScheme |= css.Contains("prefers-color-scheme", StringComparison.OrdinalIgnoreCase);
                    InspectCss(
                        css,
                        options,
                        resources,
                        ref embeddedBytes,
                        ref embeddedPixels);
                    continue;
                }

                if (reader.Depth == 1 && name.Equals("desc", StringComparison.OrdinalIgnoreCase) && !reader.IsEmptyElement)
                {
                    string text = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    ChargeStructuralPayload(
                        text,
                        options,
                        ref structuralCost,
                        "The SVG exceeds the weighted description ceiling.");
                    description ??= text.Length <= 4096 ? text.Trim() : text[..4096].Trim();
                    continue;
                }

                if (reader.IsEmptyElement && name.Equals("svg", StringComparison.OrdinalIgnoreCase))
                    svgNesting--;
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
        catch (Exception exception) when (exception is XmlException or FormatException or OverflowException)
        {
            throw Unsupported("The SVG is malformed.", exception);
        }

        if (!sawRoot)
            throw Unsupported("The SVG has no document root.");

        // The worker intentionally rejects every DTD token before handing data
        // to the native parser. Remove the already-validated, inert external
        // declaration here; internal subsets never reach this point.
        source = StripInertDoctype(source);
        source = NormalizeEmbeddedRasterMediaTypes(source);
        byte[] hash = SHA256.HashData(source);
        double? ratio = width > 0 && height > 0 ? width / height : null;
        return new SvgPreflightResult(
            source,
            hash,
            new MarkdownSvgDocumentInfo(width, height, ratio, description, hasText, usesCurrentColor, usesColorScheme),
            structuralCost,
            embeddedBytes,
            embeddedPixels,
            filterPrimitives);
    }

    private static byte[] StripInertDoctype(byte[] source)
    {
        ReadOnlySpan<byte> marker = "<!DOCTYPE"u8;
        int start = IndexOfAsciiIgnoreCase(source, marker);
        if (start < 0)
            return source;

        byte quote = 0;
        int end = -1;
        for (int index = start + marker.Length; index < source.Length; index++)
        {
            byte current = source[index];
            if (quote != 0)
            {
                if (current == quote)
                    quote = 0;
                continue;
            }
            if (current is (byte)'\'' or (byte)'"')
            {
                quote = current;
                continue;
            }
            if (current == (byte)'>')
            {
                end = index + 1;
                break;
            }
        }
        if (end < 0)
            throw Unsupported("The SVG DOCTYPE declaration is malformed.");

        byte[] sanitized = new byte[source.Length - (end - start)];
        source.AsSpan(0, start).CopyTo(sanitized);
        source.AsSpan(end).CopyTo(sanitized.AsSpan(start));
        return sanitized;
    }

    private static byte[] NormalizeEmbeddedRasterMediaTypes(byte[] source)
    {
        // resvg intentionally honors the declared data-URI media type. Browsers
        // sniff common image signatures, and generated contributor cards in the
        // wild occasionally label PNG avatars as GIF. The payload was already
        // decoded, bounded, and signature-validated above; canonicalize only
        // those admitted raster references before hashing and worker dispatch.
        if (IndexOfAsciiIgnoreCase(source, "data:"u8) < 0)
            return source;

        var document = new XmlDocument
        {
            PreserveWhitespace = true,
            XmlResolver = null,
        };
        using (var input = new MemoryStream(source, writable: false))
        using (XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = ResvgMarkdownSvgRendererOptions.HardMaxSourceBytes,
            MaxCharactersFromEntities = 0,
        }))
        {
            document.Load(reader);
        }

        bool changed = false;
        var pending = new Stack<XmlNode>();
        if (document.DocumentElement is not null)
            pending.Push(document.DocumentElement);
        while (pending.Count > 0)
        {
            XmlNode node = pending.Pop();
            if (node is XmlElement element &&
                (element.LocalName.Equals("image", StringComparison.OrdinalIgnoreCase) ||
                    element.LocalName.Equals("feImage", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (XmlAttribute attribute in element.Attributes)
                {
                    if (!attribute.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                        !TryNormalizeRasterDataUri(attribute.Value, out string normalized))
                    {
                        continue;
                    }

                    attribute.Value = normalized;
                    changed = true;
                }
            }

            for (XmlNode? child = node.LastChild; child is not null; child = child.PreviousSibling)
                pending.Push(child);
        }

        if (!changed)
            return source;

        using var output = new MemoryStream(source.Length);
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

    private static bool TryNormalizeRasterDataUri(string value, out string normalized)
    {
        normalized = value;
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        int comma = value.IndexOf(',');
        if (comma <= 5)
            return false;

        string header = value[5..comma];
        string payload = value[(comma + 1)..];
        byte[] decoded;
        try
        {
            decoded = header.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload)
                : PercentDecode(payload);
        }
        catch (FormatException)
        {
            return false;
        }

        string? actual = SniffEmbeddedImageMediaType(decoded);
        if (actual is null)
            return false;
        int parameters = header.IndexOf(';');
        string declared = (parameters < 0 ? header : header[..parameters]).Trim();
        if (declared.Equals("image/jpg", StringComparison.OrdinalIgnoreCase))
            declared = "image/jpeg";
        if (declared.Equals(actual, StringComparison.OrdinalIgnoreCase))
            return false;

        string suffix = parameters < 0 ? string.Empty : header[parameters..];
        normalized = string.Concat("data:", actual, suffix, ",", payload);
        return true;
    }

    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        for (int index = 0; index <= source.Length - value.Length; index++)
        {
            bool matches = true;
            for (int offset = 0; offset < value.Length; offset++)
            {
                byte left = source[index + offset];
                byte right = value[offset];
                if (left is >= (byte)'a' and <= (byte)'z')
                    left -= (byte)('a' - 'A');
                if (right is >= (byte)'a' and <= (byte)'z')
                    right -= (byte)('a' - 'A');
                if (left == right)
                    continue;
                matches = false;
                break;
            }
            if (matches)
                return index;
        }
        return -1;
    }

    private static void InspectReference(
        string value,
        ResvgMarkdownSvgRendererOptions options,
        HashSet<string> resources,
        ref long embeddedBytes,
        ref long embeddedPixels)
    {
        if (value.Length == 0 || value[0] == '#')
            return;
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw Unsupported("Nested network and file references are forbidden.");

        int comma = value.IndexOf(',');
        if (comma <= 5)
            throw Unsupported("An embedded image data URI is malformed.");
        string header = value[5..comma];
        string payload = value[(comma + 1)..];
        byte[] decoded = header.Contains(";base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(payload)
            : PercentDecode(payload);
        string declaredMediaType = header.Split(';', 2)[0].Trim().ToLowerInvariant();
        string? mediaType = NormalizeEmbeddedImageMediaType(declaredMediaType, decoded);
        if (mediaType is null)
            throw Unsupported("The embedded image media type is unsupported.");

        string identity = Convert.ToHexString(SHA256.HashData(decoded));
        if (!resources.Add(identity))
            return;

        embeddedBytes = checked(embeddedBytes + decoded.Length);
        if (embeddedBytes > options.MaxEmbeddedImageBytes)
            throw Resource("Embedded images exceed the decoded-byte ceiling.");

        if (mediaType != "image/svg+xml")
        {
            if (!TryReadImageDimensions(decoded, mediaType, out int imageWidth, out int imageHeight))
                throw Unsupported("An embedded raster image is malformed or has no bounded dimensions.");
            embeddedPixels = checked(embeddedPixels + checked((long)imageWidth * imageHeight));
            if (embeddedPixels > options.MaxEmbeddedImagePixels)
                throw Resource("Embedded images exceed the decoded-pixel ceiling.");
        }
    }

    private static string? NormalizeEmbeddedImageMediaType(string declaredMediaType, byte[] decoded)
    {
        if (declaredMediaType == "image/jpg")
            declaredMediaType = "image/jpeg";

        // The byte signature is authoritative. Several real contributor-card
        // generators label PNG avatars as image/gif; browsers sniff these and
        // render them, while trusting the declaration made our bounded dimension
        // parser reject otherwise valid content.
        string? sniffed = SniffEmbeddedImageMediaType(decoded);
        if (sniffed is not null)
            return sniffed;
        if (declaredMediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase) &&
            LooksLikeSvg(decoded))
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

    private static string? SniffEmbeddedImageMediaType(ReadOnlySpan<byte> data)
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

    private static bool LooksLikeSvg(ReadOnlySpan<byte> data)
    {
        int index = 0;
        if (data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf)
            index = 3;
        while (index < data.Length && data[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            index++;
        int inspectLength = Math.Min(512, data.Length - index);
        string prefix = Encoding.UTF8.GetString(data.Slice(index, inspectLength));
        return prefix.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
            (prefix.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
                prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }

    private static void InspectElementReference(
        string elementName,
        string attributeName,
        string value,
        ResvgMarkdownSvgRendererOptions options,
        HashSet<string> resources,
        ref long embeddedBytes,
        ref long embeddedPixels)
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
            throw Unsupported("Nested network and file references are forbidden.");

        InspectReference(value, options, resources, ref embeddedBytes, ref embeddedPixels);
    }

    private static void ChargeStructuralPayload(
        string value,
        ResvgMarkdownSvgRendererOptions options,
        ref int structuralCost,
        string message)
    {
        if (value.Length == 0)
            return;
        int utf8Bytes = Encoding.UTF8.GetByteCount(value);
        int payloadCost = utf8Bytes / 64;
        if (payloadCost == 0)
            return;
        ChargeStructural(
            payloadCost,
            options,
            ref structuralCost,
            message);
    }

    private static void ChargeStructural(
        int amount,
        ResvgMarkdownSvgRendererOptions options,
        ref int structuralCost,
        string message)
    {
        long next = (long)structuralCost + amount;
        if (next > options.MaxStructuralCost)
            throw Resource(message);
        structuralCost = checked((int)next);
    }

    private static int ComputePathComplexity(string pathData)
    {
        long commands = 0;
        long numbers = 0;
        ReadOnlySpan<char> path = pathData.AsSpan();
        int cursor = 0;
        while (cursor < path.Length)
        {
            char current = path[cursor];
            if ("MmZzLlHhVvCcSsQqTtAa".Contains(current))
            {
                commands++;
                cursor++;
                continue;
            }

            int start = cursor;
            if (current is '+' or '-')
                cursor++;
            bool hasDigits = false;
            while (cursor < path.Length && char.IsAsciiDigit(path[cursor]))
            {
                hasDigits = true;
                cursor++;
            }
            if (cursor < path.Length && path[cursor] == '.')
            {
                cursor++;
                while (cursor < path.Length && char.IsAsciiDigit(path[cursor]))
                {
                    hasDigits = true;
                    cursor++;
                }
            }
            if (!hasDigits)
            {
                cursor = start + 1;
                continue;
            }
            if (cursor < path.Length && path[cursor] is 'e' or 'E')
            {
                int exponent = cursor++;
                if (cursor < path.Length && path[cursor] is '+' or '-')
                    cursor++;
                int exponentDigits = cursor;
                while (cursor < path.Length && char.IsAsciiDigit(path[cursor]))
                    cursor++;
                if (cursor == exponentDigits)
                    cursor = exponent;
            }
            numbers++;
        }

        long payload = (Encoding.UTF8.GetByteCount(pathData) + 63L) / 64L;
        // Commands and coordinates are much cheaper than independent DOM
        // elements in resvg. Keep a weighted charge so adversarial paths remain
        // bounded, but do not price a legitimate chart or outlined-font path as
        // hundreds of thousands of elements. The 8 MiB source ceiling and the
        // payload charge still reject a maximally dense path before admission.
        long cost = ((commands + 7) / 8) + ((numbers + 15) / 16) + payload;
        return cost > int.MaxValue ? int.MaxValue : (int)cost;
    }

    private static bool MayContainCssResourceOrActiveContent(string attributeName, string value)
    {
        if (attributeName.Equals("style", StringComparison.OrdinalIgnoreCase))
            return true;

        string normalized = DecodeCssEscapes(value);
        if (normalized.Contains("url", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@font-face", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@keyframes", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("animation", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("transition", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string RemoveInertNamespaceDeclarations(string css)
    {
        char[]? sanitized = null;
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (TrySkipCssComment(css, ref cursor) || TrySkipCssString(css, ref cursor))
                continue;
            if (!css.AsSpan(cursor).StartsWith("@namespace", StringComparison.OrdinalIgnoreCase))
            {
                cursor++;
                continue;
            }

            int start = cursor;
            cursor += "@namespace".Length;
            int parentheses = 0;
            char quote = '\0';
            bool terminated = false;
            while (cursor < css.Length)
            {
                char current = css[cursor];
                if (quote != '\0')
                {
                    if (current == '\\' && cursor + 1 < css.Length)
                        cursor += 2;
                    else
                    {
                        if (current == quote)
                            quote = '\0';
                        cursor++;
                    }
                    continue;
                }
                if (current is '\'' or '"')
                {
                    quote = current;
                    cursor++;
                    continue;
                }
                if (current == '(')
                    parentheses++;
                else if (current == ')' && parentheses > 0)
                    parentheses--;
                else if (current == '{' && parentheses == 0)
                    throw Unsupported("A CSS namespace declaration is malformed.");
                else if (current == ';' && parentheses == 0)
                {
                    cursor++;
                    terminated = true;
                    break;
                }
                cursor++;
            }
            if (!terminated)
                throw Unsupported("A CSS namespace declaration is not terminated.");
            sanitized ??= css.ToCharArray();
            Array.Fill(sanitized, ' ', start, cursor - start);
        }
        return sanitized is null ? css : new string(sanitized);
    }

    private static byte[] PercentDecode(string value)
    {
        using var output = new MemoryStream(value.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current == '%')
            {
                if (index + 2 >= value.Length ||
                    !byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte decoded))
                {
                    throw Unsupported("An embedded image contains malformed percent encoding.");
                }
                output.WriteByte(decoded);
                index += 2;
                continue;
            }
            if (current <= 0x7f)
            {
                output.WriteByte((byte)current);
                continue;
            }
            int count = Encoding.UTF8.GetBytes(value.AsSpan(index, 1), utf8);
            output.Write(utf8[..count]);
        }
        return output.ToArray();
    }

    private static void InspectCss(
        string css,
        ResvgMarkdownSvgRendererOptions options,
        HashSet<string> resources,
        ref long embeddedBytes,
        ref long embeddedPixels)
    {
        string normalized = DecodeCssEscapes(css);
        if (normalized.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@font-face", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@keyframes", StringComparison.OrdinalIgnoreCase) ||
            ContainsMotionDeclaration(normalized))
        {
            throw Unsupported("External fonts, imports, and animation are forbidden.");
        }

        normalized = RemoveInertNamespaceDeclarations(normalized);
        int cursor = 0;
        while (cursor < normalized.Length)
        {
            if (TrySkipCssComment(normalized, ref cursor) || TrySkipCssString(normalized, ref cursor))
                continue;
            if (!normalized.AsSpan(cursor).StartsWith("url", StringComparison.OrdinalIgnoreCase))
            {
                cursor++;
                continue;
            }

            int afterName = cursor + 3;
            SkipCssWhitespaceAndComments(normalized, ref afterName);
            if (afterName >= normalized.Length || normalized[afterName] != '(')
            {
                cursor++;
                continue;
            }

            cursor = afterName + 1;
            SkipCssWhitespaceAndComments(normalized, ref cursor);
            string target;
            if (cursor < normalized.Length && normalized[cursor] is '\'' or '"')
            {
                char quote = normalized[cursor++];
                int start = cursor;
                while (cursor < normalized.Length && normalized[cursor] != quote)
                    cursor++;
                if (cursor >= normalized.Length)
                    throw Unsupported("A quoted CSS URL is not terminated.");
                target = normalized[start..cursor++];
                SkipCssWhitespaceAndComments(normalized, ref cursor);
                if (cursor >= normalized.Length || normalized[cursor] != ')')
                    throw Unsupported("A quoted CSS URL has invalid trailing content.");
            }
            else
            {
                int start = cursor;
                while (cursor < normalized.Length && normalized[cursor] != ')' &&
                       !char.IsWhiteSpace(normalized[cursor]))
                {
                    cursor++;
                }
                target = normalized[start..cursor];
                SkipCssWhitespaceAndComments(normalized, ref cursor);
                if (cursor >= normalized.Length || normalized[cursor] != ')')
                    throw Unsupported("An unquoted CSS URL is malformed.");
            }
            cursor++;
            target = target.Trim();
            if (target.StartsWith('#'))
                continue;
            InspectReference(target, options, resources, ref embeddedBytes, ref embeddedPixels);
        }
    }

    private static bool ContainsMotionDeclaration(string css)
    {
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (char.IsWhiteSpace(css[cursor]))
            {
                cursor++;
                continue;
            }
            if (TrySkipCssComment(css, ref cursor) || TrySkipCssString(css, ref cursor))
                continue;

            int nameStart = cursor;
            while (cursor < css.Length &&
                (char.IsAsciiLetterOrDigit(css[cursor]) || css[cursor] is '-' or '_'))
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
            if (separator >= css.Length || css[separator] != ':' || !IsMotionProperty(property))
                continue;

            int end = separator + 1;
            char quote = '\0';
            int parentheses = 0;
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
                    break;
                else if (parentheses == 0 && current is (';' or '}'))
                    return true;
                end++;
            }

            if (end >= css.Length)
                return true;
            cursor = end + 1;
        }

        return false;
    }

    private static bool IsMotionProperty(string property)
        => property.Equals("animation", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("animation-", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("transition", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("transition-", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("-webkit-animation", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("-webkit-animation-", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("-webkit-transition", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("-webkit-transition-", StringComparison.OrdinalIgnoreCase);

    private static string DecodeCssEscapes(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var output = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\')
            {
                output.Append(current);
                continue;
            }
            if (++index >= value.Length)
                throw Unsupported("A CSS escape is incomplete.");
            if (value[index] is '\r' or '\n' or '\f')
            {
                if (value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
                    index++;
                continue;
            }

            int scalar = 0;
            int digits = 0;
            while (index < value.Length && digits < 6 && Uri.IsHexDigit(value[index]))
            {
                scalar = checked((scalar * 16) + HexValue(value[index]));
                digits++;
                index++;
            }
            if (digits != 0)
            {
                if (index < value.Length && char.IsWhiteSpace(value[index]))
                {
                    if (value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
                        index++;
                }
                else
                {
                    index--;
                }
                if (!Rune.IsValid(scalar) || scalar == 0)
                    throw Unsupported("A CSS escape contains an invalid scalar value.");
                output.Append(char.ConvertFromUtf32(scalar));
            }
            else
            {
                output.Append(value[index]);
            }
        }
        return output.ToString();
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static void SkipCssWhitespaceAndComments(string css, ref int cursor)
    {
        while (cursor < css.Length)
        {
            if (char.IsWhiteSpace(css[cursor]))
            {
                cursor++;
                continue;
            }
            if (TrySkipCssComment(css, ref cursor))
                continue;
            break;
        }
    }

    private static bool TrySkipCssComment(string css, ref int cursor)
    {
        if (cursor + 1 >= css.Length || css[cursor] != '/' || css[cursor + 1] != '*')
            return false;
        int end = css.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
        if (end < 0)
            throw Unsupported("A CSS comment is not terminated.");
        cursor = end + 2;
        return true;
    }

    private static bool TrySkipCssString(string css, ref int cursor)
    {
        if (css[cursor] is not ('\'' or '"'))
            return false;
        char quote = css[cursor++];
        while (cursor < css.Length && css[cursor] != quote)
            cursor++;
        if (cursor >= css.Length)
            throw Unsupported("A CSS string is not terminated.");
        cursor++;
        return true;
    }

    private static double? ParseCssLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        double scale = 1;
        string number = value;
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            number = value[..^2];
        else if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^2];
            scale = 96d / 72d;
        }
        else if (value.EndsWith("in", StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^2];
            scale = 96;
        }
        else if (value.EndsWith("cm", StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^2];
            scale = 96d / 2.54d;
        }
        else if (value.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^2];
            scale = 96d / 25.4d;
        }
        else if (value.EndsWith('%') || value.EndsWith("em", StringComparison.OrdinalIgnoreCase) ||
                 value.EndsWith("ex", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
               double.IsFinite(parsed) && parsed > 0
            ? parsed * scale
            : null;
    }

    private static bool TryReadImageDimensions(byte[] bytes, string mediaType, out int width, out int height)
    {
        width = height = 0;
        ReadOnlySpan<byte> data = bytes;
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

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> value) =>
        (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];

    private static MarkdownSvgException Unsupported(string message, Exception? inner = null) =>
        new(MarkdownSvgFailureReason.UnsupportedContent, message, inner);

    private static MarkdownSvgException Resource(string message) =>
        new(MarkdownSvgFailureReason.ResourceLimitExceeded, message);
}
