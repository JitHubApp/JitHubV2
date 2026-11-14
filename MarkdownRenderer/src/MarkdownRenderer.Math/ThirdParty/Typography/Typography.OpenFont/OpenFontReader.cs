//Apache2, 2017-present, WinterDev
//Apache2, 2014-2016, Samuel Carlsson, WinterDev

using MarkdownRenderer.Math.Internal;
using Typography.OpenFont.IO;
using Typography.OpenFont.Tables;

namespace Typography.OpenFont;

/// <summary>
/// Renderer-specific minimal reader for the three audited CFF OpenType fonts.
/// It intentionally rejects collections, WOFF/WOFF2, TrueType outlines,
/// bitmap/SVG/color fonts, variable fonts, and arbitrary font sources.
/// </summary>
internal sealed class OpenFontReader
{
    private const uint OpenTypeWithCffSignature = 0x4f54544f; // OTTO
    private const int MaximumTableCount = 32;
    private const long MaximumFontBytes = 1024 * 1024;
    private const int MaximumGlyphCount = 8192;

    public Typeface Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new NotSupportedException("The audited math font stream must be readable and seekable.");
        if (stream.Length is <= 0 or > MaximumFontBytes)
            throw new NotSupportedException("The audited math font exceeds its fixed input budget.");

        using var input = new ByteOrderSwappingBinaryReader(stream);
        if (input.ReadUInt32() != OpenTypeWithCffSignature)
            throw new NotSupportedException("Only CFF OpenType math fonts are supported.");

        ushort tableCount = input.ReadUInt16();
        if (tableCount is 0 or > MaximumTableCount)
            throw new NotSupportedException("The audited math font has an invalid table count.");

        _ = input.ReadUInt16(); // searchRange
        _ = input.ReadUInt16(); // entrySelector
        _ = input.ReadUInt16(); // rangeShift

        var tables = new Dictionary<string, TableHeader>(tableCount, StringComparer.Ordinal);
        for (int i = 0; i < tableCount; i++)
        {
            MathCancellationScope.Checkpoint();
            var header = new TableHeader(
                input.ReadUInt32(),
                input.ReadUInt32(),
                input.ReadUInt32(),
                input.ReadUInt32());
            ulong end = (ulong)header.Offset + header.Length;
            if (header.Length == 0 || end > (ulong)stream.Length)
                throw new NotSupportedException($"The audited math font table '{header.Tag}' is out of bounds.");
            if (!tables.TryAdd(header.Tag, header))
                throw new NotSupportedException($"The audited math font repeats table '{header.Tag}'.");
        }

        Head head = ReadRequired(tables, input, Head.Name, static (h, r) => new Head(h, r));
        MaxProfile maxProfile = ReadRequired(tables, input, MaxProfile.Name, static (h, r) => new MaxProfile(h, r));
        if (maxProfile.GlyphCount is 0 or > MaximumGlyphCount)
            throw new NotSupportedException("The audited math font exceeds its fixed glyph budget.");
        HorizontalHeader horizontalHeader = ReadRequired(
            tables,
            input,
            HorizontalHeader.Name,
            static (h, r) => new HorizontalHeader(h, r));
        HorizontalMetrics horizontalMetrics = ReadRequired(
            tables,
            input,
            HorizontalMetrics.Name,
            (h, r) => new HorizontalMetrics(
                horizontalHeader.HorizontalMetricsCount,
                maxProfile.GlyphCount,
                h,
                r));
        Cmap cmap = ReadRequired(tables, input, Cmap.Name, static (h, r) => new Cmap(h, r));
        CFFTable cff = ReadRequired(tables, input, CFFTable.Name, static (h, r) => new CFFTable(h, r));
        if (cff.Cff1FontSet is not { _fonts.Count: > 0 } fontSet)
            throw new NotSupportedException("The audited math font does not contain supported CFF1 outlines.");
        if (fontSet._fonts.Count != 1 || fontSet._fonts[0]._glyphs.Length != maxProfile.GlyphCount)
            throw new NotSupportedException("The audited math font glyph counts are inconsistent.");

        var typeface = new Typeface(
            head,
            maxProfile,
            horizontalHeader,
            horizontalMetrics,
            cmap,
            fontSet._fonts[0]._glyphs,
            cff);

        MathTable? mathTable = ReadOptional(
            tables,
            input,
            MathTable.Name,
            static (h, r) => new MathTable(h, r));
        if (mathTable is not null)
            new MathGlyphLoader().LoadMathGlyph(typeface, mathTable);

        return typeface;
    }

    private static T ReadRequired<T>(
        IReadOnlyDictionary<string, TableHeader> tables,
        BinaryReader reader,
        string name,
        TableReader<T> tableReader)
        where T : TableEntry =>
        ReadOptional(tables, reader, name, tableReader)
        ?? throw new NotSupportedException($"The audited math font is missing required table '{name}'.");

    private static T? ReadOptional<T>(
        IReadOnlyDictionary<string, TableHeader> tables,
        BinaryReader reader,
        string name,
        TableReader<T> tableReader)
        where T : TableEntry
    {
        MathCancellationScope.Checkpoint();
        return tables.TryGetValue(name, out TableHeader? header)
            ? tableReader(header, reader)
            : null;
    }

    internal delegate T TableReader<T>(TableHeader header, BinaryReader reader)
        where T : TableEntry;
}
