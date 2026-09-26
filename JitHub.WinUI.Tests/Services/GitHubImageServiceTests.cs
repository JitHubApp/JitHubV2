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

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubImageServiceTests : IDisposable
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
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
    public async Task AdmittedStaleRefresh_CompletesWithinTheCallerLifetime()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RevalidationHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);
        const string source = "https://avatars.githubusercontent.com/u/admitted-stale.png";
        GitHubCachedImage? initial = await service.GetAsync(source);
        Assert.NotNull(initial);
        File.SetLastWriteTimeUtc(initial!.FilePath, DateTime.UtcNow.Subtract(TimeSpan.FromDays(8)));
        var admission = new TrackingAdmission(1024);

        GitHubCachedImage? refreshed = await service.GetAsync(
            source, GitHubImageFetchScope.TrustedGitHub, admission);

        Assert.NotNull(refreshed);
        Assert.False(refreshed!.IsStale);
        Assert.Null(refreshed.RefreshTask);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(PngBytes.Length, admission.PeakBytes);
        Assert.Equal(1, admission.Reservations);
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task AdmittedGet_KnownLengthRetainsTheByteLeaseThroughCacheStorage()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new RawImageHandler("image/png", PngBytes));
        using GitHubImageService service = new(store, client);
        var admission = new TrackingAdmission(1024);

        GitHubCachedImage? image = await service.GetAsync(
            "https://images.example.com/admitted-known.png",
            GitHubImageFetchScope.UserApprovedHttps,
            admission);

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image.Bytes);
        Assert.Equal(PngBytes.Length, admission.PeakBytes);
        Assert.Equal(1, admission.Reservations);
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task AdmittedGet_ColdDiskCacheReservesBeforeMaterializingBytes()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        const string source = "https://images.example.com/admitted-disk-cache.png";
        using (HttpClient populateClient = new(new RawImageHandler("image/png", PngBytes)))
        using (GitHubImageService populate = new(store, populateClient))
        {
            Assert.NotNull(await populate.GetAsync(
                source, GitHubImageFetchScope.UserApprovedHttps));
        }

        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);
        var admission = new TrackingAdmission(1024);

        GitHubCachedImage? cached = await service.GetAsync(
            source, GitHubImageFetchScope.UserApprovedHttps, admission);

        Assert.NotNull(cached);
        Assert.True(cached.IsFromCache);
        Assert.Equal(PngBytes, cached.Bytes);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(PngBytes.Length, admission.PeakBytes);
        Assert.Equal(1, admission.Reservations);
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task AdmittedGet_ColdDiskCacheDefersWhenSpeculativeBudgetIsTooSmall()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        const string source = "https://images.example.com/admitted-disk-deferral.png";
        using (HttpClient populateClient = new(new RawImageHandler("image/png", PngBytes)))
        using (GitHubImageService populate = new(store, populateClient))
        {
            Assert.NotNull(await populate.GetAsync(
                source, GitHubImageFetchScope.UserApprovedHttps));
        }

        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);
        var speculative = new TrackingAdmission(PngBytes.Length - 1, isSpeculative: true);

        await Assert.ThrowsAsync<MarkdownImageSourceDeferredException>(() => service.GetAsync(
            source, GitHubImageFetchScope.UserApprovedHttps, speculative));
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, speculative.ActiveBytes);

        var visible = new TrackingAdmission(1024);
        GitHubCachedImage? cached = await service.GetAsync(
            source, GitHubImageFetchScope.UserApprovedHttps, visible);
        Assert.NotNull(cached);
        Assert.True(cached.IsFromCache);
        Assert.Equal(PngBytes, cached.Bytes);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, visible.ActiveBytes);
    }

    [Fact]
    public async Task AdmittedCacheOnlyRead_ReservesDiskBytesWithoutFetching()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        const string source = "https://images.example.com/admitted-cache-only.png";
        using (HttpClient populateClient = new(new RawImageHandler("image/png", PngBytes)))
        using (GitHubImageService populate = new(store, populateClient))
        {
            Assert.NotNull(await populate.GetAsync(
                source, GitHubImageFetchScope.UserApprovedHttps));
        }

        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);
        var admission = new TrackingAdmission(1024);

        GitHubCachedImage? cached = await service.TryGetCachedAsync(
            source, GitHubImageFetchScope.UserApprovedHttps, admission);

        Assert.NotNull(cached);
        Assert.True(cached.IsFromCache);
        Assert.Equal(PngBytes, cached.Bytes);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(1, admission.Reservations);
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task AdmittedDiskRead_RechecksGenerationAfterWaitingForByteGrant()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        const string cacheKey = "account:admitted-generation";
        await store.PutAsync(cacheKey, PngBytes, ".img");
        byte[] replacement = [.. PngBytes, 42];
        var admission = new MutatingAdmission(
            async () => await store.PutAsync(cacheKey, replacement, ".img"));

        using AdmittedGitHubImageCacheRead? read = await store.TryReadAdmittedAsync(
            cacheKey, admission, GitHubImageService.MaxImageBytes, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(replacement, read.Value.Bytes);
        Assert.Equal(2, admission.Reservations);
        Assert.Equal(replacement.Length, admission.PeakBytes);
        read.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task AdmittedDiskRead_WaitingForBytesDoesNotHoldCacheGateAndCancelsCleanly()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        const string cacheKey = "account:admitted-cancellation";
        await store.PutAsync(cacheKey, PngBytes, ".img");
        using var admission = new GatedImageAdmission(PngBytes.Length, concurrentImages: 1);
        using IDisposable blocker = await admission.ReserveAsync(
            PngBytes.Length, CancellationToken.None);
        using CancellationTokenSource cancellation = new();

        Task<AdmittedGitHubImageCacheRead?> pending = store.TryReadAdmittedAsync(
            cacheKey, admission, GitHubImageService.MaxImageBytes, cancellation.Token);
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        // A cache writer must be able to finish while another reader waits
        // for weighted source-byte admission on the same stripe.
        await store.PutAsync(cacheKey, [.. PngBytes, 42], ".img")
            .WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        blocker.Dispose();
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdmittedGet_ReservesBytesBeforeHttpContentCreatesItsStream(bool knownLength)
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        var admission = new TrackingAdmission(1024);
        using HttpClient client = new(new AdmissionAwareHandler(
            PngBytes, knownLength, () => Assert.True(admission.ActiveBytes > 0)));
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            $"https://images.example.com/admitted-stream-{knownLength}.png",
            GitHubImageFetchScope.UserApprovedHttps,
            admission);

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image.Bytes);
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task AdmittedGet_ConcurrentKnownLengthImagesStayWithinByteBudget()
    {
        const int imageBytes = 1024 * 1024;
        byte[] bytes = new byte[imageBytes];
        PngBytes.CopyTo(bytes, 0);
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using var admission = new GatedImageAdmission(imageBytes, concurrentImages: 4);
        GatedImageHandler handler = new(bytes, admission);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        Task<GitHubCachedImage?>[] requests = Enumerable.Range(0, 8)
            .Select(index => service.GetAsync(
                $"https://images.example.com/storm/{index}.png",
                GitHubImageFetchScope.UserApprovedHttps,
                admission))
            .ToArray();
        GitHubCachedImage?[] images = await Task.WhenAll(requests)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(8, handler.RequestCount);
        Assert.All(images, image =>
        {
            Assert.NotNull(image);
            Assert.Equal(bytes, image.Bytes);
        });
        Assert.Equal(4L * imageBytes, admission.PeakBytes);
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task AdmittedGet_ConcurrentChunkedImagesDoNotHoldScratchLeaseWhileWaitingForFinalBytes()
    {
        byte[] bytes = new byte[1_200_000];
        PngBytes.CopyTo(bytes, 0);
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using var admission = new GatedImageAdmission(2 * 1024 * 1024, concurrentImages: 2);
        GatedImageHandler handler = new(bytes, admission, knownLength: false);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        Task<GitHubCachedImage?>[] requests = Enumerable.Range(0, 2)
            .Select(index => service.GetAsync(
                $"https://images.example.com/chunked-storm/{index}.png",
                GitHubImageFetchScope.UserApprovedHttps,
                admission))
            .ToArray();
        GitHubCachedImage?[] images = await Task.WhenAll(requests)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(2, handler.RequestCount);
        Assert.All(images, image =>
        {
            Assert.NotNull(image);
            Assert.Equal(bytes, image.Bytes);
        });
        Assert.Equal(4L * 1024 * 1024, admission.PeakBytes);
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task AdmittedGet_DeclaredLengthMismatchReleasesTheByteLease()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new DeclaredLengthImageHandler(PngBytes.Length - 1));
        using GitHubImageService service = new(store, client);
        var admission = new TrackingAdmission(1024);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/admitted-wrong-length.png",
            GitHubImageFetchScope.UserApprovedHttps,
            admission));

        Assert.Equal(0, admission.ActiveBytes);
        Assert.Equal(1, admission.Reservations);
    }

    [Theory]
    [InlineData(11, 1)]
    [InlineData(1_200_000, 2)]
    public async Task AdmittedGet_UnknownLengthStaysBoundedAndReleasesLeases(
        int length,
        int expectedReservations)
    {
        byte[] bytes = new byte[length];
        PngBytes.CopyTo(bytes, 0);
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new UnknownLengthImageHandler(bytes));
        using GitHubImageService service = new(store, client);
        var admission = new TrackingAdmission(2 * 1024 * 1024);

        GitHubCachedImage? image = await service.GetAsync(
            $"https://images.example.com/admitted-unknown-{length}.png",
            GitHubImageFetchScope.UserApprovedHttps,
            admission);

        Assert.NotNull(image);
        Assert.Equal(bytes, image.Bytes);
        Assert.Equal(expectedReservations, admission.Reservations);
        Assert.InRange(admission.PeakBytes, length, 2 * 1024 * 1024);
        Assert.Equal(0, admission.ActiveBytes);
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
        string cacheKey = $"101:{GitHubImageService.NormalizeCacheIdentity(source)}";
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
    public async Task CacheStore_CommitsReplacementThroughAtomicManifestWithoutTemporaryResidue()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        GitHubImageCacheEntry first = await store.PutAsync("account:url", PngBytes, ".png");
        byte[] replacement = [0x42, 0x4D, 1, 2, 3, 4];

        GitHubImageCacheEntry second = await store.PutAsync(
            "account:url",
            replacement,
            ".bmp",
            new GitHubImageCacheWriteMetadata(null, null, "image/bmp"));

        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.True(File.Exists(second.FilePath));
        Assert.Equal(replacement, await File.ReadAllBytesAsync(second.FilePath));
        GitHubImageCacheEntry? current = await store.TryGetAsync("account:url");
        Assert.NotNull(current);
        Assert.Equal(second.FilePath, current!.FilePath);
        Assert.Equal("image/bmp", current.ContentType);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CacheStore_IgnoresPayloadGenerationWithoutCommittedManifest()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        GitHubImageCacheEntry committed = await store.PutAsync(
            "account:url",
            PngBytes,
            ".png",
            new GitHubImageCacheWriteMetadata("\"v1\"", null, "image/png"));
        string prefix = Path.GetFileName(committed.FilePath).Split('.')[0];
        string uncommittedPath = Path.Combine(_root, $"{prefix}.{Guid.NewGuid():N}.img");
        await File.WriteAllBytesAsync(uncommittedPath, [0x42, 0x4D, 1, 2, 3]);

        GitHubImageCacheEntry? visible = await store.TryGetAsync("account:url");

        Assert.NotNull(visible);
        Assert.Equal(committed.FilePath, visible!.FilePath);
        Assert.Equal("\"v1\"", visible.ETag);
        Assert.Equal("image/png", visible.ContentType);
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(visible.FilePath));

        CacheStoreInspection inspection = await store.InspectAsync();
        Assert.Equal(CacheOwnerHealth.Degraded, inspection.Health);
        Assert.True(inspection.OrphanBytes > 0);
    }

    [Fact]
    public async Task CacheStore_InspectionReportsCorruptManifestAsUnhealthy()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        GitHubImageCacheEntry entry = await store.PutAsync("account:url", PngBytes, ".png");
        string prefix = Path.GetFileName(entry.FilePath).Split('.')[0];
        await File.WriteAllTextAsync(Path.Combine(_root, prefix + ".meta"), "v2\nnot-base64");

        CacheStoreInspection inspection = await store.InspectAsync();

        Assert.Equal(CacheOwnerHealth.Unhealthy, inspection.Health);
        Assert.Contains("corrupt", inspection.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CacheIdentity_CanonicalizesEquivalentHttpsUrlsAndDropsFragments()
    {
        string first = GitHubImageService.NormalizeCacheIdentity(
            "HTTPS://AVATARS.GITHUBUSERCONTENT.COM:443/u/1.png?size=80#fragment");
        string second = GitHubImageService.NormalizeCacheIdentity(
            "https://avatars.githubusercontent.com/u/1.png?size=80");

        Assert.Equal(second, first);
        Assert.DoesNotContain("fragment", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CacheStore_SizeSnapshot_IsRaceSafeDuringMutation()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        for (int index = 0; index < 40; index++)
        {
            await store.PutAsync($"account:image:{index}", PngBytes, ".png");
        }

        Task reader = Task.Run(async () =>
        {
            for (int index = 0; index < 80; index++)
            {
                Assert.True(await store.GetTotalBytesAsync() >= 0);
            }
        });
        Task writer = Task.Run(async () =>
        {
            for (int index = 0; index < 20; index++)
            {
                await store.ClearAllAsync();
                await store.PutAsync($"account:replacement:{index}", PngBytes, ".png");
            }
        });

        await Task.WhenAll(reader, writer);
        Assert.True(await store.GetTotalBytesAsync() >= 0);
    }

    [Fact]
    public async Task CacheStore_ConcurrentDistinctWritesRemainReadableAndInspectable()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        const int entryCount = 128;

        await Task.WhenAll(Enumerable.Range(0, entryCount).Select(index =>
            store.PutAsync(
                $"account:parallel:{index}",
                PngBytes.Concat(BitConverter.GetBytes(index)).ToArray(),
                ".png",
                new GitHubImageCacheWriteMetadata($"\"{index}\"", null, "image/png"))));

        GitHubImageCacheRead?[] reads = await Task.WhenAll(Enumerable.Range(0, entryCount).Select(index =>
            store.TryReadAsync($"account:parallel:{index}")));
        Assert.All(reads, Assert.NotNull);
        Assert.Equal(entryCount, reads.Select(static read => read!.Entry.FilePath).Distinct().Count());
        Assert.Equal(CacheOwnerHealth.Healthy, (await store.InspectAsync()).Health);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CacheStore_ConcurrentSameKeyWritesCommitOneCompleteGeneration()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        byte[][] payloads = Enumerable.Range(0, 32)
            .Select(index => PngBytes.Concat(BitConverter.GetBytes(index)).ToArray())
            .ToArray();

        await Task.WhenAll(payloads.Select((bytes, index) => store.PutAsync(
            "account:same-key",
            bytes,
            ".png",
            new GitHubImageCacheWriteMetadata($"\"{index}\"", null, "image/png"))));

        GitHubImageCacheRead? current = await store.TryReadAsync("account:same-key");
        Assert.NotNull(current);
        Assert.Contains(payloads, payload => payload.SequenceEqual(current!.Bytes));
        Assert.Equal(CacheOwnerHealth.Degraded, (await store.InspectAsync()).Health);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CacheStore_ClearReadOnlyFileReportsPartialFailureAndPassesAfterAttributeReset()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        GitHubImageCacheEntry entry = await store.PutAsync("read-only-clear", PngBytes, ".png");
        File.SetAttributes(entry.FilePath, File.GetAttributes(entry.FilePath) | FileAttributes.ReadOnly);
        try
        {
            CacheClearPostconditionException exception = await Assert.ThrowsAsync<CacheClearPostconditionException>(
                () => store.ClearAllAsync());
            Assert.Contains(exception.Residuals, residual =>
                string.Equals(residual.Identity, entry.FilePath, StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(entry.FilePath));
        }
        finally
        {
            if (File.Exists(entry.FilePath))
            {
                File.SetAttributes(entry.FilePath, FileAttributes.Normal);
            }
        }

        await store.ClearAllAsync();
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void SharedRequest_RetiresAtomicallyBeforeLateWaiterCanJoin()
    {
        GitHubImageService.SharedImageRequest request = new(
            _ => Task.FromResult<GitHubCachedImage?>(null));

        Assert.True(request.TryAddWaiter());
        GitHubImageService.SharedImageRequestRelease release = request.ReleaseWaiter(taskCompleted: false);

        Assert.True(release.ShouldRetire);
        Assert.True(release.ShouldCancel);
        Assert.False(request.TryAddWaiter());
        request.Cancel();
        request.DisposeWhenComplete();
    }

    [Theory]
    [InlineData("http://example.test/image.png", "SharedHttps")]
    [InlineData("https://example.test/image.png", "SharedHttps")]
    [InlineData("images/local.png", "NotHandled")]
    [InlineData("ms-appx:///Assets/image.png", "NotHandled")]
    public void MarkdownImageSourcePolicy_UsesHttpsOnly(
        string source,
        string expectedName)
    {
        MarkdownImageSourceDisposition expected = Enum.Parse<MarkdownImageSourceDisposition>(expectedName);
        MarkdownImageSourceDisposition actual =
            MarkdownImageSourcePolicy.ClassifyUnownedSource(source, out Uri? absoluteUri);

        Assert.Equal(expected, actual);
        Assert.Equal(Uri.TryCreate(source, UriKind.Absolute, out _), absoluteUri is not null);
        if (source.StartsWith("http://", StringComparison.Ordinal))
        {
            Assert.Equal(Uri.UriSchemeHttps, absoluteUri!.Scheme);
            Assert.Equal("https://example.test/image.png", absoluteUri.AbsoluteUri);
        }
    }

    [Fact]
    public async Task GetOrFetchAsync_AcceptsEveryAdvertisedMarkdownImageFormat()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new CountingHandler(PngBytes));
        using GitHubImageService service = new(store, client);
        (string ContentType, byte[] Bytes)[] formats =
        [
            ("image/bmp", [0x42, 0x4D, 1, 2]),
            ("image/x-icon", [0, 0, 1, 0, 1, 2]),
            ("image/tiff", [(byte)'I', (byte)'I', 0x2A, 0, 1, 2])
        ];

        foreach ((string contentType, byte[] bytes) in formats)
        {
            GitHubCachedImage? image = await service.GetOrFetchAsync(
                $"https://content.example.com/{Guid.NewGuid():N}",
                (_, _) => Task.FromResult<GitHubImageDownload?>(new GitHubImageDownload(bytes, contentType)));
            Assert.NotNull(image);
            Assert.True(File.Exists(image!.FilePath));
        }
    }

    [Fact]
    public async Task GetAsync_DoesNotContactArbitraryHttpsHostsByDefault()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync("https://images.example.com/tracker.png");

        Assert.Null(image);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_UserApprovedHttpsScope_AllowsPublicThirdPartyHost()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://images.example.com/readme.png",
            GitHubImageFetchScope.UserApprovedHttps);

        Assert.NotNull(image);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/owner/repo/main/image.png")]
    [InlineData("https://localhost/image.png")]
    [InlineData("https://127.0.0.1/image.png")]
    [InlineData("https://10.0.0.8/image.png")]
    [InlineData("https://100.64.0.8/image.png")]
    [InlineData("https://192.0.2.8/image.png")]
    [InlineData("https://198.18.0.8/image.png")]
    [InlineData("https://198.51.100.8/image.png")]
    [InlineData("https://203.0.113.8/image.png")]
    [InlineData("https://[::1]/image.png")]
    [InlineData("https://[2001:db8::8]/image.png")]
    [InlineData("https://[fc00::8]/image.png")]
    [InlineData("https://host.local/image.png")]
    [InlineData("https://host.internal/image.png")]
    public async Task GetAsync_NeverContactsInsecureOrPrivateDestinations(string source)
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            source,
            GitHubImageFetchScope.UserApprovedHttps);

        Assert.Null(image);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_TrustedRequestCannotRedirectToThirdParty()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectHandler handler = new("https://images.example.com/tracker.png");
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.GetAsync("https://raw.githubusercontent.com/owner/repo/main/image.png"));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RetriesOneTransientStatusAndCachesSuccessfulResponse()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        TransientStatusThenImageHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://github.com/owner/repository/releases/download/assets/image.png");

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Bytes);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RecoversAfterTwoTransientStatuses()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        TransientStatusThenImageHandler handler = new(PngBytes, failuresBeforeSuccess: 2);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://camo.githubusercontent.com/retry-example/image.png");

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Bytes);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RetriesOneTransportFailureAndCachesSuccessfulResponse()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        TransientExceptionThenImageHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://raw.githubusercontent.com/owner/repository/main/image.png");

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Bytes);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_DoesNotRetryPermanentHttpStatus()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        AlwaysStatusHandler handler = new(HttpStatusCode.NotFound);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAsync(
            "https://raw.githubusercontent.com/owner/repository/main/missing.png"));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_StopsAfterTwoTransientRetries()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        AlwaysStatusHandler handler = new(HttpStatusCode.ServiceUnavailable);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAsync(
            "https://raw.githubusercontent.com/owner/repository/main/unavailable.png"));

        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task GetOrFetchAsync_RetriesTransientGitHubApiFailure()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new CountingHandler(PngBytes));
        using GitHubImageService service = new(store, client);
        int attempts = 0;

        GitHubCachedImage? image = await service.GetOrFetchAsync(
            "https://raw.githubusercontent.com/owner/repository/ref/assets/banner.png",
            (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new GitHubApiException(
                        HttpStatusCode.ServiceUnavailable,
                        "Transient repository content failure.");
                }

                return Task.FromResult<GitHubImageDownload?>(
                    new GitHubImageDownload(PngBytes, "image/png"));
            });

        Assert.NotNull(image);
        Assert.Equal(PngBytes, image!.Bytes);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task GetOrFetchAsync_DoesNotRetryPermanentGitHubApiFailure()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new CountingHandler(PngBytes));
        using GitHubImageService service = new(store, client);
        int attempts = 0;

        await Assert.ThrowsAsync<GitHubApiException>(() => service.GetOrFetchAsync(
            "https://raw.githubusercontent.com/owner/repository/ref/assets/missing.png",
            (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new GitHubApiException(HttpStatusCode.NotFound, "Not found.");
            }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task GetAsync_TrustedUserAttachmentCanFollowGitHubSignedAssetRedirect()
    {
        const string signedAsset =
            "https://github-production-user-asset-6210df.s3.amazonaws.com/1/2.svg" +
            "?X-Amz-Credential=github%2Frequest&X-Amz-Signature=abc123";
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectThenImageHandler handler = new(signedAsset, PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://github.com/user-attachments/assets/9955dda9-1234-5678-9abc-def012345678");

        Assert.NotNull(image);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_TrustedLegacyRepositoryAssetCanFollowGitHubSignedAssetRedirect()
    {
        const string signedAsset =
            "https://github-production-user-asset-6210df.s3.amazonaws.com/1/2.svg" +
            "?X-Amz-Credential=github%2Frequest&X-Amz-Signature=abc123";
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectThenImageHandler handler = new(signedAsset, PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://github.com/owner/repository/assets/13230914/5a17bdbe-097a-4100-8363-40255b70f6e3");

        Assert.NotNull(image);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData("https://github.com/owner/repository/assets/not-a-number/5a17bdbe-097a-4100-8363-40255b70f6e3")]
    [InlineData("https://github.com/owner/repository/assets/13230914/not-a-guid")]
    [InlineData("https://github.com/owner/repository/issues/assets/13230914/5a17bdbe-097a-4100-8363-40255b70f6e3")]
    public async Task GetAsync_TrustedMalformedLegacyRepositoryAssetRejectsSignedRedirect(string source)
    {
        const string signedAsset =
            "https://github-production-user-asset-6210df.s3.amazonaws.com/1/2.svg" +
            "?X-Amz-Credential=github%2Frequest&X-Amz-Signature=abc123";
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectHandler handler = new(signedAsset);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(source));
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("https://github-production-user-asset-6210df.s3.amazonaws.com/1/2.svg")]
    [InlineData("https://github-production-user-asset-6210df.s3.amazonaws.com/1/2.svg?X-Amz-Signature=abc123")]
    [InlineData("https://github-production-user-asset-evil.s3.amazonaws.com/1/2.svg?X-Amz-Credential=x&X-Amz-Signature=y")]
    public async Task GetAsync_TrustedUserAttachmentRejectsUnsignedOrUnknownAssetRedirect(
        string destination)
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectHandler handler = new(destination);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://github.com/user-attachments/assets/9955dda9-1234-5678-9abc-def012345678"));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_UserApprovedRequestCannotRedirectToPrivateAddress()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectHandler handler = new("https://127.0.0.1/tracker.png");
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/readme.png",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RejectsHostNameThatResolvesToPrivateAddress()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(
            store,
            client,
            hostAddressResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.8") }));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/readme.png",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RejectsMixedPublicAndPrivateDnsAnswers()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        CountingHandler handler = new(PngBytes);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(
            store,
            client,
            hostAddressResolver: (_, _) => Task.FromResult(new[]
            {
                IPAddress.Parse("8.8.8.8"),
                IPAddress.Parse("fd00::8")
            }));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/readme.png",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RejectsRedirectHostNameThatResolvesToPrivateAddress()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectHandler handler = new("https://private.example.com/tracker.png");
        using HttpClient client = new(handler);
        using GitHubImageService service = new(
            store,
            client,
            hostAddressResolver: (host, _) => Task.FromResult(new[]
            {
                IPAddress.Parse(host.Equals("private.example.com", StringComparison.OrdinalIgnoreCase)
                    ? "192.168.1.20"
                    : "8.8.8.8")
            }));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/readme.png",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_StopsAfterBoundedRedirectCount()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        LoopingRedirectHandler handler = new();
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/loop-0.png",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Equal(4, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_RejectsOversizedPayloadBeforeCaching()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new OversizedImageHandler());
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/oversized.png",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    public async Task GetAsync_RejectsIncorrectDeclaredImageLength(int declaredLength)
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new DeclaredLengthImageHandler(declaredLength));
        using GitHubImageService service = new(store, client);

        Task<GitHubCachedImage?> fetch = service.GetAsync(
            $"https://images.example.com/length-{declaredLength}.png",
            GitHubImageFetchScope.UserApprovedHttps);
        if (declaredLength < PngBytes.Length)
            await Assert.ThrowsAsync<InvalidDataException>(() => fetch);
        else
            await Assert.ThrowsAsync<EndOfStreamException>(() => fetch);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("image/svg+xml", "<html><body>not svg</body></html>")]
    [InlineData("image/png", "not a png")]
    public async Task GetAsync_RejectsMismatchedImageSignatures(string contentType, string body)
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new RawImageHandler(contentType, System.Text.Encoding.UTF8.GetBytes(body)));
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/malformed",
            GitHubImageFetchScope.UserApprovedHttps));
    }

    [Fact]
    public async Task GetAsync_RetriesTruncatedCamoSvgWithoutCachingOrLeakingAdmission()
    {
        const string source = "https://camo.githubusercontent.com/example-badge.svg";
        byte[] truncated = "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>cut"u8.ToArray();
        byte[] complete = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"124\" height=\"20\"></svg>"u8.ToArray();
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        var handler = new SequencedSvgHandler(truncated, complete);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);
        var admission = new TrackingAdmission(4096);

        GitHubCachedImage? image = await service.GetAsync(
            source, GitHubImageFetchScope.TrustedGitHub, admission);
        GitHubCachedImage? cached = await service.GetAsync(
            source, GitHubImageFetchScope.TrustedGitHub, admission);

        Assert.NotNull(image);
        Assert.Equal(complete, image.Bytes);
        Assert.Equal(complete, await File.ReadAllBytesAsync(image.FilePath));
        Assert.True(cached!.IsFromCache);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(0, admission.ActiveBytes);
    }

    [Fact]
    public async Task GetAsync_AcceptsLegacyExternalDoctypeCamoSvgWithoutRetry()
    {
        const string source = "https://camo.githubusercontent.com/legacy-creative-commons.svg";
        byte[] complete = System.Text.Encoding.UTF8.GetBytes("<?xml version='1.0' encoding='utf-8'?>" +
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.0//EN' " +
            "'http://www.w3.org/TR/2001/REC-SVG-20010904/DTD/svg10.dtd'>" +
            "<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'>" +
            "<rect width='64' height='64'/></svg>");
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        var handler = new SequencedSvgHandler(complete);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            source, GitHubImageFetchScope.TrustedGitHub);
        GitHubCachedImage? cached = await service.GetAsync(
            source, GitHubImageFetchScope.TrustedGitHub);

        Assert.NotNull(image);
        Assert.Equal(complete, image.Bytes);
        Assert.True(cached!.IsFromCache);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_AcceptsLegacyExternalDoctypeFromSignedUserAttachment()
    {
        const string signedAsset =
            "https://github-production-user-asset-6210df.s3.amazonaws.com/1/2.svg" +
            "?X-Amz-Credential=github%2Frequest&X-Amz-Signature=abc123";
        byte[] complete = System.Text.Encoding.UTF8.GetBytes(
            "<!DOCTYPE svg PUBLIC '-//W3C//DTD SVG 1.1//EN' " +
            "'http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd'>" +
            "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='8' />");
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        RedirectThenImageHandler handler = new(signedAsset, complete, "image/svg+xml");
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://github.com/user-attachments/assets/9955dda9-1234-5678-9abc-def012345678");

        Assert.NotNull(image);
        Assert.Equal(complete, image.Bytes);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_ExhaustsBoundedMalformedCamoSvgRetryWithoutCaching()
    {
        byte[] truncated = "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>cut"u8.ToArray();
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        var handler = new SequencedSvgHandler(truncated);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);
        var admission = new TrackingAdmission(4096);

        await Assert.ThrowsAnyAsync<IOException>(() => service.GetAsync(
            "https://camo.githubusercontent.com/always-truncated.svg",
            GitHubImageFetchScope.TrustedGitHub,
            admission));

        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(0, admission.ActiveBytes);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task GetAsync_DoesNotRetryMalformedSvgFromUnrelatedHost()
    {
        byte[] truncated = "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>cut"u8.ToArray();
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        var handler = new SequencedSvgHandler(truncated);
        using HttpClient client = new(handler);
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/invalid.svg",
            GitHubImageFetchScope.UserApprovedHttps));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_AcceptsRasterSignatureDespiteSvgMediaType()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new RawImageHandler("image/svg+xml", PngBytes));
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://camo.githubusercontent.com/mislabeled-raster",
            GitHubImageFetchScope.TrustedGitHub);

        Assert.NotNull(image);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(PngBytes, image.Bytes);
    }

    [Fact]
    public async Task GetAsync_AcceptsSignatureValidatedJpegWithGenericMediaType()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xD9];
        using HttpClient client = new(new RawImageHandler("application/octet-stream", jpeg));
        using GitHubImageService service = new(store, client);

        GitHubCachedImage? image = await service.GetAsync(
            "https://images.example.com/release-asset",
            GitHubImageFetchScope.UserApprovedHttps);

        Assert.NotNull(image);
        Assert.Equal("image/jpeg", image!.ContentType);
        Assert.Equal(jpeg, image.Bytes);
    }

    [Fact]
    public async Task GetAsync_RejectsGenericMediaTypeWithoutSupportedImageSignature()
    {
        GitHubImageCacheStore store = new(_root, GitHubCachePolicy.Default);
        using HttpClient client = new(new RawImageHandler(
            "application/octet-stream",
            "<html>not an image</html>"u8.ToArray()));
        using GitHubImageService service = new(store, client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetAsync(
            "https://images.example.com/release-asset",
            GitHubImageFetchScope.UserApprovedHttps));
    }

    [Fact]
    public async Task GetRenderedReadmeHtmlAsync_RequestsGitHubRenderedRepresentation()
    {
        const string html = "<article><img src=\"rendered.png\"></article>";
        var handler = new RenderedReadmeHandler(html);
        using HttpClient client = new(handler);
        var service = new GitHubClientService(client);

        string result = await service.GetRenderedReadmeHtmlAsync(
            "token",
            "owner",
            "repository",
            "immutable-sha");

        Assert.Equal(html, result);
        Assert.Equal(
            "https://api.github.com/repos/owner/repository/readme?ref=immutable-sha",
            handler.RequestUri);
        Assert.Contains("application/vnd.github.html+json", handler.Accept);
    }

    [Fact]
    public async Task GetRenderedReadmeHtmlAsync_PublicReadmeRetriesWithoutTokenAfterPolicyForbidden()
    {
        const string html = "<article><img src=\"camo.png\"></article>";
        var handler = new AuthPolicyRenderedReadmeHandler(html);
        using HttpClient client = new(handler);
        var service = new GitHubClientService(client);

        string result = await service.GetRenderedReadmeHtmlAsync(
            "installation-token",
            "public-owner",
            "public-repository",
            "immutable-sha");

        Assert.Equal(html, result);
        Assert.Equal(2, handler.AuthorizationSchemes.Count);
        Assert.Equal("Bearer", handler.AuthorizationSchemes[0]);
        Assert.Null(handler.AuthorizationSchemes[1]);
        Assert.All(handler.AcceptValues, static accept =>
            Assert.Contains("application/vnd.github.html+json", accept));
    }

    [Fact]
    public void ParseGitHubCamoImageMap_AdmitsHttpOrHttpsOriginalOnlyThroughCanonicalHttpsCamo()
    {
        const string source = "https://assets.example.test/image.png?one=1&two=2";
        const string camo = "https://camo.githubusercontent.com/hash/encoded";
        string html =
            $"<img data-canonical-src='https://assets.example.test/image.png?one=1&amp;two=2' src='{camo}'>" +
            $"<img data-canonical-src='http://legacy.example.test/image.gif' src='{camo}/legacy'>" +
            "<img src=\"https://evil.example.test/image.png\" data-canonical-src=\"https://assets.example.test/evil.png\">";

        IReadOnlyDictionary<string, string> map = GitHubCamoImageMapParser.Parse(html);

        Assert.Equal(2, map.Count);
        Assert.Equal(camo, map[source]);
        Assert.Equal($"{camo}/legacy", map["http://legacy.example.test/image.gif"]);
    }

    [Fact]
    public void DecodeGitHubCamoCanonicalSource_RecoversBoundedHttpsOrigin()
    {
        const string origin = "https://asciinema.org/a/736339.svg?download=1";
        string encoded = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(origin)).ToLowerInvariant();
        var camo = new Uri(
            "https://camo.githubusercontent.com/" + new string('a', 64) + "/" + encoded);

        Assert.True(GitHubCamoImageMapParser.TryDecodeCanonicalSource(camo, out Uri decoded));
        Assert.Equal(origin, decoded.AbsoluteUri);
    }

    [Fact]
    public async Task MarkdownImageFallback_TransportTimeoutContinuesToIndependentOrigin()
    {
        int attempts = 0;
        int reportedAttempt = -1;

        string? result = await MarkdownImageFallbackPipeline.FirstSuccessfulAsync(
            _ =>
            {
                attempts++;
                return Task.FromException<string?>(
                    new TaskCanceledException("Simulated HttpClient timeout."));
            },
            _ =>
            {
                attempts++;
                return Task.FromResult<string?>("canonical-origin");
            },
            (attempt, _) => reportedAttempt = attempt,
            CancellationToken.None);

        Assert.Equal("canonical-origin", result);
        Assert.Equal(2, attempts);
        Assert.Equal(0, reportedAttempt);
    }

    [Fact]
    public async Task MarkdownImageFallback_CallerCancellationNeverStartsNextAttempt()
    {
        using CancellationTokenSource cancellation = new();
        bool secondStarted = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await MarkdownImageFallbackPipeline.FirstSuccessfulAsync(
                token =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<string?>(token);
                },
                _ =>
                {
                    secondStarted = true;
                    return Task.FromResult<string?>("must-not-run");
                },
                (_, _) => { },
                cancellation.Token));

        Assert.False(secondStarted);
    }

    [Fact]
    public async Task MarkdownImageFallback_HedgeUsesFastPrimaryWithoutOriginRequest()
    {
        bool originStarted = false;
        string? result = await MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            _ => Task.FromResult<string?>("camo"),
            _ =>
            {
                originStarted = true;
                return Task.FromResult<string?>("origin");
            },
            (_, _) => { },
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal("camo", result);
        Assert.False(originStarted);
    }

    [Fact]
    public async Task MarkdownImageFallback_HedgeUsesOriginWhilePrimaryIsStalled()
    {
        CancellationToken primaryToken = default;
        int failures = 0;
        string? result = await MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            async token =>
            {
                primaryToken = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "camo";
            },
            _ => Task.FromResult<string?>("origin"),
            (_, _) => Interlocked.Increment(ref failures),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        // Inspect the canceled token itself, without making this behavioral
        // assertion depend on a separate callback's completion signal.
        Assert.True(primaryToken.CanBeCanceled);
        Assert.True(primaryToken.IsCancellationRequested);
        Assert.Equal("origin", result);
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task MarkdownImageFallback_FailedHedgeStillAcceptsLatePrimary()
    {
        var primary = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> resultTask = MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            _ => primary.Task,
            _ => Task.FromException<string?>(new IOException("Origin failed.")),
            (_, _) => { },
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.False(resultTask.IsCompleted);
        primary.SetResult("camo");
        Assert.Equal("camo", await resultTask);
    }

    [Fact]
    public async Task MarkdownImageFallback_HedgePropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        Task<string?> resultTask = MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "camo";
            },
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "origin";
            },
            (_, _) => { },
            TimeSpan.Zero,
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resultTask);
    }

    [Fact]
    public void DecodeGitHubCamoCanonicalSource_UpgradesLegacyHttpOriginToHttps()
    {
        const string origin = "http://hits.dwyl.com/996icu/996ICU.svg";
        string encoded = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(origin)).ToLowerInvariant();
        var camo = new Uri(
            "https://camo.githubusercontent.com/" + new string('a', 64) + "/" + encoded);

        Assert.True(GitHubCamoImageMapParser.TryDecodeCanonicalSource(camo, out Uri decoded));
        Assert.Equal("https://hits.dwyl.com/996icu/996ICU.svg", decoded.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://camo.githubusercontent.com/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/68747470733a2f2f6578616d706c652e746573742f612e706e67")]
    [InlineData("https://evil.example/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/68747470733a2f2f6578616d706c652e746573742f612e706e67")]
    [InlineData("https://camo.githubusercontent.com/short/68747470733a2f2f6578616d706c652e746573742f612e706e67")]
    [InlineData("https://camo.githubusercontent.com/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/6674703a2f2f6578616d706c652e746573742f612e706e67")]
    [InlineData("https://camo.githubusercontent.com/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/not-hex")]
    public void DecodeGitHubCamoCanonicalSource_RejectsNonCanonicalOrInsecureValues(string value)
    {
        Assert.False(GitHubCamoImageMapParser.TryDecodeCanonicalSource(new Uri(value), out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CountingHandler(byte[] bytes, TimeSpan? delay = null) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (delay is TimeSpan value)
            {
                await Task.Delay(value, cancellationToken);
            }

            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class RevalidationHandler(byte[] bytes) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public bool SawConditionalRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber > 1)
            {
                SawConditionalRequest = request.Headers.IfNoneMatch.Count > 0;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
            response.Headers.ETag = new("\"avatar-v1\"");
            return Task.FromResult(response);
        }
    }

    private sealed class NonImageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            StringContent content = new("<html>not an image</html>");
            content.Headers.ContentType = new("text/html");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancellation test request unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class FailingRefreshHandler(byte[] bytes) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) > 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class TransientStatusThenImageHandler(
        byte[] bytes,
        int failuresBeforeSuccess = 1) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) <= failuresBeforeSuccess)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class TransientExceptionThenImageHandler(byte[] bytes) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Simulated HTTP/2 stream reset."));
            }

            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class AlwaysStatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private sealed class ReplacingImageHandler(byte[] first, byte[] replacement) : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[] bytes = Interlocked.Increment(ref _requestCount) == 1 ? first : replacement;
            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class RedirectHandler(string destination) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(destination);
            return Task.FromResult(response);
        }
    }

    private sealed class LoopingRedirectHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int requestNumber = Interlocked.Increment(ref _requestCount);
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri($"https://images.example.com/loop-{requestNumber}.png");
            return Task.FromResult(response);
        }
    }

    private sealed class OversizedImageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ByteArrayContent content = new(PngBytes);
            content.Headers.ContentType = new("image/png");
            content.Headers.ContentLength = 32L * 1024 * 1024 + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class RawImageHandler(string contentType, byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class SequencedSvgHandler(params byte[][] payloads) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref _requestCount) - 1;
            ByteArrayContent content = new(payloads[Math.Min(index, payloads.Length - 1)]);
            content.Headers.ContentType = new("image/svg+xml");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class UnknownLengthImageHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            UnknownLengthContent content = new(bytes);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class AdmissionAwareHandler(
        byte[] bytes,
        bool knownLength,
        Action onStreamCreated) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new AdmissionAwareContent(bytes, knownLength, onStreamCreated),
            });
    }

    private sealed class AdmissionAwareContent(
        byte[] bytes,
        bool knownLength,
        Action onStreamCreated) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = knownLength ? bytes.Length : 0;
            return knownLength;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            onStreamCreated();
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class GatedImageHandler(
        byte[] bytes,
        GatedImageAdmission admission,
        bool knownLength = true) : HttpMessageHandler
    {
        private int _requestCount;
        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new GatedImageContent(bytes, admission, knownLength),
            });
        }
    }

    private sealed class GatedImageContent(
        byte[] bytes,
        GatedImageAdmission admission,
        bool knownLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = knownLength ? bytes.Length : 0;
            return knownLength;
        }

        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            // Fill the configured admission before any body can finish, so
            // concurrent readers exercise the budget and lease handoff.
            await admission.WaitUntilFullAsync();
            return new MemoryStream(bytes, writable: false);
        }
    }

    private sealed class GatedImageAdmission(int imageBytes, int concurrentImages)
        : IMarkdownImageSourceByteAdmission, IDisposable
    {
        private readonly SemaphoreSlim _slots = new(concurrentImages, concurrentImages);
        private readonly TaskCompletionSource<bool> _full =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _activeBytes;
        private long _peakBytes;

        public bool IsSpeculative => false;
        public long MaximumReservationBytes => (long)imageBytes * concurrentImages;
        internal long ActiveBytes => Interlocked.Read(ref _activeBytes);
        internal long PeakBytes => Interlocked.Read(ref _peakBytes);

        internal Task WaitUntilFullAsync() => _full.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public async ValueTask<IDisposable> ReserveAsync(
            long bytes,
            CancellationToken cancellationToken)
        {
            Assert.InRange(bytes, 1, imageBytes);
            await _slots.WaitAsync(cancellationToken);
            long active = Interlocked.Add(ref _activeBytes, bytes);
            long peak;
            while ((peak = Interlocked.Read(ref _peakBytes)) < active &&
                   Interlocked.CompareExchange(ref _peakBytes, active, peak) != peak)
            {
            }
            if (active == MaximumReservationBytes)
                _full.TrySetResult(true);
            return new GatedImageLease(this, bytes);
        }

        private void Release(long bytes)
        {
            Interlocked.Add(ref _activeBytes, -bytes);
            _slots.Release();
        }

        public void Dispose() => _slots.Dispose();

        private sealed class GatedImageLease(GatedImageAdmission owner, long bytes) : IDisposable
        {
            private GatedImageAdmission? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(bytes);
        }
    }

    private sealed class TrackingAdmission(long maximumBytes, bool isSpeculative = false)
        : IMarkdownImageSourceByteAdmission
    {
        private long _activeBytes;
        private long _peakBytes;
        private int _reservations;

        public bool IsSpeculative => isSpeculative;
        public long MaximumReservationBytes => maximumBytes;
        internal long ActiveBytes => Interlocked.Read(ref _activeBytes);
        internal long PeakBytes => Interlocked.Read(ref _peakBytes);
        internal int Reservations => Volatile.Read(ref _reservations);

        public ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes > maximumBytes && isSpeculative)
                throw new MarkdownImageSourceDeferredException();
            if (bytes < 1 || bytes > maximumBytes)
                throw new ArgumentOutOfRangeException(nameof(bytes));
            long active = Interlocked.Add(ref _activeBytes, bytes);
            Interlocked.Increment(ref _reservations);
            long observed;
            while ((observed = Interlocked.Read(ref _peakBytes)) < active &&
                   Interlocked.CompareExchange(ref _peakBytes, active, observed) != observed)
            {
            }
            return ValueTask.FromResult<IDisposable>(new TrackingLease(this, bytes));
        }

        private sealed class TrackingLease(TrackingAdmission owner, long bytes) : IDisposable
        {
            private TrackingAdmission? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?
                .Release(bytes);
        }

        private void Release(long bytes) => Interlocked.Add(ref _activeBytes, -bytes);
    }

    private sealed class MutatingAdmission(Func<Task> mutate) : IMarkdownImageSourceByteAdmission
    {
        private readonly TrackingAdmission _inner = new(1024);
        private int _reservations;

        public bool IsSpeculative => false;
        public long MaximumReservationBytes => _inner.MaximumReservationBytes;
        internal int Reservations => Volatile.Read(ref _reservations);
        internal long PeakBytes => _inner.PeakBytes;
        internal long ActiveBytes => _inner.ActiveBytes;

        public async ValueTask<IDisposable> ReserveAsync(long bytes, CancellationToken cancellationToken)
        {
            IDisposable lease = await _inner.ReserveAsync(bytes, cancellationToken);
            if (Interlocked.Increment(ref _reservations) == 1)
            {
                try
                {
                    await mutate();
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }
            }

            return lease;
        }
    }

    private sealed class DeclaredLengthImageHandler(int declaredLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ByteArrayContent content = new(PngBytes);
            content.Headers.ContentType = new("image/png");
            content.Headers.ContentLength = declaredLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class RedirectThenImageHandler(
        string destination,
        byte[] bytes,
        string contentType = "application/octet-stream") : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber == 1)
            {
                HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri(destination);
                return Task.FromResult(redirect);
            }

            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class RenderedReadmeHandler(string html) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string Accept { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.AbsoluteUri;
            Accept = request.Headers.Accept.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
            });
        }
    }

    private sealed class AuthPolicyRenderedReadmeHandler(string html) : HttpMessageHandler
    {
        public List<string?> AuthorizationSchemes { get; } = [];
        public List<string> AcceptValues { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme);
            AcceptValues.Add(request.Headers.Accept.ToString());
            if (AuthorizationSchemes.Count == 1)
            {
                var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        "{\"message\":\"Resource not accessible by integration\"}",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
                forbidden.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "4999");
                return Task.FromResult(forbidden);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
            });
        }
    }
}
