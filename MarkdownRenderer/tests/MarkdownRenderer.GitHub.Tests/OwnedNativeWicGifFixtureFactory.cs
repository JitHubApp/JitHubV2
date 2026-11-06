using System.Buffers.Binary;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Creates small, deterministic GIF89a inputs for first-frame decoder parity.
/// These project-generated fixtures intentionally use a simple legal LZW
/// stream: clear-code/literal pairs keep the code width fixed while still
/// exercising the platform GIF codec's palette, transparency, and interlace
/// handling. No external image or decoder is required.
/// </summary>
internal static class OwnedNativeWicGifFixtureFactory
{
    internal static IReadOnlyList<Fixture> CreateCoreMatrix()
    {
        Rgb[] global =
        [
            new(0, 0, 0),
            new(241, 28, 19),
            new(18, 218, 42),
            new(24, 43, 239),
        ];
        Rgb[] local =
        [
            new(0, 0, 0),
            new(18, 221, 213),
            new(226, 24, 207),
            new(246, 220, 23),
        ];

        var fixtures = new List<Fixture>
        {
            Create(
                "gct-full-frame",
                canvasWidth: 5,
                canvasHeight: 4,
                globalPalette: global,
                new Frame(0, 0, 5, 4, Pattern(5, 4), null)),
            Create(
                "gct-offset-transparent-zero",
                canvasWidth: 8,
                canvasHeight: 6,
                globalPalette: global,
                new Frame(1, 2, 4, 3, Pattern(4, 3), null, TransparentIndex: 0)),
            Create(
                "lct-offset-transparent-nonzero",
                canvasWidth: 8,
                canvasHeight: 7,
                globalPalette: global,
                new Frame(2, 1, 3, 4, Pattern(3, 4), local, TransparentIndex: 2)),
            Create(
                "lct-only-full-frame",
                canvasWidth: 4,
                canvasHeight: 4,
                globalPalette: null,
                new Frame(0, 0, 4, 4, Pattern(4, 4), local)),
            Create(
                "gct-interlaced-offset",
                canvasWidth: 9,
                canvasHeight: 8,
                globalPalette: global,
                new Frame(1, 1, 7, 5, Pattern(7, 5), null, Interlaced: true)),
        };

        // The GIF decoder must return frame zero rather than silently selecting
        // the last frame, applying an animation disposal step, or compositing
        // unrelated later-frame pixels onto the transparent logical canvas.
        foreach (int disposal in Enumerable.Range(0, 4))
        {
            fixtures.Add(Create(
                $"animated-disposal-{disposal}",
                canvasWidth: 8,
                canvasHeight: 7,
                globalPalette: global,
                new Frame(1, 2, 5, 4, Pattern(5, 4), null,
                    IncludeGraphicControl: true,
                    DisposalMethod: disposal),
                new Frame(1, 2, 5, 4, Solid(5, 4, index: 3), local,
                    IncludeGraphicControl: true,
                    DisposalMethod: 3)));
        }

        return fixtures;
    }

    internal static Fixture CreateOverFrameLimitStaticPreview()
    {
        Rgb[] palette =
        [
            new(0, 0, 0),
            new(245, 30, 20),
            new(25, 225, 40),
            new(20, 45, 240),
        ];
        Frame[] frames = new Frame[RasterImageFrameLimit + 1];
        for (int index = 0; index < frames.Length; index++)
        {
            // Every later frame is deliberately different. Static preview must
            // still decode just the first 1x1 frame under the existing policy.
            frames[index] = new Frame(
                0, 0, 1, 1,
                [(byte)(index % 4)],
                null,
                IncludeGraphicControl: true,
                DisposalMethod: index % 4);
        }

        return Create("animated-over-frame-limit", 1, 1, palette, frames);
    }

