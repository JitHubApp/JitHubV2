using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Builds one-pixel PNG payloads and inspects rendered rasters for the
/// provider-level tests.
/// </summary>
internal static class ProviderTestRasters
{
    internal static bool HasVisiblePixel(ReadOnlySpan<byte> pixels)
    {
        for (int index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0)
                return true;
        }
        return false;
    }

    internal static void AssertTileMatchesCrop(
        ReadOnlySpan<byte> full,
        ReadOnlySpan<byte> tile,
        int fullWidth,
        int tileX,
        int tileY,
        int tileWidth,
        int tileHeight)
    {
        long totalDelta = 0;
        for (int row = 0; row < tileHeight; row++)
        {
            int fullStart = (((tileY + row) * fullWidth) + tileX) * 4;
            int tileStart = row * tileWidth * 4;
            for (int index = 0; index < tileWidth * 4; index++)
                totalDelta += Math.Abs(full[fullStart + index] - tile[tileStart + index]);
        }
        double meanDelta = totalDelta / (double)(tileWidth * tileHeight * 4);
        Assert.InRange(meanDelta, 0, 1);
    }

    internal static byte[] CreatePng(
        byte red,
        byte green,
        byte blue,
        byte alpha,
        int metadataBytes = 0,
        int metadataSeed = 0)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(output, "IHDR", header);

        if (metadataBytes > 0)
        {
            byte[] metadata = new byte[metadataBytes];
            "Comment\0"u8.CopyTo(metadata);
            for (int index = 8; index < metadata.Length; index++)
                metadata[index] = checked((byte)('A' + ((index + metadataSeed) % 26)));
            WritePngChunk(output, "tEXt", metadata);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write([0, red, green, blue, alpha]);
        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream destination, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        destination.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        destination.Write(typeBytes);
        destination.Write(data);
        byte[] crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        byte[] crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc32(crcInput));
        destination.Write(crc);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
