using System.Runtime.CompilerServices;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerLifetimeRegistryTests
{
    [Fact]
    public async Task ShutdownWaitsForDetachedRetirementAndIsIdempotent()
    {
        var registry = new WorkerLifetimeRegistry<GatedWorker>();
        using var startup = registry.Reserve();
        var worker = new GatedWorker();
        startup.Attach(worker);
        registry.Retire(worker);
        Task first = registry.DisposeAsync().AsTask();
        Task second = registry.DisposeAsync().AsTask();
        Assert.Equal(1, worker.DisposeCalls);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        registry.Retire(worker);
        worker.Release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, worker.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => registry.Reserve());
    }

    [Fact]
    public async Task LateStartupIsRetiredAndShutdownWaitsForItsCleanup()
    {
        var registry = new WorkerLifetimeRegistry<GatedWorker>();
        using var startup = registry.Reserve();
        Task shutdown = registry.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        var worker = new GatedWorker();
        startup.Attach(worker);
        startup.Dispose();
        Assert.Equal(1, worker.DisposeCalls);
        Assert.False(shutdown.IsCompleted);
        worker.Release.SetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FailedStartupReleasesShutdownBarrier()
    {
        var registry = new WorkerLifetimeRegistry<GatedWorker>();
        var startup = registry.Reserve();
        Task shutdown = registry.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        startup.Dispose();
        startup.Dispose();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => startup.Attach(new GatedWorker()));
    }

    [Fact]
    public async Task CleanupFailureDoesNotSkipAnotherWorkerOrCertifyShutdown()
    {
        var registry = new WorkerLifetimeRegistry<GatedWorker>();
        using var firstStartup = registry.Reserve();
        using var secondStartup = registry.Reserve();
        var first = new GatedWorker();
        var second = new GatedWorker();
        firstStartup.Attach(first);
        secondStartup.Attach(second);
        var failure = new InvalidOperationException("Injected retirement failure.");
        first.Release.SetException(failure);
        registry.Retire(first);
        Task shutdown = registry.DisposeAsync().AsTask();
        Assert.Equal(1, second.DisposeCalls);
        Assert.False(shutdown.IsCompleted);
        second.Release.SetResult();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => shutdown.WaitAsync(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task ConcurrentRetirementAndShutdownDisposeEveryWorkerExactlyOnce()
    {
        var registry = new WorkerLifetimeRegistry<GatedWorker>();
        GatedWorker[] workers = Enumerable.Range(0, 32).Select(_ => new GatedWorker()).ToArray();
        foreach (GatedWorker worker in workers)
        {
            using var startup = registry.Reserve();
            startup.Attach(worker);
        }
        Task[] retirements = workers.Select(worker => Task.Run(() => registry.Retire(worker))).ToArray();
        Task shutdown = registry.DisposeAsync().AsTask();
        await Task.WhenAll(retirements);
        Assert.All(workers, worker => Assert.Equal(1, worker.DisposeCalls));
        Assert.False(shutdown.IsCompleted);
        foreach (GatedWorker worker in workers)
            worker.Release.SetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RetiredWorkerIsNotRootedByHealthyRegistry()
    {
        var registry = new WorkerLifetimeRegistry<GatedWorker>();
        WeakReference retired = CreateRetiredWorker(registry);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(retired.IsAlive);
        await registry.DisposeAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateRetiredWorker(WorkerLifetimeRegistry<GatedWorker> registry)
    {
        using var startup = registry.Reserve();
        var worker = new GatedWorker();
        startup.Attach(worker);
        worker.Release.SetResult();
        registry.Retire(worker);
        return new WeakReference(worker);
    }

    private sealed class GatedWorker : IDisposable, IAsyncDisposable
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCalls;
        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
        public async ValueTask DisposeAsync()
        {
            Dispose();
            await Release.Task.ConfigureAwait(false);
        }
    }
}
