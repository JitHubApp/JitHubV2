using System;

namespace MarkdownRenderer.Performance;

/// <summary>Rejects performance options outside the bounds a session can honor on this process architecture.</summary>
internal static class MarkdownPerformanceOptionsValidation
{
    internal static void Validate(MarkdownPerformanceOptions options)
    {
        if (options.MaxConcurrentImageFetches < 2 ||
            options.MaxConcurrentImageFetches > (IntPtr.Size == 4 ? 8 : 16))
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentImageFetches));
        if (options.ReservedVisibleImageFetches < 1 ||
            options.ReservedVisibleImageFetches >= options.MaxConcurrentImageFetches)
            throw new ArgumentOutOfRangeException(nameof(options.ReservedVisibleImageFetches));
        if (options.MaxConcurrentCpuPreparations < 1 ||
            options.MaxConcurrentCpuPreparations > (IntPtr.Size == 4 ? 1 : 2))
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentCpuPreparations));
        if (options.MaxConcurrentScenePreparations < 1 ||
            options.MaxConcurrentScenePreparations > (IntPtr.Size == 4 ? 1 : 2))
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentScenePreparations));
        if (options.SourceCacheBudgetBytes < 0 ||
            options.SourceCacheBudgetBytes > (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options.SourceCacheBudgetBytes));
        if (options.SceneCacheBudgetBytes < 0 ||
            options.SceneCacheBudgetBytes > (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options.SceneCacheBudgetBytes));
        if (options.MaxInFlightSourceBytes < 2 ||
            options.MaxInFlightSourceBytes > (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options.MaxInFlightSourceBytes));
        if (options.ReservedVisibleSourceBytes < 1 ||
            options.ReservedVisibleSourceBytes >= options.MaxInFlightSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(options.ReservedVisibleSourceBytes));
        if (options.MaxRasterOutputPixels < 1 ||
            options.MaxRasterOutputPixels > (IntPtr.Size == 4 ? 4_194_304 : 8_388_608))
            throw new ArgumentOutOfRangeException(nameof(options.MaxRasterOutputPixels));
        if (options.LookAheadViewports is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(options.LookAheadViewports));
    }
}
