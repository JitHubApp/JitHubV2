using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Reads ICO directories and validates each embedded PNG or DIB payload
/// against the raster budget.
/// </summary>
internal static class RasterIconHeaderReader
{
    internal static RasterImageHeader? TryReadIcon(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 22 || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) != 1)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        int directoryLength;
        try
        {
            directoryLength = checked(6 + (count * 16));
        }
        catch (OverflowException)
        {
            return null;
        }

        if (count <= 0 || count > RasterImageResourceBudget.MaxFrameCount || bytes.Length < directoryLength)
        {
            return null;
        }

        int maxWidth = 0;
        int maxHeight = 0;
        long totalPixels = 0;
        List<IconPayloadRange> ranges = new(count);
        for (int index = 0; index < count; index++)
        {
            RasterImageHeader? nested = TryReadEntry(bytes, 6 + (index * 16), directoryLength, ranges);
            if (nested is null)
            {
                return null;
            }

            long nestedPixels;
            try
            {
                nestedPixels = checked((long)nested.Width * nested.Height);
                totalPixels = checked(totalPixels + nestedPixels);
            }
            catch (OverflowException)
            {
                return null;
            }

            if (nestedPixels > RasterImageResourceBudget.MaxPixelsPerFrame ||
                totalPixels > RasterImageResourceBudget.MaxDecodedBytes / RasterImageResourceBudget.BytesPerDecodedPixel)
            {
                return null;
            }

            maxWidth = Math.Max(maxWidth, nested.Width);
            maxHeight = Math.Max(maxHeight, nested.Height);
        }

        return new RasterImageHeader("ICO", maxWidth, maxHeight, count, totalPixels);
    }

    // Reads one 16-byte directory entry. Its payload must lie after the
    // directory, overlap no earlier payload, and hold one PNG or DIB image of
    // exactly the advertised size within the dimension budget.
    private static RasterImageHeader? TryReadEntry(
        ReadOnlySpan<byte> bytes,
        int entryOffset,
        int directoryLength,
        List<IconPayloadRange> ranges)
    {
        int advertisedWidth = bytes[entryOffset] == 0 ? 256 : bytes[entryOffset];
        int advertisedHeight = bytes[entryOffset + 1] == 0 ? 256 : bytes[entryOffset + 1];
        uint payloadLengthValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(entryOffset + 8)..]);
        uint payloadOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(entryOffset + 12)..]);
        if (payloadLengthValue == 0 || payloadLengthValue > int.MaxValue ||
            payloadOffsetValue > int.MaxValue)
        {
            return null;
        }

        int payloadLength = (int)payloadLengthValue;
        int payloadOffset = (int)payloadOffsetValue;
        if (payloadOffset < directoryLength || payloadOffset > bytes.Length - payloadLength)
        {
            return null;
        }

        var range = new IconPayloadRange(payloadOffset, payloadLength);
        foreach (IconPayloadRange existing in ranges)
        {
            if (range.Offset < existing.End && existing.Offset < range.End)
            {
                return null;
            }
        }

        ranges.Add(range);
        ReadOnlySpan<byte> payload = bytes.Slice(payloadOffset, payloadLength);
        RasterImageHeader? nested = TryReadCompleteIconPng(payload) ?? TryReadIconDib(payload);
        if (nested is null || nested.FrameCount != 1 ||
            nested.Width != advertisedWidth || nested.Height != advertisedHeight ||
            nested.Width <= 0 || nested.Height <= 0 ||
            nested.Width > RasterImageResourceBudget.MaxDimension || nested.Height > RasterImageResourceBudget.MaxDimension)
        {
            return null;
        }

        return nested;
    }

    private static RasterImageHeader? TryReadCompleteIconPng(ReadOnlySpan<byte> bytes)
    {
        RasterImageHeader? header = RasterImageHeaderReader.TryReadPng(bytes);
        if (header is null)
        {
            return null;
        }

        int offset = 8;
        bool firstChunk = true;
        while (offset <= bytes.Length - 12)
        {
            uint chunkLengthValue = RasterImageHeaderReader.ReadUInt32BigEndian(bytes, offset);
            if (chunkLengthValue > int.MaxValue)
            {
                return null;
            }

            int chunkLength = (int)chunkLengthValue;
            int chunkEnd;
            try
            {
                chunkEnd = checked(offset + 12 + chunkLength);
            }
            catch (OverflowException)
            {
                return null;
            }

            if (chunkEnd > bytes.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4);
            if (firstChunk && (!type.SequenceEqual("IHDR"u8) || chunkLength != 13))
            {
                return null;
            }

            firstChunk = false;
            if (type.SequenceEqual("IEND"u8))
            {
                return chunkLength == 0 && chunkEnd == bytes.Length ? header : null;
            }

            offset = chunkEnd;
        }

        return null;
    }

    private static RasterImageHeader? TryReadIconDib(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
        {
            return null;
        }

        uint headerSizeValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (headerSizeValue > int.MaxValue)
        {
            return null;
        }

        int headerSize = (int)headerSizeValue;
        if (!TryReadDibInfo(
                bytes,
                headerSize,
                out int width,
                out int doubledHeight,
                out int bitsPerPixel,
                out int paletteEntryBytes,
                out uint colorsUsed))
        {
            return null;
        }

        if (width <= 0 || doubledHeight <= 0 || (doubledHeight & 1) != 0 ||
            bitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            return null;
        }

        int height = doubledHeight / 2;
        long paletteEntries = colorsUsed > 0
            ? colorsUsed
            : bitsPerPixel <= 8 ? 1L << bitsPerPixel : 0;
        long requiredBytes;
        try
        {
            long colorRowBytes = checked((((long)width * bitsPerPixel + 31) / 32) * 4);
            long maskRowBytes = checked((((long)width + 31) / 32) * 4);
            requiredBytes = checked(
                headerSize +
                (paletteEntries * paletteEntryBytes) +
                (colorRowBytes * height) +
                (maskRowBytes * height));
        }
        catch (OverflowException)
        {
            return null;
        }

        return requiredBytes <= bytes.Length
            ? new RasterImageHeader("ICO-DIB", width, height, 1)
            : null;
    }

    // Reads the size and palette fields of a BITMAPCOREHEADER (12 bytes) or
    // an uncompressed BITMAPINFOHEADER or later (40 bytes or more).
    private static bool TryReadDibInfo(
        ReadOnlySpan<byte> bytes,
        int headerSize,
        out int width,
        out int doubledHeight,
        out int bitsPerPixel,
        out int paletteEntryBytes,
        out uint colorsUsed)
    {
        colorsUsed = 0;
        if (headerSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
            doubledHeight = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
            paletteEntryBytes = 3;
            return true;
        }

        width = doubledHeight = bitsPerPixel = paletteEntryBytes = 0;
        if (headerSize < 40 || headerSize > bytes.Length)
        {
            return false;
        }

        int rawWidth = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        int rawDoubledHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        if (rawWidth == int.MinValue || rawDoubledHeight == int.MinValue)
        {
            return false;
        }

        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        if (compression != 0)
        {
            return false;
        }

        width = Math.Abs(rawWidth);
        doubledHeight = Math.Abs(rawDoubledHeight);
        bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]);
        paletteEntryBytes = 4;
        return true;
    }

    internal readonly struct IconPayloadRange(int Offset, int Length)
    {
        public readonly int Offset = Offset;
        public readonly int Length = Length;
        public int End => Offset + Length;
    }
}
