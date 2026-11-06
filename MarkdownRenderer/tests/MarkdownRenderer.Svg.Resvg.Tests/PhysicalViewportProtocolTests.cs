using System.Buffers.Binary;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class PhysicalViewportProtocolTests
{
    [Fact]
    public void ProtocolV10CarriesExactViewportWithoutChangingIntegerAllocation()
    {
        WorkerRequest request = RenderRequest(new MarkdownSvgPhysicalViewport(1112.5, 197.5));

        byte[] frame = WorkerProtocol.Encode(request);

        Assert.Equal((ushort)10, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(32)) & 4u);
        Assert.Equal(1113u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(44)));
        Assert.Equal(198u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(48)));
        Assert.Equal(1112.5, BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(376))));
        Assert.Equal(197.5, BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(384))));
        Assert.Equal(-1, frame.AsSpan(392).IndexOfAnyExcept((byte)0));
    }

    [Fact]
    public void AbsentViewportLeavesNewWireFieldsZeroAndPreservesLegacyScaleMode()
    {
        byte[] frame = WorkerProtocol.Encode(RenderRequest(physicalViewport: null));

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(32)) & 4u);
        Assert.Equal(-1, frame.AsSpan(376, 16).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, frame.AsSpan(392).IndexOfAnyExcept((byte)0));
    }

    [Fact]
    public void PhysicalViewportCannotBeAddedToOpenOrControlOperations()
    {
        WorkerRequest open = ControlOrOpenRequest(WorkerOperation.Open) with
        {
            PhysicalViewport = new MarkdownSvgPhysicalViewport(4, 4),
        };
        WorkerRequest hello = ControlOrOpenRequest(WorkerOperation.Hello) with
        {
            PhysicalViewport = new MarkdownSvgPhysicalViewport(4, 4),
        };

        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Encode(open));
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.Encode(hello));
    }

    [Fact]
    public void EncoderRejectsNonFiniteAndMismatchedViewportValues()
    {
        foreach (MarkdownSvgPhysicalViewport viewport in new[]
        {
            new MarkdownSvgPhysicalViewport(double.NaN, 197.5),
            new MarkdownSvgPhysicalViewport(1112.5, double.PositiveInfinity),
            new MarkdownSvgPhysicalViewport(1112, 197.5),
            new MarkdownSvgPhysicalViewport(1112.5, 198.1),
            new MarkdownSvgPhysicalViewport(-1, 197.5),
        })
        {
            Assert.Throws<WorkerProtocolException>(() =>
                WorkerProtocol.Encode(RenderRequest(viewport)));
        }
    }

    private static WorkerRequest RenderRequest(MarkdownSvgPhysicalViewport? physicalViewport) => new(
        WorkerOperation.Render,
        RequestId: 7,
        NonceLow: 11,
        NonceHigh: 13,
        MappingHandle: 0x1020_3040,
        ContentHash: new byte[32],
        SourceLength: 0,
        OutputLength: 1113 * 198 * 4,
        TargetWidth: 1113,
        TargetHeight: 198,
        Tile: null,
        Locale: "en",
        ColorScheme: MarkdownSvgColorScheme.Light,
        SemanticColor: null,
        PixelFormat: MarkdownSvgPixelFormat.Rgba8Premultiplied,
        FontGeneration: 0,
        ParsedResourceCacheBytes: 1024,
        DocumentId: 0x1020_3040_5060_7080,
        Options: new ResvgMarkdownSvgRendererOptions(),
        PhysicalViewport: physicalViewport);

    private static WorkerRequest ControlOrOpenRequest(WorkerOperation operation) => new(
        operation,
        RequestId: 7,
        NonceLow: 11,
        NonceHigh: 13,
        MappingHandle: operation == WorkerOperation.Open ? 0x1020_3040ul : 0ul,
        ContentHash: new byte[32],
        SourceLength: operation == WorkerOperation.Open ? 4 : 0,
        OutputLength: 0,
        TargetWidth: 0,
        TargetHeight: 0,
        Tile: null,
        Locale: operation == WorkerOperation.Open ? "en" : string.Empty,
        ColorScheme: MarkdownSvgColorScheme.Light,
        SemanticColor: null,
        PixelFormat: MarkdownSvgPixelFormat.Rgba8Premultiplied,
        FontGeneration: 0,
        ParsedResourceCacheBytes: 1024,
        DocumentId: operation == WorkerOperation.Open ? 9ul : 0ul,
        Options: new ResvgMarkdownSvgRendererOptions());
}
