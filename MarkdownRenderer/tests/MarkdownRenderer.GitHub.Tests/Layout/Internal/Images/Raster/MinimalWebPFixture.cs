using System.Buffers.Binary;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;

/// <summary>
/// Builds the smallest extended WebP container (VP8X canvas plus a stub VP8
/// chunk) whose header metadata states the requested canvas size. The VP8
/// payload is only a frame-tag stub, so the bytes exercise metadata and
/// raster-provider paths, not a real codec decode.
/// </summary>
internal static class MinimalWebPFixture
{
    internal static byte[] Create(int width, int height)
    {
        using var body = new MemoryStream();
        Span<byte> extendedHeader = stackalloc byte[10];
        extendedHeader.Clear();
        WriteUInt24LittleEndian(extendedHeader[4..], checked((uint)(width - 1)));
        WriteUInt24LittleEndian(extendedHeader[7..], checked((uint)(height - 1)));
        WriteWebPChunk(body, "VP8X"u8, extendedHeader);
        WriteWebPChunk(body, "VP8 "u8, [0, 0, 0, 0x9D, 0x01, 0x2A, 0, 0, 0, 0]);
        byte[] chunks = body.ToArray();
        byte[] result = new byte[checked(12 + chunks.Length)];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)(result.Length - 8)));
        "WEBP"u8.CopyTo(result.AsSpan(8));
        chunks.CopyTo(result, 12);
        return result;
    }

    private static void WriteWebPChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        type.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)payload.Length));
        stream.Write(header);
        stream.Write(payload);
        if ((payload.Length & 1) != 0)
            stream.WriteByte(0);
    }

    private static void WriteUInt24LittleEndian(Span<byte> bytes, uint value)
    {
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
        bytes[2] = (byte)(value >> 16);
    }
}
