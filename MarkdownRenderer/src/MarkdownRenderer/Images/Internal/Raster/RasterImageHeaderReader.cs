using System;
using System.Buffers.Binary;

namespace MarkdownRenderer.Images.Internal.Raster;

/// <summary>
/// Bounded, allocation-light readers for the raster container headers that
/// <see cref="RasterImageResourceBudget"/> admits. Readers never decode pixels.
/// </summary>
internal static class RasterImageHeaderReader
{
    /// <summary>
    /// Reads the format, dimensions and frame count from a supported raster
    /// header, or returns null when the header is unsupported or malformed.
    /// </summary>
    internal static RasterImageHeader? TryRead(ReadOnlySpan<byte> bytes) =>
        TryReadPng(bytes) ??
        RasterGifHeaderReader.TryRead(bytes) ??
        TryReadJpeg(bytes) ??
        TryReadBmp(bytes) ??
        TryReadWebP(bytes) ??
        TryReadAvif(bytes) ??
        RasterIconHeaderReader.TryReadIcon(bytes);

    internal static RasterImageHeader? TryReadPng(ReadOnlySpan<byte> bytes)
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
            if (type.SequenceEqual("IEND"u8))
            {
                // PNG decoders, including WIC and Chromium, terminate at IEND.
                // Some real repository assets contain harmless encoder debris
                // after that required marker. Do not reinterpret the trailer as
                // another chunk (which can turn four arbitrary bytes into a
                // fictitious multi-gigabyte length and reject a decodable PNG).
                if (chunkLength != 0)
                    return null;
                break;
            }

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

        return new RasterImageHeader("PNG", width, height, frames);
    }

    private static RasterImageHeader? TryReadAvif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || !bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
            return null;

        uint firstBoxSize = ReadUInt32BigEndian(bytes, 0);
        if (firstBoxSize < 16 || firstBoxSize > bytes.Length)
            return null;

        int brandEnd = checked((int)firstBoxSize);
        bool isAvif = false;
        bool isSequence = false;
        for (int offset = 8; offset <= brandEnd - 4; offset += 4)
        {
            ReadOnlySpan<byte> brand = bytes.Slice(offset, 4);
            isAvif |= brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8);
            isSequence |= brand.SequenceEqual("avis"u8);
        }
        if (!isAvif || isSequence)
            return null;

        int width = 0;
        int height = 0;
        if (!TryReadAvifBoxes(bytes, 0, bytes.Length, depth: 0, ref width, ref height) ||
            width <= 0 || height <= 0)
        {
            return null;
        }

        return new RasterImageHeader("AVIF", width, height, FrameCount: 1);
    }

    private static bool TryReadAvifBoxes(
        ReadOnlySpan<byte> bytes,
        int start,
        int end,
        int depth,
        ref int maximumWidth,
        ref int maximumHeight)
    {
        if (depth > 16 || start < 0 || end < start || end > bytes.Length)
            return false;

        int offset = start;
        while (offset <= end - 8)
        {
            uint compactSize = ReadUInt32BigEndian(bytes, offset);
            ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4);
            int headerSize = 8;
            ulong boxSize = compactSize;
            if (compactSize == 1)
            {
                if (offset > end - 16)
                    return false;
                boxSize = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(offset + 8, 8));
                headerSize = 16;
            }
            else if (compactSize == 0)
            {
                boxSize = checked((ulong)(end - offset));
            }

            if (boxSize < (ulong)headerSize || boxSize > int.MaxValue)
                return false;
            int boxEnd;
            try
            {
                boxEnd = checked(offset + (int)boxSize);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (boxEnd > end)
                return false;

            int payload = offset + headerSize;
            if (type.SequenceEqual("ispe"u8))
            {
                if (payload > boxEnd - 12)
                    return false;
                uint width = ReadUInt32BigEndian(bytes, payload + 4);
                uint height = ReadUInt32BigEndian(bytes, payload + 8);
                if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
                    return false;
                if ((ulong)width * height >
                    (ulong)Math.Max(1, maximumWidth) * (ulong)Math.Max(1, maximumHeight))
                {
                    maximumWidth = (int)width;
                    maximumHeight = (int)height;
                }
            }
            else if (type.SequenceEqual("meta"u8))
            {
                if (payload > boxEnd - 4 ||
                    !TryReadAvifBoxes(bytes, payload + 4, boxEnd, depth + 1, ref maximumWidth, ref maximumHeight))
                {
                    return false;
                }
            }
            else if (type.SequenceEqual("iprp"u8) || type.SequenceEqual("ipco"u8))
            {
                if (!TryReadAvifBoxes(bytes, payload, boxEnd, depth + 1, ref maximumWidth, ref maximumHeight))
                    return false;
            }

            offset = boxEnd;
        }

        return offset == end;
    }

    private static RasterImageHeader? TryReadJpeg(ReadOnlySpan<byte> bytes)
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
                return new RasterImageHeader("JPEG", width, height, 1);
            }

            offset += segmentLength;
        }

        return null;
    }

    private static RasterImageHeader? TryReadBmp(ReadOnlySpan<byte> bytes)
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
            return new RasterImageHeader("BMP", coreWidth, coreHeight, 1);
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

        return new RasterImageHeader("BMP", Math.Abs(width), Math.Abs(rawHeight), 1);
    }

    private static RasterImageHeader? TryReadWebP(ReadOnlySpan<byte> bytes)
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

            return new RasterImageHeader("WEBP", width, height, frames);
        }

        if (chunkType.SequenceEqual("VP8 "u8) && bytes.Length >= 30 &&
            bytes.Slice(23, 3).SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
        {
            int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3FFF;
            int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3FFF;
            return new RasterImageHeader("WEBP", width, height, 1);
        }

        if (chunkType.SequenceEqual("VP8L"u8) && bytes.Length >= 25 && bytes[20] == 0x2F)
        {
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
            int width = (int)(packed & 0x3FFF) + 1;
            int height = (int)((packed >> 14) & 0x3FFF) + 1;
            return new RasterImageHeader("WEBP", width, height, 1);
        }

        return null;
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

    private static bool IsJpegStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    internal static uint ReadUInt32BigEndian(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);

    private static int ReadPositiveInt32BigEndian(ReadOnlySpan<byte> bytes, int offset)
    {
        uint value = ReadUInt32BigEndian(bytes, offset);
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes, int offset) =>
        bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
}
