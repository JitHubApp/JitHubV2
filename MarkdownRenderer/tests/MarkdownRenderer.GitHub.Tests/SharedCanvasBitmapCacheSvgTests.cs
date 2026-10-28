using System.Diagnostics.Tracing;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Utilities;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedCanvasBitmapCacheTestCollection
{
    public const string Name = "Shared canvas bitmap cache";
}

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class SharedCanvasBitmapCacheSvgTests
{
    [Theory]
    [InlineData(1.25, 2.25, 12L)]
    [InlineData(1.5, 1.5, 9L)]
    [InlineData(0.5, 0.5, 4L)]
    public void RasterCacheWeightUsesTheDoublePixelAreaCeiling(
        double width,
        double height,
        long expectedBytes)
    {
        Assert.Equal(expectedBytes, SharedCanvasBitmapCache.EstimateWeightBytes(width, height));
    }

    [Theory]
    [InlineData("", 48L)]
    [InlineData("A", 50L)]
    [InlineData("\uD83E\uDDED", 52L)]
    public void CacheKeyWeightUsesUtf16IdentityStorage(string identity, long expectedBytes)
    {
        Assert.Equal(
            expectedBytes,
            SharedCanvasBitmapCache.EstimateCacheKeyWeightBytes(identity));
    }

    [Fact]
    public void InvalidRasterDimensionsUseFallbackWeightAndAreNotPreviewCandidates()
    {
        using var device = new CanvasDevice();
        long retainedBefore = SharedCanvasBitmapCache.RetainedBytesForTests;
        string sourceIdentity = $"disposed-raster-cache-{Guid.NewGuid():N}";
        var sizing = new RasterImageSizingRequest(4, 1, null, null, 16);
        string family = sizing.GetCacheFamily(sourceIdentity, true);
        string displayIdentity = sizing.GetCacheKey(family, (2, 1));
        CanvasBitmap bitmap = CreateBitmap(device, width: 2, height: 1);
        bitmap.Dispose();
        SharedCanvasBitmapCache.Lease? lease = null;

        try
        {
            lease = SharedCanvasBitmapCache.StoreAndAcquire(
                device,
                displayIdentity,
                bitmap,
                intrinsicSize: new Size(2, 1));

            Assert.Equal(
                retainedBefore +
                    (1L * 1024 * 1024) +
                    SharedCanvasBitmapCache.EstimateCacheKeyWeightBytes(displayIdentity),
                SharedCanvasBitmapCache.RetainedBytesForTests);
            Assert.False(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device,
                family,
                sizing,
                out _));
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            lease?.Dispose();
        }

        Assert.Equal(retainedBefore, SharedCanvasBitmapCache.RetainedBytesForTests);
    }

    [Fact]
    public void ValidatedSvgDimensionsProvideExactWeightPreviewSizeAndLeaseLifetime()
    {
        using var device = new CanvasDevice();
        long retainedBefore = SharedCanvasBitmapCache.RetainedBytesForTests;
        string sourceIdentity = $"svg-cache-preview-{Guid.NewGuid():N}";
        var sizing = new RasterImageSizingRequest(4, 1, null, null, 16);
        string family = sizing.GetCacheFamily(sourceIdentity, true);
        string displayIdentity = sizing.GetCacheKey(family, (2, 1));
        SharedCanvasBitmapCache.Lease? stored = null;
        SharedCanvasBitmapCache.Lease? preview = null;
        SharedCanvasBitmapCache.Lease? duplicate = null;
        SharedCanvasBitmapCache.Lease? inFlightCopy = null;
        try
        {
            CanvasBitmap bitmap = CreateBitmap(device, width: 2, height: 1);
            stored = SharedCanvasBitmapCache.StoreSvgAndAcquire(
                device,
                displayIdentity,
                bitmap,
                intrinsicSize: new Size(4, 2),
                pixelWidth: 2,
                pixelHeight: 1
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                , svgWorkId: 0,
                svgSourceBytes: 0,
                svgTargetPixels: 0
#endif
            );

            Assert.Equal(
                retainedBefore + 8 + SharedCanvasBitmapCache.EstimateCacheKeyWeightBytes(displayIdentity),
                SharedCanvasBitmapCache.RetainedBytesForTests);
            Assert.InRange(
                SharedCanvasBitmapCache.RetainedBytesForTests,
                0,
                SharedCanvasBitmapCache.DefaultBudgetBytes);

            Assert.True(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device,
                family,
                sizing,
                out preview));
            Assert.NotNull(preview);
            Assert.Equal(2u, preview.Bitmap.SizeInPixels.Width);
            Assert.Equal(1u, preview.Bitmap.SizeInPixels.Height);
            Assert.True(preview.IsWithinPixelBudget(2));
            Assert.False(preview.IsWithinPixelBudget(1));
            Assert.False(preview.IsWithinPixelBudget(-1));

            Assert.True(SharedCanvasBitmapCache.TryAcquire(device, displayIdentity, out duplicate));
            Assert.NotNull(duplicate);
            inFlightCopy = stored.TryClone();
            Assert.NotNull(inFlightCopy);

            // Device release drops cache ownership, not live layout leases.
            SharedCanvasBitmapCache.ReleaseDevice(device);
            Assert.Equal(retainedBefore, SharedCanvasBitmapCache.RetainedBytesForTests);
            Assert.False(SharedCanvasBitmapCache.TryAcquire(device, displayIdentity, out _));
            Assert.Equal(2u, stored.Bitmap.SizeInPixels.Width);
            Assert.Equal(1u, preview.Bitmap.SizeInPixels.Height);

            stored.Dispose();
            Assert.False(stored.IsWithinPixelBudget(long.MaxValue));
            stored = null;
            Assert.Equal(2u, preview.Bitmap.SizeInPixels.Width);
            Assert.Equal(1u, duplicate.Bitmap.SizeInPixels.Height);
            Assert.Equal(2u, inFlightCopy.Bitmap.SizeInPixels.Width);
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            duplicate?.Dispose();
            inFlightCopy?.Dispose();
            preview?.Dispose();
            stored?.Dispose();
        }
    }

    [Fact]
    public void UncachedLeaseCloneKeepsOwnedBitmapAliveWithoutLruRetention()
    {
        using var device = new CanvasDevice();
        long retainedBefore = SharedCanvasBitmapCache.RetainedBytesForTests;
        SharedCanvasBitmapCache.Lease? owner = null;
        SharedCanvasBitmapCache.Lease? inFlightCopy = null;
        try
        {
            owner = SharedCanvasBitmapCache.CreateUncachedLease(
                CreateBitmap(device, width: 2, height: 1));
            inFlightCopy = owner.TryClone();

            Assert.NotNull(inFlightCopy);
            Assert.Equal(retainedBefore, SharedCanvasBitmapCache.RetainedBytesForTests);
            owner.Dispose();
            owner = null;
            Assert.Equal(2u, inFlightCopy.Bitmap.SizeInPixels.Width);
            Assert.Equal(1u, inFlightCopy.Bitmap.SizeInPixels.Height);
        }
        finally
        {
            inFlightCopy?.Dispose();
            owner?.Dispose();
        }

        Assert.Equal(retainedBefore, SharedCanvasBitmapCache.RetainedBytesForTests);
    }

    [Fact]
    public void UncachedOwnershipEntryTracksPixelsWithoutAddingAnLruReference()
    {
        using var device = new CanvasDevice();
        long retainedBefore = SharedCanvasBitmapCache.RetainedBytesForTests;
        var entry = new SharedCanvasBitmapCache.Entry(CreateBitmap(device, width: 2, height: 1));
        try
        {
            Assert.Null(entry.IntrinsicSize);
            Assert.Equal(2, entry.PixelWidth);
            Assert.Equal(1, entry.PixelHeight);
            Assert.Equal(8, entry.WeightBytes);
            Assert.Equal(8, entry.PixelWeightBytes);
            Assert.Equal(retainedBefore, SharedCanvasBitmapCache.RetainedBytesForTests);
        }
        finally
        {
            entry.Release();
        }
    }

    [Fact]
    public void FinalLeaseReleasePropagatesWhileFinalCacheReleaseIsContainedAndCounted()
    {
        var leaseFailure = new InvalidOperationException("lease cleanup");
        var leaseResource = new ThrowingDisposable(leaseFailure);
        int leaseReferences = 2;

        SharedCanvasBitmapCache.ReleaseCacheReference(ref leaseReferences, leaseResource);
        Assert.Equal(1, leaseReferences);
        Assert.Equal(0, leaseResource.DisposeCount);

        Assert.Same(
            leaseFailure,
            Assert.Throws<InvalidOperationException>(() =>
                SharedCanvasBitmapCache.ReleaseFinalReference(ref leaseReferences, leaseResource)));
        Assert.Equal(0, leaseReferences);
        Assert.Equal(1, leaseResource.DisposeCount);

        var cacheFailure = new InvalidOperationException("cache cleanup");
        var cacheResource = new ThrowingDisposable(cacheFailure);
        int cacheReferences = 1;
        int failuresBefore = SharedCanvasBitmapCache.CacheDisposalFailureCountForTests;

        Exception? escaped = Record.Exception(() =>
            SharedCanvasBitmapCache.ReleaseCacheReference(ref cacheReferences, cacheResource));

        Assert.Null(escaped);
        Assert.Equal(0, cacheReferences);
        Assert.Equal(1, cacheResource.DisposeCount);
        Assert.Equal(failuresBefore + 1, SharedCanvasBitmapCache.CacheDisposalFailureCountForTests);
        Assert.Equal(cacheFailure.HResult, SharedCanvasBitmapCache.LastCacheDisposalFailureHResultForTests);
    }

    [Fact]
    public void LruEvictionContainsEveryFaultAndPreservesNewEntryLiveReference()
    {
        using var performanceSink = new MarkdownRenderer.PerformanceHarness.PerformanceEventListener();
        using var listener = new BitmapCacheCleanupFailureListener();
        using var faultingListener = new FaultingBitmapCacheCleanupListener();
        int failuresBefore = SharedCanvasBitmapCache.CacheDisposalFailureCountForTests;
        var cache = new WeightedLruCache<int, CacheEntryProbe>(
            100,
            static entry => entry.Weight,
            static entry => SharedCanvasBitmapCache.ReleaseCacheReference(
                ref entry.ReferenceCount,
                entry));
        var evictionFailure = new InvalidOperationException("eviction cleanup");
        var evicted = new CacheEntryProbe[100];
        for (int i = 0; i < evicted.Length; i++)
        {
            evicted[i] = new CacheEntryProbe(1, 1, evictionFailure);
            cache.Set(i, evicted[i]);
        }

        var newestFailure = new InvalidOperationException("live cleanup");
        var newest = new CacheEntryProbe(100, 2, newestFailure);
        cache.Set(100, newest);

        Assert.Equal(1, cache.Count);
        Assert.All(evicted, entry =>
        {
            Assert.Equal(0, entry.ReferenceCount);
            Assert.Equal(1, entry.DisposeCount);
        });
        Assert.Equal(failuresBefore + evicted.Length, SharedCanvasBitmapCache.CacheDisposalFailureCountForTests);
        Assert.Equal(evictionFailure.HResult, SharedCanvasBitmapCache.LastCacheDisposalFailureHResultForTests);
        int[] eventHResults = listener.GetHResults();
        Assert.Equal(evicted.Length, eventHResults.Length);
        Assert.All(eventHResults, hresult => Assert.Equal(evictionFailure.HResult, hresult));
        Assert.Equal(
            $"Bitmap cache cleanup failed (HResult=0x{evictionFailure.HResult:X8}).",
            Assert.Single(performanceSink.ProviderErrors));
        Assert.True(cache.TryGetValue(100, out CacheEntryProbe acquired));
        Assert.Same(newest, acquired);
        Assert.Equal(2, newest.ReferenceCount);

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(1, newest.ReferenceCount);
        Assert.Equal(0, newest.DisposeCount);
        Assert.Same(
            newestFailure,
            Assert.Throws<InvalidOperationException>(() =>
                SharedCanvasBitmapCache.ReleaseFinalReference(ref newest.ReferenceCount, newest)));
        Assert.Equal(0, newest.ReferenceCount);
        Assert.Equal(1, newest.DisposeCount);
    }

    [Fact]
    public void OverBudgetSvgIsNotRetainedButReturnedLeaseKeepsTheBitmapAlive()
    {
        using var device = new CanvasDevice();
        long originalBudget = SharedCanvasBitmapCache.BudgetBytesForTests;
        SharedCanvasBitmapCache.Clear();
        SharedCanvasBitmapCache.SetBudgetForTests(4);
        SharedCanvasBitmapCache.Lease? lease = null;
        string identity = $"svg-cache-over-budget-{Guid.NewGuid():N}";
        try
        {
            lease = SharedCanvasBitmapCache.StoreSvgAndAcquire(
                device,
                identity,
                CreateBitmap(device, width: 2, height: 1),
                intrinsicSize: null,
                pixelWidth: 2,
                pixelHeight: 1
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                , svgWorkId: 0,
                svgSourceBytes: 0,
                svgTargetPixels: 0
#endif
            );

            Assert.Equal(0, SharedCanvasBitmapCache.RetainedBytesForTests);
            Assert.False(SharedCanvasBitmapCache.TryAcquire(device, identity, out _));
            Assert.Equal(2u, lease.Bitmap.SizeInPixels.Width);
            Assert.Equal(1u, lease.Bitmap.SizeInPixels.Height);
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            lease?.Dispose();
            SharedCanvasBitmapCache.SetBudgetForTests(originalBudget);
        }
    }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    [Fact]
    public void SvgStoreAvoidsWin2DSizeQueriesWhileGenericRasterStoreRetainsFallback()
    {
        using var device = new CanvasDevice();
        using var listener = new SvgStageListener();
        long retainedBefore = SharedCanvasBitmapCache.RetainedBytesForTests;
        SharedCanvasBitmapCache.Lease? svgLease = null;
        SharedCanvasBitmapCache.Lease? rasterLease = null;
        string svgIdentity = $"svg-cache-diagnostic-{Guid.NewGuid():N}";
        string rasterIdentity = $"raster-cache-diagnostic-{Guid.NewGuid():N}";
        try
        {
            long svgWorkId = MarkdownSvgStageTrace.BeginWork();
            Assert.True(svgWorkId > 0);
            svgLease = SharedCanvasBitmapCache.StoreSvgAndAcquire(
                device,
                svgIdentity,
                CreateBitmap(device, width: 2, height: 1),
                intrinsicSize: null,
                pixelWidth: 2,
                pixelHeight: 1,
                svgWorkId: svgWorkId,
                svgSourceBytes: 32,
                svgTargetPixels: 2);

            int[] svgStages = listener.GetStages(svgWorkId);
            Assert.Contains((int)MarkdownSvgWorkStage.SvgCacheLruInsertAndEviction, svgStages);
            Assert.DoesNotContain((int)MarkdownSvgWorkStage.SvgCacheWeightSizeQuery, svgStages);
            Assert.DoesNotContain((int)MarkdownSvgWorkStage.SvgCacheEntryPixelSizeQuery, svgStages);

            long rasterWorkId = MarkdownSvgStageTrace.BeginWork();
            Assert.True(rasterWorkId > 0);
            rasterLease = SharedCanvasBitmapCache.StoreAndAcquire(
                device,
                rasterIdentity,
                CreateBitmap(device, width: 2, height: 1),
                intrinsicSize: null,
                svgWorkId: rasterWorkId,
                svgSourceBytes: 32,
                svgTargetPixels: 2);

            int[] rasterStages = listener.GetStages(rasterWorkId);
            Assert.Single(
                rasterStages,
                stage => stage == (int)MarkdownSvgWorkStage.SvgCacheWeightSizeQuery);
            Assert.DoesNotContain((int)MarkdownSvgWorkStage.SvgCacheEntryPixelSizeQuery, rasterStages);
            Assert.Equal(
                retainedBefore + 16 +
                    SharedCanvasBitmapCache.EstimateCacheKeyWeightBytes(svgIdentity) +
                    SharedCanvasBitmapCache.EstimateCacheKeyWeightBytes(rasterIdentity),
                SharedCanvasBitmapCache.RetainedBytesForTests);
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            rasterLease?.Dispose();
            svgLease?.Dispose();
        }
    }
