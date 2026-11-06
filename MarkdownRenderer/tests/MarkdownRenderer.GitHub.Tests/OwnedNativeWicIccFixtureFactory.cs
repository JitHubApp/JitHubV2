using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Builds a small self-authored ICC v4.4 RGB matrix/TRC display profile and
/// embeds it in PNG iCCP, JPEG APP2 ICC_PROFILE, or WebP VP8X/ICCP metadata.
/// These fixtures exercise WIC profile discovery and color-management behavior,
/// not production color use.
/// </summary>
/// <remarks>
/// <see cref="OwnedNativeWicIccProfile"/> authors the ICC.1:2022-05 profile and
/// <see cref="OwnedNativeWicPngChunks"/> frames PNG chunks. PNG
/// iCCP/gAMA/cHRM/sRGB chunk ordering follows the W3C PNG Third
/// Edition; JPEG APP2 segmentation follows the ICC-in-JPEG marker convention;
/// WebP VP8X/ICCP framing follows the extended WebP container specification:
/// https://www.w3.org/TR/png-3/
/// https://developers.google.com/speed/webp/docs/riff_container
/// No third-party binary profile or profile-generation code is copied here;
/// the RGB primaries, D50 white, and gamma-1.8 transfer curves are encoded
/// directly from the standard's declared data structures.
/// </remarks>
internal static class OwnedNativeWicIccFixtureFactory
{
    private const int MaximumPngBytes = OwnedNativeWicPngChunks.MaximumPngBytes;
    private const int MaximumProfileBytes = 1024 * 1024;

    private static readonly byte[] PngSignature = OwnedNativeWicPngChunks.PngSignature;
    private static readonly byte[] IccProfileName = Encoding.ASCII.GetBytes("JitHub-Gamma18");

    internal static byte[] CreateGamma18MatrixTrcProfile() => OwnedNativeWicIccProfile.CreateGamma18MatrixTrc();

    internal static async Task<byte[]> CreatePatternedPngWithGamma18ProfileAsync(
        int width = 37,
        int height = 23,
        bool withAlphaEdges = false)
    {
        byte[] png = await OwnedNativeWicGeneralFixtureFactory.CreatePatternedPngAsync(
            width,
            height,
            withAlphaEdges);
        return EmbedProfileInPng(png, CreateGamma18MatrixTrcProfile());
    }

