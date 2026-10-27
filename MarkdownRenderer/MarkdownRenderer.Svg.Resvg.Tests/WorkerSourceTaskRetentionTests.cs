using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerSourceTaskRetentionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RetainedSourceMappingTaskDoesNotPinBytesAfterTerminalOutcome(int outcome)
    {
        (Task<int> task, WeakReference source) = CreateSourceMappingTask(outcome);

        switch (outcome)
        {
            case 0:
                Assert.Equal(1024 * 1024, await task);
                break;
            case 1:
                await Assert.ThrowsAsync<IOException>(async () => await task);
                break;
            default:
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
                break;
        }

        ForceCollection();

        Assert.False(source.IsAlive);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task SourceAndMappingRemainAliveUntilAdmittedOperationDrains()
    {
        PendingSourceMapping fixture = CreatePendingSourceMapping();
        try
        {
            await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ForceCollection();
            Assert.True(fixture.Source.IsAlive);
            Assert.NotNull(fixture.Mapping);
            _ = fixture.Mapping!.ReadOpenProgressPhase();
        }
        finally
        {
            fixture.Release.TrySetResult();
        }

        Assert.Equal(17, await fixture.Task);
        Assert.Throws<ObjectDisposedException>(() => fixture.Mapping!.ReadOpenProgressPhase());

        ForceCollection();
        Assert.False(fixture.Source.IsAlive);
        GC.KeepAlive(fixture.Task);
        GC.KeepAlive(fixture.Mapping);
    }

    [Fact]
    public async Task RetainedOutputAdmissionTaskDoesNotPinDelegatesAfterAdmissionFailure()
    {
        (Task<WorkerRenderResult> task, WeakReference source) = CreateFailedOutputAdmissionTask();

        await Assert.ThrowsAsync<IOException>(async () => await task);
        ForceCollection();

        Assert.False(source.IsAlive);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task RetainedPreCanceledOpenTaskDoesNotPinCallerOrOwnedSourceBytes()
    {
        (Task<WorkerOpenResult> task, WeakReference callerSource, WeakReference ownedSource) =
            CreatePreCanceledOpenTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        ForceCollection();

        Assert.False(callerSource.IsAlive);
        Assert.False(ownedSource.IsAlive);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task RetainedPreCanceledRenderTaskDoesNotPinCallerOrOwnedSourceBytes()
    {
        (Task<WorkerRenderResult> task, WeakReference callerSource, WeakReference ownedSource) =
            CreatePreCanceledRenderTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        ForceCollection();

        Assert.False(callerSource.IsAlive);
        Assert.False(ownedSource.IsAlive);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task RetainedQueueRejectedRenderTaskDoesNotPinCallerOrOwnedSourceBytes()
    {
        (Task<WorkerRenderResult> task, WeakReference callerSource, WeakReference ownedSource) =
            CreateQueueRejectedRenderTask();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await task);
        ForceCollection();

        Assert.False(callerSource.IsAlive);
        Assert.False(ownedSource.IsAlive);
        GC.KeepAlive(task);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<int> Task, WeakReference Source) CreateSourceMappingTask(int outcome)
    {
        byte[] sourceBytes = new byte[1024 * 1024];
        var source = new WeakReference(sourceBytes);
        Func<SharedMemoryLease, Task<int>> operation = outcome switch
        {
            0 => _ =>
            {
                GC.KeepAlive(sourceBytes);
                return Task.FromResult(sourceBytes.Length);
            },
            1 => _ =>
            {
                GC.KeepAlive(sourceBytes);
                return Task.FromException<int>(new IOException("synthetic source transaction failure"));
            },
            _ => _ =>
            {
                GC.KeepAlive(sourceBytes);
                return Task.FromResult(sourceBytes.Length);
            },
        };
        using var cancellation = new CancellationTokenSource();
        if (outcome == 2)
            cancellation.Cancel();

        Task<int> task = WorkerPool.WithSourceMappingAsync(
            static (name, bytes) => new SharedMemoryLease(name, bytes, outputLength: 0),
            NewMappingName(),
            SvgSourceBuffer.TakeOwnedArray(sourceBytes),
            operation,
            cancellation.Token);
        return (task, source);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PendingSourceMapping CreatePendingSourceMapping()
    {
        byte[] sourceBytes = new byte[1024 * 1024];
        var source = new WeakReference(sourceBytes);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SharedMemoryLease? mapping = null;
        Func<SharedMemoryLease, Task<int>> operation = async activeMapping =>
        {
            try
            {
                GC.KeepAlive(sourceBytes);
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
                _ = activeMapping.ReadOpenProgressPhase();
                GC.KeepAlive(sourceBytes);
                return 17;
            }
            finally
            {
                // Task completion can inline the awaiting test before this
                // producer's stack unwinds. Retire the fixture's own capture
                // after its last read so GC measures the mapping helper's
                // aliases, not a still-unwinding synthetic callback frame.
                sourceBytes = Array.Empty<byte>();
            }
        };

        Task<int> task = WorkerPool.WithSourceMappingAsync(
            (name, bytes) =>
            {
                var created = new SharedMemoryLease(name, bytes, outputLength: 0);
                mapping = created;
                return created;
            },
            NewMappingName(),
            SvgSourceBuffer.TakeOwnedArray(sourceBytes),
            operation,
            CancellationToken.None);
        return new PendingSourceMapping(task, source, started, release, mapping);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<WorkerRenderResult> Task, WeakReference Source) CreateFailedOutputAdmissionTask()
    {
        byte[] sourceBytes = new byte[1024 * 1024];
        var source = new WeakReference(sourceBytes);
        Func<CancellationToken, Task<TestAdmission>> admit = _ =>
        {
            GC.KeepAlive(sourceBytes);
            return Task.FromException<TestAdmission>(new IOException("synthetic admission failure"));
        };
        Func<TestAdmission, SharedMemoryLease, CancellationToken, Task<WorkerResponse>> render = (_, _, _) =>
        {
            GC.KeepAlive(sourceBytes);
            return Task.FromException<WorkerResponse>(new InvalidOperationException("render must not run"));
        };

        Task<WorkerRenderResult> task = WorkerPool.WithAdmittedOutputMappingAsync(
            admit,
            render,
            static (name, length) => new SharedMemoryLease(name, length),
            NewMappingName(),
            16 * 8 * 4,
            CancellationToken.None);
        return (task, source);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<WorkerOpenResult> Task, WeakReference CallerSource, WeakReference OwnedSource)
        CreatePreCanceledOpenTask()
    {
        byte[] callerBytes = new byte[1024 * 1024];
        byte[] ownedBytes = new byte[1024 * 1024];
        var callerSource = new WeakReference(callerBytes);
        var ownedSource = new WeakReference(ownedBytes);
        var request = new MarkdownSvgOpenRequest(callerBytes);
        var preflight = new SvgPreflightResult(
            SvgSourceBuffer.TakeOwnedArray(ownedBytes),
            new byte[32],
            new MarkdownSvgDocumentInfo(16, 8),
            0,
            0,
            0,
            0);
        var pool = new WorkerPool(
            new ResvgMarkdownSvgRendererOptions(),
            TimeProvider.System,
            static () => true,
            static (name, source) => new SharedMemoryLease(name, source, outputLength: 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Task<WorkerOpenResult> task = pool.OpenDocumentAsync(preflight, request, cancellation.Token).AsTask();
        preflight.ReleaseSource();
        preflight = null!;
        request = null!;
        callerBytes = [];
        ownedBytes = [];
        pool.Dispose();
        return (task, callerSource, ownedSource);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<WorkerRenderResult> Task, WeakReference CallerSource, WeakReference OwnedSource)
        CreatePreCanceledRenderTask()
    {
        byte[] callerBytes = new byte[1024 * 1024];
        byte[] ownedBytes = new byte[1024 * 1024];
        var callerSource = new WeakReference(callerBytes);
        var ownedSource = new WeakReference(ownedBytes);
        var request = new MarkdownSvgOpenRequest(callerBytes);
        var preflight = new SvgPreflightResult(
            SvgSourceBuffer.TakeOwnedArray(ownedBytes),
            new byte[32],
            new MarkdownSvgDocumentInfo(16, 8),
            0,
            0,
            0,
            0);
        var pool = new WorkerPool(
            new ResvgMarkdownSvgRendererOptions(),
            TimeProvider.System,
            static () => true,
            static (name, source) => new SharedMemoryLease(name, source, outputLength: 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Task<WorkerRenderResult> task = pool.RenderDocumentAsync(
            new WorkerDocumentHandle(11),
            preflight,
            request,
            new MarkdownSvgRenderRequest(16, 8),
            16 * 8 * 4,
            cancellation.Token);
        preflight.ReleaseSource();
        preflight = null!;
        request = null!;
        callerBytes = [];
        ownedBytes = [];
        pool.Dispose();
        return (task, callerSource, ownedSource);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<WorkerRenderResult> Task, WeakReference CallerSource, WeakReference OwnedSource)
        CreateQueueRejectedRenderTask()
    {
        byte[] callerBytes = new byte[1024 * 1024];
        byte[] ownedBytes = new byte[1024 * 1024];
        var callerSource = new WeakReference(callerBytes);
        var ownedSource = new WeakReference(ownedBytes);
        var request = new MarkdownSvgOpenRequest(callerBytes);
        var preflight = new SvgPreflightResult(
            SvgSourceBuffer.TakeOwnedArray(ownedBytes),
            new byte[32],
            new MarkdownSvgDocumentInfo(16, 8),
            0,
            0,
            0,
            0);
        var pool = new WorkerPool(
            new ResvgMarkdownSvgRendererOptions(),
            TimeProvider.System,
            static () => true,
            static (name, source) => new SharedMemoryLease(name, source, outputLength: 0));
        pool.Dispose();

        Task<WorkerRenderResult> task = pool.RenderDocumentAsync(
            new WorkerDocumentHandle(12),
            preflight,
            request,
            new MarkdownSvgRenderRequest(16, 8),
            16 * 8 * 4,
            CancellationToken.None);
        preflight.ReleaseSource();
        preflight = null!;
        request = null!;
        callerBytes = [];
        ownedBytes = [];
        return (task, callerSource, ownedSource);
    }

    private static string NewMappingName() =>
        $"Local\\MarkdownRenderer.Resvg.SourceRetention.{Environment.ProcessId}.{Guid.NewGuid():N}";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class TestAdmission : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed record PendingSourceMapping(
        Task<int> Task,
        WeakReference Source,
        TaskCompletionSource Started,
        TaskCompletionSource Release,
        SharedMemoryLease? Mapping);
}
