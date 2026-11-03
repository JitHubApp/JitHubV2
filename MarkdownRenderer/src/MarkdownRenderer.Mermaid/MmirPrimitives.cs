using System.Buffers.Binary;
using static MarkdownRenderer.Mermaid.MmirFormat;

namespace MarkdownRenderer.Mermaid;

/// <summary>
/// Little-endian reads, bounds and index checks, periodic cancellation and the
/// failure result shared by the MMIR scene decoder and its validators.
/// </summary>
internal static class MmirPrimitives
{
    internal static void ThrowIfCancellationRequestedPeriodically(
        CancellationToken cancellationToken,
        int index)
    {
        if ((index & 0xff) == 0)
            cancellationToken.ThrowIfCancellationRequested();
    }

    internal static bool IsRangeInBounds(int offset, int length, int limit) =>
        offset >= 0 && length >= 0 && offset <= limit && length <= limit - offset;

    internal static bool IsUnsignedRangeInBounds(uint offset, uint length, int limit) =>
        offset <= (uint)limit && length <= (uint)limit - offset;

    internal static bool IsRequiredIndex(uint index, int count) => index < (uint)count;

    internal static bool IsOptionalIndex(uint index, int count) => index == NoIndex || IsRequiredIndex(index, count);

    internal static int OptionalIndex(uint index) => index == NoIndex ? -1 : checked((int)index);

    internal static bool TryUIntToInt(uint value, out int result)
    {
        if (value > int.MaxValue)
        {
            result = 0;
            return false;
        }

        result = (int)value;
        return true;
    }

    internal static ushort ReadUInt16(ReadOnlySpan<byte> buffer, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));

    internal static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));

    internal static float ReadSingle(ReadOnlySpan<byte> buffer, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4)));

    internal static bool AreFinite(float first, float second, float third, float fourth) =>
        float.IsFinite(first) && float.IsFinite(second) && float.IsFinite(third) && float.IsFinite(fourth);

    internal static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    internal static MermaidSceneDecodeResult Failure(MermaidSceneDecodeStatus status, int offset, string message) =>
        new(status, null, offset, message);
}
