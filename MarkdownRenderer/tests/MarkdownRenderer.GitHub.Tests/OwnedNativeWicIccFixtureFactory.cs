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
/// The profile fields and required tags follow ICC.1:2022-05, especially
/// clauses 7.2–7.4, 8.2, 8.4.3, 9.2, 10.15, 10.18, 10.31, and F.3:
/// https://www.color.org/specification/ICC.1-2022-05.pdf
/// PNG iCCP/gAMA/cHRM/sRGB chunk ordering and CRC follow the W3C PNG Third
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
    private const int IccHeaderSize = 128;
    private const int IccTagRecordSize = 12;
    private const int IccTagCount = 9;
    private const int MaximumPngBytes = 16 * 1024 * 1024;
    private const int MaximumProfileBytes = 1024 * 1024;
    private const double Gamma = 1.8;

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] IccProfileName = Encoding.ASCII.GetBytes("JitHub-Gamma18");

    internal static byte[] CreateGamma18MatrixTrcProfile()
    {
        double[][] colorants = ComputeD50WhiteRgbPrimaryColorants();
        var tags = new[]
        {
            new ProfileTag("bXYZ", CreateXyzTag(colorants[2])),
            new ProfileTag("bTRC", CreateGammaTag()),
            new ProfileTag("cprt", CreateMlucTag("Copyright 2026 JitHub test fixture authors.")),
            new ProfileTag("desc", CreateMlucTag("JitHub Test RGB Matrix Gamma 1.8")),
            new ProfileTag("gXYZ", CreateXyzTag(colorants[1])),
            new ProfileTag("gTRC", CreateGammaTag()),
            new ProfileTag("rXYZ", CreateXyzTag(colorants[0])),
            new ProfileTag("rTRC", CreateGammaTag()),
            new ProfileTag("wtpt", CreateXyzTag([0.9642, 1.0, 0.8249])),
        };

        int tagTableEnd = checked(IccHeaderSize + sizeof(uint) + IccTagRecordSize * tags.Length);
        int[] offsets = new int[tags.Length];
        int profileSize = tagTableEnd;
        for (int index = 0; index < tags.Length; index++)
        {
            profileSize = Align4(profileSize);
            offsets[index] = profileSize;
            profileSize = checked(profileSize + Align4(tags[index].Data.Length));
        }

        byte[] profile = new byte[profileSize];
        WriteHeader(profile, profileSize);
        WriteUInt32(profile, IccHeaderSize, checked((uint)tags.Length));

        for (int index = 0; index < tags.Length; index++)
        {
            int recordOffset = checked(IccHeaderSize + sizeof(uint) + index * IccTagRecordSize);
            WriteAscii(profile, recordOffset, tags[index].Signature);
            WriteUInt32(profile, recordOffset + 4, checked((uint)offsets[index]));
            WriteUInt32(profile, recordOffset + 8, checked((uint)tags[index].Data.Length));
            tags[index].Data.CopyTo(profile, offsets[index]);
        }

        ValidateConstructedProfile(profile, tags, offsets, tagTableEnd);
        return profile;
    }

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

        ValidateConstructedProfile(profile, expectedTagCount: IccTagCount);
        List<PngChunk> chunks = ParsePngChunks(png);
        byte[] iccpChunk = CreateIccpChunk(profile);

        using var output = new MemoryStream(checked(png.Length + iccpChunk.Length));
        output.Write(PngSignature);
        bool insertedProfile = false;
        foreach (PngChunk chunk in chunks)
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
        WriteUInt32(gamma, 0, 55_555);
        byte[] chromaticities = new byte[8 * sizeof(uint)];
        uint[] values =
        [
            31_270, 32_900, // white point x/y
            64_000, 33_000, // red x/y
            30_000, 60_000, // green x/y
            15_000, 6_000,  // blue x/y
        ];
        for (int index = 0; index < values.Length; index++)
            WriteUInt32(chromaticities, index * sizeof(uint), values[index]);

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
        ValidateConstructedProfile(profile, expectedTagCount: IccTagCount);
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
        ValidateConstructedProfile(profile, expectedTagCount: IccTagCount);
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
        List<PngChunk> chunks = ParsePngChunks(png, allowExistingIccp: true);
        using var output = new MemoryStream(png.Length + metadata.Sum(static item => item.Data.Length + 12));
        output.Write(PngSignature);
        bool inserted = false;
        foreach (PngChunk chunk in chunks)
        {
            if (IsCompetingColorMetadata(chunk.Type))
                continue;
            output.Write(png, chunk.Offset, chunk.TotalLength);
            if (chunk.Type == "IHDR" && !inserted)
            {
                foreach ((string type, byte[] data) in metadata)
                    output.Write(CreatePngChunk(type, data));
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

    private static void WriteHeader(byte[] profile, int profileSize)
    {
        WriteUInt32(profile, 0, checked((uint)profileSize));
        // Bytes 4-7: no preferred CMM. Version 4.4.0.0 is BCD: 04 40 00 00.
        profile[8] = 0x04;
        profile[9] = 0x40;
        WriteAscii(profile, 12, "mntr");
        WriteAscii(profile, 16, "RGB ");
        WriteAscii(profile, 20, "XYZ ");

        // Stable valid calendar fields keep the fixture byte-for-byte repeatable.
        WriteUInt16(profile, 24, 2026);
        WriteUInt16(profile, 26, 1);
        WriteUInt16(profile, 28, 1);
        WriteUInt16(profile, 30, 0);
        WriteUInt16(profile, 32, 0);
        WriteUInt16(profile, 34, 0);

        WriteAscii(profile, 36, "acsp");
        WriteAscii(profile, 40, "MSFT");
        // Profile flags, device manufacturer/model/attributes, creator, ID,
        // and reserved fields stay zero. A zero Profile ID means uncomputed.
        WriteUInt32(profile, 64, 0); // perceptual rendering intent
        WriteS15Fixed16(profile, 68, 0.9642);
        WriteS15Fixed16(profile, 72, 1.0);
        WriteS15Fixed16(profile, 76, 0.8249);
    }

    private static byte[] CreateXyzTag(double[] xyz)
    {
        if (xyz.Length != 3)
        {
            throw new ArgumentException("XYZ values must have exactly three components.", nameof(xyz));
        }

        byte[] tag = new byte[20];
        WriteAscii(tag, 0, "XYZ ");
        WriteS15Fixed16(tag, 8, xyz[0]);
        WriteS15Fixed16(tag, 12, xyz[1]);
        WriteS15Fixed16(tag, 16, xyz[2]);
        return tag;
    }

    private static byte[] CreateGammaTag()
    {
        // parametricCurveType, function 0: y = x^gamma.
        byte[] tag = new byte[16];
        WriteAscii(tag, 0, "para");
        WriteUInt16(tag, 8, 0);
        WriteS15Fixed16(tag, 12, Gamma);
        return tag;
    }

    private static byte[] CreateMlucTag(string value)
    {
        byte[] stringBytes = Encoding.BigEndianUnicode.GetBytes(value);
        byte[] tag = new byte[checked(28 + stringBytes.Length)];
        WriteAscii(tag, 0, "mluc");
        WriteUInt32(tag, 8, 1); // one language/region record
        WriteUInt32(tag, 12, 12); // fixed record size
        WriteUInt16(tag, 16, PackLanguageOrCountryCode("en"));
        WriteUInt16(tag, 18, PackLanguageOrCountryCode("US"));
        WriteUInt32(tag, 20, checked((uint)stringBytes.Length));
        WriteUInt32(tag, 24, 28); // string data starts after header + one record
        stringBytes.CopyTo(tag, 28);
        return tag;
    }

    private static double[][] ComputeD50WhiteRgbPrimaryColorants()
    {
        // This fixture models a fictional RGB device using sRGB-like primary
        // chromaticities but a native D50 white. The scale solution makes its
        // columns sum to D50; it is not Bradford adaptation of an sRGB profile,
        // and no chad tag is needed because the device white is already D50.
        double[,] primaryMatrix =
        {
            { 0.64 / 0.33, 0.30 / 0.60, 0.15 / 0.06 },
            { 1.0, 1.0, 1.0 },
            { (1.0 - 0.64 - 0.33) / 0.33, (1.0 - 0.30 - 0.60) / 0.60, (1.0 - 0.15 - 0.06) / 0.06 },
        };
        double[] scale = Solve3x3(primaryMatrix, [0.9642, 1.0, 0.8249]);
        var colorants = new double[3][];
        for (int channel = 0; channel < 3; channel++)
        {
            colorants[channel] =
            [
                primaryMatrix[0, channel] * scale[channel],
                primaryMatrix[1, channel] * scale[channel],
                primaryMatrix[2, channel] * scale[channel],
            ];

            if (colorants[channel].Any(value => !double.IsFinite(value) || value < 0 || value > 1.2))
            {
                throw new InvalidOperationException("Generated D50 colorant fell outside the expected range.");
            }
        }

        return colorants;
    }

    private static double[] Solve3x3(double[,] matrix, double[] rhs)
    {
        double[,] augmented = new double[3, 4];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                augmented[row, column] = matrix[row, column];
            }

            augmented[row, 3] = rhs[row];
        }

        for (int pivot = 0; pivot < 3; pivot++)
        {
            double divisor = augmented[pivot, pivot];
            if (!double.IsFinite(divisor) || System.Math.Abs(divisor) < 1e-12)
            {
                throw new InvalidOperationException("RGB primary matrix is singular.");
            }

            for (int column = pivot; column < 4; column++)
            {
                augmented[pivot, column] /= divisor;
            }

            for (int row = 0; row < 3; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                double factor = augmented[row, pivot];
                for (int column = pivot; column < 4; column++)
                {
                    augmented[row, column] -= factor * augmented[pivot, column];
                }
            }
        }

        return [augmented[0, 3], augmented[1, 3], augmented[2, 3]];
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
        return CreatePngChunk("iCCP", data);
    }

    private static byte[] CreatePngChunk(string type, byte[] data)
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
        WriteUInt32(chunk, chunk.Length - sizeof(uint), ComputePngCrc(typeBytes, data));
        return chunk;
    }

    private static List<PngChunk> ParsePngChunks(byte[] png, bool allowExistingIccp = false)
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
            uint actualCrc = ComputePngCrc(png.AsSpan(offset + 4, 4), png.AsSpan(dataOffset, dataLength));
            if (expectedCrc != actualCrc)
            {
                throw new InvalidDataException($"PNG chunk {type} has an invalid CRC.");
            }

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

                byte bitDepth = png[dataOffset + 8];
                byte colorType = png[dataOffset + 9];
                if (ReadUInt32(png, dataOffset) == 0 || ReadUInt32(png, dataOffset + 4) == 0 ||
                    colorType is not (2 or 3 or 6) ||
                    png[dataOffset + 10] != 0 || png[dataOffset + 11] != 0 || png[dataOffset + 12] > 1 ||
                    !IsAllowedBitDepth(colorType, bitDepth))
                {
                    throw new InvalidDataException("PNG IHDR is invalid for an embedded RGB profile.");
                }

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
                if (dataLength != 0 || !sawImageData || offset + totalLength != png.Length)
                {
                    throw new InvalidDataException("PNG IEND is malformed or out of order.");
                }

                sawEnd = true;
            }

            chunks.Add(new PngChunk(offset, totalLength, type));
            offset = checked(offset + totalLength);
        }

        if (!sawHeader || !sawImageData || !sawEnd)
        {
            throw new InvalidDataException("PNG is missing a required IHDR, IDAT, or IEND chunk.");
        }

        return chunks;
    }

    private static bool IsCompetingColorMetadata(string type) =>
        type is "iCCP" or "sRGB" or "cICP" or "gAMA" or "cHRM";

    private static bool IsAllowedBitDepth(byte colorType, byte bitDepth) => colorType switch
    {
        2 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        6 => bitDepth is 8 or 16,
        _ => false,
    };

    private static uint ComputePngCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in type)
        {
            crc = UpdatePngCrc(crc, value);
        }

        foreach (byte value in data)
        {
            crc = UpdatePngCrc(crc, value);
        }

        return ~crc;
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> type, byte[] data) => ComputePngCrc(type, data.AsSpan());

    private static uint UpdatePngCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return crc;
    }

    private static void ValidateConstructedProfile(
        byte[] profile,
        ProfileTag[] tags,
        int[] offsets,
        int tagTableEnd)
    {
        ValidateConstructedProfile(profile, tags.Length);
        int expectedOffset = tagTableEnd;
        for (int index = 0; index < tags.Length; index++)
        {
            expectedOffset = Align4(expectedOffset);
            if (offsets[index] != expectedOffset || offsets[index] % 4 != 0)
            {
                throw new InvalidOperationException("ICC tag data is not contiguous and four-byte aligned.");
            }

            expectedOffset = checked(expectedOffset + Align4(tags[index].Data.Length));
        }

        if (expectedOffset != profile.Length)
        {
            throw new InvalidOperationException("ICC profile size does not match its padded tag data.");
        }
    }

    private static void ValidateConstructedProfile(byte[] profile, int expectedTagCount)
    {
        if (profile.Length < IccHeaderSize + 4 || ReadUInt32(profile, 0) != checked((uint)profile.Length) ||
            !MatchesAscii(profile, 36, "acsp") || !MatchesAscii(profile, 12, "mntr") ||
            !MatchesAscii(profile, 16, "RGB ") || !MatchesAscii(profile, 20, "XYZ ") ||
            profile[8] != 0x04 || profile[9] != 0x40 || profile[10] != 0 || profile[11] != 0)
        {
            throw new InvalidOperationException("Generated ICC header is invalid.");
        }

        uint tagCount = ReadUInt32(profile, IccHeaderSize);
        if (tagCount != checked((uint)expectedTagCount))
        {
            throw new InvalidOperationException("Generated ICC tag count is invalid.");
        }

        int count = checked((int)tagCount);
        int tableEnd = checked(IccHeaderSize + 4 + count * IccTagRecordSize);
        if (tableEnd > profile.Length)
        {
            throw new InvalidOperationException("Generated ICC tag table extends beyond the profile.");
        }

        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var ranges = new List<(int Offset, int PaddedEnd)>();
        for (int index = 0; index < count; index++)
        {
            int recordOffset = checked(IccHeaderSize + 4 + index * IccTagRecordSize);
            string signature = Encoding.ASCII.GetString(profile, recordOffset, 4);
            int offset = checked((int)ReadUInt32(profile, recordOffset + 4));
            int size = checked((int)ReadUInt32(profile, recordOffset + 8));
            if (!signatures.Add(signature) || offset < tableEnd || offset % 4 != 0 || size < 8 ||
                offset > profile.Length || size > profile.Length - offset ||
                Align4(size) > profile.Length - offset)
            {
                throw new InvalidOperationException("Generated ICC tag table is invalid.");
            }

            int tagEnd = checked(offset + size);
            int paddedEnd = checked(offset + Align4(size));
            string dataType = Encoding.ASCII.GetString(profile, offset, 4);
            bool validTypeAndSize = signature switch
            {
                "desc" or "cprt" => dataType == "mluc" && IsSingleMlucRecordValid(profile, offset, size),
                "wtpt" or "rXYZ" or "gXYZ" or "bXYZ" => dataType == "XYZ " && size == 20,
                "rTRC" or "gTRC" or "bTRC" =>
                    dataType == "para" && size == 16 &&
                    BinaryPrimitives.ReadUInt16BigEndian(profile.AsSpan(offset + 8, 2)) == 0 &&
                    BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(offset + 12, 4)) ==
                    checked((int)System.Math.Round(Gamma * 65536.0, MidpointRounding.AwayFromZero)),
                _ => false,
            };
            if (!validTypeAndSize)
            {
                throw new InvalidOperationException($"Generated ICC tag {signature} has invalid data.");
            }

            for (int padOffset = tagEnd; padOffset < paddedEnd; padOffset++)
            {
                if (profile[padOffset] != 0)
                {
                    throw new InvalidOperationException("ICC tag padding must be zero-filled.");
                }
            }

            ranges.Add((offset, paddedEnd));
        }

        var requiredSignatures = new HashSet<string>(
            ["desc", "cprt", "wtpt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC"],
            StringComparer.Ordinal);
        if (!signatures.SetEquals(requiredSignatures))
        {
            throw new InvalidOperationException("Generated ICC profile omits a required matrix/TRC tag.");
        }

        ranges.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        int expectedOffset = tableEnd;
        foreach ((int offset, int paddedEnd) in ranges)
        {
            expectedOffset = Align4(expectedOffset);
            if (offset != expectedOffset || paddedEnd > profile.Length)
            {
                throw new InvalidOperationException("Generated ICC tag ranges overlap or have gaps.");
            }

            expectedOffset = paddedEnd;
        }

        if (expectedOffset != profile.Length)
        {
            throw new InvalidOperationException("Generated ICC tag padding does not end at the profile boundary.");
        }
    }

    private static bool MatchesAscii(byte[] bytes, int offset, string value) =>
        bytes.AsSpan(offset, value.Length).SequenceEqual(Encoding.ASCII.GetBytes(value));

    private static bool IsSingleMlucRecordValid(byte[] profile, int offset, int size)
    {
        if (size < 28 || ReadUInt32(profile, offset + 8) != 1 || ReadUInt32(profile, offset + 12) != 12)
        {
            return false;
        }

        uint stringLength = ReadUInt32(profile, offset + 20);
        uint stringOffset = ReadUInt32(profile, offset + 24);
        uint unsignedSize = checked((uint)size);
        return stringLength > 0 && (stringLength & 1) == 0 && stringOffset >= 28 &&
            stringOffset <= unsignedSize && stringLength <= unsignedSize - stringOffset;
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, sizeof(uint)));

    private static void WriteAscii(byte[] bytes, int offset, string value)
    {
        byte[] encoded = Encoding.ASCII.GetBytes(value);
        encoded.CopyTo(bytes, offset);
    }

    private static void WriteUInt16(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, sizeof(ushort)), checked((ushort)value));

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, sizeof(uint)), value);

    private static void WriteS15Fixed16(byte[] bytes, int offset, double value)
    {
        double scaled = System.Math.Round(value * 65536.0, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(scaled) || scaled < int.MinValue || scaled > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value does not fit s15Fixed16Number.");
        }

        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset, sizeof(int)), checked((int)scaled));
    }

    private static ushort PackLanguageOrCountryCode(string value)
    {
        if (value.Length != 2 || value.Any(character => character > 0x7F))
        {
            throw new ArgumentException("Language and region codes must contain two ASCII characters.", nameof(value));
        }

        return checked((ushort)((value[0] << 8) | value[1]));
    }

    private readonly record struct ProfileTag(string Signature, byte[] Data);

    private readonly record struct PngChunk(int Offset, int TotalLength, string Type);
}
