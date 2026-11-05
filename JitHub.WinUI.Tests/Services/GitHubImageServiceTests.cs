using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceHttpDoubles;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceAdmissionDoubles;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// Caching, sharing, revalidation and disposal of <c>GitHubImageService</c> image fetches.
/// </summary>
public sealed class GitHubImageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubImageServiceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetAsync_CachesNetworkBytes_AndReusesThemOffline()
    {
        GitHubCachePolicy policy = new(avatarImageSoftCapBytes: 1024 * 1024);
        GitHubImageCacheStore store = new(_root, policy);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? first = await service.GetAsync("https://avatars.githubusercontent.com/u/1.png");
        GitHubCachedImage? second = await service.GetAsync("https://avatars.githubusercontent.com/u/1.png");

        Assert.NotNull(first);
        Assert.False(first!.IsFromCache);
        Assert.NotNull(second);
        Assert.True(second!.IsFromCache);
        Assert.Equal(first.FilePath, second.FilePath);
        Assert.Equal(PngBytes, first.Bytes);
        Assert.Equal(PngBytes, second.Bytes);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(first.FilePath));
    }

    [Fact]
    public async Task MemoryPressure_EvictsMemoryBytesAndReusesDiskCacheWithoutNetwork()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        MemoryPressureSource pressure = new();
        using GitHubImageService service = new(
            store,
            client,
            subscribeMemoryPressure: pressure.Subscribe,
            unsubscribeMemoryPressure: pressure.Unsubscribe);
        const string source = "https://avatars.githubusercontent.com/u/memory-pressure.png";

        GitHubCachedImage? first = await service.GetAsync(source);
        Assert.NotNull(first);
        Assert.Equal(1, pressure.SubscriberCount);

        pressure.Raise();
        GitHubCachedImage? afterPressure = await service.GetAsync(source);

        Assert.NotNull(afterPressure);
        Assert.True(afterPressure!.IsFromCache);
        Assert.Equal(PngBytes, afterPressure.Bytes);
        Assert.NotSame(first!.Bytes, afterPressure.Bytes);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, pressure.SubscriberCount);
    }

    [Fact]
    public void Dispose_UnsubscribesOnceAndCapturedLatePressureCallbackIsHarmless()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new CountingHandler(PngBytes));
        MemoryPressureSource pressure = new();
        GitHubImageService service = new(
            store,
            client,
            subscribeMemoryPressure: pressure.Subscribe,
            unsubscribeMemoryPressure: pressure.Unsubscribe);
        EventHandler<object>? captured = pressure.CaptureHandlers();

        Assert.Equal(1, pressure.SubscriberCount);
        service.Dispose();
        service.Dispose();

        Assert.Equal(0, pressure.SubscriberCount);
        Assert.NotNull(captured);
        Exception? lateCallbackFailure = Record.Exception(
            () => captured!(pressure, EventArgs.Empty));
        Assert.Null(lateCallbackFailure);
        pressure.Raise();
        Assert.Equal(0, pressure.SubscriberCount);
    }

    [Fact]
    public void Constructor_PartialPressureSubscribeFailureRollsBackAndDisposesOwnedClient()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        DisposalTrackingHandler transport = new();
        HttpClient client = new(transport);
        MemoryPressureSource pressure = new();

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            new GitHubImageService(
                store,
                client,
                ownsHttpClient: true,
                subscribeMemoryPressure: pressure.SubscribeThenThrow,
                unsubscribeMemoryPressure: pressure.Unsubscribe));

        Assert.Equal("Injected partial memory-pressure subscription failure.", failure.Message);
        Assert.Equal(0, pressure.SubscriberCount);
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public void Dispose_UnsubscribeFailureStillCleansOwnedStateAndDoesNotRootService()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        MemoryPressureSource pressure = new()
        {
            UnsubscribeFailure = new InvalidOperationException(
                "Injected memory-pressure unsubscribe failure."),
        };
        (WeakReference<GitHubImageService> service, EventHandler<object> callback,
            DisposalTrackingHandler transport) = CreateServiceWithFailingPressureUnsubscribe(
                store,
                pressure);

        Assert.True(transport.IsDisposed);
        Assert.Equal(1, pressure.SubscriberCount);
        for (int attempt = 0; attempt < 3 && IsImageServiceAlive(service); attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Assert.False(IsImageServiceAlive(service));
        Exception? lateCallbackFailure = Record.Exception(
            () => callback(pressure, EventArgs.Empty));
        Assert.Null(lateCallbackFailure);
    }

    [Fact]
    public void Dispose_ExpectedWinRtUnsubscribeFailureStillFinishesCleanup()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        MemoryPressureSource pressure = new()
        {
            UnsubscribeFailure = new PlatformNotSupportedException(
                "Injected unavailable WinRT event removal."),
        };
        DisposalTrackingHandler transport = new();
        HttpClient client = new(transport);
        GitHubImageService service = new(
            store,
            client,
            ownsHttpClient: true,
            subscribeMemoryPressure: pressure.Subscribe,
            unsubscribeMemoryPressure: pressure.Unsubscribe);
        EventHandler<object> callback = pressure.CaptureHandlers()!;

        service.Dispose();

        Assert.True(transport.IsDisposed);
        Assert.Equal(1, pressure.SubscriberCount);
        Exception? lateCallbackFailure = Record.Exception(
            () => callback(pressure, EventArgs.Empty));
        Assert.Null(lateCallbackFailure);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool IsImageServiceAlive(WeakReference<GitHubImageService> service) =>
        service.TryGetTarget(out _);

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference<GitHubImageService> Service, EventHandler<object> Callback,
        DisposalTrackingHandler Transport) CreateServiceWithFailingPressureUnsubscribe(
            GitHubImageCacheStore store,
            MemoryPressureSource pressure)
    {
        DisposalTrackingHandler transport = new();
        HttpClient client = new(transport);
        GitHubImageService service = new(
            store,
            client,
            ownsHttpClient: true,
            subscribeMemoryPressure: pressure.Subscribe,
            unsubscribeMemoryPressure: pressure.Unsubscribe);
        EventHandler<object> callback = pressure.CaptureHandlers()!;
        WeakReference<GitHubImageService> weakService = new(service);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(service.Dispose);
        Assert.Equal("Injected memory-pressure unsubscribe failure.", failure.Message);
        return (weakService, callback, transport);
    }

    [Fact]
    public async Task GetAsync_DeduplicatesConcurrentRequests()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes, TimeSpan.FromMilliseconds(30));
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        Task<GitHubCachedImage?> first = service.GetAsync("https://avatars.githubusercontent.com/u/2.jpg");
        Task<GitHubCachedImage?> second = service.GetAsync("https://avatars.githubusercontent.com/u/2.jpg");
        GitHubCachedImage?[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(results[0]!.FilePath, results[1]!.FilePath);
    }

    [Fact]
    public async Task GetAsync_RejectsNonHttpSources()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        Assert.Null(await service.GetAsync("ms-appx:///Assets/Octocat.png"));
        Assert.Null(await service.GetAsync("file:///private/image.png"));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_PartitionsCachedImagesByAuthenticatedAccount()
    {
        long accountId = 11;
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client, partitionProvider: () => accountId);

        GitHubCachedImage? firstAccount = await service.GetAsync("https://avatars.githubusercontent.com/u/3.png");
        accountId = 22;
        GitHubCachedImage? secondAccount = await service.GetAsync("https://avatars.githubusercontent.com/u/3.png");

        Assert.NotNull(firstAccount);
        Assert.NotNull(secondAccount);
        Assert.NotEqual(firstAccount!.FilePath, secondAccount!.FilePath);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_ReturnsStaleImmediately_AndConditionallyRevalidates()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RevalidationHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? initial = await service.GetAsync("https://avatars.githubusercontent.com/u/4.png");
        Assert.NotNull(initial);
        File.SetLastWriteTimeUtc(initial!.FilePath, DateTime.UtcNow.Subtract(TimeSpan.FromDays(8)));

        GitHubCachedImage? stale = await service.GetAsync("https://avatars.githubusercontent.com/u/4.png");
        Assert.NotNull(stale);
        Assert.True(stale!.IsStale);
        Assert.NotNull(stale.RefreshTask);

        GitHubCachedImage? refreshed = await stale.RefreshTask!;
        Assert.NotNull(refreshed);
        Assert.True(refreshed!.IsFromCache);
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(refreshed.FilePath) < TimeSpan.FromMinutes(1));
        Assert.Equal(2, handler.RequestCount);
        Assert.True(handler.SawConditionalRequest);
    }

    [Fact]
    public async Task GetAsync_DoesNotCacheNonImageResponses()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new NonImageHandler());
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.GetAsync("https://avatars.githubusercontent.com/u/not-an-image"));

        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task GetAsync_NormalizesMislabeledRasterContentFromValidatedSignature()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new RawImageHandler("image/gif", PngBytes));
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://avatars.githubusercontent.com/u/11906529?v=4&s=48");

        Assert.NotNull(image);
        Assert.Equal("image/png", image!.ContentType);
        Assert.Equal(".img", Path.GetExtension(image.FilePath));
        Assert.Equal(PngBytes, image.Bytes);
    }

    [Fact]
    public async Task GetAsync_AdmitsStillAvifFromValidatedSignature()
    {
        byte[] avif =
        [
            0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'a', (byte)'v', (byte)'i', (byte)'f', 0, 0, 0, 0,
            (byte)'a', (byte)'v', (byte)'i', (byte)'f', (byte)'m', (byte)'i', (byte)'f', (byte)'1',
        ];
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new RawImageHandler("application/octet-stream", avif));
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://camo.githubusercontent.com/hash/contributor.avif");

        Assert.NotNull(image);
        Assert.Equal("image/avif", image!.ContentType);
        Assert.Equal(".img", Path.GetExtension(image.FilePath));
        Assert.Equal(avif, image.Bytes);
    }

    [Fact]
    public async Task GetAsync_RejectsAnimatedAvifSequence()
    {
        byte[] avis =
        [
            0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'a', (byte)'v', (byte)'i', (byte)'f', 0, 0, 0, 0,
            (byte)'a', (byte)'v', (byte)'i', (byte)'f', (byte)'a', (byte)'v', (byte)'i', (byte)'s',
        ];
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new RawImageHandler("image/avif", avis));
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://camo.githubusercontent.com/hash/animated.avif"));
    }

    [Fact]
    public async Task GetAsync_CancelsSharedTransferOnlyAfterAllWaitersLeave()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CancellationAwareHandler handler = new();
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);
        using CancellationTokenSource firstCancellation = new();
        using CancellationTokenSource secondCancellation = new();

        Task<GitHubCachedImage?> first = service.GetAsync(
            "https://avatars.githubusercontent.com/u/cancel.png",
            firstCancellation.Token);
        Task<GitHubCachedImage?> second = service.GetAsync(
            "https://avatars.githubusercontent.com/u/cancel.png",
            secondCancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(handler.CancellationObserved.Task.IsCompleted);

        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    [Trait("Category", "ReleaseSecurity")]
    public async Task AccountRemoval_DrainsImageFetchBeforeClear_AndPreventsLateCacheWrite()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CancellationAwareHandler handler = new();
        using HttpClient client = new(handler);
        AccountWorkQuiescence accountWork = new();
        using GitHubImageService service = new(
            store,
            client,
            partitionProvider: () => 101,
            accountWork: accountWork);
        const string source = "https://avatars.githubusercontent.com/u/account-removal.png";
        Task<GitHubCachedImage?> fetch = service.GetAsync(source);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        AccountDataRemovalCoordinator coordinator = new(
            [new AccountDataRemovalStep(AccountDataComponentIds.ImageCache, store.ClearPartitionAsync)],
            accountWork);

        AccountDataRemovalResult result = await coordinator.RemoveAsync("101");

        Assert.True(result.IsComplete);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        string cacheKey = $"101:{GitHubImageAccountScope.NormalizeCacheIdentity(source)}";
        Assert.Null(await store.TryGetAsync(cacheKey));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(source));
    }

    [Fact]
    public async Task GetAsync_FailedStaleRefreshKeepsExistingFileVisible()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        FailingRefreshHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage initial = (await service.GetAsync("https://avatars.githubusercontent.com/u/stale.png"))!;
        File.SetLastWriteTimeUtc(initial.FilePath, DateTime.UtcNow.Subtract(TimeSpan.FromDays(8)));

        GitHubCachedImage stale = (await service.GetAsync("https://avatars.githubusercontent.com/u/stale.png"))!;
        GitHubCachedImage? refreshResult = await stale.RefreshTask!;

        Assert.NotNull(refreshResult);
        Assert.True(refreshResult!.IsStale);
        Assert.Equal(initial.FilePath, refreshResult.FilePath);
        Assert.True(File.Exists(initial.FilePath));
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(initial.FilePath));
    }

    [Fact]
    public async Task GetAsync_ReturnedBytesRemainUsableWhenRefreshEvictsOldGeneration()
    {
        byte[] replacement = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 8, 7];
        GitHubCachePolicy policy = new(avatarImageSoftCapBytes: PngBytes.Length + 1);
        GitHubImageCacheStore store = new(_root, policy);
        ReplacingImageHandler handler = new(PngBytes, replacement);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage initial = (await service.GetAsync("https://avatars.githubusercontent.com/u/generation.png"))!;
        File.SetLastWriteTimeUtc(initial.FilePath, DateTime.UtcNow.Subtract(TimeSpan.FromDays(8)));

        GitHubCachedImage stale = (await service.GetAsync("https://avatars.githubusercontent.com/u/generation.png"))!;
        GitHubCachedImage refreshed = (await stale.RefreshTask!)!;

        Assert.False(File.Exists(stale.FilePath));
        Assert.Equal(PngBytes, stale.Bytes);
        Assert.Equal(replacement, refreshed.Bytes);
        Assert.True(File.Exists(refreshed.FilePath));
    }

    [Fact]
    public void SharedRequest_RetiresAtomicallyBeforeLateWaiterCanJoin()
    {
        SharedImageRequest request = new(
            _ => Task.FromResult<GitHubCachedImage?>(null));

        Assert.True(request.TryAddWaiter());
        SharedImageRequestRelease release = request.ReleaseWaiter(taskCompleted: false);

        Assert.True(release.ShouldRetire);
        Assert.True(release.ShouldCancel);
        Assert.False(request.TryAddWaiter());
        request.Cancel();
        request.DisposeWhenComplete();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
