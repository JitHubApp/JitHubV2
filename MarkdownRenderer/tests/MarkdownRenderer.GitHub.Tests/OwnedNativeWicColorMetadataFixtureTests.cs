using System.Buffers.Binary;
using System.Text;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Independently checks the bounded test fixtures' color-metadata framing so
/// parity results cannot silently exercise malformed or misplaced metadata.
/// </summary>
[Collection(NativeWicDecodeTestCollection.CollectionName)]
public sealed class OwnedNativeWicColorMetadataFixtureTests
{
    private const string WebpFixtureResource =
        "MarkdownRenderer.GitHub.Tests.Fixtures.oversized-static.webp";
    private const string WebpFixtureSha256 =
        "6FEB51F257EF7FAC608FFA54E0D942EA4FE4DE11DECD165A12EA254C3886DB85";
    private static readonly byte[] IccJpegSignature = Encoding.ASCII.GetBytes("ICC_PROFILE\0");

    [Fact]
    public async Task PngColorFixturesHaveValidChunkOrderAndCrcAndRemovalDropsCompetingClaims()
    {
        byte[] basePng = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync(
            width: 37, height: 23, withAlphaEdges: true);
        byte[] profile = OwnedNativeWicIccFixtureFactory.CreateGamma18MatrixTrcProfile();
        byte[] profiledPng = OwnedNativeWicIccFixtureFactory.EmbedProfileInPng(basePng, profile);
        byte[] gammaChromaticityPng = OwnedNativeWicIccFixtureFactory.EmbedGammaAndChromaticitiesInPng(basePng);
        byte[] srgbPng = OwnedNativeWicIccFixtureFactory.EmbedSrgbChunkInPng(basePng);
        byte[] cicpPng = InsertPngChunkAfterHeader(basePng, "cICP", [1, 13, 0, 1]);

        try
        {
            PngChunk[] gammaChunks = ReadPngChunks(gammaChromaticityPng);
            Assert.Collection(
                gammaChunks.Take(3),
                chunk => Assert.Equal("IHDR", chunk.Type),
                chunk => Assert.Equal("cHRM", chunk.Type),
                chunk => Assert.Equal("gAMA", chunk.Type));
            Assert.Equal(1, gammaChunks.Count(static chunk => chunk.Type == "cHRM"));
            Assert.Equal(1, gammaChunks.Count(static chunk => chunk.Type == "gAMA"));
            Assert.DoesNotContain(gammaChunks, static chunk => chunk.Type is "iCCP" or "sRGB" or "cICP");
            AssertPngChunkCrcs(gammaChromaticityPng, gammaChunks);

            PngChunk gammaChunk = Assert.Single(gammaChunks, static chunk => chunk.Type == "gAMA");
            Assert.Equal(55_555u, ReadPngUInt32(gammaChromaticityPng, gammaChunk.DataOffset));
            PngChunk chrmChunk = Assert.Single(gammaChunks, static chunk => chunk.Type == "cHRM");
            Assert.Equal(32, chrmChunk.DataLength);

            PngChunk[] srgbChunks = ReadPngChunks(srgbPng);
            Assert.Equal("sRGB", srgbChunks[1].Type);
            Assert.Equal(1, srgbChunks.Count(static chunk => chunk.Type == "sRGB"));
            Assert.DoesNotContain(srgbChunks, static chunk => chunk.Type is "iCCP" or "gAMA" or "cHRM" or "cICP");
            Assert.Equal(0, srgbPng[srgbChunks[1].DataOffset]);
            AssertPngChunkCrcs(srgbPng, srgbChunks);

            AssertPngChunkCrcs(profiledPng, ReadPngChunks(profiledPng));
            Assert.Single(ReadPngChunks(profiledPng), static chunk => chunk.Type == "iCCP");
            AssertPngChunkCrcs(cicpPng, ReadPngChunks(cicpPng));

            foreach (byte[] tagged in new[] { profiledPng, gammaChromaticityPng, srgbPng, cicpPng })
            {
                byte[] stripped = OwnedNativeWicIccFixtureFactory.RemovePngColorMetadata(tagged);
                try
                {
                    PngChunk[] strippedChunks = ReadPngChunks(stripped);
                    Assert.DoesNotContain(strippedChunks,
                        static chunk => chunk.Type is "iCCP" or "sRGB" or "cICP" or "gAMA" or "cHRM");
                    AssertPngChunkCrcs(stripped, strippedChunks);
                }
                finally
                {
                    Array.Clear(stripped);
                }
            }
        }
        finally
        {
            Array.Clear(basePng);
            Array.Clear(profile);
            Array.Clear(profiledPng);
            Array.Clear(gammaChromaticityPng);
            Array.Clear(srgbPng);
            Array.Clear(cicpPng);
        }
    }

