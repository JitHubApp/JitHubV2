using System;
using System.Runtime.InteropServices;
using System.Threading;
using MarkdownRenderer.Images;
using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Owns the caches, budgets and admission gates that image boxes share:
/// uploaded bitmaps, bitmap output admission, decode preparation admission,
/// SVG source metadata, the negative failure caches, provider cache
/// identities, the SVG open and provider gates, and native WIC decode
/// counters. Every view given the same scope shares its budgets, so a
/// scope's bounds hold across all of those views together.
/// </summary>
/// <remarks>
/// An image box reaches its scope through one property and hands it to the
/// objects it composes. A scope holds no reference to a box, document or
/// control.
/// </remarks>
internal sealed class ImageResourceScope
{
    // Keep aggregate host admission below the optional provider's default
    // bounded queue. Queue wait is not rendering work and must never consume a
    // document's hard worker deadline or turn a large badge wall into hundreds
    // of false resource-limit failures.
    private const int SvgProviderAdmissionCount = 16;

    /// <summary>
    /// The scope every view uses. It is the one root
    /// that keeps the image budgets bounded across all controls in a process;
    /// a per-control scope would multiply them by the number of open views.
    /// </summary>
    internal static ImageResourceScope Shared { get; } = CreateShared();

    /// <summary>Creates an isolated scope with the default budgets.</summary>
    internal ImageResourceScope()
        : this(BitmapOutputAdmission.CreateDefault(), MarkdownImagePreparationAdmission.CreateDefault())
    {
    }

    /// <summary>Creates an isolated scope over the given admissions and budgets.</summary>
    /// <param name="bitmapOutput">Bounds decoded bitmap output across the scope.</param>
    /// <param name="preparation">Bounds transient decode and preparation work.</param>
    /// <param name="bitmapCacheBudgetBytes">Retained-byte budget of the uploaded bitmap cache.</param>
    /// <param name="svgSourceBudgetBytes">Retained-byte budget of the SVG source cache.</param>
    internal ImageResourceScope(
        BitmapOutputAdmission bitmapOutput,
        MarkdownImagePreparationAdmission preparation,
        long bitmapCacheBudgetBytes = -1,
        long svgSourceBudgetBytes = -1)
    {
        ArgumentNullException.ThrowIfNull(bitmapOutput);
        ArgumentNullException.ThrowIfNull(preparation);
        BitmapOutput = bitmapOutput;
        Preparation = preparation;
        BitmapCache = new SharedCanvasBitmapCache(
            bitmapOutput,
            bitmapCacheBudgetBytes < 0 ? SharedCanvasBitmapCache.DefaultBudgetBytes : bitmapCacheBudgetBytes);
        SvgSources = new SvgSourceCache(
            svgSourceBudgetBytes < 0 ? SvgSourceCache.DefaultBudgetBytes : svgSourceBudgetBytes);
    }

    /// <summary>Bounds decoded bitmap output across the scope.</summary>
    internal BitmapOutputAdmission BitmapOutput { get; }

    /// <summary>Bounds transient decode and preparation work across the scope.</summary>
    internal MarkdownImagePreparationAdmission Preparation { get; }

    /// <summary>Device-partitioned uploaded bitmaps.</summary>
    internal SharedCanvasBitmapCache BitmapCache { get; }

    /// <summary>Device-independent SVG source state retained across relayout.</summary>
    internal SvgSourceCache SvgSources { get; }

    /// <summary>Sources that failed permanently.</summary>
    internal ImageFailureCache Failures { get; } = new();

    /// <summary>Cache-key numbers of SVG renderers and raster decoders.</summary>
    internal ImageProviderIdentities ProviderIdentities { get; } = new();

    /// <summary>Serializes concurrent opens of the same SVG source.</summary>
    internal BoundedAsyncKeyedGate SvgOpenAdmissions { get; } = new(
        maximumTrackedKeys: 256,
        overflowStripeCount: 128);

    /// <summary>Bounds concurrent SVG provider opens and renders.</summary>
    internal SemaphoreSlim SvgProviderAdmission { get; } =
        new(SvgProviderAdmissionCount, SvgProviderAdmissionCount);

    /// <summary>Counts native WIC decodes made for this scope.</summary>
    internal RasterNativeWicDiagnostics NativeWic { get; } = new();

    /// <summary>Drops cache ownership of every bitmap created on a lost device.</summary>
    /// <param name="device">The lost device.</param>
    /// <remarks>
    /// Live boxes keep their leases only until the recovery rebuild disposes
    /// them, at which point the invalid resources are released.
    /// </remarks>
    internal void ReleaseDevice(CanvasDevice device) => BitmapCache.ReleaseDevice(device);

    /// <summary>
    /// Drops cached bitmaps and device-independent SVG source metadata under
    /// system memory pressure. Live boxes retain the source required by their
    /// active document handles; future boxes can repopulate the bounded caches.
    /// </summary>
    internal void TrimForMemoryPressure()
    {
        BitmapCache.Clear();
        SvgSources.Clear();
    }

    private static ImageResourceScope CreateShared()
    {
        var scope = new ImageResourceScope();
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += scope.OnAppMemoryUsageIncreased;
        }
        catch (Exception exception) when (
            exception is COMException or PlatformNotSupportedException or TypeInitializationException)
        {
            // MemoryManager is unavailable in some unpackaged and test hosts.
            // The hard LRU budgets and explicit device-loss cleanup still apply.
        }

        return scope;
    }

    private void OnAppMemoryUsageIncreased(object? sender, object args) => TrimForMemoryPressure();
}
