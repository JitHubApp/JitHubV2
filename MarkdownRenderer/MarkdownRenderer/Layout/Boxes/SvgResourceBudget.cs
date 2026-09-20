using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace MarkdownRenderer.Layout.Boxes;

internal readonly record struct SvgResourceBudgetResult(bool Accepted, string? Reason)
{
    public static SvgResourceBudgetResult Success => new(true, null);

    public static SvgResourceBudgetResult Reject(string reason) => new(false, reason);
}

/// <summary>
/// Performs a bounded, streaming security preflight before SVG bytes are sent to
/// an optional renderer. Embedded images are charged by unique decoded cost
/// instead of element count, so avatar mosaics remain valid while image bombs
/// fail closed.
/// </summary>
internal static class SvgResourceBudget
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

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
    public const long MaxDecodedEmbeddedImageBytes = 64L * 1024 * 1024;
    public const long MaxEmbeddedCompressedBytes = 32L * 1024 * 1024;
    public const long MaxComplexityUnits = 100_000;
    public const double MaxDeclaredFontSize = 4096;
    public static readonly TimeSpan ValidationDeadline = TimeSpan.FromMilliseconds(750);

    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    public static SvgResourceBudgetResult Validate(byte[]? bytes, CancellationToken cancellationToken)
    {
        if (bytes is null || bytes.Length == 0)
            return SvgResourceBudgetResult.Reject("empty");
        if (bytes.Length > MaxInputBytes)
            return SvgResourceBudgetResult.Reject("input-bytes");

        var context = new ValidationContext(Stopwatch.GetTimestamp(), cancellationToken);
        return ValidateDocument(bytes, context, nestedDepth: 0);
    }

    private static SvgResourceBudgetResult ValidateDocument(
        byte[] bytes,
        ValidationContext context,
        int nestedDepth)
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
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = false,
                // A large amount of otherwise-static legacy artwork carries the
                // standard SVG 1.1 external DOCTYPE declaration. Parse the
                // declaration without a resolver so it is inert, then reject any
                // internal subset below. This preserves browser compatibility
                // without permitting external retrieval or entity expansion.
                DtdProcessing = DtdProcessing.Parse,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersFromEntities = 1,
                MaxCharactersInDocument = MaxInputBytes,
            });

            while (reader.Read())
            {
                context.ThrowIfStopped();
                if (reader.Depth > MaxDepth)
                    return SvgResourceBudgetResult.Reject("element-depth");

                if (reader.NodeType == XmlNodeType.DocumentType)
                {
                    if (!string.IsNullOrWhiteSpace(reader.Value))
                        return SvgResourceBudgetResult.Reject("dtd-internal-subset");
                    continue;
                }

                if (reader.NodeType == XmlNodeType.Element)
                {
                    string elementName = reader.LocalName;
                    bool isSvgElement = IsSvgNamespace(reader.NamespaceURI);
                    if (!sawSvgRoot)
                    {
                        sawSvgRoot = elementName.Equals("svg", StringComparison.OrdinalIgnoreCase) &&
                            isSvgElement;
                        if (!sawSvgRoot)
                            return SvgResourceBudgetResult.Reject("missing-root");
                    }

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
                            context.ThrowIfStopped();
                            if (++context.AttributeCount > MaxAttributes)
                                return SvgResourceBudgetResult.Reject("attribute-count");

                            string name = reader.LocalName;
                            string value = reader.Value;
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
                                SvgResourceBudgetResult styleResult = ValidateCss(value);
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
                            else if (ContainsCssUrl(value))
                            {
                                SvgResourceBudgetResult urlResult = ValidateCssUrls(value);
                                if (!urlResult.Accepted)
                                    return urlResult;
                            }
                        }

                        reader.MoveToElement();
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (styleElementDepth == reader.Depth &&
                        reader.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                    {
                        styleElementDepth = -1;
                    }
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                {
                    if (++context.TextNodeCount > MaxTextNodes)
                        return SvgResourceBudgetResult.Reject("text-node-count");
                    context.TextCharacters += reader.Value.Length;
                    if (context.TextCharacters > MaxTextCharacters)
                        return SvgResourceBudgetResult.Reject("text-length");

                    if (styleElementDepth >= 0)
                    {
                        SvgResourceBudgetResult styleResult = ValidateCss(reader.Value);
                        if (!styleResult.Accepted)
                            return styleResult;
                    }
                }
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

    private static SvgResourceBudgetResult ValidateHref(
        string elementName,
        string value,
        ValidationContext context,
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
        if (!TryDecodeDataUri(value, out string mediaType, out byte[] payload))
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
            return ValidateDocument(payload, context, nestedDepth + 1);
        }

        RasterImageBudgetResult raster = RasterImageResourceBudget.Validate(payload);
        if (!raster.Accepted || raster.FrameCount != 1 ||
            !IsSupportedEmbeddedRasterType(mediaType, raster.Format))
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

    private static SvgResourceBudgetResult ValidateCss(string css)
    {
        if (css.IndexOf("@import", StringComparison.OrdinalIgnoreCase) >= 0 ||
            css.IndexOf("@font-face", StringComparison.OrdinalIgnoreCase) >= 0 ||
            css.IndexOf("@keyframes", StringComparison.OrdinalIgnoreCase) >= 0 ||
            css.IndexOf("animation", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return SvgResourceBudgetResult.Reject("active-content");
        }

        return TryRemoveInertNamespaceDeclarations(css, out string inspectable)
            ? ValidateCssUrls(inspectable)
            : SvgResourceBudgetResult.Reject("invalid-css-namespace");
    }

    private static bool TryRemoveInertNamespaceDeclarations(string css, out string inspectable)
    {
        char[]? sanitized = null;
        int cursor = 0;
        while (cursor < css.Length)
        {
            if (cursor + 1 < css.Length && css[cursor] == '/' && css[cursor + 1] == '*')
            {
                int commentEnd = css.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    inspectable = string.Empty;
                    return false;
                }
                cursor = commentEnd + 2;
                continue;
            }
            if (css[cursor] is '\'' or '"')
            {
                char quote = css[cursor++];
                while (cursor < css.Length && css[cursor] != quote)
                    cursor += css[cursor] == '\\' && cursor + 1 < css.Length ? 2 : 1;
                if (cursor >= css.Length)
                {
                    inspectable = string.Empty;
                    return false;
                }
                cursor++;
                continue;
            }
            if (!css.AsSpan(cursor).StartsWith("@namespace", StringComparison.OrdinalIgnoreCase))
            {
                cursor++;
                continue;
            }

            int start = cursor;
            cursor += "@namespace".Length;
            int parentheses = 0;
            char statementQuote = '\0';
            bool terminated = false;
            while (cursor < css.Length)
            {
                char current = css[cursor];
                if (statementQuote != '\0')
                {
                    if (current == '\\' && cursor + 1 < css.Length)
                        cursor += 2;
                    else
                    {
                        if (current == statementQuote)
                            statementQuote = '\0';
                        cursor++;
                    }
                    continue;
                }
                if (current is '\'' or '"')
                {
                    statementQuote = current;
                    cursor++;
                    continue;
                }
                if (current == '(')
                    parentheses++;
                else if (current == ')' && parentheses > 0)
                    parentheses--;
                else if (current == '{' && parentheses == 0)
                    break;
                else if (current == ';' && parentheses == 0)
                {
                    cursor++;
                    terminated = true;
                    break;
                }
                cursor++;
            }
            if (!terminated)
            {
                inspectable = string.Empty;
                return false;
            }
            sanitized ??= css.ToCharArray();
            Array.Fill(sanitized, ' ', start, cursor - start);
        }

        inspectable = sanitized is null ? css : new string(sanitized);
        return true;
    }

    private static SvgResourceBudgetResult ValidateCssUrls(string value)
    {
        int searchFrom = 0;
        while (true)
        {
            int urlIndex = value.IndexOf("url(", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (urlIndex < 0)
                return SvgResourceBudgetResult.Success;

            int argumentStart = urlIndex + 4;
            int close = value.IndexOf(')', argumentStart);
            if (close < 0)
                return SvgResourceBudgetResult.Reject("invalid-css-url");

            ReadOnlySpan<char> argument = value.AsSpan(argumentStart, close - argumentStart).Trim();
            if (argument.Length >= 2 &&
                ((argument[0] == '\'' && argument[^1] == '\'') ||
                 (argument[0] == '"' && argument[^1] == '"')))
            {
                argument = argument[1..^1].Trim();
            }

            if (argument.IsEmpty || argument[0] != '#')
                return SvgResourceBudgetResult.Reject("external-resource");

            searchFrom = close + 1;
        }
    }

    private static bool ContainsCssUrl(string value) =>
        value.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool TryDecodeDataUri(string value, out string mediaType, out byte[] payload)
    {
        mediaType = string.Empty;
        payload = Array.Empty<byte>();
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        int comma = value.IndexOf(',');
        if (comma <= 5)
            return false;

        string metadata = value.Substring(5, comma - 5);
        int semicolon = metadata.IndexOf(';');
        mediaType = (semicolon >= 0 ? metadata[..semicolon] : metadata).Trim();
        bool base64 = metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ||
            metadata.IndexOf(";base64;", StringComparison.OrdinalIgnoreCase) >= 0;
        string encoded = value[(comma + 1)..];
        try
        {
            payload = base64 ? Convert.FromBase64String(encoded) : PercentDecode(encoded);
            if (payload.Length == 0 || payload.Length > MaxInputBytes)
                return false;

            if (IsSupportedEmbeddedMediaType(mediaType))
                return true;

            // Match browser data-URI tolerance without weakening the resource
            // boundary: only a positively identified supported raster payload
            // can replace a missing or invalid MIME label. In particular, this
            // admits OpenCollective's `data:false;base64,...` PNG avatars while
            // arbitrary bytes and undeclared nested SVG remain rejected.
            mediaType = DetectEmbeddedRasterMediaType(payload) ?? string.Empty;
            return mediaType.Length != 0;
        }
        catch (Exception ex) when (ex is FormatException or EncoderFallbackException)
        {
            payload = Array.Empty<byte>();
            return false;
        }
    }

    private static byte[] PercentDecode(string value)
    {
        using MemoryStream output = new(capacity: Math.Min(value.Length, MaxInputBytes));
        Span<char> chars = stackalloc char[2];
        Span<byte> utf8 = stackalloc byte[4];
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current == '%' && index + 2 < value.Length &&
                TryHex(value[index + 1], out int high) && TryHex(value[index + 2], out int low))
            {
                output.WriteByte((byte)((high << 4) | low));
                index += 2;
            }
            else if (current <= 0x7F)
            {
                output.WriteByte((byte)current);
            }
            else
            {
                chars[0] = current;
                int charCount = 1;
                if (char.IsHighSurrogate(current) && index + 1 < value.Length &&
                    char.IsLowSurrogate(value[index + 1]))
                {
                    chars[1] = value[++index];
                    charCount = 2;
                }

                int count = Encoding.UTF8.GetBytes(chars[..charCount], utf8);
                output.Write(utf8[..count]);
            }

            if (output.Length > MaxInputBytes)
                throw new FormatException("Decoded data URI exceeds the SVG resource budget.");
        }

        return output.ToArray();
    }

    private static bool TryHex(char value, out int digit)
    {
        if (value is >= '0' and <= '9')
        {
            digit = value - '0';
            return true;
        }
        if (value is >= 'a' and <= 'f')
        {
            digit = value - 'a' + 10;
            return true;
        }
        if (value is >= 'A' and <= 'F')
        {
            digit = value - 'A' + 10;
            return true;
        }

        digit = 0;
        return false;
    }

    private static bool IsSupportedEmbeddedMediaType(string value) =>
        value.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image/jpg", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image/gif", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image/webp", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);

    private static string? DetectEmbeddedRasterMediaType(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 8 &&
            payload[..8].SequenceEqual(PngSignature))
        {
            return "image/png";
        }
        if (payload.Length >= 3 && payload[0] == 0xff && payload[1] == 0xd8 && payload[2] == 0xff)
            return "image/jpeg";
        if (payload.Length >= 6 &&
            (payload[..6].SequenceEqual("GIF87a"u8) || payload[..6].SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }
        if (payload.Length >= 12 &&
            payload[..4].SequenceEqual("RIFF"u8) && payload[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return null;
    }

    private static bool IsSupportedEmbeddedRasterType(string mediaType, string? detectedFormat) =>
        (mediaType.Equals("image/png", StringComparison.OrdinalIgnoreCase) && detectedFormat == "PNG") ||
        ((mediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
          mediaType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)) && detectedFormat == "JPEG") ||
        (mediaType.Equals("image/gif", StringComparison.OrdinalIgnoreCase) && detectedFormat == "GIF") ||
        (mediaType.Equals("image/webp", StringComparison.OrdinalIgnoreCase) && detectedFormat == "WEBP");

    private static bool IsSvgNamespace(string? namespaceUri) =>
        string.IsNullOrEmpty(namespaceUri) ||
        string.Equals(namespaceUri, SvgNamespace, StringComparison.Ordinal);

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

    private sealed class ValidationContext
    {
        private readonly long _started;
        private readonly CancellationToken _cancellationToken;

        public ValidationContext(long started, CancellationToken cancellationToken)
        {
            _started = started;
            _cancellationToken = cancellationToken;
        }

        public int ElementCount { get; set; }
        public int AttributeCount { get; set; }
        public int FilterPrimitiveCount { get; set; }
        public int TextNodeCount { get; set; }
        public long TextCharacters { get; set; }
        public long PathCharacters { get; set; }
        public long TransformCharacters { get; set; }
        public long EmbeddedCompressedBytes { get; set; }
        public long DecodedEmbeddedImageBytes { get; set; }
        public long ComplexityUnits { get; set; }
        public HashSet<string> SeenEmbeddedResources { get; } = new(StringComparer.Ordinal);

        public void ThrowIfStopped()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(_started) > ValidationDeadline)
                throw new SvgValidationDeadlineException();
        }
    }

    private sealed class SvgValidationDeadlineException : Exception
    {
    }
}