#endif

    private static CanvasBitmap CreateBitmap(CanvasDevice device, int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        return CanvasBitmap.CreateFromBytes(
            device,
            pixels,
            width,
            height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
    }

    private sealed class ThrowingDisposable(Exception failure) : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            throw failure;
        }
    }

    private sealed class CacheEntryProbe(int weight, int referenceCount, Exception failure) : IDisposable
    {
        public int Weight { get; } = weight;

        public int ReferenceCount = referenceCount;

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            throw failure;
        }
    }

    private sealed class BitmapCacheCleanupFailureListener : EventListener
    {
        private readonly object _gate = new();
        private readonly List<int> _hresults = [];

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "MarkdownRenderer-Performance")
                EnableEvents(eventSource, EventLevel.Warning);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != "MarkdownRenderer-Performance" ||
                eventData.EventId != 16 ||
                eventData.Payload is not { Count: 1 } payload ||
                payload[0] is not int hresult)
            {
                return;
            }

            lock (_gate)
                _hresults.Add(hresult);
        }

        internal int[] GetHResults()
        {
            lock (_gate)
                return _hresults.ToArray();
        }
    }

    private sealed class FaultingBitmapCacheCleanupListener : EventListener
    {
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "MarkdownRenderer-Performance")
                EnableEvents(eventSource, EventLevel.Warning);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name == "MarkdownRenderer-Performance" && eventData.EventId == 16)
                throw new InvalidOperationException("diagnostic listener failure");
        }
    }

#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private sealed class SvgStageListener : EventListener
    {
        private readonly object _gate = new();
        private readonly List<(long WorkId, int Stage)> _events = [];

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "MarkdownRenderer-Performance")
                EnableEvents(source, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name != "MarkdownRenderer-Performance" ||
                eventData.EventId != 12 ||
                eventData.Payload is not { Count: 3 } payload ||
                payload[0] is not long workId ||
                payload[2] is not long packedMetrics)
            {
                return;
            }

            int stage = (int)((packedMetrics >> 49) & 0x1F);
            lock (_gate)
                _events.Add((workId, stage));
        }

        internal int[] GetStages(long workId)
        {
            lock (_gate)
                return _events.Where(item => item.WorkId == workId)
                    .Select(item => item.Stage)
                    .ToArray();
        }
    }
#endif
}
