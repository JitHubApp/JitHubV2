using System.Buffers.Binary;
using MarkdownRenderer.Mermaid;

namespace MarkdownRenderer.Mermaid.Tests;

public sealed class MermaidSceneDecoderTests
{
    [Fact]
    public void Decode_PreCancelledToken_ThrowsOperationCanceledException()
    {
        byte[] buffer = MmirBuilder.CreateMinimal();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MermaidSceneDecoder.Decode(buffer, string.Empty, MermaidRenderBudgets.Default, cancellation.Token));
    }

    [Fact]
    public void MinimalSceneDecodes()
    {
        byte[] buffer = MmirBuilder.CreateMinimal();

        MermaidSceneDecodeResult result = MermaidSceneDecoder.Decode(buffer, string.Empty);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(new MermaidSceneVersion(1, 0), result.Scene!.Version);
        Assert.Equal(new MermaidViewport(0, 0, 640, 480), result.Scene.Viewport);
        Assert.Empty(result.Scene.Commands);
        Assert.Empty(result.Scene.Strings);
    }

    [Fact]
    public void Utf8NativeRangeIsExposedAsUtf16Range()
    {
        byte[] mapping = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(mapping.AsSpan(0), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(mapping.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mapping.AsSpan(8), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(mapping.AsSpan(12), uint.MaxValue);
        byte[] buffer = MmirBuilder.CreateMinimal(new MmirBuilder.Section(9, 1, 20, mapping));

        MermaidSceneDecodeResult result = MermaidSceneDecoder.Decode(buffer, "A😀B");

        Assert.True(result.IsSuccess, result.Error);
        MermaidSceneSourceMapping sourceMapping = Assert.Single(result.Scene!.SourceMappings);
        Assert.Equal(7u, sourceMapping.SourceId);
        Assert.Equal(new MermaidSourceRange(1, 2), sourceMapping.SourceRange);
        Assert.Equal(-1, sourceMapping.SemanticIndex);
    }

    [Fact]
    public void RangeInsideUtf8ScalarIsRejected()
    {
        byte[] mapping = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(mapping.AsSpan(0), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(mapping.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(mapping.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mapping.AsSpan(12), uint.MaxValue);
        byte[] buffer = MmirBuilder.CreateMinimal(new MmirBuilder.Section(9, 1, 20, mapping));

        MermaidSceneDecodeResult result = MermaidSceneDecoder.Decode(buffer, "A😀B");

        Assert.Equal(MermaidSceneDecodeStatus.InvalidSourceMapping, result.Status);
        Assert.Null(result.Scene);
    }

    [Fact]
    public void NonFiniteViewportIsRejected()
    {
        byte[] buffer = MmirBuilder.CreateMinimal();
        int viewportOffset = MmirBuilder.GetSectionOffset(buffer, 1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(viewportOffset + 8), BitConverter.SingleToInt32Bits(float.NaN));

        MermaidSceneDecodeResult result = MermaidSceneDecoder.Decode(buffer, string.Empty);

        Assert.Equal(MermaidSceneDecodeStatus.NonFiniteNumber, result.Status);
    }

    [Fact]
    public void InvalidCommandReferenceIsRejected()
    {
        byte[] command = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(0), 1); // GroupBegin
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(4), 0); // Missing style zero.
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(12), uint.MaxValue);
        byte[] buffer = MmirBuilder.Create(
            MmirBuilder.Viewport,
            MmirBuilder.EmptyStrings,
            new MmirBuilder.Section(5, 1, 32, command));

        MermaidSceneDecodeResult result = MermaidSceneDecoder.Decode(buffer, string.Empty);

        Assert.Equal(MermaidSceneDecodeStatus.InvalidReference, result.Status);
    }

    [Fact]
    public void OverlappingPayloadsAreRejected()
    {
        byte[] mapping = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(mapping.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mapping.AsSpan(12), uint.MaxValue);
        byte[] buffer = MmirBuilder.CreateMinimal(new MmirBuilder.Section(9, 1, 20, mapping));
        int firstOffset = MmirBuilder.GetSectionOffset(buffer, 1);
        int mappingDescriptor = MmirBuilder.GetDescriptorOffset(buffer, 9);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(mappingDescriptor + 4), checked((uint)firstOffset));

        MermaidSceneDecodeResult result = MermaidSceneDecoder.Decode(buffer, string.Empty);

        Assert.Equal(MermaidSceneDecodeStatus.OverlappingSections, result.Status);
    }

    [Fact]
    public void SceneBudgetIsCheckedBeforeDirectoryAllocation()
    {
        byte[] buffer = MmirBuilder.CreateMinimal();
        var budgets = MermaidRenderBudgets.Default with { MaxSceneBytes = buffer.Length - 1 };

        MermaidSceneDecodeResult result = MermaidSceneDecoder.Decode(buffer, string.Empty, budgets);

        Assert.Equal(MermaidSceneDecodeStatus.BudgetExceeded, result.Status);
    }

    [Fact]
    public void SemanticTreeWithSharedDeepTailTraversesEachParentOnce()
    {
        const int semanticCount = 100_000;
        const int maxDepth = 64;
        const int sharedTailLength = maxDepth - 1;
        const int sharedTailStart = semanticCount - sharedTailLength;
        var semantics = new MermaidSemanticItem[semanticCount];
        for (int i = 0; i < semantics.Length; i++)
        {
            int parentIndex = i < sharedTailStart
                ? semanticCount - 1
                : i == sharedTailStart
                    ? -1
                    : i - 1;
            semantics[i] = SemanticItem(parentIndex);
        }

        bool isValid = MermaidSceneDecoder.ValidateSemanticTree(
            semantics,
            maxDepth,
            CancellationToken.None,
            out MermaidSceneDecodeResult? failure,
            out int parentTraversalCount);

        Assert.True(isValid, failure?.Error);
        Assert.Null(failure);
        Assert.Equal(semanticCount, parentTraversalCount);
    }

    [Fact]
    public void SemanticTreeCycleIsRejected()
    {
        MermaidSemanticItem[] semantics =
        [
            SemanticItem(-1),
            SemanticItem(0),
            SemanticItem(3),
            SemanticItem(4),
            SemanticItem(2),
        ];

        bool isValid = MermaidSceneDecoder.ValidateSemanticTree(
            semantics,
            maxDepth: 64,
            CancellationToken.None,
            out MermaidSceneDecodeResult? failure,
            out int parentTraversalCount);

        Assert.False(isValid);
        Assert.Equal(MermaidSceneDecodeStatus.InvalidReference, failure!.Status);
        Assert.InRange(parentTraversalCount, 1, semantics.Length);
    }

    [Theory]
    [InlineData(1_024, true)]
    [InlineData(1_025, false)]
    public void SemanticTreeEnforcesDepthBoundaryWithoutRecursion(int semanticCount, bool expectedValid)
    {
        const int maxDepth = 1_024;
        var semantics = new MermaidSemanticItem[semanticCount];
        for (int i = 0; i < semantics.Length; i++)
        {
            int parentIndex = i + 1 < semantics.Length ? i + 1 : -1;
            semantics[i] = SemanticItem(parentIndex);
        }

        bool isValid = MermaidSceneDecoder.ValidateSemanticTree(
            semantics,
            maxDepth,
            CancellationToken.None,
            out MermaidSceneDecodeResult? failure,
            out int parentTraversalCount);

        Assert.Equal(expectedValid, isValid);
        Assert.InRange(parentTraversalCount, 1, semanticCount);
        if (expectedValid)
        {
            Assert.Null(failure);
            Assert.Equal(semanticCount, parentTraversalCount);
        }
        else
        {
            Assert.Equal(MermaidSceneDecodeStatus.BudgetExceeded, failure!.Status);
        }
    }

    [Fact]
    public void UnknownSectionForCurrentVersionIsRejected()
    {
        byte[] buffer = MmirBuilder.CreateMinimal(new MmirBuilder.Section(99, 0, 0, []));

        MermaidSceneDecodeResult result = MermaidSceneDecoder.Decode(buffer, string.Empty);

        Assert.Equal(MermaidSceneDecodeStatus.InvalidDirectory, result.Status);
    }

    [Fact]
    public void ArbitraryBuffersNeverEscapeFormatExceptions()
    {
        var random = new Random(0x4d4d4952);
        for (int i = 0; i < 2_000; i++)
        {
            var buffer = new byte[random.Next(0, 768)];
            random.NextBytes(buffer);
            Exception? exception = Record.Exception(() => MermaidSceneDecoder.Decode(buffer, "A😀B"));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void VersionOneOneTextMetadataDecodes()
    {
        byte[] command = MmirBuilder.TextCommand(fontFamilyIndex: 1, textFlags: MermaidTextFlags.Italic);
        byte[] buffer = MmirBuilder.Create(
            1,
            MmirBuilder.Viewport,
            MmirBuilder.Strings("Label", "Segoe UI"),
            MmirBuilder.Geometry(10, 20, 16, 600),
            new MmirBuilder.Section(5, 1, 32, command));

        MermaidSceneDecodeResult result = MermaidSceneDecoder.Decode(buffer, string.Empty);

        Assert.True(result.IsSuccess, result.Error);
        MermaidDrawCommand decoded = Assert.Single(result.Scene!.Commands);
        Assert.Equal(1, decoded.FontFamilyIndex);
        Assert.Equal(MermaidTextFlags.Italic, decoded.TextFlags);
        Assert.Equal(4, decoded.GeometryCount);
    }

    [Theory]
    [InlineData(2u, 0u)]
    [InlineData(1u, (uint)(MermaidTextFlags.AnchorMiddle | MermaidTextFlags.AnchorEnd))]
    public void VersionOneOneRejectsCorruptTextMetadata(uint fontFamilyIndex, uint textFlags)
    {
        byte[] command = MmirBuilder.TextCommand(fontFamilyIndex, (MermaidTextFlags)textFlags);
        byte[] buffer = MmirBuilder.Create(
            1,
            MmirBuilder.Viewport,
            MmirBuilder.Strings("Label", "Segoe UI"),
            MmirBuilder.Geometry(10, 20, 16, 400),
            new MmirBuilder.Section(5, 1, 32, command));

        MermaidSceneDecodeResult result = MermaidSceneDecoder.Decode(buffer, string.Empty);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Scene);
        Assert.Contains(result.Status, new[]
        {
            MermaidSceneDecodeStatus.InvalidReference,
            MermaidSceneDecodeStatus.InvalidSection,
        });
    }

    private static MermaidSemanticItem SemanticItem(int parentIndex) => new(
        SourceId: 0,
        Role: MermaidSemanticRole.Group,
        NameIndex: -1,
        DescriptionIndex: -1,
        ParentIndex: parentIndex,
        SourceMappingIndex: -1,
        Flags: MermaidSemanticFlags.None);
}

