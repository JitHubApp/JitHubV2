using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Creates sessions and leased results and waits for conditions and
/// collection for the performance source lease tests.
/// </summary>
internal static class MarkdownPerformanceSourceLeaseFixture
{
    internal static MarkdownPerformanceSession CreateSession(
        long maxSourceBytes,
        long sourceCacheBytes,
        int maxFetches = 2) => new(MarkdownPerformanceOptions.Progressive with
        {
            MaxConcurrentImageFetches = maxFetches,
            ReservedVisibleImageFetches = 1,
            MaxInFlightSourceBytes = maxSourceBytes,
            ReservedVisibleSourceBytes = System.Math.Max(1, maxSourceBytes / 4),
            SourceCacheBudgetBytes = sourceCacheBytes,
        });

    internal static async ValueTask<MarkdownImageResolutionLease> CreateLeasedResultAsync(
        string source,
        int byteLength,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken,
        Func<byte[]> createBytes)
    {
        IDisposable? reservation = null;
        try
        {
            reservation = await admission.ReserveAsync(byteLength, cancellationToken);
            byte[] bytes = createBytes();
            if (bytes.Length != byteLength)
                throw new InvalidOperationException("The test resolver allocated a payload of the wrong size.");
            var resolution = MarkdownImageResolution.Resolved(
                new MarkdownImageAsset(bytes, "image/png", CacheKey: source));
            var result = new MarkdownImageResolutionLease(resolution, reservation);
            reservation = null;
            return result;
        }
        finally
        {
            reservation?.Dispose();
        }
    }

    internal static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("The expected source-byte admission state was not reached.");
            await Task.Delay(10);
        }
    }

    internal static async Task DisposeWhenCompleteAsync(Task<IMarkdownImageResolutionLease> task)
    {
        try
        {
            (await task.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        }
        catch
        {
            // Cleanup path for a failed assertion: no test-owned source lease
            // may escape and keep await-using session retirement blocked.
        }
    }

    internal static async Task AssertCollectedAsync(WeakReference<byte[]> reference)
    {
        for (int attempt = 0; attempt < 12 && IsAlive(reference); attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            await Task.Delay(10);
        }

        Assert.False(IsAlive(reference));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool IsAlive(WeakReference<byte[]> reference) => reference.TryGetTarget(out _);
}
