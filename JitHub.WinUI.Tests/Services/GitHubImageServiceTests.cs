using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
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
    [InlineData("http://example.test/image.png", "BlockedInsecureRemote")]
    [InlineData("https://example.test/image.png", "SharedHttps")]
    [InlineData("images/local.png", "NotHandled")]
    [InlineData("ms-appx:///Assets/image.png", "NotHandled")]
    public void MarkdownImageSourcePolicy_PreventsHttpFallback(
        string source,
        string expectedName)
    {
        MarkdownImageSourceDisposition expected = Enum.Parse<MarkdownImageSourceDisposition>(expectedName);
        MarkdownImageSourceDisposition actual =
            MarkdownImageSourcePolicy.ClassifyUnownedSource(source, out Uri? absoluteUri);

        Assert.Equal(expected, actual);
        Assert.Equal(Uri.TryCreate(source, UriKind.Absolute, out _), absoluteUri is not null);
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
            content.Headers.ContentLength = 10L * 1024 * 1024 + 1;
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
}
