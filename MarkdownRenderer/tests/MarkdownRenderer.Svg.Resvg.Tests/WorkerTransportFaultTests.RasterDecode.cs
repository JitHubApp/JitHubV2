using System.Buffers.Binary;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed partial class WorkerTransportFaultTests
{
    [Theory]
    [InlineData(TransportFault.WrongRequestId)]
    [InlineData(TransportFault.MalformedResponse)]
    [InlineData(TransportFault.PartialResponse)]
    [InlineData(TransportFault.PipeLoss)]
    public async Task RasterDecodeFaultRetiresWorkerBeforeBorrowedSectionIsReleased(TransportFault fault)
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        byte[] source = [0x52, 0x49, 0x46, 0x46];
        using var memory = new SharedMemoryLease("raster-fault", source.AsMemory(), 16, fixture.Worker.ObjectSecurity);
        memory.GetSpan().Fill(0x6D);
        Task<WorkerResponse> exchange = fixture.Worker.ExchangeAsync(CreateRasterRequest(),
            TimeSpan.FromSeconds(5), CancellationToken.None, memory).AsTask();
        byte[] request = await ReadRequestAsync(fixture.Peer);
        Assert.Equal((ushort)WorkerOperation.RasterDecode, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6)));
        ulong remoteHandle = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(240));
        Assert.NotEqual(0ul, remoteHandle);
        if (fault is TransportFault.WrongRequestId or TransportFault.MalformedResponse)
        {
            byte[] response = CreateResponse(request);
            if (fault == TransportFault.WrongRequestId)
                BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(8), 0);
            else
                response[73] = 0x80;
            await fixture.Peer.WriteAsync(response);
            await fixture.Peer.FlushAsync();
        }
        else if (fault == TransportFault.PartialResponse)
        {
            await fixture.Peer.WriteAsync(CreateResponse(request).AsMemory(0, 31));
            await fixture.Peer.FlushAsync();
            fixture.Peer.Dispose();
        }
        else
        {
            fixture.Peer.Dispose();
        }
        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await exchange.WaitAsync(TimeSpan.FromSeconds(8)));
        await fixture.Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        AssertRemoteHandleUnavailable(fixture.InspectionProcess, remoteHandle);
        Assert.True(fixture.Child.HasExited);
        Assert.Equal((byte)0x6D, memory.GetSpan()[0]);
        Assert.Equal(16, memory.Memory.Length); // The source prefix is never exposed as pixels.
    }

    [Fact]
    public async Task RasterDecodeDeadlineRetiresWorkerAndReclaimsRemoteMapping()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        using var memory = new SharedMemoryLease("raster-deadline", new byte[4].AsMemory(), 16,
            fixture.Worker.ObjectSecurity);
        Task<WorkerResponse> exchange = fixture.Worker.ExchangeAsync(CreateRasterRequest(),
            TimeSpan.FromMilliseconds(150), CancellationToken.None, memory).AsTask();
        byte[] request = await ReadRequestAsync(fixture.Peer);
        ulong remoteHandle = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(240));
        await Assert.ThrowsAsync<WorkerDeadlineException>(async () =>
            await exchange.WaitAsync(TimeSpan.FromSeconds(8)));
        await fixture.Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Child.HasExited);
        AssertRemoteHandleUnavailable(fixture.InspectionProcess, remoteHandle);
    }

    private static WorkerRequest CreateRasterRequest() => new(WorkerOperation.RasterDecode,
        73, 0x1122_3344_5566_7788, 0x8877_6655_4433_2211, 0, new byte[32], 4, 16, 2, 2,
        null, string.Empty, MarkdownSvgColorScheme.Light, null, MarkdownSvgPixelFormat.Bgra8Premultiplied,
        0, 0, 0, new ResvgMarkdownSvgRendererOptions(), RasterWorkingBytes: 1024,
        RasterSourceWidth: 2, RasterSourceHeight: 2, RasterOrientation: 1);
}
