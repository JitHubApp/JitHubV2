using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Services.GitHub.Caching;
using JitHub.Core.Services.Images;
using JitHub.Core.Services.Images.Caching;
using JitHub.Markdown.Images.Caching;
using MarkdownRenderer.Images;
using Xunit;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceHttpDoubles;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceAdmissionDoubles;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// How <c>GitHubImageService</c> reserves image bytes through an admission before it
/// materializes them, for network, cache and cache-only reads.
/// </summary>
public sealed class GitHubImageServiceAdmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubImageServiceTests", Guid.NewGuid().ToString("N"));

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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
