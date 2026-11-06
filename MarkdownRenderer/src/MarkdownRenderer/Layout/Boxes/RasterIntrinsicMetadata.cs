using System;
using System.Buffers.Binary;
using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Small, source-cache-owned raster dimensions which are safe to use before
/// WIC has produced the authoritative decoded image.
/// </summary>
internal readonly struct RasterIntrinsicMetadata(int Width, int Height) : IEquatable<RasterIntrinsicMetadata>
{
    public int Width { get; init; } = Width;
    public int Height { get; init; } = Height;

    public bool Equals(RasterIntrinsicMetadata other) => Width == other.Width && Height == other.Height;

    public override bool Equals(object? obj) => obj is RasterIntrinsicMetadata other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Width, Height);

    public static bool operator ==(RasterIntrinsicMetadata left, RasterIntrinsicMetadata right) => left.Equals(right);

    public static bool operator !=(RasterIntrinsicMetadata left, RasterIntrinsicMetadata right) => !left.Equals(right);

    internal static bool TryRead(
        ReadOnlySpan<byte> bytes,
        out RasterIntrinsicMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        metadata = default;
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.IsEmpty || bytes.Length > RasterImageResourceBudget.MaxInputBytes)
            return false;

        // The budget parser is deliberately the first authority for accepted
        // dimensions. This parser only refines display axes from bounded EXIF
        // metadata; it never changes source/pixel admission.
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(bytes);
        return TryReadValidated(bytes, budget, out metadata, out _, cancellationToken);
    }

    /// <summary>
    /// Refines validated source dimensions with bounded orientation metadata.
    /// The caller must pass the result produced by validating these exact bytes;
    /// this method deliberately avoids repeating the full source-budget scan.
    /// </summary>
    internal static bool TryReadValidated(
        ReadOnlySpan<byte> bytes,
        RasterImageBudgetResult validatedBudget,
        out RasterIntrinsicMetadata metadata,
        out int orientation,
        CancellationToken cancellationToken = default)
    {
        metadata = default;
        orientation = 1;
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.IsEmpty || bytes.Length > RasterImageResourceBudget.MaxInputBytes)
            return false;

        ArgumentNullException.ThrowIfNull(validatedBudget);
        if (!validatedBudget.Accepted && !validatedBudget.CanRenderStaticPreview)
            return false;

        if (validatedBudget.Width <= 0 || validatedBudget.Height <= 0 ||
            validatedBudget.Width > RasterImageResourceBudget.MaxDimension ||
            validatedBudget.Height > RasterImageResourceBudget.MaxDimension ||
            validatedBudget.FrameCount <= 0)
        {
            return false;
        }

        int parsedOrientation = 1;
        bool valid = validatedBudget.Format switch
        {
            "PNG" => TryReadPngOrientation(bytes, cancellationToken, out parsedOrientation),
            "JPEG" => TryReadJpegOrientation(bytes, cancellationToken, out parsedOrientation),
            "WEBP" when validatedBudget.FrameCount == 1 =>
                TryReadWebPOrientation(bytes, cancellationToken, out parsedOrientation),
            "WEBP" => false,
            "GIF" => true,
            "BMP" => true,
            // AVIF item transforms and ICO frame choice require decoder-specific
            // interpretation. Do not invent an intrinsic size for either.
            _ => false,
        };
        if (!valid || parsedOrientation is < 1 or > 8)
            return false;

        orientation = parsedOrientation;
        bool swapAxes = orientation is 5 or 6 or 7 or 8;
        int width = swapAxes ? validatedBudget.Height : validatedBudget.Width;
        int height = swapAxes ? validatedBudget.Width : validatedBudget.Height;
        if (width <= 0 || height <= 0 ||
            width > RasterImageResourceBudget.MaxDimension ||
            height > RasterImageResourceBudget.MaxDimension)
        {
            return false;
        }

        metadata = new RasterIntrinsicMetadata(width, height);
        return true;
    }

    private static bool TryReadPngOrientation(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken,
        out int orientation)
    {
        orientation = 1;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(signature))
            return false;

        int offset = 8;
        bool firstChunk = true;
        bool sawEnd = false;
        bool sawExif = false;
        bool sawImageData = false;
        while (offset <= bytes.Length - 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint lengthValue = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (lengthValue > int.MaxValue)
                return false;
            int length = (int)lengthValue;
            int chunkEnd;
            try
            {
                chunkEnd = checked(offset + 12 + length);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (chunkEnd > bytes.Length)
                return false;

            ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4);
            if (firstChunk)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13)
                    return false;
                firstChunk = false;
            }

            if (type.SequenceEqual("eXIf"u8))
            {
                if (sawExif || sawImageData || !RasterTiffOrientationReader.TryReadOrientation(
                    bytes.Slice(offset + 8, length), cancellationToken, out orientation))
                    return false;
                sawExif = true;
            }
            if (type.SequenceEqual("IDAT"u8))
                sawImageData = true;

            if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0)
                    return false;
                sawEnd = true;
                break;
            }

            offset = chunkEnd;
        }

        // A partial PNG prefix is sufficient for the rendering safety budget,
        // but not enough evidence that a later eXIf chunk cannot rotate it.
        // Bytes after a complete IEND are ignored by PNG decoders as well.
        return sawEnd;
    }

    private static bool TryReadJpegOrientation(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken,
        out int orientation)
    {
        orientation = 1;
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            return false;

        bool sawExif = false;
        int offset = 2;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes[offset] != 0xFF)
                return false;
            int fillBytes = 0;
            while (offset < bytes.Length && bytes[offset] == 0xFF)
            {
                if ((fillBytes++ & 0xFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                offset++;
            }
            if (offset >= bytes.Length)
                return false;

            byte marker = bytes[offset++];
            if (marker == 0xDA) // Start of scan: later bytes are entropy-coded data.
                return true;
            if (marker == 0xD9)
                return true;
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;
            if (marker is 0x00 or 0xD8 || offset > bytes.Length - 2)
                return false;

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (segmentLength < 2 || offset > bytes.Length - segmentLength)
                return false;
            int payloadStart = offset + 2;
            int payloadLength = segmentLength - 2;
            int segmentEnd = offset + segmentLength;

            if (marker == 0xE1 && payloadLength >= 6 &&
                bytes.Slice(payloadStart, 6).SequenceEqual("Exif\0\0"u8))
            {
                if (sawExif || !RasterTiffOrientationReader.TryReadOrientation(
                    bytes.Slice(payloadStart + 6, payloadLength - 6),
                    cancellationToken, out orientation))
                {
                    return false;
                }
                sawExif = true;
            }

            offset = segmentEnd;
        }

        // Do not guess orientation from an incomplete marker stream.
        return false;
    }

    private static bool TryReadWebPOrientation(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken,
        out int orientation)
    {
        orientation = 1;
        if (bytes.Length < 20 || !bytes[..4].SequenceEqual("RIFF"u8) ||
            !bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return false;
        }

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (riffSize != bytes.Length - 8)
            return false;

        bool sawExif = false;
        int imageChunkCount = 0;
        int offset = 12;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset > bytes.Length - 8)
                return false;
            ReadOnlySpan<byte> type = bytes.Slice(offset, 4);
            uint lengthValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            if (lengthValue > int.MaxValue)
                return false;
            int length = (int)lengthValue;
            int paddedLength = length + (length & 1);
            if (paddedLength < length || paddedLength > bytes.Length - (offset + 8))
                return false;
            int payload = offset + 8;

            if (type.SequenceEqual("VP8 "u8) || type.SequenceEqual("VP8L"u8))
            {
                if (++imageChunkCount != 1)
                    return false;
            }
            else if (type.SequenceEqual("ANMF"u8))
                return false;
            else if (type.SequenceEqual("EXIF"u8))
            {
                if (sawExif || imageChunkCount != 1)
                    return false;
                ReadOnlySpan<byte> exif = bytes.Slice(payload, length);
                if (exif.StartsWith("Exif\0\0"u8))
                    exif = exif[6..];
                if (!RasterTiffOrientationReader.TryReadOrientation(exif, cancellationToken, out orientation))
                    return false;
                sawExif = true;
            }

            offset = payload + paddedLength;
        }

        return offset == bytes.Length && imageChunkCount == 1;
    }
}