internal static class MmirBuilder
{
    internal readonly record struct Section(uint Kind, int Count, int Stride, byte[] Payload);

    internal static Section Viewport
    {
        get
        {
            var payload = new byte[16];
            WriteSingle(payload, 0, 0);
            WriteSingle(payload, 4, 0);
            WriteSingle(payload, 8, 640);
            WriteSingle(payload, 12, 480);
            return new Section(1, 1, 16, payload);
        }
    }

    internal static Section EmptyStrings => new(2, 0, 0, []);

    internal static Section Strings(params string[] values)
    {
        using var stream = new MemoryStream();
        var lengthBuffer = new byte[sizeof(uint)];
        foreach (string value in values)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteUInt32LittleEndian(lengthBuffer, checked((uint)bytes.Length));
            stream.Write(lengthBuffer);
            stream.Write(bytes);
            while ((stream.Length & 3) != 0)
                stream.WriteByte(0);
        }
        return new Section(2, values.Length, 0, stream.ToArray());
    }

    internal static Section Geometry(params float[] values)
    {
        var payload = new byte[checked(values.Length * sizeof(float))];
        for (int i = 0; i < values.Length; i++)
            WriteSingle(payload, i * sizeof(float), values[i]);
        return new Section(4, values.Length, sizeof(float), payload);
    }

    internal static byte[] TextCommand(uint fontFamilyIndex, MermaidTextFlags textFlags)
    {
        var command = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(0), (ushort)MermaidDrawOpcode.Text);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(20), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(24), fontFamilyIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(28), (uint)textFlags);
        return command;
    }

    internal static byte[] CreateMinimal(params Section[] additionalSections) =>
        Create([Viewport, EmptyStrings, new Section(5, 0, 32, []), .. additionalSections]);

    internal static byte[] Create(params Section[] sections)
        => Create(0, sections);

    internal static byte[] Create(ushort minor, params Section[] sections)
    {
        const int headerSize = 32;
        const int descriptorSize = 20;
        int directorySize = checked(sections.Length * descriptorSize);
        int cursor = Align4(headerSize + directorySize);
        var offsets = new int[sections.Length];
        for (int i = 0; i < sections.Length; i++)
        {
            offsets[i] = cursor;
            cursor = Align4(checked(cursor + sections[i].Payload.Length));
        }

        var result = new byte[cursor];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0), 0x52494D4D);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), minor);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), checked((uint)result.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), checked((uint)sections.Length));

        for (int i = 0; i < sections.Length; i++)
        {
            int descriptor = headerSize + (i * descriptorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(descriptor), sections[i].Kind);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(descriptor + 4), checked((uint)offsets[i]));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(descriptor + 8), checked((uint)sections[i].Payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(descriptor + 12), checked((uint)sections[i].Count));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(descriptor + 16), checked((uint)sections[i].Stride));
            sections[i].Payload.CopyTo(result, offsets[i]);
        }

        return result;
    }

    internal static int GetSectionOffset(byte[] buffer, uint kind)
    {
        int descriptor = GetDescriptorOffset(buffer, kind);
        return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(descriptor + 4)));
    }

    internal static int GetDescriptorOffset(byte[] buffer, uint kind)
    {
        int directoryOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16)));
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20)));
        for (int i = 0; i < count; i++)
        {
            int descriptor = directoryOffset + (i * 20);
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(descriptor)) == kind)
            {
                return descriptor;
            }
        }

        throw new InvalidOperationException("Section was not found.");
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static void WriteSingle(byte[] target, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset), BitConverter.SingleToInt32Bits(value));
}
