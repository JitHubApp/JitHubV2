using System;

namespace MarkdownRenderer.Performance;

/// <summary>Controls the opt-in, bounded preparation of document resources.</summary>
public sealed record MarkdownPerformanceOptions
{
    /// <summary>Balanced progressive preparation for a reading surface.</summary>
    public static MarkdownPerformanceOptions Progressive { get; } = new();

    /// <summary>Maximum number of concurrent image source resolutions.</summary>
    public int MaxConcurrentImageFetches { get; init; } = IntPtr.Size == 4 ? 8 : 16;

    /// <summary>Fetch slots unavailable to speculative work, so visible images can advance.</summary>
    public int ReservedVisibleImageFetches { get; init; } = IntPtr.Size == 4 ? 2 : 4;

    /// <summary>Maximum concurrent static raster decode and upload operations.</summary>
    public int MaxConcurrentCpuPreparations { get; init; } = IntPtr.Size == 4 ? 1 : 2;

    /// <summary>Maximum bytes of resolved source images retained in memory.</summary>
    public long SourceCacheBudgetBytes { get; init; } = (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024;

    /// <summary>Maximum pixels in a prepared static raster; this cannot exceed the progressive safety ceiling.</summary>
    public long MaxRasterOutputPixels { get; init; } = IntPtr.Size == 4 ? 4_194_304 : 8_388_608;

    /// <summary>Lookahead used when preparing resources around a viewport.</summary>
    public int LookAheadViewports { get; init; } = 2;

    /// <summary>Decodes static raster images at their displayed physical size.</summary>
    public bool UseDisplaySizedRasterDecode { get; init; } = true;

    /// <summary>Shows a cached smaller raster while an exact larger raster is prepared.</summary>
    public bool UseCachedRasterPreview { get; init; } = true;

    /// <summary>Allows admitted image sources throughout the document to be fetched in spare capacity.</summary>
    public bool PrefetchDocumentImages { get; init; } = true;

    internal void Validate()
    {
        if (MaxConcurrentImageFetches < 2 ||
            MaxConcurrentImageFetches > (IntPtr.Size == 4 ? 8 : 16))
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentImageFetches));
        if (ReservedVisibleImageFetches < 1 || ReservedVisibleImageFetches >= MaxConcurrentImageFetches)
            throw new ArgumentOutOfRangeException(nameof(ReservedVisibleImageFetches));
        if (MaxConcurrentCpuPreparations < 1 ||
            MaxConcurrentCpuPreparations > (IntPtr.Size == 4 ? 1 : 2))
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentCpuPreparations));
        if (SourceCacheBudgetBytes < 0 ||
            SourceCacheBudgetBytes > (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(SourceCacheBudgetBytes));
        if (MaxRasterOutputPixels < 1 ||
            MaxRasterOutputPixels > (IntPtr.Size == 4 ? 4_194_304 : 8_388_608))
            throw new ArgumentOutOfRangeException(nameof(MaxRasterOutputPixels));
        if (LookAheadViewports is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(LookAheadViewports));
    }
}