    [Fact]
    public async Task JpegApp2ProfileFollowsLeadingJfifAndPreservesOriginalBytes()
    {
        byte[] source = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedJpegAsync(
            orientation: 1, width: 37, height: 23);
        byte[] profile = OwnedNativeWicIccFixtureFactory.CreateGamma18MatrixTrcProfile();
        byte[] tagged = OwnedNativeWicIccFixtureFactory.EmbedProfileInJpeg(source, profile);

        try
        {
            Assert.True(source.Length >= 6);
            Assert.Equal(0xFF, source[0]);
            Assert.Equal(0xD8, source[1]);
            Assert.Equal(0xFF, source[2]);
            Assert.Equal(0xE0, source[3]);
            ushort jfifLength = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(4, sizeof(ushort)));
            Assert.True(jfifLength >= 7);
            Assert.True(jfifLength <= source.Length - 4);
            Assert.Equal("JFIF\0", Encoding.ASCII.GetString(source, 6, 5));
            int jfifEnd = checked(4 + jfifLength);

            Assert.Equal(source.AsSpan(0, jfifEnd).ToArray(), tagged.AsSpan(0, jfifEnd).ToArray());
            Assert.Equal(0xFF, tagged[jfifEnd]);
            Assert.Equal(0xE2, tagged[jfifEnd + 1]);
            ushort app2Length = BinaryPrimitives.ReadUInt16BigEndian(tagged.AsSpan(jfifEnd + 2, sizeof(ushort)));
            Assert.Equal(sizeof(ushort) + IccJpegSignature.Length + 2 + profile.Length, app2Length);
            Assert.Equal(IccJpegSignature,
                tagged.AsSpan(jfifEnd + 4, IccJpegSignature.Length).ToArray());
            int profileOffset = jfifEnd + 4 + IccJpegSignature.Length;
            Assert.Equal(1, tagged[profileOffset++]);
            Assert.Equal(1, tagged[profileOffset++]);
            Assert.Equal(profile, tagged.AsSpan(profileOffset, profile.Length).ToArray());
            int app2End = checked(jfifEnd + 2 + app2Length);
            Assert.Equal(source.AsSpan(jfifEnd).ToArray(), tagged.AsSpan(app2End).ToArray());

            Assert.Equal(1, CountJpegIccProfileSegments(tagged));
        }
        finally
        {
            Array.Clear(source);
            Array.Clear(profile);
            Array.Clear(tagged);
        }
    }

    [Fact]
    public async Task WebpIccpFixtureHasOrderedVp8xProfileFlagsAndUnchangedImageChunk()
    {
        byte[] source = await ReadPinnedWebpFixtureAsync();
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(source);
        Assert.True(budget.Accepted || budget.CanRenderStaticPreview, budget.Reason);
        Assert.Equal(1, budget.FrameCount);
        byte[] profile = OwnedNativeWicIccFixtureFactory.CreateGamma18MatrixTrcProfile();
        byte[] tagged = OwnedNativeWicIccFixtureFactory.EmbedProfileInSimpleWebp(
            source, profile, budget.Width, budget.Height);

        try
        {
            Assert.Equal("RIFF", Encoding.ASCII.GetString(tagged, 0, 4));
            Assert.Equal(checked((uint)(tagged.Length - 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(tagged.AsSpan(4, sizeof(uint))));
            Assert.Equal("WEBP", Encoding.ASCII.GetString(tagged, 8, 4));
            Assert.Equal("VP8X", Encoding.ASCII.GetString(tagged, 12, 4));
            Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(tagged.AsSpan(16, sizeof(uint))));
            byte flags = tagged[20];
            Assert.Equal(0x20, flags & 0x20); // ICC profile present
            Assert.Equal(0, flags & 0x0E); // No XMP/EXIF/animation claims were authored.
            Assert.Equal(0, flags & 0xC0); // Reserved VP8X bits remain zero.

            bool sourceHasAlpha = source[20] == 0x2F &&
                ((BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(21, sizeof(uint))) >> 28) & 1) != 0;
            Assert.Equal(sourceHasAlpha ? 0x10 : 0, flags & 0x10);
            Assert.Equal(budget.Width - 1, ReadWebpUInt24LittleEndian(tagged, 24));
            Assert.Equal(budget.Height - 1, ReadWebpUInt24LittleEndian(tagged, 27));

            int iccpOffset = 30;
            Assert.Equal("ICCP", Encoding.ASCII.GetString(tagged, iccpOffset, 4));
            uint profileLength = BinaryPrimitives.ReadUInt32LittleEndian(
                tagged.AsSpan(iccpOffset + 4, sizeof(uint)));
            Assert.Equal(checked((uint)profile.Length), profileLength);
            Assert.Equal(profile, tagged.AsSpan(iccpOffset + 8, profile.Length).ToArray());
            int imageChunkOffset = checked(iccpOffset + 8 + (int)profileLength + ((int)profileLength & 1));
            Assert.Equal("VP8L", Encoding.ASCII.GetString(tagged, imageChunkOffset, 4));
            Assert.Equal(source.AsSpan(12).ToArray(), tagged.AsSpan(imageChunkOffset).ToArray());

            RasterImageBudgetResult taggedBudget = RasterImageResourceBudget.Validate(tagged);
            Assert.True(taggedBudget.Accepted || taggedBudget.CanRenderStaticPreview, taggedBudget.Reason);
            Assert.Equal(budget.Width, taggedBudget.Width);
            Assert.Equal(budget.Height, taggedBudget.Height);
            Assert.Equal(1, taggedBudget.FrameCount);
        }
        finally
        {
            Array.Clear(source);
            Array.Clear(profile);
            Array.Clear(tagged);
        }
    }

    private static byte[] InsertPngChunkAfterHeader(byte[] png, string type, byte[] data)
    {
        PngChunk[] chunks = ReadPngChunks(png);
        Assert.Equal("IHDR", chunks[0].Type);
        byte[] chunk = CreatePngChunk(type, data);
        int insertionOffset = checked(chunks[0].Offset + chunks[0].TotalLength);
        byte[] result = new byte[checked(png.Length + chunk.Length)];
        png.AsSpan(0, insertionOffset).CopyTo(result);
        chunk.CopyTo(result, insertionOffset);
        png.AsSpan(insertionOffset).CopyTo(result.AsSpan(insertionOffset + chunk.Length));
        return result;
    }

    private static byte[] CreatePngChunk(string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        Assert.Equal(4, typeBytes.Length);
        byte[] chunk = new byte[checked(data.Length + 12)];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, sizeof(uint)), checked((uint)data.Length));
        typeBytes.CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - sizeof(uint)),
            ComputePngCrc(chunk.AsSpan(4, 4), chunk.AsSpan(8, data.Length)));
        return chunk;
    }

    private static PngChunk[] ReadPngChunks(byte[] png)
    {
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        Assert.True(png.AsSpan().StartsWith(signature));
        var chunks = new List<PngChunk>();
        int offset = signature.Length;
        bool sawEnd = false;
        while (offset < png.Length)
        {
            Assert.False(sawEnd, "PNG contains bytes after IEND.");
            Assert.True(png.Length - offset >= 12, "PNG chunk header is truncated.");
            uint dataLength = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, sizeof(uint)));
            Assert.InRange(dataLength, 0u, checked((uint)(png.Length - offset - 12)));
            int length = checked((int)dataLength);
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            chunks.Add(new PngChunk(type, offset, offset + 8, length, checked(length + 12)));
            offset = checked(offset + length + 12);
            sawEnd = type == "IEND";
        }
        Assert.True(sawEnd, "PNG has no IEND chunk.");
        Assert.Equal(png.Length, offset);
        return [.. chunks];
    }

    private static void AssertPngChunkCrcs(byte[] png, IEnumerable<PngChunk> chunks)
    {
        foreach (PngChunk chunk in chunks)
        {
            uint expected = BinaryPrimitives.ReadUInt32BigEndian(
                png.AsSpan(chunk.DataOffset + chunk.DataLength, sizeof(uint)));
            uint actual = ComputePngCrc(
                png.AsSpan(chunk.Offset + 4, 4),
                png.AsSpan(chunk.DataOffset, chunk.DataLength));
            Assert.Equal(expected, actual);
        }
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in type)
            crc = UpdatePngCrc(crc, value);
        foreach (byte value in data)
            crc = UpdatePngCrc(crc, value);
        return ~crc;
    }

    private static uint UpdatePngCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
        return crc;
    }

    private static uint ReadPngUInt32(byte[] png, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, sizeof(uint)));

    private static int CountJpegIccProfileSegments(byte[] jpeg)
    {
        int count = 0;
        int offset = 2;
        while (offset < jpeg.Length)
        {
            Assert.Equal(0xFF, jpeg[offset]);
            while (offset < jpeg.Length && jpeg[offset] == 0xFF)
                offset++;
            Assert.True(offset < jpeg.Length, "JPEG ends inside a marker.");
            byte marker = jpeg[offset++];
            if (marker is 0xDA or 0xD9)
                return count;
            if (marker is 0x01 or >= 0xD0 and <= 0xD8)
                continue;

            Assert.True(offset <= jpeg.Length - sizeof(ushort), "JPEG marker has no length.");
            ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset, sizeof(ushort)));
            Assert.True(segmentLength >= sizeof(ushort) && segmentLength <= jpeg.Length - offset,
                "JPEG segment exceeds the fixture boundary.");
            int payloadOffset = offset + sizeof(ushort);
            int payloadLength = segmentLength - sizeof(ushort);
            if (marker == 0xE2 && payloadLength >= IccJpegSignature.Length &&
                jpeg.AsSpan(payloadOffset, IccJpegSignature.Length).SequenceEqual(IccJpegSignature))
                count++;
            offset = checked(offset + segmentLength);
        }
        return count;
    }

    private static async Task<byte[]> ReadPinnedWebpFixtureAsync()
    {
        using Stream fixture = typeof(OwnedNativeWicColorMetadataFixtureTests).Assembly
            .GetManifestResourceStream(WebpFixtureResource)
            ?? throw new InvalidOperationException("The pinned WebP fixture is missing.");
        using var bytes = new MemoryStream();
        await fixture.CopyToAsync(bytes);
        byte[] source = bytes.ToArray();
        Assert.Equal(51_112, source.Length);
        Assert.Equal(WebpFixtureSha256,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)));
        return source;
    }

    private static int ReadWebpUInt24LittleEndian(byte[] bytes, int offset) =>
        bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;

    private readonly record struct PngChunk(
        string Type, int Offset, int DataOffset, int DataLength, int TotalLength);
}
