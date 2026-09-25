using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using System.Threading.Channels;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownPerformanceSessionTests
{
    [Fact]
    public void Options_RejectUnboundedSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { SourceCacheBudgetBytes = 65L * 1024 * 1024 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { ReservedVisibleImageFetches = 16 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentImageFetches = 32 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentCpuPreparations = 4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentScenePreparations = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentScenePreparations = 4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxRasterOutputPixels = 8_388_609 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxInFlightSourceBytes = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxInFlightSourceBytes = 65L * 1024 * 1024 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { ReservedVisibleSourceBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { ReservedVisibleSourceBytes = 64L * 1024 * 1024 }));
    }

    [Fact]
    public async Task ByteAdmittedResolver_ReceivesVisibleAndSpeculativeBudgets()
    {
        var resolver = new ByteAdmittedResolver();
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxInFlightSourceBytes = 8,
                ReservedVisibleSourceBytes = 2,
            });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);

        await document.PrefetchAsync(["prefetch.png"], CancellationToken.None);
        await document.ResolveAsync("visible.png", context, CancellationToken.None);

        Assert.Equal(6, resolver.SpeculativeMaximum);
        Assert.Equal(8, resolver.VisibleMaximum);
        Assert.Equal(0, resolver.LegacyCalls);
        Assert.Equal(0, session.ActiveSourceBytes);
        Assert.Equal(3, session.GetSnapshot().PeakInFlightSourceBytes);
        Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        Assert.Equal(0, session.GetSnapshot().PendingSourceByteRequests);
    }

    [Fact]
    public async Task RetiringSession_CancelsAByteWaitEvenWhenResolverPassesNoToken()
    {
        var resolver = new WaitingByteAdmittedResolver();
        var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxConcurrentImageFetches = 2,
                ReservedVisibleImageFetches = 1,
                MaxInFlightSourceBytes = 8,
                ReservedVisibleSourceBytes = 2,
            });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        Task<MarkdownImageResolution> occupied = document.ResolveAsync(
            "occupied.png", context, CancellationToken.None).AsTask();
        await resolver.Occupied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MarkdownImageResolution> waiting = document.ResolveAsync(
            "waiting.png", context, CancellationToken.None).AsTask();
        await resolver.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => occupied);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, session.ActiveSourceBytes);
    }

    [Fact]
    public async Task VisibleResolveInAnotherDocument_SupersedesAnActiveSpeculativeRead()
    {
        var resolver = new PromotedByteAdmittedResolver();
        await using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxConcurrentImageFetches = 2,
                ReservedVisibleImageFetches = 1,
                MaxInFlightSourceBytes = 8,
                ReservedVisibleSourceBytes = 2,
            });
        var context = new MarkdownImageResolveContext(null);
        using var backgroundDocument = session.OpenDocument(resolver, context);
        using var visibleDocument = session.OpenDocument(resolver, context);
        Task prefetch = backgroundDocument.PrefetchAsync(["shared.png"], CancellationToken.None);
        await resolver.SpeculativeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        MarkdownImageResolution visible = await visibleDocument.ResolveAsync(
            "shared.png", context, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await prefetch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(visible.Asset);
        Assert.Equal(2, resolver.CallCount);
        Assert.Equal(0, session.ActiveSourceBytes);
    }

    [Fact]
    public async Task LargeAdmittedSourceStorm_NeverExceedsTheInFlightByteBudget()
    {
        var resolver = new ByteStormResolver();
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxConcurrentImageFetches = 4,
                ReservedVisibleImageFetches = 1,
                MaxInFlightSourceBytes = 8,
                ReservedVisibleSourceBytes = 2,
                SourceCacheBudgetBytes = 0,
            });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        string[] sources = Enumerable.Range(0, 1_800)
            .Select(index => $"storm-{index:D4}.png")
            .ToArray();

        await document.PrefetchAsync(sources, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        MarkdownPerformanceSnapshot snapshot = session.GetSnapshot();
        Assert.Equal(sources.Length, resolver.CallCount);
        Assert.InRange(snapshot.PeakInFlightSourceBytes, 3, 6);
        Assert.Equal(0, snapshot.InFlightSourceBytes);
        Assert.Equal(0, snapshot.PendingSourceByteRequests);
        Assert.Equal(0, snapshot.SourceCacheBytes);
    }

    [Fact]
    public async Task OversizedSpeculativeReservation_DefersWithoutPoisoningVisibleImage()
    {
        var resolver = new DeferredByteAdmittedResolver();
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxInFlightSourceBytes = 8,
                ReservedVisibleSourceBytes = 2,
            });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);

        await document.PrefetchAsync(["large.png"], CancellationToken.None);
        MarkdownImageResolution visible = await document.ResolveAsync(
            "large.png", context, CancellationToken.None);

        Assert.NotNull(visible.Asset);
        Assert.Equal(2, resolver.CallCount);
        Assert.Equal(0, session.GetSnapshot().ImageFetchFailures);
        Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
    }

    [Fact]
    public async Task Prefetch_ThenVisibleResolve_ReusesTheResolvedAsset()
    {
        var resolver = new RecordingResolver();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(new Uri("https://example.test/"));
        using var document = session.OpenDocument(resolver, context);

        await document.PrefetchAsync(["one.png", "two.png"], CancellationToken.None);
        MarkdownImageResolution result = await document.ResolveAsync(
            "one.png", context, CancellationToken.None);

        Assert.True(result.IsHandled);
        Assert.Equal(2, resolver.CallCount);
        Assert.Equal(1, session.GetSnapshot().SourceCacheHits);
    }

    [Fact]
    public async Task DifferentContexts_DoNotShareSourceBytes()
    {
        var resolver = new RecordingResolver();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var first = new MarkdownImageResolveContext(new Uri("https://example.test/a/"));
        var second = new MarkdownImageResolveContext(new Uri("https://example.test/b/"));
        using var firstDocument = session.OpenDocument(resolver, first);
        using var secondDocument = session.OpenDocument(resolver, second);

        await firstDocument.ResolveAsync("avatar.png", first, CancellationToken.None);
        await secondDocument.ResolveAsync("avatar.png", second, CancellationToken.None);

        Assert.Equal(2, resolver.CallCount);
    }

    [Fact]
    public async Task UnkeyedAssets_AreReusedOnlyForTheDocumentLifetime()
    {
        var resolver = new UnkeyedResolver();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using (var first = session.OpenDocument(resolver, context))
        {
            await first.PrefetchAsync(["unkeyed.png"], CancellationToken.None);
            await first.ResolveAsync("unkeyed.png", context, CancellationToken.None);
            Assert.Equal(1, resolver.CallCount);
            Assert.True(session.GetSnapshot().SourceCacheBytes > 0);
        }

        Assert.Equal(0, session.GetSnapshot().SourceCacheBytes);
        using var second = session.OpenDocument(resolver, context);
        await second.ResolveAsync("unkeyed.png", context, CancellationToken.None);
        Assert.Equal(2, resolver.CallCount);
    }

    [Fact]
    public async Task KeyedAssets_AreSharedAcrossDocumentsInOnePartition()
    {
        var resolver = new RecordingResolver();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var first = session.OpenDocument(resolver, context);
        using var second = session.OpenDocument(resolver, context);

        await first.ResolveAsync("shared.png", context, CancellationToken.None);
        await second.ResolveAsync("shared.png", context, CancellationToken.None);

        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(1, session.GetSnapshot().SourceCacheHits);
    }

    [Fact]
    public async Task SourceBudget_EvictsWithoutReportingAnImageUnavailable()
    {
        var resolver = new RecordingResolver();
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { SourceCacheBudgetBytes = 5 });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);

        await document.ResolveAsync("first.png", context, CancellationToken.None);
        await document.ResolveAsync("second.png", context, CancellationToken.None);
        MarkdownImageResolution firstAgain = await document.ResolveAsync(
            "first.png", context, CancellationToken.None);

        Assert.True(firstAgain.IsHandled);
        Assert.NotNull(firstAgain.Asset);
        Assert.Equal(3, resolver.CallCount);
        Assert.InRange(session.GetSnapshot().SourceCacheBytes, 0, 5);
        Assert.True(session.GetSnapshot().SourceCacheEvictions >= 1);
    }

    [Fact]
    public async Task LargeDocumentPrefetch_RemainsBoundedAndCompletes()
    {
        var resolver = new ConcurrentResolver();
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxConcurrentImageFetches = 4,
                ReservedVisibleImageFetches = 1,
                SourceCacheBudgetBytes = 1_024,
            });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        string[] sources = Enumerable.Range(0, 1_800)
            .Select(index => $"image-{index:D4}.png")
            .ToArray();

        await document.PrefetchAsync(sources, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(sources.Length, resolver.CallCount);
        Assert.InRange(resolver.PeakConcurrent, 1, 3);
        Assert.InRange(session.GetSnapshot().SourceCacheBytes, 0, 1_024);
        Assert.Equal(0, session.GetSnapshot().PendingImageFetches);
        Assert.Equal(0, session.GetSnapshot().ActiveImageFetches);
    }

    [Fact]
    public async Task Trim_RemovesRetainedSourceBytes()
    {
        var resolver = new RecordingResolver();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        await document.ResolveAsync("image.png", context, CancellationToken.None);
        Assert.True(session.GetSnapshot().SourceCacheBytes > 0);

        session.Trim();

        Assert.Equal(0, session.GetSnapshot().SourceCacheBytes);
    }

    [Fact]
    public async Task VisibleResolve_PromotesAQueuedPrefetch()
    {
        var resolver = new BlockingResolver();
        await using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxConcurrentImageFetches = 2,
                ReservedVisibleImageFetches = 1,
            });
        var context = new MarkdownImageResolveContext(null);
        using var first = session.OpenDocument(resolver, context);
        using var second = session.OpenDocument(resolver, context);

        Task firstPrefetch = first.PrefetchAsync(["blocked.png"], CancellationToken.None);
        await resolver.BlockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task secondPrefetch = second.PrefetchAsync(["visible.png"], CancellationToken.None);

        MarkdownImageResolution visible = await second.ResolveAsync(
            "visible.png", context, CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(visible.IsHandled);
        Assert.Equal(1, resolver.VisibleCalls);

        resolver.ReleaseBlocked.TrySetResult(true);
        await Task.WhenAll(firstPrefetch, secondPrefetch).WaitAsync(TimeSpan.FromSeconds(5));
    }

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
        Assert.True(active.CancellationToken.IsCancellationRequested);
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

    [Fact]
    public async Task DisposeAsync_CancelsAndDrainsAdmittedFetches()
    {
        var resolver = new BlockingResolver();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        Task<MarkdownImageResolution> pending = document.ResolveAsync(
            "blocked.png", context, CancellationToken.None).AsTask();
        await resolver.BlockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task DocumentDisposal_CancelsAnActiveFetchWithoutBlockingTheCaller()
    {
        var resolver = new BlockingResolver();
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        var document = session.OpenDocument(resolver, context);
        Task<MarkdownImageResolution> pending = document.ResolveAsync(
            "blocked.png", context, CancellationToken.None).AsTask();
        await resolver.BlockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        document.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SimultaneousDocuments_ShareOneActiveResolution()
    {
        var resolver = new BlockingResolver();
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var first = session.OpenDocument(resolver, context);
        using var second = session.OpenDocument(resolver, context);

        Task<MarkdownImageResolution> firstResult = first.ResolveAsync(
            "blocked.png", context, CancellationToken.None).AsTask();
        await resolver.BlockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MarkdownImageResolution> secondResult = second.ResolveAsync(
            "blocked.png", context, CancellationToken.None).AsTask();
        Assert.Equal(1, resolver.BlockedCalls);

        resolver.ReleaseBlocked.TrySetResult(true);
        Assert.True((await firstResult.WaitAsync(TimeSpan.FromSeconds(5))).IsHandled);
        Assert.True((await secondResult.WaitAsync(TimeSpan.FromSeconds(5))).IsHandled);
        Assert.Equal(1, resolver.BlockedCalls);
    }

    [Fact]
    public async Task OneDocumentCancellation_DoesNotAbortAnotherWaiter()
    {
        var resolver = new BlockingResolver();
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        var first = session.OpenDocument(resolver, context);
        using var second = session.OpenDocument(resolver, context);

        Task<MarkdownImageResolution> canceled = first.ResolveAsync(
            "blocked.png", context, CancellationToken.None).AsTask();
        await resolver.BlockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MarkdownImageResolution> surviving = second.ResolveAsync(
            "blocked.png", context, CancellationToken.None).AsTask();
        first.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.WaitAsync(TimeSpan.FromSeconds(5)));

        resolver.ReleaseBlocked.TrySetResult(true);
        Assert.True((await surviving.WaitAsync(TimeSpan.FromSeconds(5))).IsHandled);
        Assert.Equal(1, resolver.BlockedCalls);
    }

    [Fact]
    public async Task RetiredSession_CancelsAnOldDocumentInsteadOfTouchingDisposedSemaphores()
    {
        var resolver = new RecordingResolver();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        await document.ResolveAsync("late.png", context, CancellationToken.None);
        await session.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => document.ResolveAsync("late.png", context, CancellationToken.None).AsTask());
    }

    private sealed class RecordingResolver : IMarkdownImageResolver
    {
        private int _callCount;
        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source)));
        }
    }

    private sealed class ByteAdmittedResolver : IMarkdownImageSourceByteAdmittedResolver
    {
        internal long SpeculativeMaximum { get; private set; }
        internal long VisibleMaximum { get; private set; }
        internal int LegacyCalls { get; private set; }

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            LegacyCalls++;
            return ValueTask.FromResult(MarkdownImageResolution.Unavailable);
        }

        public async ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            if (source == "prefetch.png")
                SpeculativeMaximum = admission.MaximumReservationBytes;
            else
                VisibleMaximum = admission.MaximumReservationBytes;
            using (await admission.ReserveAsync(3, cancellationToken))
            {
                return MarkdownImageResolution.Resolved(
                    new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source));
            }
        }
    }

    private sealed class WaitingByteAdmittedResolver : IMarkdownImageSourceByteAdmittedResolver
    {
        internal TaskCompletionSource<bool> Occupied { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Waiting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The admitted path must be used.");

        public async ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            if (source == "occupied.png")
            {
                using IDisposable held = await admission.ReserveAsync(8, CancellationToken.None);
                Occupied.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            else
            {
                Waiting.TrySetResult(true);
                using IDisposable held = await admission.ReserveAsync(1, CancellationToken.None);
            }
            return MarkdownImageResolution.Unavailable;
        }
    }

    private sealed class PromotedByteAdmittedResolver : IMarkdownImageSourceByteAdmittedResolver
    {
        private int _calls;
        internal int CallCount => Volatile.Read(ref _calls);
        internal TaskCompletionSource<bool> SpeculativeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The admitted path must be used.");

        public async ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            using IDisposable held = await admission.ReserveAsync(1, cancellationToken);
            if (admission.MaximumReservationBytes == 6)
            {
                SpeculativeStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1], "image/png", CacheKey: source));
        }
    }

    private sealed class ByteStormResolver : IMarkdownImageSourceByteAdmittedResolver
    {
        private int _calls;
        internal int CallCount => Volatile.Read(ref _calls);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The admitted path must be used.");

        public async ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            using IDisposable held = await admission.ReserveAsync(3, cancellationToken);
            Interlocked.Increment(ref _calls);
            await Task.Delay(1, cancellationToken);
            return MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source));
        }
    }

    private sealed class DeferredByteAdmittedResolver : IMarkdownImageSourceByteAdmittedResolver
    {
        private int _calls;
        internal int CallCount => Volatile.Read(ref _calls);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The admitted path must be used.");

        public async ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            using IDisposable held = await admission.ReserveAsync(7, cancellationToken);
            return MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1], "image/png", CacheKey: source));
        }
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

    private sealed class BlockingResolver : IMarkdownImageResolver
    {
        private int _visibleCalls;
        private int _blockedCalls;
        internal int VisibleCalls => Volatile.Read(ref _visibleCalls);
        internal int BlockedCalls => Volatile.Read(ref _blockedCalls);
        internal TaskCompletionSource<bool> BlockedStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            if (source == "blocked.png")
            {
                Interlocked.Increment(ref _blockedCalls);
                BlockedStarted.TrySetResult(true);
                await ReleaseBlocked.Task.WaitAsync(cancellationToken);
            }
            else
            {
                Interlocked.Increment(ref _visibleCalls);
            }

            return MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source));
        }
    }

    private sealed class UnkeyedResolver : IMarkdownImageResolver
    {
        private int _callCount;
        internal int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1, 2, 3], "image/png")));
        }
    }

    private sealed class ConcurrentResolver : IMarkdownImageResolver
    {
        private int _active;
        private int _peak;
        private int _calls;
        internal int PeakConcurrent => Volatile.Read(ref _peak);
        internal int CallCount => Volatile.Read(ref _calls);

        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _calls);
            int observed;
            while ((observed = Volatile.Read(ref _peak)) < active &&
                   Interlocked.CompareExchange(ref _peak, active, observed) != observed)
            {
            }
            try
            {
                await Task.Delay(1, cancellationToken);
                return MarkdownImageResolution.Resolved(
                    new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
