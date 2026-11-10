using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images.Caching;
using JitHub.Services;
using MarkdownRenderer.Images;
using Xunit;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceHttpDoubles;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceAdmissionDoubles;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// Atomic commits, inspection, identity and concurrency of the image cache store behind
/// <c>GitHubImageService</c>.
/// </summary>
public sealed class GitHubImageServiceCacheStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubImageServiceTests", Guid.NewGuid().ToString("N"));

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
        string first = GitHubImageAccountScope.NormalizeCacheIdentity(
            "HTTPS://AVATARS.GITHUBUSERCONTENT.COM:443/u/1.png?size=80#fragment");
        string second = GitHubImageAccountScope.NormalizeCacheIdentity(
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
