using System.Buffers.Binary;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed partial class WorkerTransportFaultTests
{
    [Fact]
    public async Task FontCatalogSupplyCoalescesAndCallerCancellationDoesNotCancelSharedTransfer()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        WorkerRequest request = FontCatalogRequest(fixture.Worker, 0);
        Task first = fixture.Worker.EnsureFontCatalogSuppliedAsync(request, CancellationToken.None);
        Task second = fixture.Worker.EnsureFontCatalogSuppliedAsync(request, CancellationToken.None);
        Assert.Same(first, second);
        using var canceledCaller = new CancellationTokenSource();
        Task abandonedWait = fixture.Worker.EnsureFontCatalogSuppliedAsync(request, canceledCaller.Token);

        byte[] frame = await ReadRequestAsync(fixture.Peer);
        Assert.Equal((ushort)WorkerOperation.FontCatalog, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)));
        Assert.Equal(-1, frame.AsSpan(32, 4).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, frame.AsSpan(40, 80).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, frame.AsSpan(124, 116).IndexOfAnyExcept((byte)0));
        Assert.Equal(-1, frame.AsSpan(248).IndexOfAnyExcept((byte)0));
        ulong tableHandle = BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(240));
        byte[] table = ReadRemoteFontCatalog(fixture.InspectionProcess, tableHandle,
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(36))));
        Assert.Equal(0x544E464Du, BinaryPrimitives.ReadUInt32LittleEndian(table));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(4)));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(8));
        Assert.InRange(count, 1u, 4096u);
        Assert.Equal(16 + checked((int)count * 16), table.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(12)));
        ulong firstFontHandle = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(16));
        ulong lastFontHandle = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(table.Length - 16));
        using var tableIdentity = new SafeFileHandle(DuplicateRemoteHandle(fixture.InspectionProcess, tableHandle), ownsHandle: true);
        using var firstFontIdentity = new SafeFileHandle(DuplicateRemoteHandle(fixture.InspectionProcess, firstFontHandle), ownsHandle: true);
        using var lastFontIdentity = new SafeFileHandle(DuplicateRemoteHandle(fixture.InspectionProcess, lastFontHandle), ownsHandle: true);

        canceledCaller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandonedWait);
        Assert.False(first.IsCompleted);
        Assert.False(fixture.Child.HasExited);
        await fixture.Peer.WriteAsync(FontCatalogResponse(frame));
        await fixture.Peer.FlushAsync();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        AssertRemoteHandleClosed(fixture.InspectionProcess, tableHandle, tableIdentity);
        AssertRemoteHandleClosed(fixture.InspectionProcess, firstFontHandle, firstFontIdentity);
        AssertRemoteHandleClosed(fixture.InspectionProcess, lastFontHandle, lastFontIdentity);
        Assert.False(fixture.Child.HasExited);
        Assert.Equal(-1, fixture.Worker.ReadyFontGeneration); // Mapping ACK is not font readiness.
        Assert.Same(first, fixture.Worker.EnsureFontCatalogSuppliedAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task BusyCatalogDoesNotRecreateFontSectionsAndReleasesThemAfterAcceptance()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        Task supply = fixture.Worker.EnsureFontCatalogSuppliedAsync(
            FontCatalogRequest(fixture.Worker, 1), CancellationToken.None);
        byte[] firstFrame = await ReadRequestAsync(fixture.Peer);
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(firstFrame.AsSpan(36)));
        byte[] firstTable = ReadRemoteFontCatalog(fixture.InspectionProcess,
            BinaryPrimitives.ReadUInt64LittleEndian(firstFrame.AsSpan(240)), length);
        ulong fontHandle = BinaryPrimitives.ReadUInt64LittleEndian(firstTable.AsSpan(16));
        using var fontIdentity = new SafeFileHandle(DuplicateRemoteHandle(fixture.InspectionProcess, fontHandle), ownsHandle: true);
        byte[] busy = FontCatalogResponse(firstFrame, WorkerStatus.FontCatalogPending);
        "font-catalog-busy"u8.CopyTo(busy.AsSpan(80));
        BinaryPrimitives.WriteUInt16LittleEndian(busy.AsSpan(74), 17);
        await fixture.Peer.WriteAsync(busy);
        await fixture.Peer.FlushAsync();

        byte[] probe = await ReadRequestAsync(fixture.Peer);
        Assert.Equal((ushort)WorkerOperation.Hello, BinaryPrimitives.ReadUInt16LittleEndian(probe.AsSpan(6)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(36)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(probe.AsSpan(240)));
        await fixture.Peer.WriteAsync(FontCatalogResponse(probe, WorkerStatus.FontCatalogPending));
        await fixture.Peer.FlushAsync();

        byte[] nextFrame = await ReadRequestAsync(fixture.Peer);
        byte[] nextTable = ReadRemoteFontCatalog(fixture.InspectionProcess,
            BinaryPrimitives.ReadUInt64LittleEndian(nextFrame.AsSpan(240)), length);
        Assert.Equal(firstTable, nextTable); // Same read-only font handles, not a fresh transfer.
        Assert.False(supply.IsCompleted);
        await fixture.Peer.WriteAsync(FontCatalogResponse(nextFrame));
        await fixture.Peer.FlushAsync();
        await supply.WaitAsync(TimeSpan.FromSeconds(5));
        AssertRemoteHandleClosed(fixture.InspectionProcess, fontHandle, fontIdentity);
        Assert.False(fixture.Child.HasExited);
    }

    [Fact]
    public async Task MalformedFontCatalogAcknowledgementRetiresWorkerBeforeTransferIsReleased()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        Task supply = fixture.Worker.EnsureFontCatalogSuppliedAsync(
            FontCatalogRequest(fixture.Worker, 0), CancellationToken.None);
        byte[] frame = await ReadRequestAsync(fixture.Peer);
        ulong tableHandle = BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(240));
        byte[] response = FontCatalogResponse(frame);
        response[73] = 1; // Valid protocol metadata, but invalid for a catalog ACK.
        await fixture.Peer.WriteAsync(response);
        await fixture.Peer.FlushAsync();

        await Assert.ThrowsAsync<WorkerInitializationException>(async () => await supply.WaitAsync(TimeSpan.FromSeconds(8)));
        await fixture.Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        AssertRemoteHandleUnavailable(fixture.InspectionProcess, tableHandle);
    }

    [Fact]
    public async Task AsyncDisposalDrainsUnacknowledgedFontTransfer()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        Task supply = fixture.Worker.EnsureFontCatalogSuppliedAsync(
            FontCatalogRequest(fixture.Worker, 0), CancellationToken.None);
        byte[] frame = await ReadRequestAsync(fixture.Peer);
        ulong tableHandle = BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(240));

        await fixture.Worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(supply.IsCompleted);
        await Assert.ThrowsAnyAsync<Exception>(async () => await supply);
        await fixture.Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        AssertRemoteHandleUnavailable(fixture.InspectionProcess, tableHandle);
    }

    [Fact]
    public async Task EnvironmentalFontFailureIsCachedUntilGenerationChanges()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        WorkerRequest request = FontCatalogRequest(fixture.Worker, 0);
        Task initial = fixture.Worker.EnsureFontCatalogSuppliedAsync(request, CancellationToken.None);
        byte[] firstFrame = await ReadRequestAsync(fixture.Peer);
        await fixture.Peer.WriteAsync(FontCatalogResponse(firstFrame, WorkerStatus.FontCatalogFailure));
        await fixture.Peer.FlushAsync();
        await Assert.ThrowsAsync<WorkerInitializationException>(async () => await initial);
        Assert.Same(initial, fixture.Worker.EnsureFontCatalogSuppliedAsync(request, CancellationToken.None));
        Assert.False(fixture.Child.HasExited);

        Task next = fixture.Worker.EnsureFontCatalogSuppliedAsync(
            FontCatalogRequest(fixture.Worker, 1), CancellationToken.None);
        byte[] nextFrame = await ReadRequestAsync(fixture.Peer);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(nextFrame.AsSpan(120)));
        await fixture.Peer.WriteAsync(FontCatalogResponse(nextFrame));
        await fixture.Peer.FlushAsync();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await fixture.Worker.EnsureFontCatalogSuppliedAsync(request, CancellationToken.None));
    }

    private static WorkerRequest FontCatalogRequest(WindowsWorkerProcess worker, long generation) => new(
        WorkerOperation.FontCatalog, checked((ulong)generation + 100), worker.NonceLow, worker.NonceHigh,
        0, new byte[32], 0, 0, 0, 0, null, string.Empty, MarkdownSvgColorScheme.Light, null,
        MarkdownSvgPixelFormat.Rgba8Premultiplied, generation, 64 * 1024 * 1024, 0,
        new ResvgMarkdownSvgRendererOptions());

    private static byte[] FontCatalogResponse(ReadOnlySpan<byte> request, WorkerStatus status = WorkerStatus.Ok)
    {
        byte[] response = CreateResponse(request);
        response.AsSpan(32, 40).Clear();
        response[72] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(6), (ushort)status);
        return response;
    }

    private static byte[] ReadRemoteFontCatalog(Microsoft.Win32.SafeHandles.SafeProcessHandle worker,
        ulong remoteHandle, int length)
    {
        nint local = DuplicateRemoteHandle(worker, remoteHandle);
        try
        {
            nint writable = MapViewOfFile(local, FileMapWrite, 0, 0, checked((nuint)length));
            if (writable != 0)
                UnmapViewOfFile(writable);
            Assert.Equal(nint.Zero, writable);
            nint view = MapViewOfFile(local, FileMapRead, 0, 0, checked((nuint)length));
            Assert.NotEqual(nint.Zero, view);
            try
            {
                byte[] bytes = new byte[length];
                Marshal.Copy(view, bytes, 0, length);
                return bytes;
            }
            finally { Assert.True(UnmapViewOfFile(view)); }
        }
        finally { Assert.True(CloseHandle(local)); }
    }
}
