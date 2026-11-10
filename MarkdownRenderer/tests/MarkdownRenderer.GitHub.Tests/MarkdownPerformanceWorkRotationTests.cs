using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.Residency;
using System.Threading.Channels;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks that speculative fetches and raster and scene preparations
/// rotate between document owners, and that session retirement cancels
/// queued preparations and drains active work in each lane.
/// </summary>
public sealed class MarkdownPerformanceWorkRotationTests
{
    [Fact]
    public async Task SpeculativeFetches_RotateBetweenDocumentsWithoutDelayingVisibleWork()
    {
        var resolver = new SequencedBlockingResolver();
        await using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxConcurrentImageFetches = 2,
                ReservedVisibleImageFetches = 1,
            });
        var context = new MarkdownImageResolveContext(null);
        using var first = session.OpenDocument(resolver, context);
        using var second = session.OpenDocument(resolver, context);

        Task firstA = first.PrefetchAsync(["a1"], CancellationToken.None);
        Assert.Equal("a1", await resolver.NextStartedAsync());
        Task secondA = first.PrefetchAsync(["a2"], CancellationToken.None);
        await WaitForPendingFetchesAsync(session, 1);
        Task thirdA = first.PrefetchAsync(["a3"], CancellationToken.None);
        await WaitForPendingFetchesAsync(session, 2);
        Task firstB = second.PrefetchAsync(["b1"], CancellationToken.None);
        await WaitForPendingFetchesAsync(session, 3);
        Task secondB = second.PrefetchAsync(["b2"], CancellationToken.None);
        await WaitForPendingFetchesAsync(session, 4);

        MarkdownImageResolution visible = await second.ResolveAsync(
            "visible", context, CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(visible.IsHandled);

        foreach (string expected in new[] { "a2", "b1", "a3", "b2" })
        {
            resolver.ReleaseOne();
            Assert.Equal(expected, await resolver.NextStartedAsync());
        }
        resolver.ReleaseOne();
        await Task.WhenAll(firstA, secondA, thirdA, firstB, secondB)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RasterPreparations_RotateBetweenDocumentOwners()
    {
        await using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentCpuPreparations = 1 });
        var firstOwner = new object();
        var secondOwner = new object();
        using IDisposable initial = await session.EnterCpuPreparationAsync(
            firstOwner, CancellationToken.None);
        Task<IDisposable> firstA = session.EnterCpuPreparationAsync(
            firstOwner, CancellationToken.None).AsTask();
        Task<IDisposable> secondA = session.EnterCpuPreparationAsync(
            firstOwner, CancellationToken.None).AsTask();
        Task<IDisposable> firstB = session.EnterCpuPreparationAsync(
            secondOwner, CancellationToken.None).AsTask();
        Task<IDisposable> secondB = session.EnterCpuPreparationAsync(
            secondOwner, CancellationToken.None).AsTask();

        initial.Dispose();
        using IDisposable leaseA1 = await firstA.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(firstB.IsCompleted);
        leaseA1.Dispose();
        using IDisposable leaseB1 = await firstB.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondA.IsCompleted);
        leaseB1.Dispose();
        using IDisposable leaseA2 = await secondA.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondB.IsCompleted);
        leaseA2.Dispose();
        using IDisposable leaseB2 = await secondB.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, session.GetSnapshot().CpuPreparations);
    }

    [Fact]
    public async Task ExclusiveRasterPreparationBlocksSmallSourcesUntilItsPixelsAreRetired()
    {
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var service = (IMarkdownPerformanceSessionInternal)session;
        using IDisposable active = await service.EnterCpuPreparationAsync(
            new object(), exclusive: true, CancellationToken.None);
        Task<IDisposable> small = service.EnterCpuPreparationAsync(
            new object(), exclusive: false, CancellationToken.None).AsTask();
        Assert.False(small.IsCompleted);
        active.Dispose();
        using IDisposable next = await small.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, session.GetSnapshot().CpuPreparations);
    }

    [Fact]
    public async Task SessionRetirementCancelsQueuedExclusiveRasterAndDrainsAllSlots()
    {
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        using IDisposable active = await session.EnterCpuPreparationAsync(
            new object(), CancellationToken.None);
        Task<IDisposable> large = session.EnterCpuPreparationAsync(
            new object(), exclusive: true, CancellationToken.None).AsTask();
        Task retirement = session.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => large.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(retirement.IsCompleted);
        active.Dispose();
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, session.GetSnapshot().CpuPreparations);
    }

    [Fact]
    public async Task SessionRetirement_CancelsQueuedRasterPreparationAndDrainsActiveWork()
    {
        var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentCpuPreparations = 1 });
        using IDisposable active = await session.EnterCpuPreparationAsync(
            new object(), CancellationToken.None);
        Task<IDisposable> queued = session.EnterCpuPreparationAsync(
            new object(), CancellationToken.None).AsTask();

        Task retirement = session.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(retirement.IsCompleted);
        active.Dispose();
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ScenePreparations_RotateBetweenDocumentOwners()
    {
        await using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentScenePreparations = 1 });
        var firstOwner = new object();
        var secondOwner = new object();
        using IMarkdownScenePreparationLease initial = await session.EnterScenePreparationAsync(
            firstOwner, CancellationToken.None);
        Task<IMarkdownScenePreparationLease> firstA = session.EnterScenePreparationAsync(
            firstOwner, CancellationToken.None).AsTask();
        Task<IMarkdownScenePreparationLease> secondA = session.EnterScenePreparationAsync(
            firstOwner, CancellationToken.None).AsTask();
        Task<IMarkdownScenePreparationLease> firstB = session.EnterScenePreparationAsync(
            secondOwner, CancellationToken.None).AsTask();
        Task<IMarkdownScenePreparationLease> secondB = session.EnterScenePreparationAsync(
            secondOwner, CancellationToken.None).AsTask();

        initial.Dispose();
        using IMarkdownScenePreparationLease leaseA1 = await firstA.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(firstB.IsCompleted);
        leaseA1.Dispose();
        using IMarkdownScenePreparationLease leaseB1 = await firstB.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondA.IsCompleted);
        leaseB1.Dispose();
        using IMarkdownScenePreparationLease leaseA2 = await secondA.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondB.IsCompleted);
        leaseA2.Dispose();
        using IMarkdownScenePreparationLease leaseB2 = await secondB.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, session.GetSnapshot().ScenePreparations);
    }

    [Fact]
    public async Task SessionRetirement_CancelsQueuedScenePreparationAndSignalsActiveWork()
    {
        var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentScenePreparations = 1 });
        using IMarkdownScenePreparationLease active = await session.EnterScenePreparationAsync(
            new object(), CancellationToken.None);
        Task<IMarkdownScenePreparationLease> queued = session.EnterScenePreparationAsync(
            new object(), CancellationToken.None).AsTask();

        Task retirement = session.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        using (var cancellationWait = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (!active.CancellationToken.IsCancellationRequested)
                await Task.Delay(10, cancellationWait.Token);
        }
        Assert.False(retirement.IsCompleted);
        active.Dispose();
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SceneCancellation_DoesNotOccupyTheIndependentRasterLane()
    {
        await using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxConcurrentScenePreparations = 1,
                MaxConcurrentCpuPreparations = 1
            });
        using var cancellation = new CancellationTokenSource();
        using IMarkdownScenePreparationLease scene = await session.EnterScenePreparationAsync(
            new object(), cancellation.Token);
        using IDisposable raster = await session.EnterCpuPreparationAsync(
            new object(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        Assert.True(scene.CancellationToken.IsCancellationRequested);
        Assert.Equal(1, session.GetSnapshot().ScenePreparations);
        Assert.Equal(1, session.GetSnapshot().CpuPreparations);
    }

    private static async Task WaitForPendingFetchesAsync(
        MarkdownPerformanceSession session, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (session.GetSnapshot().PendingImageFetches != expected)
            await Task.Delay(10, timeout.Token);
    }

    private sealed class SequencedBlockingResolver : IMarkdownImageResolver
    {
        private readonly Channel<string> _started = Channel.CreateUnbounded<string>();
        private readonly SemaphoreSlim _releases = new(0);

        internal async Task<string> NextStartedAsync() =>
            await _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        internal void ReleaseOne() => _releases.Release();

        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            if (source != "visible")
            {
                await _started.Writer.WriteAsync(source, cancellationToken);
                await _releases.WaitAsync(cancellationToken);
            }
            return MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source));
        }
    }
}
