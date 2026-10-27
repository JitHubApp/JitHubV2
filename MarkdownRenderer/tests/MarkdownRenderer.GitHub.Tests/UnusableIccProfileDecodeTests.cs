using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Images carrying an ICC profile that declares version 2.1 but uses v4-only
/// tag types (<c>mluc</c> text, <c>para</c> curves) must not be discarded. Windows' color system rejects such a
/// profile (0x800707DB, ERROR_INVALID_PROFILE), and the decoder threw. Browsers,
/// and so GitHub, ignore an unusable profile and show the pixels as sRGB.
/// </summary>
public sealed class UnusableIccProfileDecodeTests
{
    [Fact]
    public async Task PngWithAnUnusableEmbeddedProfileDecodesAsUntaggedSrgb()
    {
        byte[] untagged = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync(withAlphaEdges: false);
        byte[] tagged = InsertIccpChunk(untagged, CreateMislabeledV2Profile());
        Assert.NotEqual(untagged.Length, tagged.Length);

        byte[] expected = Decode(untagged);
        byte[] actual = Decode(tagged);

        Assert.Equal(expected, actual);
    }

    private static byte[] Decode(byte[] source)
    {
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted, budget.Reason);
        var sizing = new RasterImageSizingRequest(budget.Width, 1, null, null, 10_000);
        Assert.True(RasterImageDecodePlan.TryCreate(source, budget, sizing, out var plan));
        var credit = new RasterNativeWicProductParity.CountingCredit();
        using RasterDecodedPixelLease lease = Task.Run(
            () => RasterNativeWicDecoder.Decode(source, plan, credit, CancellationToken.None))
            .GetAwaiter().GetResult();
        Assert.Equal(budget.Width, lease.Width);
        return lease.Pixels.ToArray();
    }

    /// <summary>
    /// An RGB display profile with a v2.1 header and v4 tag types, the shape
    /// produced by common web tooling. The values are sRGB-like primaries.
    /// </summary>
    private static byte[] CreateMislabeledV2Profile()
    {
        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", Mluc("Synthetic mislabeled profile")),
            ("rXYZ", Xyz(0.4361, 0.2225, 0.0139)),
            ("gXYZ", Xyz(0.3851, 0.7169, 0.0971)),
            ("bXYZ", Xyz(0.1431, 0.0606, 0.7141)),
            ("rTRC", Para(2.2)),
            ("gTRC", Para(2.2)),
            ("bTRC", Para(2.2)),
            ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
            ("cprt", Mluc("No copyright")),
        };
        int tableLength = 4 + 12 * tags.Count;
        int offset = 128 + tableLength;
        var body = new MemoryStream();
        var table = new byte[tableLength];
        BinaryPrimitives.WriteUInt32BigEndian(table, (uint)tags.Count);
        for (int index = 0; index < tags.Count; index++)
        {
            byte[] data = tags[index].Data;
            Encoding.ASCII.GetBytes(tags[index].Signature).CopyTo(table, 4 + 12 * index);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8 + 12 * index), (uint)(offset + body.Length));
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(12 + 12 * index), (uint)data.Length);
            body.Write(data);
            while (body.Length % 4 != 0)
                body.WriteByte(0);
        }

        var header = new byte[128];
        int size = 128 + tableLength + (int)body.Length;
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0x02100000); // version 2.1
        Encoding.ASCII.GetBytes("mntr").CopyTo(header, 12);
        Encoding.ASCII.GetBytes("RGB ").CopyTo(header, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(header, 20);
        Encoding.ASCII.GetBytes("acsp").CopyTo(header, 36);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(64), 1); // relative colorimetric
        WriteS15Fixed16(header.AsSpan(68), 0.9642);
        WriteS15Fixed16(header.AsSpan(72), 1.0);
        WriteS15Fixed16(header.AsSpan(76), 0.8249);

        var profile = new byte[size];
        header.CopyTo(profile, 0);
        table.CopyTo(profile, 128);
        body.ToArray().CopyTo(profile, 128 + tableLength);
        return profile;
    }

    private static byte[] Mluc(string text)
    {
        byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        var data = new byte[16 + 12 + utf16.Length];
        Encoding.ASCII.GetBytes("mluc").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), 12);
        Encoding.ASCII.GetBytes("enUS").CopyTo(data, 16);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)utf16.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 28);
        utf16.CopyTo(data, 28);
        return data;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var data = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(data, 0);
        WriteS15Fixed16(data.AsSpan(8), x);
        WriteS15Fixed16(data.AsSpan(12), y);
        WriteS15Fixed16(data.AsSpan(16), z);
        return data;
    }

    private static byte[] Para(double gamma)
    {
        var data = new byte[16];
        Encoding.ASCII.GetBytes("para").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), 0); // Y = X^gamma
        WriteS15Fixed16(data.AsSpan(12), gamma);
        return data;
    }

    private static void WriteS15Fixed16(Span<byte> destination, double value) =>
        BinaryPrimitives.WriteInt32BigEndian(destination, (int)System.Math.Round(value * 65536));

    private static byte[] InsertIccpChunk(byte[] png, byte[] profile)
    {
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(profile);
        byte[] name = Encoding.ASCII.GetBytes("ICC Profile");
        var payload = new byte[name.Length + 2 + compressed.Length];
        name.CopyTo(payload, 0);
        compressed.ToArray().CopyTo(payload, name.Length + 2);

        // Signature (8) + IHDR chunk (4 length + 4 type + 13 data + 4 CRC).
        const int afterIhdr = 8 + 25;
        var chunk = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)payload.Length);
        Encoding.ASCII.GetBytes("iCCP").CopyTo(chunk, 4);
        payload.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + payload.Length), Crc32(chunk.AsSpan(4, 4 + payload.Length)));

        var result = new byte[png.Length + chunk.Length];
        png.AsSpan(0, afterIhdr).CopyTo(result);
        chunk.CopyTo(result, afterIhdr);
        png.AsSpan(afterIhdr).CopyTo(result.AsSpan(afterIhdr + chunk.Length));
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        return result;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
