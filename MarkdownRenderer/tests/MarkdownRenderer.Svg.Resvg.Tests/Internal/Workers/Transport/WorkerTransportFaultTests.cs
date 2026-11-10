using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using MarkdownRenderer.Svg.Resvg.Internal.Fonts;
using MarkdownRenderer.Svg.Resvg.Internal.Workers;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Transport;
using MarkdownRenderer.Svg.Resvg.Tests.TestSupport;
using Microsoft.Win32.SafeHandles;
using Xunit;
using static MarkdownRenderer.Svg.Resvg.Tests.TestSupport.WorkerTransportExchanges;
using static MarkdownRenderer.Svg.Resvg.Tests.TestSupport.WorkerTransportNative;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.Workers.Transport;

public sealed class WorkerTransportFaultTests
{
    [Fact]
    public async Task PoolShutdownWaitsForFontOwnersOfAnAlreadyDetachedWorker()
    {
        var preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchangeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await WorkerTransportFixture.CreateAsync(
            fontCatalogSupplierFactory: host => new WorkerFontCatalogSupplier(
                new BlockingFontCatalogExchangeHost(host, exchangeEntered, preparation.Task)));
        var launcher = new AdoptingWorkerLauncher(fixture.Worker);
        using var pool = new WorkerPool(new ResvgMarkdownSvgRendererOptions(), launcher);
        WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity = fixture.Worker.ObjectSecurity;

        Task? shutdown = null;
        try
        {
            // Warmup adopts the fixture worker as the primary and starts its
            // eager font-catalog preparation, which parks in the exchange.
            await pool.WarmUpAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            await exchangeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, launcher.StartCount);

            pool.InvalidateWorker(fixture.Worker.InstanceId);
            Assert.True(fixture.Child.HasExited);
            Assert.Same(objectSecurity, fixture.Worker.ObjectSecurity);

            // The detached worker no longer serves the pool: the next request
            // asks the launcher for a replacement instead of reusing it.
            await Assert.ThrowsAsync<MarkdownSvgException>(() => pool.WarmUpAsync(CancellationToken.None));
            Assert.Equal(2, launcher.StartCount);

            shutdown = pool.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            preparation.TrySetResult();
        }
        await shutdown!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => _ = fixture.Worker.ObjectSecurity);
    }

    [Fact]
    public async Task AsyncDisposalWaitsForExactWorkerExitAfterOrdinaryDisposal()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync(allowTermination: false);
        fixture.Worker.Dispose();
        Task shutdown = fixture.Worker.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        Assert.False(fixture.Child.HasExited);
        fixture.Child.Kill();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Child.HasExited);
        await fixture.Worker.DisposeAsync();
    }

    [Fact]
    public async Task AsyncDisposalFailsInsteadOfCertifyingAnUnterminatedWorker()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync(allowTermination: false);
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await fixture.Worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.False(fixture.Child.HasExited);
    }

    [Fact]
    public async Task AsyncDisposalCompletesOnlyAfterWorkerHasExited()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        await fixture.Worker.DisposeAsync();
        Assert.True(fixture.Child.HasExited);
    }

    [Fact]
    public async Task SuccessfulExchangeClosesRemoteMappingHandleBeforeReturning()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        using var sharedMemory = new SharedMemoryLease("remote-close", outputLength: 16);
        sharedMemory.GetSpan().Fill(0x4A);
        Task<WorkerResponse> exchange = StartExchange(fixture, sharedMemory, CancellationToken.None);

        byte[] request = await ReadRequestAsync(fixture.Peer);
        ulong remoteHandle = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(240));
        Assert.NotEqual(0ul, remoteHandle);
        using var mappingIdentity = new SafeFileHandle(DuplicateRemoteHandle(fixture.InspectionProcess, remoteHandle), ownsHandle: true);
        AssertRemoteMappingMatches(fixture.InspectionProcess, remoteHandle, sharedMemory.GetSpan());

        await fixture.Peer.WriteAsync(CreateResponse(request));
        await fixture.Peer.FlushAsync();
        WorkerResponse response = await exchange.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkerStatus.Ok, response.Status);
        AssertRemoteHandleClosed(fixture.InspectionProcess, remoteHandle, mappingIdentity);
        Assert.False(fixture.Child.HasExited);
    }

    [Theory]
    [InlineData(TransportFault.WrongRequestId)]
    [InlineData(TransportFault.MalformedResponse)]
    [InlineData(TransportFault.PartialResponse)]
    [InlineData(TransportFault.PipeLoss)]
    public async Task InvalidOrLostTransportRetiresWorkerAndReclaimsMapping(TransportFault fault)
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        using var sharedMemory = new SharedMemoryLease($"fault-{fault}", outputLength: 16);
        sharedMemory.GetSpan().Fill(0x6D);
        Task<WorkerResponse> exchange = StartExchange(fixture, sharedMemory, CancellationToken.None);

        byte[] request = await ReadRequestAsync(fixture.Peer);
        ulong remoteHandle = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(240));
        Assert.NotEqual(0ul, remoteHandle);
        AssertRemoteMappingMatches(fixture.InspectionProcess, remoteHandle, sharedMemory.GetSpan());

        if (fault is TransportFault.WrongRequestId or TransportFault.MalformedResponse)
        {
            byte[] response = CreateResponse(request);
            if (fault == TransportFault.WrongRequestId)
            {
                ulong requestId = BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(8));
                BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(8), checked(requestId + 1));
            }
            else
            {
                response[73] = 0x80;
            }

            await fixture.Peer.WriteAsync(response);
            await fixture.Peer.FlushAsync();
        }
        else if (fault == TransportFault.PartialResponse)
        {
            byte[] response = CreateResponse(request);
            await fixture.Peer.WriteAsync(response.AsMemory(0, 31));
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
        Assert.True(fixture.Child.HasExited);
        AssertRemoteHandleUnavailable(fixture.InspectionProcess, remoteHandle);
        Assert.Equal((byte)0x6D, sharedMemory.GetSpan()[0]);
    }

    [Fact]
    public async Task CallerCancellationDuringHungExchangeRetiresWorkerAndReclaimsMapping()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        using var sharedMemory = new SharedMemoryLease("cancel-hang", outputLength: 16);
        sharedMemory.GetSpan().Fill(0x7E);
        using var cancellation = new CancellationTokenSource();
        Task<WorkerResponse> exchange = StartExchange(fixture, sharedMemory, cancellation.Token);

        byte[] request = await ReadRequestAsync(fixture.Peer);
        ulong remoteHandle = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(240));
        Assert.NotEqual(0ul, remoteHandle);
        AssertRemoteMappingMatches(fixture.InspectionProcess, remoteHandle, sharedMemory.GetSpan());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await exchange.WaitAsync(TimeSpan.FromSeconds(8)));

        await fixture.Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Child.HasExited);
        AssertRemoteHandleUnavailable(fixture.InspectionProcess, remoteHandle);
    }

    [Fact]
    public async Task DeadlineDuringHungExchangeRetiresWorkerAndReclaimsMapping()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        using var sharedMemory = new SharedMemoryLease("deadline-hang", outputLength: 16);
        Task<WorkerResponse> exchange = fixture.Worker.ExchangeAsync(
            CreateRenderRequest(),
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None,
            sharedMemory).AsTask();

        byte[] request = await ReadRequestAsync(fixture.Peer);
        ulong remoteHandle = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(240));
        Assert.NotEqual(0ul, remoteHandle);

        await Assert.ThrowsAsync<WorkerDeadlineException>(async () =>
            await exchange.WaitAsync(TimeSpan.FromSeconds(8)));

        await fixture.Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Child.HasExited);
        AssertRemoteHandleUnavailable(fixture.InspectionProcess, remoteHandle);
    }

    [Fact]
    public async Task ConcurrentRestartDisposeWaitsAfterOrdinaryDisposeAndClosesRemoteMapping()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync(allowTermination: false);
        using var sharedMemory = new SharedMemoryLease("concurrent-dispose", outputLength: 16);
        sharedMemory.GetSpan().Fill(0x3C);
        Task<WorkerResponse> exchange = StartExchange(fixture, sharedMemory, CancellationToken.None);
        byte[] request = await ReadRequestAsync(fixture.Peer);
        ulong remoteHandle = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(240));
        Assert.NotEqual(0ul, remoteHandle);
        using var mappingIdentity = new SafeFileHandle(DuplicateRemoteHandle(fixture.InspectionProcess, remoteHandle), ownsHandle: true);
        AssertRemoteMappingMatches(fixture.InspectionProcess, remoteHandle, sharedMemory.GetSpan());

        var ordinaryDispose = Stopwatch.StartNew();
        fixture.Worker.Dispose();
        Assert.True(ordinaryDispose.Elapsed < TimeSpan.FromSeconds(1),
            $"Ordinary Dispose must not block while the registered process wait is pending; observed {ordinaryDispose.Elapsed}.");
        Assert.False(fixture.Child.HasExited);

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await exchange.WaitAsync(TimeSpan.FromSeconds(8)));

        // The worker-owned process handle intentionally lacks PROCESS_TERMINATE.
        // The registered duplicate must remain usable after Dispose() starts
        // shutdown. Exchange's restart disposal waits the full bound, then
        // closes the still-live worker's remote mapping handle.
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(2.5),
            $"Expected the bounded restart wait after ordinary disposal; observed {stopwatch.Elapsed}.");
        Assert.False(fixture.Child.HasExited);
        AssertRemoteHandleClosed(fixture.InspectionProcess, remoteHandle, mappingIdentity);
    }
}
