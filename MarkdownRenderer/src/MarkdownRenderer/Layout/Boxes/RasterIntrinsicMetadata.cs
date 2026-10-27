using System;
using System.Buffers.Binary;
using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Small, source-cache-owned raster dimensions which are safe to use before
/// WIC has produced the authoritative decoded image.
/// </summary>
internal readonly struct RasterIntrinsicMetadata(int Width, int Height) : IEquatable<RasterIntrinsicMetadata>
{
    public int Width { get; init; } = Width;
    public int Height { get; init; } = Height;

    public bool Equals(RasterIntrinsicMetadata other) => Width == other.Width && Height == other.Height;

    public override bool Equals(object? obj) => obj is RasterIntrinsicMetadata other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Width, Height);

    public static bool operator ==(RasterIntrinsicMetadata left, RasterIntrinsicMetadata right) => left.Equals(right);

    public static bool operator !=(RasterIntrinsicMetadata left, RasterIntrinsicMetadata right) => !left.Equals(right);

    private const int MaxTiffIfdDepth = 4;

    internal static bool TryRead(
        ReadOnlySpan<byte> bytes,
        out RasterIntrinsicMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        metadata = default;
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.IsEmpty || bytes.Length > RasterImageResourceBudget.MaxInputBytes)
            return false;

        // The budget parser is deliberately the first authority for accepted
        // dimensions. This parser only refines display axes from bounded EXIF
        // metadata; it never changes source/pixel admission.
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(bytes);
        return TryReadValidated(bytes, budget, out metadata, out _, cancellationToken);
    }

    /// <summary>
    /// Refines validated source dimensions with bounded orientation metadata.
    /// The caller must pass the result produced by validating these exact bytes;
    /// this method deliberately avoids repeating the full source-budget scan.
    /// </summary>
    internal static bool TryReadValidated(
        ReadOnlySpan<byte> bytes,
        RasterImageBudgetResult validatedBudget,
        out RasterIntrinsicMetadata metadata,
        out int orientation,
        CancellationToken cancellationToken = default)
    {
        metadata = default;
        orientation = 1;
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.IsEmpty || bytes.Length > RasterImageResourceBudget.MaxInputBytes)
            return false;

        ArgumentNullException.ThrowIfNull(validatedBudget);
        if (!validatedBudget.Accepted && !validatedBudget.CanRenderStaticPreview)
            return false;

        if (validatedBudget.Width <= 0 || validatedBudget.Height <= 0 ||
            validatedBudget.Width > RasterImageResourceBudget.MaxDimension ||
            validatedBudget.Height > RasterImageResourceBudget.MaxDimension ||
            validatedBudget.FrameCount <= 0)
        {
            return false;
        }

        int parsedOrientation = 1;
        bool valid = validatedBudget.Format switch
        {
            "PNG" => TryReadPngOrientation(bytes, cancellationToken, out parsedOrientation),
            "JPEG" => TryReadJpegOrientation(bytes, cancellationToken, out parsedOrientation),
            "WEBP" when validatedBudget.FrameCount == 1 =>
                TryReadWebPOrientation(bytes, cancellationToken, out parsedOrientation),
            "WEBP" => false,
            "GIF" => true,
            "BMP" => true,
            // AVIF item transforms and ICO frame choice require decoder-specific
            // interpretation. Do not invent an intrinsic size for either.
            _ => false,
        };
        if (!valid || parsedOrientation is < 1 or > 8)
            return false;

        orientation = parsedOrientation;
        bool swapAxes = orientation is 5 or 6 or 7 or 8;
        int width = swapAxes ? validatedBudget.Height : validatedBudget.Width;
        int height = swapAxes ? validatedBudget.Width : validatedBudget.Height;
        if (width <= 0 || height <= 0 ||
            width > RasterImageResourceBudget.MaxDimension ||
            height > RasterImageResourceBudget.MaxDimension)
        {
            return false;
        }

        metadata = new RasterIntrinsicMetadata(width, height);
        return true;
    }

    private static bool TryReadPngOrientation(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken,
        out int orientation)
    {
        orientation = 1;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(signature))
            return false;

        int offset = 8;
        bool firstChunk = true;
        bool sawEnd = false;
        bool sawExif = false;
        bool sawImageData = false;
        while (offset <= bytes.Length - 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint lengthValue = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (lengthValue > int.MaxValue)
                return false;
            int length = (int)lengthValue;
            int chunkEnd;
            try
            {
                chunkEnd = checked(offset + 12 + length);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (chunkEnd > bytes.Length)
                return false;

            ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4);
            if (firstChunk)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13)
                    return false;
                firstChunk = false;
            }

            if (type.SequenceEqual("eXIf"u8))
            {
                if (sawExif || sawImageData || !TryReadTiffOrientation(
                    bytes.Slice(offset + 8, length), cancellationToken, out orientation))
                    return false;
                sawExif = true;
            }
            if (type.SequenceEqual("IDAT"u8))
                sawImageData = true;

            if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0)
                    return false;
                sawEnd = true;
                break;
            }

            offset = chunkEnd;
        }

        // A partial PNG prefix is sufficient for the rendering safety budget,
        // but not enough evidence that a later eXIf chunk cannot rotate it.
        // Bytes after a complete IEND are ignored by PNG decoders as well.
        return sawEnd;
    }

    private static bool TryReadJpegOrientation(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken,
        out int orientation)
    {
        orientation = 1;
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            return false;

        bool sawExif = false;
        int offset = 2;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes[offset] != 0xFF)
                return false;
            int fillBytes = 0;
            while (offset < bytes.Length && bytes[offset] == 0xFF)
            {
                if ((fillBytes++ & 0xFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                offset++;
            }
            if (offset >= bytes.Length)
                return false;

            byte marker = bytes[offset++];
            if (marker == 0xDA) // Start of scan: later bytes are entropy-coded data.
                return true;
            if (marker == 0xD9)
                return true;
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;
            if (marker is 0x00 or 0xD8 || offset > bytes.Length - 2)
                return false;

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (segmentLength < 2 || offset > bytes.Length - segmentLength)
                return false;
            int payloadStart = offset + 2;
            int payloadLength = segmentLength - 2;
            int segmentEnd = offset + segmentLength;

            if (marker == 0xE1 && payloadLength >= 6 &&
                bytes.Slice(payloadStart, 6).SequenceEqual("Exif\0\0"u8))
            {
                if (sawExif || !TryReadTiffOrientation(
                    bytes.Slice(payloadStart + 6, payloadLength - 6),
                    cancellationToken, out orientation))
                {
                    return false;
                }
                sawExif = true;
            }

            offset = segmentEnd;
        }

        // Do not guess orientation from an incomplete marker stream.
        return false;
    }

    private static bool TryReadWebPOrientation(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken,
        out int orientation)
    {
        orientation = 1;
        if (bytes.Length < 20 || !bytes[..4].SequenceEqual("RIFF"u8) ||
            !bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return false;
        }

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (riffSize != bytes.Length - 8)
            return false;

        bool sawExif = false;
        int imageChunkCount = 0;
        int offset = 12;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset > bytes.Length - 8)
                return false;
            ReadOnlySpan<byte> type = bytes.Slice(offset, 4);
            uint lengthValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            if (lengthValue > int.MaxValue)
                return false;
            int length = (int)lengthValue;
            int paddedLength = length + (length & 1);
            if (paddedLength < length || paddedLength > bytes.Length - (offset + 8))
                return false;
            int payload = offset + 8;

            if (type.SequenceEqual("VP8 "u8) || type.SequenceEqual("VP8L"u8))
            {
                if (++imageChunkCount != 1)
                    return false;
            }
            else if (type.SequenceEqual("ANMF"u8))
                return false;
            else if (type.SequenceEqual("EXIF"u8))
            {
                if (sawExif || imageChunkCount != 1)
                    return false;
                ReadOnlySpan<byte> exif = bytes.Slice(payload, length);
                if (exif.StartsWith("Exif\0\0"u8))
                    exif = exif[6..];
                if (!TryReadTiffOrientation(exif, cancellationToken, out orientation))
                    return false;
                sawExif = true;
            }

            offset = payload + paddedLength;
        }

        return offset == bytes.Length && imageChunkCount == 1;
    }

    private static bool TryReadTiffOrientation(
        ReadOnlySpan<byte> data,
        CancellationToken cancellationToken,
        out int orientation)
    {
        orientation = 1;
        cancellationToken.ThrowIfCancellationRequested();
        if (data.StartsWith("Exif\0\0"u8))
            data = data[6..];
        if (data.Length < 8)
            return false;

        bool littleEndian;
        if (data[..2].SequenceEqual("II"u8))
            littleEndian = true;
        else if (data[..2].SequenceEqual("MM"u8))
            littleEndian = false;
        else
            return false;

        if (ReadUInt16(data, 2, littleEndian) != 42)
            return false;
        uint ifdOffsetValue = ReadUInt32(data, 4, littleEndian);
        if (ifdOffsetValue > int.MaxValue)
            return false;

        Span<int> visitedOffsets = stackalloc int[MaxTiffIfdDepth];
        int visitedCount = 0;
        int orientationCount = 0;
        if (!ValidateIfd(data, (int)ifdOffsetValue, littleEndian, 0, cancellationToken,
                visitedOffsets, ref visitedCount, ref orientationCount, ref orientation))
        {
            return false;
        }

        return orientationCount <= 1;
    }

    private static bool ValidateIfd(
        ReadOnlySpan<byte> data,
        int offset,
        bool littleEndian,
        int depth,
        CancellationToken cancellationToken,
        Span<int> visitedOffsets,
        ref int visitedCount,
        ref int orientationCount,
        ref int orientation)
    {
        if (depth >= MaxTiffIfdDepth || offset < 0 || offset > data.Length - 2 ||
            visitedCount >= visitedOffsets.Length)
        {
            return false;
        }
        for (int i = 0; i < visitedCount; i++)
        {
            if (visitedOffsets[i] == offset)
                return false;
        }
        visitedOffsets[visitedCount++] = offset;

        int entryCount = ReadUInt16(data, offset, littleEndian);
        int entriesStart = offset + 2;
        int entriesBytes;
        int nextOffsetPosition;
        bool hasThumbnailOffset = false;
        bool hasThumbnailLength = false;
        uint thumbnailOffset = 0;
        uint thumbnailLength = 0;
        try
        {
            entriesBytes = checked(entryCount * 12);
            nextOffsetPosition = checked(entriesStart + entriesBytes);
        }
        catch (OverflowException)
        {
            return false;
        }
        if (nextOffsetPosition > data.Length - 4)
            return false;

        for (int i = 0; i < entryCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int entry = entriesStart + i * 12;
            ushort tag = ReadUInt16(data, entry, littleEndian);
            ushort type = ReadUInt16(data, entry + 2, littleEndian);
            uint count = ReadUInt32(data, entry + 4, littleEndian);
            if (!TryGetTiffTypeWidth(type, out int typeWidth) || count == 0)
                return false;

            ulong valueBytes = (ulong)count * (uint)typeWidth;
            if (valueBytes > int.MaxValue)
                return false;
            if (valueBytes > 4)
            {
                uint valueOffset = ReadUInt32(data, entry + 8, littleEndian);
                if (valueOffset > int.MaxValue || valueOffset > (uint)data.Length ||
                    valueBytes > (ulong)data.Length - valueOffset)
                {
                    return false;
                }
            }

            if (tag == 0x0112)
            {
                orientationCount++;
                if (depth != 0 || orientationCount != 1 || type != 3 || count != 1)
                    return false;
                int value = ReadUInt16(data, entry + 8, littleEndian);
                if (value is < 1 or > 8)
                    return false;
                orientation = value;
            }
            else if (IsIfdPointerTag(tag))
            {
                if (type is not (4 or 13) || count != 1)
                    return false;
                uint childOffsetValue = ReadUInt32(data, entry + 8, littleEndian);
                if (childOffsetValue != 0)
                {
                    if (childOffsetValue > int.MaxValue ||
                        !ValidateIfd(data, (int)childOffsetValue, littleEndian, depth + 1,
                            cancellationToken,
                            visitedOffsets, ref visitedCount, ref orientationCount, ref orientation))
                    {
                        return false;
                    }
                }
            }
            else if (tag == 0x0201)
            {
                if (type != 4 || count != 1)
                    return false;
                hasThumbnailOffset = true;
                thumbnailOffset = ReadUInt32(data, entry + 8, littleEndian);
            }
            else if (tag == 0x0202)
            {
                if (type != 4 || count != 1)
                    return false;
                hasThumbnailLength = true;
                thumbnailLength = ReadUInt32(data, entry + 8, littleEndian);
            }
        }

        if (hasThumbnailOffset != hasThumbnailLength ||
            (hasThumbnailOffset &&
             (thumbnailOffset > (uint)data.Length ||
              thumbnailLength > (ulong)data.Length - thumbnailOffset)))
        {
            return false;
        }

        uint nextIfd = ReadUInt32(data, nextOffsetPosition, littleEndian);
        if (nextIfd != 0)
        {
            if (nextIfd > int.MaxValue || nextIfd > data.Length - 2 ||
                !ValidateIfd(data, (int)nextIfd, littleEndian, depth + 1, cancellationToken,
                    visitedOffsets, ref visitedCount, ref orientationCount, ref orientation))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryGetTiffTypeWidth(ushort type, out int width)
    {
        width = type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 or 13 => 4,
            5 or 10 or 12 => 8,
            _ => 0,
        };
        return width != 0;
    }

    private static bool IsIfdPointerTag(ushort tag) => tag is
        0x014A or // SubIFDs
        0x8769 or // Exif IFD
        0x8825 or // GPS IFD
        0xA005;   // Interoperability IFD

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..])
            : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..])
            : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