    /// <summary>
    /// Adds the ICC.1 GIF Application Extension to a generated source. ICC.1
    /// Annex B.5 specifies identifier ICCRGBG1, authentication code 012, and
    /// the profile bytes in ordinary GIF data sub-blocks of at most 255 bytes.
    /// https://www.color.org/specification/ICC1v43_2010-12.pdf
    /// </summary>
    internal static Fixture EmbedIccProfile(Fixture fixture, byte[] profile)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Length is <= 0 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(profile));

        int insertionOffset = checked(13 + (fixture.GlobalPalette is null ? 0 : 12));
        if (fixture.Bytes.Length < insertionOffset + 1 ||
            !fixture.Bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8))
        {
            throw new InvalidDataException("The generated GIF does not have the expected bounded header.");
        }

        using var stream = new MemoryStream(checked(fixture.Bytes.Length + profile.Length + 24));
        stream.Write(fixture.Bytes.AsSpan(0, insertionOffset));
        stream.WriteByte(0x21); // Extension introducer.
        stream.WriteByte(0xFF); // Application Extension label.
        stream.WriteByte(11);   // Identifier and authentication code bytes.
        stream.Write("ICCRGBG1"u8);
        stream.Write("012"u8);
        WriteDataSubBlocks(stream, profile);
        stream.Write(fixture.Bytes.AsSpan(insertionOffset));
        return fixture with { Bytes = stream.ToArray() };
    }

    internal const int RasterImageFrameLimit = 60;

    internal static Fixture Create(
        string name,
        int canvasWidth,
        int canvasHeight,
        Rgb[]? globalPalette,
        params Frame[] frames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(frames);
        ValidateDimensions(canvasWidth, canvasHeight);
        if (frames.Length == 0)
            throw new ArgumentException("At least one GIF frame is required.", nameof(frames));
        ValidatePalette(globalPalette, nameof(globalPalette));

        foreach (Frame frame in frames)
        {
            if (frame.Left < 0 || frame.Top < 0 || frame.Width <= 0 || frame.Height <= 0 ||
                frame.Left > canvasWidth || frame.Top > canvasHeight ||
                frame.Width > canvasWidth - frame.Left || frame.Height > canvasHeight - frame.Top)
            {
                throw new ArgumentOutOfRangeException(nameof(frames), "A frame must fit inside the logical canvas.");
            }
            if (frame.PixelIndices.Length != checked(frame.Width * frame.Height))
                throw new ArgumentException("Frame indices must match the descriptor dimensions.", nameof(frames));
            ValidatePalette(frame.LocalPalette, nameof(frames));
            if (frame.GlobalPaletteIsRequired(globalPalette) && globalPalette is null)
                throw new ArgumentException("Each GIF frame needs a global or local color table.", nameof(frames));
            if (frame.PixelIndices.Any(static index => index > 3))
                throw new ArgumentException("The deterministic fixture uses a four-color palette.", nameof(frames));
            if (frame.TransparentIndex is { } transparent && transparent is < 0 or > 3)
                throw new ArgumentOutOfRangeException(nameof(frames), "The transparent index must be within the palette.");
            if (frame.DisposalMethod is < 0 or > 3)
                throw new ArgumentOutOfRangeException(nameof(frames), "GIF disposal methods are 0 through 3.");
        }

        using var stream = new MemoryStream();
        stream.Write("GIF89a"u8);
        WriteUInt16LittleEndian(stream, canvasWidth);
        WriteUInt16LittleEndian(stream, canvasHeight);
        stream.WriteByte(globalPalette is null ? (byte)0x70 : (byte)0xF1);
        stream.WriteByte(0); // Logical-screen background color index.
        stream.WriteByte(0); // Pixel aspect ratio.
        if (globalPalette is not null)
            WritePalette(stream, globalPalette);

        foreach (Frame frame in frames)
        {
            if (frame.IncludeGraphicControl || frame.TransparentIndex is not null || frame.DisposalMethod != 0)
                WriteGraphicControl(stream, frame);

            stream.WriteByte(0x2C); // Image descriptor.
            WriteUInt16LittleEndian(stream, frame.Left);
            WriteUInt16LittleEndian(stream, frame.Top);
            WriteUInt16LittleEndian(stream, frame.Width);
            WriteUInt16LittleEndian(stream, frame.Height);
            byte descriptorFlags = (byte)((frame.Interlaced ? 0x40 : 0) |
                (frame.LocalPalette is null ? 0 : 0x81));
            stream.WriteByte(descriptorFlags);
            if (frame.LocalPalette is not null)
                WritePalette(stream, frame.LocalPalette);

            stream.WriteByte(2); // LZW minimum code size for four palette entries.
            byte[] compressed = EncodeLzwWithLiteralResets(frame.GetVisualPixelOrder());
            WriteDataSubBlocks(stream, compressed);
        }

        stream.WriteByte(0x3B); // Complete file trailer.
        return new Fixture(
            name,
            stream.ToArray(),
            canvasWidth,
            canvasHeight,
            frames,
            globalPalette);
    }

    internal static Frame FullFrame(int width, int height, bool interlaced = false) =>
        new(0, 0, width, height, Pattern(width, height), null, Interlaced: interlaced);

    internal static Frame OffsetFrame(
        int left,
        int top,
        int width,
        int height,
        bool interlaced = false,
        int? transparentIndex = null,
        Rgb[]? localPalette = null) =>
        new(left, top, width, height, Pattern(width, height), localPalette,
            Interlaced: interlaced, TransparentIndex: transparentIndex);

    internal static byte[] Pattern(int width, int height)
    {
        var indices = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                indices[checked(y * width + x)] = (byte)((x + (2 * y)) & 3);
        }
        return indices;
    }

    internal static byte[] Solid(int width, int height, byte index)
    {
        if (index > 3)
            throw new ArgumentOutOfRangeException(nameof(index));
        var indices = new byte[checked(width * height)];
        Array.Fill(indices, index);
        return indices;
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width));
        _ = checked(width * height);
    }

    private static void ValidatePalette(Rgb[]? palette, string argumentName)
    {
        if (palette is not null && palette.Length != 4)
            throw new ArgumentException("GIF fixture palettes must contain exactly four RGB entries.", argumentName);
    }

    private static void WritePalette(Stream stream, Rgb[] palette)
    {
        foreach (Rgb color in palette)
        {
            stream.WriteByte(color.Red);
            stream.WriteByte(color.Green);
            stream.WriteByte(color.Blue);
        }
    }

    private static void WriteGraphicControl(Stream stream, Frame frame)
    {
        stream.WriteByte(0x21);
        stream.WriteByte(0xF9);
        stream.WriteByte(4);
        byte packed = (byte)((frame.DisposalMethod << 2) | (frame.TransparentIndex is null ? 0 : 1));
        stream.WriteByte(packed);
        stream.WriteByte(0); // Delay low byte.
        stream.WriteByte(0); // Delay high byte.
        stream.WriteByte(checked((byte)(frame.TransparentIndex ?? 0)));
        stream.WriteByte(0); // GCE terminator.
    }

    private static byte[] EncodeLzwWithLiteralResets(ReadOnlySpan<byte> indices)
    {
        const int minimumCodeSize = 2;
        const int clearCode = 1 << minimumCodeSize;
        const int endCode = clearCode + 1;
        const int codeWidth = minimumCodeSize + 1;

        using var packed = new MemoryStream();
        int bitBuffer = 0;
        int bitCount = 0;
        void WriteCode(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeWidth;
            while (bitCount >= 8)
            {
                packed.WriteByte((byte)(bitBuffer & 0xFF));
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        foreach (byte index in indices)
        {
            WriteCode(clearCode);
            WriteCode(index);
        }
        WriteCode(endCode);
        if (bitCount > 0)
            packed.WriteByte((byte)(bitBuffer & 0xFF));
        return packed.ToArray();
    }

    private static void WriteDataSubBlocks(Stream stream, ReadOnlySpan<byte> compressed)
    {
        int offset = 0;
        while (offset < compressed.Length)
        {
            int count = System.Math.Min(255, compressed.Length - offset);
            stream.WriteByte(checked((byte)count));
            stream.Write(compressed.Slice(offset, count));
            offset += count;
        }
        stream.WriteByte(0); // Image data sub-block terminator.
    }

    private static void WriteUInt16LittleEndian(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)value));
        stream.Write(bytes);
    }

    private static byte[] ReorderForInterlace(ReadOnlySpan<byte> visualOrder, int width, int height)
    {
        var encoded = new byte[visualOrder.Length];
        int target = 0;
        ReadOnlySpan<(int Start, int Step)> passes = [(0, 8), (4, 8), (2, 4), (1, 2)];
        foreach ((int start, int step) in passes)
        {
            for (int y = start; y < height; y += step)
            {
                visualOrder.Slice(checked(y * width), width).CopyTo(encoded.AsSpan(target));
                target += width;
            }
        }
        if (target != encoded.Length)
            throw new InvalidOperationException("The GIF interlace pass order did not cover every row.");
        return encoded;
    }

    internal sealed record Fixture(
        string Name,
        byte[] Bytes,
        int CanvasWidth,
        int CanvasHeight,
        IReadOnlyList<Frame> Frames,
        Rgb[]? GlobalPalette)
    {
        internal Frame FirstFrame => Frames[0];
        internal int FrameCount => Frames.Count;
    }

    internal sealed record Frame(
        int Left,
        int Top,
        int Width,
        int Height,
        byte[] PixelIndices,
        Rgb[]? LocalPalette,
        bool Interlaced = false,
        bool IncludeGraphicControl = false,
        int DisposalMethod = 0,
        int? TransparentIndex = null)
    {
        internal bool GlobalPaletteIsRequired(Rgb[]? globalPalette) =>
            LocalPalette is null && globalPalette is null;

        internal Rgb[] Palette(Rgb[]? globalPalette) => LocalPalette ?? globalPalette
            ?? throw new InvalidOperationException("The GIF frame has no palette.");

        internal byte[] GetVisualPixelOrder() => Interlaced
            ? ReorderForInterlace(PixelIndices, Width, Height)
            : PixelIndices;
    }

    internal readonly record struct Rgb(byte Red, byte Green, byte Blue);
}
