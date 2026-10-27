using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed partial class WorkerTransportFaultTests
{
    [Fact]
    public async Task RepeatedExchangesDoNotLeakRequestOrResponseState()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        for (ulong id = 1; id <= 100; id++)
        {
            using var memory = new SharedMemoryLease("exchange-reuse", 16);
            WorkerRequest request = CreateRenderRequest() with { RequestId = id };
            request.ContentHash.AsSpan().Fill((byte)id);
            ValueTask<WorkerResponse> exchange = fixture.Worker.ExchangeAsync(
                request, TimeSpan.FromSeconds(5), CancellationToken.None, memory);
            byte[] frame = await ReadRequestAsync(fixture.Peer);
            Assert.Equal(id, BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(8)));
            Assert.True(frame.AsSpan(136, 32).SequenceEqual(request.ContentHash));
            await fixture.Peer.WriteAsync(CreateResponse(frame));
            WorkerResponse response = await exchange;
            Assert.Equal(WorkerStatus.Ok, response.Status);
            AssertExchangeFramesCleared(fixture.Worker);
        }
        Assert.False(fixture.Child.HasExited);
    }

    [Fact]
    public async Task CanceledQueuedExchangeDoesNotPoisonActiveOrNextExchange()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        using var firstMemory = new SharedMemoryLease("active", 16);
        ValueTask<WorkerResponse> first = fixture.Worker.ExchangeAsync(
            CreateRenderRequest() with { RequestId = 101 },
            TimeSpan.FromSeconds(5), CancellationToken.None, firstMemory);
        byte[] firstFrame = await ReadRequestAsync(fixture.Peer);

        using var queuedMemory = new SharedMemoryLease("queued", 16);
        using var cancellation = new CancellationTokenSource();
        ValueTask<WorkerResponse> queued = fixture.Worker.ExchangeAsync(
            CreateRenderRequest() with { RequestId = 102 },
            TimeSpan.FromSeconds(5), cancellation.Token, queuedMemory);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
        Assert.False(fixture.Child.HasExited);

        await fixture.Peer.WriteAsync(CreateResponse(firstFrame));
        Assert.Equal(WorkerStatus.Ok, (await first).Status);
        AssertExchangeFramesCleared(fixture.Worker);

        ValueTask<WorkerResponse> next = fixture.Worker.ExchangeAsync(
            CreateRenderRequest() with { RequestId = 103 },
            TimeSpan.FromSeconds(5), CancellationToken.None, queuedMemory);
        byte[] nextFrame = await ReadRequestAsync(fixture.Peer);
        Assert.Equal(103ul, BinaryPrimitives.ReadUInt64LittleEndian(nextFrame.AsSpan(8)));
        await fixture.Peer.WriteAsync(CreateResponse(nextFrame));
        Assert.Equal(WorkerStatus.Ok, (await next).Status);
        AssertExchangeFramesCleared(fixture.Worker);
    }

    [Fact]
    public async Task ConsumedExchangeDoesNotRetainRequestOrMappingThroughReusableState()
    {
        await using var fixture = await WorkerTransportFixture.CreateAsync();
        var result = await CompleteWeakExchangeAsync(fixture);
        // Functional ownership check only: the AOT process-memory release gate
        // does not force GC and does not use this test's measurements.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(result.Hash.IsAlive);
        Assert.False(result.Mapping.IsAlive);
        GC.KeepAlive(result.ConsumedOperation);
        GC.KeepAlive(fixture.Worker);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ValueTask<WorkerResponse> ConsumedOperation, WeakReference Hash, WeakReference Mapping)>
        CompleteWeakExchangeAsync(WorkerTransportFixture fixture)
    {
        using var memory = new SharedMemoryLease("weak-exchange", 16);
        WorkerRequest request = CreateRenderRequest();
        var hashReference = new WeakReference(request.ContentHash);
        var mappingReference = new WeakReference(memory);
        ValueTask<WorkerResponse> operation = fixture.Worker.ExchangeAsync(
            request, TimeSpan.FromSeconds(5), CancellationToken.None, memory);
        byte[] frame = await ReadRequestAsync(fixture.Peer);
        await fixture.Peer.WriteAsync(CreateResponse(frame));
        Assert.Equal(WorkerStatus.Ok, (await operation).Status);
        return (operation, hashReference, mappingReference);
    }

    private static void AssertExchangeFramesCleared(WindowsWorkerProcess worker)
    {
        foreach (string name in new[] { "_requestFrame", "_responseFrame" })
        {
            byte[] frame = (byte[])typeof(WindowsWorkerProcess).GetField(
                name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(worker)!;
            Assert.True(frame.AsSpan().IndexOfAnyExcept((byte)0) < 0);
        }
    }
}