    internal static byte[] EmbedProfileInPng(byte[] png, byte[] profile)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentNullException.ThrowIfNull(profile);
        if (png.Length > MaximumPngBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(png), "PNG fixture exceeds the bounded input size.");
        }

        if (profile.Length > MaximumProfileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "ICC fixture exceeds the bounded profile size.");
        }

        OwnedNativeWicIccProfile.Validate(profile);
        List<OwnedNativeWicPngChunks.PngChunk> chunks = OwnedNativeWicPngChunks.Parse(png);
        byte[] iccpChunk = CreateIccpChunk(profile);

        using var output = new MemoryStream(checked(png.Length + iccpChunk.Length));
        output.Write(PngSignature);
        bool insertedProfile = false;
        foreach (OwnedNativeWicPngChunks.PngChunk chunk in chunks)
        {
            if (IsCompetingColorMetadata(chunk.Type))
            {
                // iCCP has the highest color-metadata precedence. Do not leave
                // contradictory sRGB/cICP/gAMA/cHRM claims in this fixture.
                continue;
            }

            output.Write(png, chunk.Offset, chunk.TotalLength);
            if (!insertedProfile && chunk.Type == "IHDR")
            {
                // iCCP is constrained to appear before PLTE and IDAT; placing
                // it immediately after IHDR satisfies both ordering rules.
                output.Write(iccpChunk);
                insertedProfile = true;
            }
        }

        if (!insertedProfile)
        {
            throw new InvalidDataException("PNG did not contain its required IHDR chunk.");
        }

        return output.ToArray();
    }

    internal static byte[] EmbedGammaAndChromaticitiesInPng(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);

        // PNG gAMA stores the image encoding exponent scaled by 100,000.
        // Pair it with the standard D65/sRGB primary chromaticities, but do
        // not add iCCP or sRGB: the test must exercise these chunks alone.
        byte[] gamma = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(gamma, 55_555);
        byte[] chromaticities = new byte[8 * sizeof(uint)];
        uint[] values =
        [
            31_270, 32_900, // white point x/y
            64_000, 33_000, // red x/y
            30_000, 60_000, // green x/y
            15_000, 6_000,  // blue x/y
        ];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32BigEndian(chromaticities.AsSpan(index * sizeof(uint), sizeof(uint)), values[index]);

        return EmbedPngColorChunks(png, ("cHRM", chromaticities), ("gAMA", gamma));
    }

    internal static byte[] EmbedSrgbChunkInPng(byte[] png, byte renderingIntent = 0)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (renderingIntent > 3)
            throw new ArgumentOutOfRangeException(nameof(renderingIntent));
        return EmbedPngColorChunks(png, ("sRGB", [renderingIntent]));
    }

    internal static byte[] RemovePngColorMetadata(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        return EmbedPngColorChunks(png);
    }

    internal static byte[] EmbedProfileInJpeg(byte[] jpeg, byte[] profile)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        ArgumentNullException.ThrowIfNull(profile);
        OwnedNativeWicIccProfile.Validate(profile);
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("Input does not have a JPEG SOI marker.");

        byte[] profileSignature = Encoding.ASCII.GetBytes("ICC_PROFILE\0");
        const int sequenceAndCountBytes = 2;
        int segmentLength = checked(sizeof(ushort) + profileSignature.Length + sequenceAndCountBytes + profile.Length);
        if (segmentLength > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(profile), "The project-authored profile does not fit one JPEG APP2 segment.");
        if (ContainsJpegIccApp2(jpeg, profileSignature))
            throw new InvalidDataException("JPEG already contains an ICC_PROFILE APP2 segment.");

        // One segment is sufficient for this small deterministic profile.
        // JPEG APP2's length includes its two-byte length field.
        byte[] app2 = new byte[checked(segmentLength + 2)];
        app2[0] = 0xFF;
        app2[1] = 0xE2;
        BinaryPrimitives.WriteUInt16BigEndian(app2.AsSpan(2, sizeof(ushort)), checked((ushort)segmentLength));
        int cursor = 4;
        profileSignature.CopyTo(app2, cursor);
        cursor += profileSignature.Length;
        app2[cursor++] = 1; // sequence number
        app2[cursor++] = 1; // segment count
        profile.CopyTo(app2, cursor);

        int insertionOffset = GetLeadingApp0SegmentEnd(jpeg);
        byte[] result = new byte[checked(jpeg.Length + app2.Length)];
        jpeg.AsSpan(0, insertionOffset).CopyTo(result);
        app2.CopyTo(result, insertionOffset);
        jpeg.AsSpan(insertionOffset).CopyTo(result.AsSpan(insertionOffset + app2.Length));
        return result;
    }

    internal static byte[] EmbedProfileInSimpleWebp(
        byte[] webp,
        byte[] profile,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(webp);
        ArgumentNullException.ThrowIfNull(profile);
        OwnedNativeWicIccProfile.Validate(profile);
        if (webp.Length < 25 || !webp.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !webp.AsSpan(8, 4).SequenceEqual("WEBP"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(4, sizeof(uint))) != checked((uint)(webp.Length - 8)) ||
            !(webp.AsSpan(12, 4).SequenceEqual("VP8L"u8) || webp.AsSpan(12, 4).SequenceEqual("VP8 "u8)))
        {
            throw new InvalidDataException("Only a bounded simple VP8/VP8L WebP fixture can be wrapped.");
        }
        if (width <= 0 || height <= 0 || width > 0x0100_0000 || height > 0x0100_0000)
            throw new ArgumentOutOfRangeException(nameof(width), "Extended WebP canvas dimensions must fit 24 bits plus one.");

        // The pinned lossless fixture uses one VP8L chunk. Retain it byte for
        // byte and add only the required VP8X+ICCP wrapper, with the ICC flag
        // and canvas dimensions mandated by the WebP extended-container spec.
        uint imageChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(16, sizeof(uint)));
        long paddedImageChunkLength = checked(8L + imageChunkLength + (imageChunkLength & 1));
        if (imageChunkLength > int.MaxValue || paddedImageChunkLength != webp.Length - 12)
            throw new InvalidDataException("The simple WebP fixture must contain exactly one complete image chunk.");
        if (webp.AsSpan(12, 4).SequenceEqual("VP8L"u8) && imageChunkLength < 5)
            throw new InvalidDataException("The lossless WebP chunk is missing its five-byte image header.");
        if (webp.AsSpan(20, 1).SequenceEqual(new byte[] { 0x2F }) && webp.AsSpan(12, 4).SequenceEqual("VP8L"u8))
        {
            uint packedHeader = BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(21, sizeof(uint)));
            int encodedWidth = (int)(packedHeader & 0x3FFF) + 1;
            int encodedHeight = (int)((packedHeader >> 14) & 0x3FFF) + 1;
            if (encodedWidth != width || encodedHeight != height)
                throw new InvalidDataException("The requested WebP canvas does not match the VP8L bitstream.");
        }

        const int vp8xTotalLength = 8 + 10;
        int iccpTotalLength = checked(8 + profile.Length + (profile.Length & 1));
        byte[] result = new byte[checked(webp.Length + vp8xTotalLength + iccpTotalLength)];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, sizeof(uint)), checked((uint)(result.Length - 8)));
        "WEBP"u8.CopyTo(result.AsSpan(8));

        int cursor = 12;
        "VP8X"u8.CopyTo(result.AsSpan(cursor));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(cursor + 4, sizeof(uint)), 10);
        int flags = 0x20; // ICC profile present
        if (webp.AsSpan(12, 4).SequenceEqual("VP8L"u8) && webp.Length >= 25 && webp[20] == 0x2F)
        {
            uint packedHeader = BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(21, sizeof(uint)));
            if (((packedHeader >> 28) & 1) != 0)
                flags |= 0x10; // VP8L alpha is present
        }
        result[cursor + 8] = checked((byte)flags);
        WriteUInt24LittleEndian(result, cursor + 12, width - 1);
        WriteUInt24LittleEndian(result, cursor + 15, height - 1);
        cursor += vp8xTotalLength;

        "ICCP"u8.CopyTo(result.AsSpan(cursor));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(cursor + 4, sizeof(uint)), checked((uint)profile.Length));
        profile.CopyTo(result, cursor + 8);
        cursor += iccpTotalLength;
        webp.AsSpan(12).CopyTo(result.AsSpan(cursor));
        return result;
    }

    private static byte[] EmbedPngColorChunks(byte[] png, params (string Type, byte[] Data)[] metadata)
    {
        if (png.Length > MaximumPngBytes)
            throw new ArgumentOutOfRangeException(nameof(png), "PNG fixture exceeds the bounded input size.");
        // The rewrite removes an existing iCCP as a competing color claim, so
        // parsing for this operation permits the chunk while still validating
        // its bounded framing and CRC. EmbedProfileInPng keeps rejecting an
        // existing iCCP to prevent accidental duplicate profiles.
        List<OwnedNativeWicPngChunks.PngChunk> chunks = OwnedNativeWicPngChunks.Parse(png, allowExistingIccp: true);
        using var output = new MemoryStream(png.Length + metadata.Sum(static item => item.Data.Length + 12));
        output.Write(PngSignature);
        bool inserted = false;
        foreach (OwnedNativeWicPngChunks.PngChunk chunk in chunks)
        {
            if (IsCompetingColorMetadata(chunk.Type))
                continue;
            output.Write(png, chunk.Offset, chunk.TotalLength);
            if (chunk.Type == "IHDR" && !inserted)
            {
                foreach ((string type, byte[] data) in metadata)
                    output.Write(OwnedNativeWicPngChunks.CreateChunk(type, data));
                inserted = true;
            }
        }
        if (!inserted)
            throw new InvalidDataException("PNG did not contain its required IHDR chunk.");
        return output.ToArray();
    }

    private static bool ContainsJpegIccApp2(byte[] jpeg, byte[] profileSignature)
    {
        int offset = 2;
        while (offset < jpeg.Length)
        {
            if (jpeg[offset] != 0xFF)
                throw new InvalidDataException("JPEG header is malformed before its scan data.");
            while (offset < jpeg.Length && jpeg[offset] == 0xFF)
                offset++;
            if (offset >= jpeg.Length)
                throw new InvalidDataException("JPEG ends inside a marker.");
            byte marker = jpeg[offset++];
            if (marker == 0xDA || marker == 0xD9)
                return false;
            if (marker == 0xD8 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;
            if (offset > jpeg.Length - sizeof(ushort))
                throw new InvalidDataException("JPEG marker has no segment length.");
            ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset, sizeof(ushort)));
            if (segmentLength < sizeof(ushort) || segmentLength > jpeg.Length - offset)
                throw new InvalidDataException("JPEG segment exceeds the fixture boundary.");
            int payloadOffset = offset + sizeof(ushort);
            int payloadLength = segmentLength - sizeof(ushort);
            if (marker == 0xE2 && payloadLength >= profileSignature.Length &&
                jpeg.AsSpan(payloadOffset, profileSignature.Length).SequenceEqual(profileSignature))
                return true;
            offset = checked(offset + segmentLength);
        }
        return false;
    }

    private static int GetLeadingApp0SegmentEnd(byte[] jpeg)
    {
        // JFIF requires APP0 immediately after SOI. Preserve a leading APP0
        // segment before the new APP2 ICC marker, using its bounded marker
        // length rather than assuming a fixed JFIF payload size.
        int cursor = 2;
        if (cursor >= jpeg.Length || jpeg[cursor] != 0xFF)
            return 2;

        while (cursor < jpeg.Length && jpeg[cursor] == 0xFF)
            cursor++;
        if (cursor >= jpeg.Length)
            throw new InvalidDataException("JPEG ends inside its leading marker.");

        byte marker = jpeg[cursor++];
        if (marker != 0xE0)
            return 2;
        if (cursor > jpeg.Length - sizeof(ushort))
            throw new InvalidDataException("Leading JPEG APP0 marker has no segment length.");

        ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(cursor, sizeof(ushort)));
        if (segmentLength < sizeof(ushort) || segmentLength > jpeg.Length - cursor)
            throw new InvalidDataException("Leading JPEG APP0 segment exceeds the fixture boundary.");

        return checked(cursor + segmentLength);
    }

    private static void WriteUInt24LittleEndian(byte[] destination, int offset, int value)
    {
        if ((uint)value > 0x00FF_FFFFu)
            throw new ArgumentOutOfRangeException(nameof(value));
        destination[offset] = (byte)value;
        destination[offset + 1] = (byte)(value >> 8);
        destination[offset + 2] = (byte)(value >> 16);
    }

    private static byte[] CreateIccpChunk(byte[] profile)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(profile);
        }

        byte[] compressedProfile = compressed.ToArray();
        byte[] data = new byte[checked(IccProfileName.Length + 2 + compressedProfile.Length)];
        IccProfileName.CopyTo(data, 0);
        data[IccProfileName.Length] = 0;
        data[IccProfileName.Length + 1] = 0; // PNG compression method 0 (zlib)
        compressedProfile.CopyTo(data, IccProfileName.Length + 2);
        return OwnedNativeWicPngChunks.CreateChunk("iCCP", data);
    }

    private static bool IsCompetingColorMetadata(string type) =>
        type is "iCCP" or "sRGB" or "cICP" or "gAMA" or "cHRM";

}
