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

        bool isValid = MermaidSceneSemanticValidator.ValidateSemanticTree(
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

        bool isValid = MermaidSceneSemanticValidator.ValidateSemanticTree(
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

        bool isValid = MermaidSceneSemanticValidator.ValidateSemanticTree(
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
