using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Produces small PNG fixtures for pixel-format conversion coverage without
/// machine profiles, downloaded assets, or extra image-codec dependencies.
/// </summary>
internal static class OwnedNativeWicFormatFixtureFactory
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    internal static byte[] CreatePalette4WithTransparencyPng(int width = 37, int height = 23)
    {
        ValidateDimensions(width, height);
        byte[] palette =
        [
            0, 0, 0,       // black
            232, 24, 32,    // red
            24, 224, 48,    // green
            24, 56, 240,    // blue
            240, 208, 16,   // yellow
            16, 216, 224,   // cyan
            232, 32, 208,   // magenta
        ];
        byte[] alpha = [255, 160, 0, 255, 96, 224, 255];
        byte[] rows = new byte[checked(height * checked(1 + (width + 1) / 2))];
        int rowBytes = 1 + (width + 1) / 2;
        for (int y = 0; y < height; y++)
        {
            int row = checked(y * rowBytes);
            rows[row] = 0; // PNG filter None.
            for (int x = 0; x < width; x++)
            {
                int index = (x * 3 + y * 5 + (x ^ y)) % alpha.Length;
                int packedOffset = row + 1 + x / 2;
                if ((x & 1) == 0)
                    rows[packedOffset] = checked((byte)(index << 4));
                else
                    rows[packedOffset] |= checked((byte)index);
            }
        }

        return CreatePng(width, height, bitDepth: 4, colorType: 3, rows, palette, alpha);
    }

    internal static byte[] CreateGrayscale8Png(int width = 37, int height = 23)
    {
        ValidateDimensions(width, height);
        byte[] rows = new byte[checked(height * checked(width + 1))];
        for (int y = 0; y < height; y++)
        {
            int row = checked(y * (width + 1));
            rows[row] = 0;
            for (int x = 0; x < width; x++)
                rows[row + 1 + x] = checked((byte)((x * 31 + y * 47 + (x ^ (y * 3))) & 0xFF));
        }

        return CreatePng(width, height, bitDepth: 8, colorType: 0, rows);
    }

    internal static byte[] CreateGrayscaleAlpha8Png(int width = 37, int height = 23)
    {
        ValidateDimensions(width, height);
        int rowBytes = checked(1 + width * 2);
        byte[] rows = new byte[checked(height * rowBytes)];
        for (int y = 0; y < height; y++)
        {
            int row = checked(y * rowBytes);
            rows[row] = 0;
            for (int x = 0; x < width; x++)
            {
                int pixel = row + 1 + x * 2;
                rows[pixel] = checked((byte)((x * 29 + y * 53 + 17) & 0xFF));
                rows[pixel + 1] = GetAlpha(x, y, width, height);
            }
        }

        return CreatePng(width, height, bitDepth: 8, colorType: 4, rows);
    }

    internal static byte[] CreateGrayscale16Png(int width = 37, int height = 23)
    {
        ValidateDimensions(width, height);
        int rowBytes = checked(1 + width * 2);
        byte[] rows = new byte[checked(height * rowBytes)];
        for (int y = 0; y < height; y++)
        {
            int row = checked(y * rowBytes);
            rows[row] = 0;
            for (int x = 0; x < width; x++)
            {
                ushort value = checked((ushort)((x * 7_919 + y * 3_271 + x * y * 113) & 0xFFFF));
                BinaryPrimitives.WriteUInt16BigEndian(rows.AsSpan(row + 1 + x * 2, sizeof(ushort)), value);
            }
        }

        return CreatePng(width, height, bitDepth: 16, colorType: 0, rows);
    }

    internal static byte[] CreateRgba16Png(int width = 37, int height = 23)
    {
        ValidateDimensions(width, height);
        const int bytesPerPixel = 8;
        int rowBytes = checked(1 + width * bytesPerPixel);
        byte[] rows = new byte[checked(height * rowBytes)];
        for (int y = 0; y < height; y++)
        {
            int row = checked(y * rowBytes);
            rows[row] = 0;
            for (int x = 0; x < width; x++)
            {
                int pixel = row + 1 + x * bytesPerPixel;
                WriteSample(pixel, (ushort)((x * 7_919 + y * 1_231 + 0x1234) & 0xFFFF));
                WriteSample(pixel + 2, (ushort)((x * 2_713 + y * 9_187 + 0x4321) & 0xFFFF));
                WriteSample(pixel + 4, (ushort)((x * 5_003 + y * 3_119 + 0xABCD) & 0xFFFF));
                WriteSample(pixel + 6, Alpha16(x, y, width, height));
            }
        }

        return CreatePng(width, height, bitDepth: 16, colorType: 6, rows);

        void WriteSample(int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16BigEndian(rows.AsSpan(offset, sizeof(ushort)), value);
    }

    private static byte[] CreatePng(
        int width,
        int height,
        byte bitDepth,
        byte colorType,
        byte[] filteredRows,
        byte[]? palette = null,
        byte[]? transparency = null)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(filteredRows);

        using var png = new MemoryStream();
        png.Write(PngSignature);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), checked((uint)height));
        header[8] = bitDepth;
        header[9] = colorType;
        WriteChunk(png, "IHDR", header);
        if (palette is not null)
            WriteChunk(png, "PLTE", palette);
        if (transparency is not null)
            WriteChunk(png, "tRNS", transparency);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static void WriteChunk(Stream destination, string type, ReadOnlySpan<byte> data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        if (typeBytes.Length != 4)
            throw new ArgumentException("PNG chunk identifiers must be four ASCII bytes.", nameof(type));

        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        destination.Write(length);
        destination.Write(typeBytes);
        destination.Write(data);

        uint crc = uint.MaxValue;
        foreach (byte value in typeBytes)
            crc = UpdateCrc(crc, value);
        foreach (byte value in data)
            crc = UpdateCrc(crc, value);
        Span<byte> crcBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ~crc);
        destination.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }

    private static byte GetAlpha(int x, int y, int width, int height)
    {
        int edge = System.Math.Min(System.Math.Min(x, width - 1 - x), System.Math.Min(y, height - 1 - y));
        return edge switch { 0 => 0, 1 => 72, 2 => 176, _ => 255 };
    }

    private static ushort Alpha16(int x, int y, int width, int height)
    {
        int edge = System.Math.Min(System.Math.Min(x, width - 1 - x), System.Math.Min(y, height - 1 - y));
        return edge switch { 0 => 0, 1 => 18_000, 2 => 43_000, _ => ushort.MaxValue };
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width is <= 0 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(height));
    }
}
