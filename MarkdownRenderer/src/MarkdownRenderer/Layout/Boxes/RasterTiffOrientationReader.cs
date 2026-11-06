using System;
using System.Buffers.Binary;
using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Reads the orientation tag from a TIFF/Exif payload for
/// <see cref="RasterIntrinsicMetadata"/>, validating the bounded IFD chain it
/// walks: entry types and counts, nested IFD pointers up to a fixed depth,
/// and the JPEG thumbnail tags that must stay inside the payload.
/// </summary>
internal static class RasterTiffOrientationReader
{
    private const int MaxTiffIfdDepth = 4;

    internal static bool TryReadOrientation(
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
        if (!TryVisitIfd(data.Length, offset, depth, visitedOffsets, ref visitedCount))
            return false;

        int entryCount = ReadUInt16(data, offset, littleEndian);
        int entriesStart = offset + 2;
        int entriesBytes;
        int nextOffsetPosition;
        TiffThumbnailTags thumbnail = default;
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
            if (!ValidateIfdEntry(data, entriesStart + i * 12, littleEndian, depth, cancellationToken,
                    visitedOffsets, ref visitedCount, ref orientationCount, ref orientation, ref thumbnail))
            {
                return false;
            }
        }

        if (thumbnail.HasOffset != thumbnail.HasLength ||
            (thumbnail.HasOffset &&
             (thumbnail.Offset > (uint)data.Length ||
              thumbnail.Length > (ulong)data.Length - thumbnail.Offset)))
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

    // Rejects an IFD beyond the depth limit, outside the data, or already
    // visited, and records it as visited otherwise.
    private static bool TryVisitIfd(
        int dataLength,
        int offset,
        int depth,
        Span<int> visitedOffsets,
        ref int visitedCount)
    {
        if (depth >= MaxTiffIfdDepth || offset < 0 || offset > dataLength - 2 ||
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
        return true;
    }

    // Validates one 12-byte IFD entry: its value range, the orientation tag,
    // child IFD pointers (recursively) and the JPEG thumbnail tags.
    private static bool ValidateIfdEntry(
        ReadOnlySpan<byte> data,
        int entry,
        bool littleEndian,
        int depth,
        CancellationToken cancellationToken,
        Span<int> visitedOffsets,
        ref int visitedCount,
        ref int orientationCount,
        ref int orientation,
        ref TiffThumbnailTags thumbnail)
    {
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
            thumbnail.HasOffset = true;
            thumbnail.Offset = ReadUInt32(data, entry + 8, littleEndian);
        }
        else if (tag == 0x0202)
        {
            if (type != 4 || count != 1)
                return false;
            thumbnail.HasLength = true;
            thumbnail.Length = ReadUInt32(data, entry + 8, littleEndian);
        }
        return true;
    }

    // The JPEG interchange-format thumbnail tags seen in one IFD; both or
    // neither must be present, and together they must stay inside the data.
    private struct TiffThumbnailTags
    {
        internal bool HasOffset;
        internal bool HasLength;
        internal uint Offset;
        internal uint Length;
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
