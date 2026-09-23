using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

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
        long cpuPreparationMilliseconds)
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
    /// <summary>Raster preparation slots admitted.</summary>
    public long CpuPreparations { get; }
    /// <summary>Total elapsed raster preparation-slot time, rounded to milliseconds.</summary>
    public long CpuPreparationMilliseconds { get; }
}

/// <summary>
/// Borrowed, opt-in preparation service. This is a package-boundary contract,
/// not a custom-provider extension point: controls accept the implementation
/// from the optional MarkdownRenderer.Performance package and never own it.
/// </summary>
public interface IMarkdownPerformanceSession
{
    /// <summary>Gets the immutable preparation settings.</summary>
    MarkdownPerformanceOptions Options { get; }

    /// <summary>Returns privacy-safe aggregate counters.</summary>
    MarkdownPerformanceSnapshot GetSnapshot();

    /// <summary>Releases retained source bytes without canceling visible work.</summary>
    void Trim();
}

internal interface IMarkdownPerformanceSessionInternal : IMarkdownPerformanceSession
{
    bool IsDisposed { get; }
    IMarkdownPerformanceDocumentScope OpenDocument(
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context);
    ValueTask<IDisposable> EnterCpuPreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken);
}

internal interface IMarkdownPerformanceDocumentScope : IMarkdownImageResolver, IDisposable
{
    bool IsDisposed { get; }
    CancellationToken CancellationToken { get; }
    bool Matches(
        IMarkdownPerformanceSessionInternal session,
        IMarkdownImageResolver resolver,
        MarkdownImageResolveContext context);
    Task PrefetchAsync(IReadOnlyList<string> sources, CancellationToken cancellationToken);
}
