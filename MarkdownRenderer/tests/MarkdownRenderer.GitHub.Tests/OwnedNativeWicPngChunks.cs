using System.Buffers.Binary;
using System.Text;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Bounded PNG chunk framing for the color-metadata fixtures: builds chunks
/// with their CRC and parses a fixture PNG into validated chunk ranges.
/// </summary>
/// <remarks>
/// Chunk ordering and CRC follow the W3C PNG Third Edition:
/// https://www.w3.org/TR/png-3/
/// </remarks>
internal static class OwnedNativeWicPngChunks
{
    internal const int MaximumPngBytes = 16 * 1024 * 1024;

    internal static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    internal static byte[] CreateChunk(string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        if (typeBytes.Length != 4)
        {
            throw new ArgumentException("PNG chunk type must be four ASCII bytes.", nameof(type));
        }

        byte[] chunk = new byte[checked(data.Length + 12)];
        WriteUInt32(chunk, 0, checked((uint)data.Length));
        typeBytes.CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        WriteUInt32(chunk, chunk.Length - sizeof(uint), ComputeCrc(typeBytes, data));
        return chunk;
    }

    internal static List<PngChunk> Parse(byte[] png, bool allowExistingIccp = false)
    {
        if (png.Length < PngSignature.Length || !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            throw new InvalidDataException("Input does not have the PNG signature.");
        }

        var chunks = new List<PngChunk>();
        int offset = PngSignature.Length;
        bool sawHeader = false;
        bool sawImageData = false;
        bool imageDataEnded = false;
        bool sawEnd = false;

        while (offset < png.Length)
        {
            if (sawEnd || png.Length - offset < 12)
            {
                throw new InvalidDataException("PNG has truncated data or bytes after IEND.");
            }

            (string type, int dataOffset, int dataLength, int totalLength) = ReadCheckedFrame(png, offset);
            if (chunks.Count == 0 && (type != "IHDR" || dataLength != 13))
            {
                throw new InvalidDataException("PNG must begin with one 13-byte IHDR chunk.");
            }

            if (type == "IHDR")
            {
                if (sawHeader || chunks.Count != 0)
                {
                    throw new InvalidDataException("PNG contains duplicate or misplaced IHDR chunks.");
                }

                ValidateRgbHeader(png, dataOffset);
                sawHeader = true;
            }
            else if (!sawHeader)
            {
                throw new InvalidDataException("PNG is missing its IHDR chunk.");
            }

            if (type == "iCCP" && !allowExistingIccp)
            {
                throw new InvalidDataException("PNG already has an iCCP profile.");
            }

            TrackImageDataOrder(
                type, dataLength, offset + totalLength == png.Length,
                ref sawImageData, ref imageDataEnded, ref sawEnd);
            chunks.Add(new PngChunk(offset, totalLength, type));
            offset = checked(offset + totalLength);
        }

        if (!sawHeader || !sawImageData || !sawEnd)
        {
            throw new InvalidDataException("PNG is missing a required IHDR, IDAT, or IEND chunk.");
        }

        return chunks;
    }

    // Reads one chunk's length, type and CRC at offset, rejecting a length
    // past the bound or the input and a CRC that does not match.
    private static (string Type, int DataOffset, int DataLength, int TotalLength) ReadCheckedFrame(
        byte[] png,
        int offset)
    {
        uint unsignedLength = ReadUInt32(png, offset);
        if (unsignedLength > MaximumPngBytes || unsignedLength > int.MaxValue)
        {
            throw new InvalidDataException("PNG chunk exceeds the bounded input size.");
        }

        int dataLength = checked((int)unsignedLength);
        int totalLength = checked(dataLength + 12);
        if (totalLength > png.Length - offset)
        {
            throw new InvalidDataException("PNG chunk length extends beyond the input.");
        }

        string type = Encoding.ASCII.GetString(png, offset + 4, 4);
        int dataOffset = offset + 8;
        uint expectedCrc = ReadUInt32(png, dataOffset + dataLength);
        uint actualCrc = ComputeCrc(png.AsSpan(offset + 4, 4), png.AsSpan(dataOffset, dataLength));
        if (expectedCrc != actualCrc)
        {
            throw new InvalidDataException($"PNG chunk {type} has an invalid CRC.");
        }

        return (type, dataOffset, dataLength, totalLength);
    }

    // IDAT chunks must be consecutive; IEND must be empty, follow image data
    // and end the input.
    private static void TrackImageDataOrder(
        string type,
        int dataLength,
        bool endsInput,
        ref bool sawImageData,
        ref bool imageDataEnded,
        ref bool sawEnd)
    {
        if (type == "IDAT")
        {
            if (imageDataEnded)
            {
                throw new InvalidDataException("PNG IDAT chunks must be consecutive.");
            }

            sawImageData = true;
        }
        else if (sawImageData && type != "IEND")
        {
            imageDataEnded = true;
        }

        if (type == "IEND")
        {
            if (dataLength != 0 || !sawImageData || !endsInput)
            {
                throw new InvalidDataException("PNG IEND is malformed or out of order.");
            }

            sawEnd = true;
        }
    }

    private static void ValidateRgbHeader(byte[] png, int dataOffset)
    {
        byte bitDepth = png[dataOffset + 8];
        byte colorType = png[dataOffset + 9];
        if (ReadUInt32(png, dataOffset) == 0 || ReadUInt32(png, dataOffset + 4) == 0 ||
            colorType is not (2 or 3 or 6) ||
            png[dataOffset + 10] != 0 || png[dataOffset + 11] != 0 || png[dataOffset + 12] > 1 ||
            !IsAllowedBitDepth(colorType, bitDepth))
        {
            throw new InvalidDataException("PNG IHDR is invalid for an embedded RGB profile.");
        }
    }

    private static bool IsAllowedBitDepth(byte colorType, byte bitDepth) => colorType switch
    {
        2 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        6 => bitDepth is 8 or 16,
        _ => false,
    };

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in type)
        {
            crc = UpdateCrc(crc, value);
        }

        foreach (byte value in data)
        {
            crc = UpdateCrc(crc, value);
        }

        return ~crc;
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> type, byte[] data) => ComputeCrc(type, data.AsSpan());

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return crc;
    }

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, sizeof(uint)));

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, sizeof(uint)), value);

    internal readonly record struct PngChunk(int Offset, int TotalLength, string Type);
}
