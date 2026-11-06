using System.Buffers.Binary;
using System.Text;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Writes and validates the self-authored ICC v4.4 RGB matrix/TRC display
/// profile that <see cref="OwnedNativeWicIccFixtureFactory"/> embeds in
/// PNG, JPEG and WebP fixtures.
/// </summary>
/// <remarks>
/// The profile fields and required tags follow ICC.1:2022-05, especially
/// clauses 7.2–7.4, 8.2, 8.4.3, 9.2, 10.15, 10.18, 10.31, and F.3:
/// https://www.color.org/specification/ICC.1-2022-05.pdf
/// The RGB primaries, D50 white, and gamma-1.8 transfer curves are encoded
/// directly from the standard's declared data structures.
/// </remarks>
internal static class OwnedNativeWicIccProfile
{
    internal const int TagCount = 9;
    private const int IccHeaderSize = 128;
    private const int IccTagRecordSize = 12;
    private const double Gamma = 1.8;

    internal static byte[] CreateGamma18MatrixTrc()
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

    internal static void Validate(byte[] profile) =>
        ValidateConstructedProfile(profile, expectedTagCount: TagCount);

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
            ranges.Add(ValidateTagRecord(profile, index, tableEnd, signatures));

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

    // Validates tag record index (unique signature, bounded aligned range,
    // expected data type and size, zero padding) and returns its padded range.
    private static (int Offset, int PaddedEnd) ValidateTagRecord(
        byte[] profile,
        int index,
        int tableEnd,
        HashSet<string> signatures)
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

        return (offset, paddedEnd);
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
}
