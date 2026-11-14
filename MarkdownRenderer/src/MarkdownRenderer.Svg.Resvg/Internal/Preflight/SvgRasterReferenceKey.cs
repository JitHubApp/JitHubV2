using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MarkdownRenderer.Svg.Resvg.Internal.Preflight;

/// <summary>
/// A digest-only identity for one embedded resource reference or payload.
/// It never retains an XML attribute string or decoded payload.
/// </summary>
internal readonly record struct SvgRasterReferenceKey(ulong A, ulong B, ulong C, ulong D)
{
    internal static SvgRasterReferenceKey Create(ReadOnlySpan<char> reference)
        => Create(MemoryMarshal.AsBytes(reference));

    internal static SvgRasterReferenceKey Create(ReadOnlySpan<byte> payload)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(payload, digest);
        return new(
            BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
    }
}
