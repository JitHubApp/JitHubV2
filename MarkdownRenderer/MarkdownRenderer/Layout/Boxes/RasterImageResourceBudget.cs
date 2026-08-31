using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Boxes;

internal sealed record RasterImageBudgetResult(
    bool Accepted,
    string? Format,
    int Width,
    int Height,
    int FrameCount,
    long TotalPixels,
    long DecodedBytes,
    string? Reason)
{
    public static RasterImageBudgetResult Reject(string reason) =>
        new(false, null, 0, 0, 0, 0, 0, reason);
}

internal static class RasterImageResourceBudget
{
    internal const int MaxInputBytes = 10 * 1024 * 1024;
    internal const int MaxDimension = 8192;
    internal const long MaxPixelsPerFrame = 16_777_216;
    internal const int MaxFrameCount = 60;
    internal const long MaxDecodedBytes = 64L * 1024 * 1024;
    private const int BytesPerDecodedPixel = 4;

    public static RasterImageBudgetResult Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return RasterImageBudgetResult.Reject("The raster image is empty.");
        }

        if (bytes.Length > MaxInputBytes)
        {
            return RasterImageBudgetResult.Reject("The compressed raster image exceeds the input budget.");
        }

        HeaderInfo? header = TryReadPng(bytes) ??
            TryReadGif(bytes) ??
            TryReadJpeg(bytes) ??
            TryReadBmp(bytes) ??
            TryReadWebP(bytes) ??
            TryReadIcon(bytes);
        if (header is null)
        {
            return RasterImageBudgetResult.Reject("The raster image header is unsupported or malformed.");
        }

        if (header.Width <= 0 || header.Height <= 0 ||
            header.Width > MaxDimension || header.Height > MaxDimension)
        {
            return RasterImageBudgetResult.Reject("The raster image dimensions exceed the safe budget.");
        }

        if (header.FrameCount <= 0 || header.FrameCount > MaxFrameCount)
        {
            return RasterImageBudgetResult.Reject("The raster animation frame count exceeds the safe budget.");
        }

        long pixelsPerFrame;
        long totalPixels;
        long decodedBytes;
        try
        {
            pixelsPerFrame = checked((long)header.Width * header.Height);
            totalPixels = header.TotalPixels ?? checked(pixelsPerFrame * header.FrameCount);
            decodedBytes = checked(totalPixels * BytesPerDecodedPixel);
        }
        catch (OverflowException)
        {
            return RasterImageBudgetResult.Reject("The raster image decoded size overflows the safe budget.");
        }

        if (pixelsPerFrame > MaxPixelsPerFrame)
        {
            return RasterImageBudgetResult.Reject("The raster image pixel count exceeds the safe budget.");
        }

        if (totalPixels < pixelsPerFrame || decodedBytes > MaxDecodedBytes)
        {
            return RasterImageBudgetResult.Reject("The raster image decoded-memory budget is exceeded.");
        }

        return new RasterImageBudgetResult(
            true,
            header.Format,
            header.Width,
            header.Height,
            header.FrameCount,
            totalPixels,
            decodedBytes,
            null);
    }

    private static HeaderInfo? TryReadPng(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(signature) ||
            ReadUInt32BigEndian(bytes, 8) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        int width = ReadPositiveInt32BigEndian(bytes, 16);
        int height = ReadPositiveInt32BigEndian(bytes, 20);
        int frames = 1;
        int offset = 8;
        while (offset <= bytes.Length - 12)
        {
            uint chunkLengthValue = ReadUInt32BigEndian(bytes, offset);
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
                break;
            }

            ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8))
            {
                if (chunkLength != 8)
                {
                    return null;
                }

                uint frameCount = ReadUInt32BigEndian(bytes, offset + 8);
                frames = frameCount > int.MaxValue ? int.MaxValue : (int)frameCount;
            }

            offset = chunkEnd;
        }

        return new HeaderInfo("PNG", width, height, frames);
    }

    private static HeaderInfo? TryReadGif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 13 ||
            (!bytes[..6].SequenceEqual("GIF87a"u8) && !bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            return null;
        }

        int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        int offset = 13;
        if ((bytes[10] & 0x80) != 0)
        {
            int tableBytes = 3 * (1 << ((bytes[10] & 0x07) + 1));
            if (!TryAdvance(ref offset, tableBytes, bytes.Length))
            {
                return null;
            }
        }

        int frames = 0;
        while (offset < bytes.Length)
        {
            byte marker = bytes[offset++];
            if (marker == 0x3B)
            {
                break;
            }

            if (marker == 0x21)
            {
                if (!TryAdvance(ref offset, 1, bytes.Length) || !TrySkipSubBlocks(bytes, ref offset))
                {
                    return null;
                }

                continue;
            }

            if (marker != 0x2C || !TryAdvance(ref offset, 9, bytes.Length))
            {
                return null;
            }

            frames++;
            byte packed = bytes[offset - 1];
            if ((packed & 0x80) != 0)
            {
                int tableBytes = 3 * (1 << ((packed & 0x07) + 1));
                if (!TryAdvance(ref offset, tableBytes, bytes.Length))
                {
                    return null;
                }
            }

            if (!TryAdvance(ref offset, 1, bytes.Length) || !TrySkipSubBlocks(bytes, ref offset))
            {
                return null;
            }
        }

        return frames == 0 ? null : new HeaderInfo("GIF", width, height, frames);
    }

    private static HeaderInfo? TryReadJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return null;
        }

        int offset = 2;
        while (offset < bytes.Length)
        {
            while (offset < bytes.Length && bytes[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= bytes.Length)
            {
                return null;
            }

            byte marker = bytes[offset++];
            if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (offset > bytes.Length - 2)
            {
                return null;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (segmentLength < 2 || offset > bytes.Length - segmentLength)
            {
                return null;
            }

            if (IsJpegStartOfFrame(marker))
            {
                if (segmentLength < 7)
                {
                    return null;
                }

                int height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]);
                return new HeaderInfo("JPEG", width, height, 1);
            }

            offset += segmentLength;
        }

        return null;
    }

    private static HeaderInfo? TryReadBmp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 26 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
        {
            return null;
        }

        uint dibSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
        if (dibSize == 12)
        {
            int coreWidth = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]);
            int coreHeight = BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]);
            return new HeaderInfo("BMP", coreWidth, coreHeight, 1);
        }

        if (dibSize < 40 || bytes.Length < 54)
        {
            return null;
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]);
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]);
        if (width == int.MinValue || rawHeight == int.MinValue)
        {
            return null;
        }

        return new HeaderInfo("BMP", Math.Abs(width), Math.Abs(rawHeight), 1);
    }

    private static HeaderInfo? TryReadWebP(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 30 || !bytes[..4].SequenceEqual("RIFF"u8) ||
            !bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return null;
        }

        ReadOnlySpan<byte> chunkType = bytes.Slice(12, 4);
        if (chunkType.SequenceEqual("VP8X"u8))
        {
            int width = ReadUInt24LittleEndian(bytes, 24) + 1;
            int height = ReadUInt24LittleEndian(bytes, 27) + 1;
            bool animated = (bytes[20] & 0x02) != 0;
            int frames = 1;
            if (animated)
            {
                frames = CountWebPFrames(bytes);
                if (frames == 0)
                {
                    return null;
                }
            }

            return new HeaderInfo("WEBP", width, height, frames);
        }

        if (chunkType.SequenceEqual("VP8 "u8) && bytes.Length >= 30 &&
            bytes.Slice(23, 3).SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
        {
            int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3FFF;
            int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3FFF;
            return new HeaderInfo("WEBP", width, height, 1);
        }

        if (chunkType.SequenceEqual("VP8L"u8) && bytes.Length >= 25 && bytes[20] == 0x2F)
        {
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
            int width = (int)(packed & 0x3FFF) + 1;
            int height = (int)((packed >> 14) & 0x3FFF) + 1;
            return new HeaderInfo("WEBP", width, height, 1);
        }

        return null;
    }

    private static HeaderInfo? TryReadIcon(ReadOnlySpan<byte> bytes)
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

        if (count <= 0 || count > MaxFrameCount || bytes.Length < directoryLength)
        {
            return null;
        }

        int maxWidth = 0;
        int maxHeight = 0;
        long totalPixels = 0;
        List<IconPayloadRange> ranges = new(count);
        for (int index = 0; index < count; index++)
        {
            int entryOffset = 6 + (index * 16);
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
            HeaderInfo? nested = TryReadCompleteIconPng(payload) ?? TryReadIconDib(payload);
            if (nested is null || nested.FrameCount != 1 ||
                nested.Width != advertisedWidth || nested.Height != advertisedHeight ||
                nested.Width <= 0 || nested.Height <= 0 ||
                nested.Width > MaxDimension || nested.Height > MaxDimension)
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

            if (nestedPixels > MaxPixelsPerFrame ||
                totalPixels > MaxDecodedBytes / BytesPerDecodedPixel)
            {
                return null;
            }

            maxWidth = Math.Max(maxWidth, nested.Width);
            maxHeight = Math.Max(maxHeight, nested.Height);
        }

        return new HeaderInfo("ICO", maxWidth, maxHeight, count, totalPixels);
    }

    private static HeaderInfo? TryReadCompleteIconPng(ReadOnlySpan<byte> bytes)
    {
        HeaderInfo? header = TryReadPng(bytes);
        if (header is null)
        {
            return null;
        }

        int offset = 8;
        bool firstChunk = true;
        while (offset <= bytes.Length - 12)
        {
            uint chunkLengthValue = ReadUInt32BigEndian(bytes, offset);
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

    private static HeaderInfo? TryReadIconDib(ReadOnlySpan<byte> bytes)
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
        int width;
        int doubledHeight;
        int bitsPerPixel;
        int paletteEntryBytes;
        uint colorsUsed = 0;
        if (headerSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
            doubledHeight = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
            paletteEntryBytes = 3;
        }
        else
        {
            if (headerSize < 40 || headerSize > bytes.Length)
            {
                return null;
            }

            int rawWidth = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
            int rawDoubledHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
            if (rawWidth == int.MinValue || rawDoubledHeight == int.MinValue)
            {
                return null;
            }

            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
            if (compression != 0)
            {
                return null;
            }

            width = Math.Abs(rawWidth);
            doubledHeight = Math.Abs(rawDoubledHeight);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
            colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]);
            paletteEntryBytes = 4;
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
            ? new HeaderInfo("ICO-DIB", width, height, 1)
            : null;
    }

    private static int CountWebPFrames(ReadOnlySpan<byte> bytes)
    {
        int frames = 0;
        int offset = 12;
        while (offset <= bytes.Length - 8)
        {
            uint chunkSizeValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            if (chunkSizeValue > int.MaxValue)
            {
                return 0;
            }

            int chunkSize = (int)chunkSizeValue;
            int advance;
            try
            {
                advance = checked(8 + chunkSize + (chunkSize & 1));
            }
            catch (OverflowException)
            {
                return 0;
            }

            if (advance > bytes.Length - offset)
            {
                return 0;
            }

            if (bytes.Slice(offset, 4).SequenceEqual("ANMF"u8))
            {
                frames++;
            }

            offset += advance;
        }

        return frames;
    }

    private static bool TrySkipSubBlocks(ReadOnlySpan<byte> bytes, ref int offset)
    {
        while (offset < bytes.Length)
        {
            int length = bytes[offset++];
            if (length == 0)
            {
                return true;
            }

            if (!TryAdvance(ref offset, length, bytes.Length))
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryAdvance(ref int offset, int amount, int length)
    {
        if (amount < 0 || offset > length - amount)
        {
            return false;
        }

        offset += amount;
        return true;
    }

    private static bool IsJpegStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);

    private static int ReadPositiveInt32BigEndian(ReadOnlySpan<byte> bytes, int offset)
    {
        uint value = ReadUInt32BigEndian(bytes, offset);
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes, int offset) =>
        bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);

    private sealed record HeaderInfo(
        string Format,
        int Width,
        int Height,
        int FrameCount,
        long? TotalPixels = null);

    private readonly record struct IconPayloadRange(int Offset, int Length)
    {
        public int End => Offset + Length;
    }
}
