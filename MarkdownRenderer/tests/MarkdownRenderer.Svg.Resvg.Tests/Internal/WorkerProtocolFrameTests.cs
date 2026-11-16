using System.Buffers.Binary;
using System.Text;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal;

/// <summary>
/// Pins the worker protocol's request and response frames: request ids,
/// reserved fields, the font-catalog pending status, and which operations
/// carry a mapping handle.
/// </summary>
public sealed class WorkerProtocolFrameTests
{
    [Fact]
    public void ProtocolDecoder_RejectsWrongRequestIdAndMalformedReservedData()
    {
        byte[] response = new byte[WorkerProtocol.ResponseSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(response, WorkerProtocol.Magic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(4), WorkerProtocol.Version);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(8), 42);
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.DecodeResponse(response, 41, 0, 0));

        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(8), 41);
        response[207] = 1;
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.DecodeResponse(response, 41, 0, 0));

        response[207] = 0;
        response[206] = 1;
        Assert.Throws<WorkerProtocolException>(() => WorkerProtocol.DecodeResponse(response, 41, 0, 0));
    }

    [Fact]
    public void ProtocolDecoder_AdmitsNonblockingFontCatalogPending()
    {
        byte[] response = new byte[WorkerProtocol.ResponseSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(response, WorkerProtocol.Magic);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(4), WorkerProtocol.Version);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(6), (ushort)WorkerStatus.FontCatalogPending);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(8), 41);
        byte[] phase = Encoding.UTF8.GetBytes("user-roaming-fonts");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(74), checked((ushort)phase.Length));
        phase.CopyTo(response.AsSpan(80));

        WorkerResponse decoded = WorkerProtocol.DecodeResponse(response, 41, 0, 0);

        Assert.Equal(WorkerStatus.FontCatalogPending, decoded.Status);
        Assert.Equal(0, decoded.OutputLength);
        Assert.Equal("user-roaming-fonts", decoded.Detail);
    }

    [Fact]
    public void RenderProtocol_UsesDocumentTokenAndOutputOnlyMappingHandle()
    {
        var request = new WorkerRequest(
            WorkerOperation.Render,
            7,
            11,
            13,
            0x1020_3040ul,
            new byte[32],
            SourceLength: 0,
            OutputLength: 64,
            TargetWidth: 4,
            TargetHeight: 4,
            Tile: null,
            Locale: "en",
            ColorScheme: MarkdownSvgColorScheme.Light,
            SemanticColor: null,
            PixelFormat: MarkdownSvgPixelFormat.Rgba8Premultiplied,
            FontGeneration: 0,
            ParsedResourceCacheBytes: 1024,
            DocumentId: 0x1020_3040_5060_7080,
            Options: new ResvgMarkdownSvgRendererOptions());

        byte[] frame = WorkerProtocol.Encode(request);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(36)));
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(40)));
        Assert.Equal(0x1020_3040ul, BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(240)));
        Assert.Equal(-1, frame.AsSpan(170, 6).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, frame.AsSpan(248, 120).IndexOfAnyExcept((byte)0));
        Assert.Equal(0x1020_3040_5060_7080ul, BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(368)));
    }

    [Fact]
    public void ProtocolV10RequiresAHandleOnlyForDataAndFontCatalogRequests()
    {
        WorkerRequest CreateRequest(WorkerOperation operation, ulong mappingHandle) => new(
            operation,
            7,
            11,
            13,
            mappingHandle,
            new byte[32],
            SourceLength: operation == WorkerOperation.Open ? 4 : operation == WorkerOperation.FontCatalog ? 32 : 0,
            OutputLength: operation == WorkerOperation.Render ? 64 : 0,
            TargetWidth: operation == WorkerOperation.Render ? 4 : 0,
            TargetHeight: operation == WorkerOperation.Render ? 4 : 0,
            Tile: null,
            Locale: operation == WorkerOperation.FontCatalog ? string.Empty : "en",
            ColorScheme: MarkdownSvgColorScheme.Light,
            SemanticColor: null,
            PixelFormat: MarkdownSvgPixelFormat.Rgba8Premultiplied,
            FontGeneration: 0,
            ParsedResourceCacheBytes: 1024,
            DocumentId: operation is WorkerOperation.Open or WorkerOperation.Render ? 9ul : 0ul,
            Options: new ResvgMarkdownSvgRendererOptions());

        Assert.Equal((ushort)10, WorkerProtocol.Version);
        Assert.Throws<InvalidOperationException>(() => WorkerProtocol.Encode(CreateRequest(WorkerOperation.Open, 0)));
        Assert.Throws<InvalidOperationException>(() => WorkerProtocol.Encode(CreateRequest(WorkerOperation.Render, 0)));
        Assert.Throws<InvalidOperationException>(() => WorkerProtocol.Encode(CreateRequest(WorkerOperation.FontCatalog, 0)));
        Assert.Throws<InvalidOperationException>(() => WorkerProtocol.Encode(CreateRequest(WorkerOperation.Hello, 1)));
        Assert.Throws<InvalidOperationException>(() => WorkerProtocol.Encode(CreateRequest(WorkerOperation.TrimCache, 1)));
        Assert.Throws<InvalidOperationException>(() => WorkerProtocol.Encode(CreateRequest(WorkerOperation.CloseDocument, 1)));
        Assert.Throws<InvalidOperationException>(() => WorkerProtocol.Encode(CreateRequest(WorkerOperation.Render, ulong.MaxValue)));

        byte[] hello = WorkerProtocol.Encode(CreateRequest(WorkerOperation.Hello, 0));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(hello.AsSpan(240)));
        Assert.Equal(-1, hello.AsSpan(170, 6).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, hello.AsSpan(248, 120).IndexOfAnyExcept((byte)0));

        byte[] catalog = WorkerProtocol.Encode(CreateRequest(WorkerOperation.FontCatalog, 0x100));
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(catalog.AsSpan(36)));
        Assert.Equal(0x100ul, BinaryPrimitives.ReadUInt64LittleEndian(catalog.AsSpan(240)));
        Assert.Equal(-1, catalog.AsSpan(40, 80).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, catalog.AsSpan(124, 116).IndexOfAnyExcept((byte)0));
    }
}
