namespace MarkdownRenderer.Performance;

/// <summary>Aggregate, privacy-safe resource-preparation counters.</summary>
public sealed class MarkdownPerformanceSnapshot
{
    internal MarkdownPerformanceSnapshot(
        long sourceCacheBytes,
        long sourceCacheHits,
        long imageFetches,
        long imageFetchMilliseconds,
        long imageFetchFailures,
        long imageFetchCancellations,
        long sourceCacheEvictions,
        int pendingImageFetches,
        int activeImageFetches,
        long cpuPreparations,
        long cpuPreparationMilliseconds,
        long scenePreparations,
        long scenePreparationMilliseconds,
        long inFlightSourceBytes,
        long peakInFlightSourceBytes,
        int pendingSourceByteRequests,
        long sceneCacheEstimatedBytes,
        long sceneCacheHits,
        long sceneCacheEvictions)
    {
        SourceCacheBytes = sourceCacheBytes;
        SourceCacheHits = sourceCacheHits;
        ImageFetches = imageFetches;
        ImageFetchMilliseconds = imageFetchMilliseconds;
        ImageFetchFailures = imageFetchFailures;
        ImageFetchCancellations = imageFetchCancellations;
        SourceCacheEvictions = sourceCacheEvictions;
        PendingImageFetches = pendingImageFetches;
        ActiveImageFetches = activeImageFetches;
        CpuPreparations = cpuPreparations;
        CpuPreparationMilliseconds = cpuPreparationMilliseconds;
        ScenePreparations = scenePreparations;
        ScenePreparationMilliseconds = scenePreparationMilliseconds;
        InFlightSourceBytes = inFlightSourceBytes;
        PeakInFlightSourceBytes = peakInFlightSourceBytes;
        PendingSourceByteRequests = pendingSourceByteRequests;
        SceneCacheEstimatedBytes = sceneCacheEstimatedBytes;
        SceneCacheHits = sceneCacheHits;
        SceneCacheEvictions = sceneCacheEvictions;
    }

    /// <summary>Retained source bytes.</summary>
    public long SourceCacheBytes { get; }
    /// <summary>Successful reads from the source cache.</summary>
    public long SourceCacheHits { get; }
    /// <summary>Source resolutions begun by the session.</summary>
    public long ImageFetches { get; }
    /// <summary>Total elapsed source-resolution time, rounded to milliseconds.</summary>
    public long ImageFetchMilliseconds { get; }
    /// <summary>Source resolutions that faulted for a reason other than cancellation.</summary>
    public long ImageFetchFailures { get; }
    /// <summary>Queued or active resolutions canceled before completion.</summary>
    public long ImageFetchCancellations { get; }
    /// <summary>Source cache entries evicted to respect the byte budget.</summary>
    public long SourceCacheEvictions { get; }
    /// <summary>Resolutions waiting for admission or a speculative-work policy.</summary>
    public int PendingImageFetches { get; }
    /// <summary>Resolutions currently inside a host resolver.</summary>
    public int ActiveImageFetches { get; }
    /// <summary>Raster and SVG host-source preparation slots admitted; provider/worker waits are excluded.</summary>
    public long CpuPreparations { get; }
    /// <summary>Total elapsed raster and SVG host-source preparation-slot time, rounded to milliseconds; provider/worker waits are excluded.</summary>
    public long CpuPreparationMilliseconds { get; }
    /// <summary>Scene preparation slots admitted.</summary>
    public long ScenePreparations { get; }
    /// <summary>Conservative weight held only by the completed-scene cache, excluding active layouts and pending handoffs.</summary>
    public long SceneCacheEstimatedBytes { get; }
    /// <summary>Successful completed-scene cache reads; weak reuse from live layouts is not a cache hit.</summary>
    public long SceneCacheHits { get; }
    /// <summary>Completed-scene cache entries removed to respect its weighted budget.</summary>
    public long SceneCacheEvictions { get; }
    /// <summary>Total elapsed scene preparation-slot time, rounded to milliseconds.</summary>
    public long ScenePreparationMilliseconds { get; }
    /// <summary>Bytes admitted for source reads and participating consumer lifetimes; independently bounded caches and legacy resolver allocations are not included.</summary>
    public long InFlightSourceBytes { get; }
    /// <summary>Peak admitted source-buffer bytes since this session was created.</summary>
    public long PeakInFlightSourceBytes { get; }
    /// <summary>Source-buffer reservations waiting for weighted admission.</summary>
    public int PendingSourceByteRequests { get; }
}
