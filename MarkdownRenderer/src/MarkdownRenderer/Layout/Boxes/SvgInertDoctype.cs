using System;
using System.IO;
using System.Threading;
using System.Xml;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Common SVG exporters write the W3C SVG public DOCTYPE
/// (<c>&lt;!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "…/svg11.dtd"&gt;</c>).
/// Browsers render those files without fetching the DTD. Host source
/// preparation neutralizes exactly that declaration before admission, so
/// every later stage (budget, static snapshot, provider preflight and the
/// worker) still rejects every DTD it sees.
/// </summary>
/// <remarks>
/// A declaration is inert only when its root name is <c>svg</c>, its public
/// and system identifiers are one of the published W3C SVG pairs, and it has
/// no internal subset. Only UTF-8 (or ASCII) sources are rewritten. Anything
/// else is returned unchanged and is rejected by the existing DTD policy:
/// internal subsets, entity declarations, unknown or local identifiers,
/// SYSTEM-only declarations, other encodings and nested SVG documents.
/// The DTD is never resolved; the reader's resolver is disabled.
/// </remarks>
internal static class SvgInertDoctype
{
    private static readonly (string PublicId, string SystemId)[] W3cSvgIdentifiers =
    [
        ("-//W3C//DTD SVG 1.0//EN", "http://www.w3.org/TR/2001/REC-SVG-20010904/DTD/svg10.dtd"),
        ("-//W3C//DTD SVG 20010904//EN", "http://www.w3.org/TR/2001/REC-SVG-20010904/DTD/svg10.dtd"),
        ("-//W3C//DTD SVG 1.1//EN", "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd"),
        ("-//W3C//DTD SVG 1.1 Basic//EN", "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11-basic.dtd"),
        ("-//W3C//DTD SVG 1.1 Tiny//EN", "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11-tiny.dtd"),
    ];

    /// <summary>
    /// Returns <paramref name="source"/> itself when it has no inert W3C SVG
    /// DOCTYPE, or a copy in which exactly that declaration is replaced by
    /// XML whitespace of the same length. The caller's bytes never change.
    /// </summary>
    internal static byte[] Neutralize(byte[] source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length == 0 || source.Length > SvgResourceBudget.MaxInputBytes)
            return source;
        if (!TryLocatePrologDoctype(source, out int start, out int end))
            return source;
        if (!IsInertW3cSvgDeclaration(source, cancellationToken))
            return source;

        byte[] neutralized = (byte[])source.Clone();
        neutralized.AsSpan(start, end - start).Fill((byte)' ');
        return neutralized;
    }

    internal static bool IsW3cSvgIdentifierPair(string? publicId, string? systemId)
    {
        foreach ((string PublicId, string SystemId) pair in W3cSvgIdentifiers)
        {
            if (string.Equals(publicId, pair.PublicId, StringComparison.Ordinal) &&
                string.Equals(systemId, pair.SystemId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Finds a DOCTYPE in a UTF-8 or ASCII prolog, skipping only whitespace,
    /// comments and processing instructions. Its end is the first
    /// unquoted <c>&gt;</c>; an unquoted <c>[</c> opens an internal subset,
    /// which is never neutralized.
    /// </summary>
    private static bool TryLocatePrologDoctype(ReadOnlySpan<byte> source, out int start, out int end)
    {
        start = end = -1;
        int cursor = source.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        if (source.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || source.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return false;
        if (source.Length > 1 && (source[0] == 0 || source[1] == 0))
            return false;

        ReadOnlySpan<byte> marker = "<!DOCTYPE"u8;
        while (cursor < source.Length)
        {
            byte current = source[cursor];
            if (current is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                cursor++;
                continue;
            }

            ReadOnlySpan<byte> rest = source[cursor..];
            if (rest.StartsWith("<!--"u8))
            {
                int close = rest[4..].IndexOf("-->"u8);
                if (close < 0)
                    return false;
                cursor += 4 + close + 3;
                continue;
            }

            if (rest.StartsWith("<?"u8))
            {
                int close = rest[2..].IndexOf("?>"u8);
                if (close < 0)
                    return false;
                if (cursor == 0 || (cursor == 3 && source[0] == 0xEF))
                {
                    if (!IsUtf8CompatibleDeclaration(rest[..(2 + close + 2)]))
                        return false;
                }
                cursor += 2 + close + 2;
                continue;
            }

            if (!rest.StartsWith(marker))
                return false;

            byte quote = 0;
            for (int index = marker.Length; index < rest.Length; index++)
            {
                byte value = rest[index];
                if (quote != 0)
                {
                    if (value == quote)
                        quote = 0;
                    continue;
                }
                if (value is (byte)'\'' or (byte)'"')
                {
                    quote = value;
                    continue;
                }
                if (value == (byte)'[')
                    return false;
                if (value == (byte)'>')
                {
                    start = cursor;
                    end = cursor + index + 1;
                    return true;
                }
            }
            return false;
        }
        return false;
    }

    private static bool IsUtf8CompatibleDeclaration(ReadOnlySpan<byte> declaration)
    {
        if (!declaration.StartsWith("<?xml"u8) || declaration.Length < 6 ||
            declaration[5] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'?'))
        {
            return true;
        }

        int encoding = declaration.IndexOf("encoding"u8);
        if (encoding < 0)
            return true;
        int cursor = encoding + "encoding".Length;
        while (cursor < declaration.Length && declaration[cursor] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'=')
            cursor++;
        if (cursor >= declaration.Length || declaration[cursor] is not ((byte)'"' or (byte)'\''))
            return false;
        byte quote = declaration[cursor++];
        int close = declaration[cursor..].IndexOf(quote);
        if (close < 0)
            return false;
        string name = System.Text.Encoding.ASCII.GetString(declaration.Slice(cursor, close));
        return name.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("utf8", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("us-ascii", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Confirms with a real XML parser, resolver disabled, that the first
    /// node of declaration type is the inert W3C SVG form.
    /// </summary>
    private static bool IsInertW3cSvgDeclaration(byte[] source, CancellationToken cancellationToken)
    {
        try
        {
            using MemoryStream stream = new(source, writable: false);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = false,
                DtdProcessing = DtdProcessing.Parse,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersFromEntities = 1,
                MaxCharactersInDocument = SvgResourceBudget.MaxInputBytes,
            });
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.DocumentType)
                {
                    return string.Equals(reader.Name, "svg", StringComparison.Ordinal) &&
                           string.IsNullOrEmpty(reader.Value) &&
                           IsW3cSvgIdentifierPair(reader.GetAttribute("PUBLIC"), reader.GetAttribute("SYSTEM"));
                }
                if (reader.NodeType == XmlNodeType.Element)
                    return false;
            }
            return false;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
