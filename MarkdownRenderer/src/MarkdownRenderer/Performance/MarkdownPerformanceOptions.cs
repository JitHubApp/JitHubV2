using System;

namespace MarkdownRenderer.Performance;

/// <summary>Controls the opt-in, bounded preparation of document resources.</summary>
public sealed record MarkdownPerformanceOptions
{
    /// <summary>Balanced progressive preparation for a reading surface.</summary>
    public static MarkdownPerformanceOptions Progressive { get; } = new()
    {
        DeferOffscreenScenes = true,
        ReleaseOffscreenImageResources = true,
    };

    /// <summary>Maximum number of concurrent image source resolutions.</summary>
    public int MaxConcurrentImageFetches { get; init; } = IntPtr.Size == 4 ? 8 : 16;

    /// <summary>Fetch slots unavailable to speculative work, so visible images can advance.</summary>
    public int ReservedVisibleImageFetches { get; init; } = IntPtr.Size == 4 ? 2 : 4;

    /// <summary>
    /// Maximum concurrent static raster decode/upload and SVG host-source
    /// sanitization/preflight preparations in this session. SVG slots end before
    /// provider or worker waits; this is not an aggregate source-memory limit.
    /// The process-wide codec safety admission can impose a tighter
    /// limit across sessions and controls without a session. Sources
    /// requiring a bounded preview or exceeding the raster output pixel limit
    /// take all slots: a transformed output does not bound codec intermediates.
    /// </summary>
    public int MaxConcurrentCpuPreparations { get; init; } = IntPtr.Size == 4 ? 1 : 2;

    /// <summary>Maximum in-flight scene preparations and queued publications across documents.</summary>
    public int MaxConcurrentScenePreparations { get; init; } = IntPtr.Size == 4 ? 1 : 2;

    /// <summary>
    /// Maximum bytes of resolved source images retained in the shared session cache.
    /// This does not include decoded or native resources owned by live image boxes;
    /// <see cref="ReleaseOffscreenImageResources"/> controls their off-band residency separately.
    /// </summary>
    public long SourceCacheBudgetBytes { get; init; } = (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024;

    /// <summary>
    /// Maximum conservative graph weight retained by the completed Math/Mermaid
    /// cache across documents. Zero disables strong cache retention. Active
    /// layouts and pending layout handoffs are separately owned and excluded;
    /// this is not a process-memory or native-resource limit.
    /// </summary>
    public long SceneCacheBudgetBytes { get; init; } = (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024;

    /// <summary>
    /// Maximum bytes admitted for source buffers, including downstream consumers
    /// of lease-aware resolvers and built-in image reads. Legacy resolver hidden
    /// allocations and separately budgeted cache or SVG-provider copies are excluded.
    /// </summary>
    public long MaxInFlightSourceBytes { get; init; } = (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024;

    /// <summary>Part of the in-flight source budget unavailable to speculative reads.</summary>
    public long ReservedVisibleSourceBytes { get; init; } = (IntPtr.Size == 4 ? 8L : 16L) * 1024 * 1024;

    /// <summary>
    /// Maximum pixels in a prepared static raster; this cannot exceed the progressive safety ceiling.
    /// When <see cref="ReleaseOffscreenImageResources"/> is enabled, shared live-output
    /// admission may impose a lower ceiling: each atomic
    /// output fits the fixed transition reserve (16 MiB on x64/ARM64, 8 MiB on x86).
    /// A partial GIF reserves both its temporary frame and composed canvas.
    /// SVGs above that atomic-output ceiling use visible tiles instead of downsampling.
    /// </summary>
    public long MaxRasterOutputPixels { get; init; } = IntPtr.Size == 4 ? 4_194_304 : 8_388_608;

    /// <summary>Lookahead used when preparing resources around a viewport.</summary>
    public int LookAheadViewports { get; init; } = 2;

    /// <summary>Decodes static raster images at their displayed physical size.</summary>
    public bool UseDisplaySizedRasterDecode { get; init; } = true;

    /// <summary>Shows a cached smaller raster while an exact larger raster is prepared.</summary>
    public bool UseCachedRasterPreview { get; init; } = true;

    /// <summary>Allows admitted image sources throughout the document to be fetched in spare capacity.</summary>
    public bool PrefetchDocumentImages { get; init; } = true;

    /// <summary>
    /// Releases per-image decoded/source/native resources after an image leaves the active layout band
    /// and its in-flight work drains. This is a live-box residency policy, not source-cache eviction:
    /// <see cref="SourceCacheBudgetBytes"/> independently controls the shared resolved-source cache.
    /// Enabling this policy also opts into live-output admission. Live image boxes
    /// remain layout-owned. Participating bitmap outputs across sessions
    /// additionally share a fixed 64 MiB logical pixel-byte ceiling (32 MiB on x86),
    /// including cache references, pending publications and resize fallbacks until
    /// actual native disposal. A quarter is unavailable to initial/speculative outputs
    /// so visible replacements can keep their previous good pixels during rendering.
    /// Another quarter is unavailable to speculative outputs, protecting initial
    /// visible images from background work. Capacity deferral releases source and
    /// preparation ownership before retrying
    /// through the same authorized resolver. It does not retain a source snapshot,
    /// so a custom resolver must support repeat resolution when progressive residency
    /// is enabled. Controls without this residency policy, WIC/driver intermediates,
    /// DirectWrite and Math/Mermaid scene resources are not covered by this pixel-byte
    /// ceiling; it is not a process committed-memory or RSS guarantee.
    /// </summary>
    public bool ReleaseOffscreenImageResources { get; init; }

    /// <summary>
    /// Defers built-in block and inline mathematics, and Mermaid scene work,
    /// until their atomic source placeholders enter the visible viewport band. Public engine parsing
    /// remains eager; this option is honored only by progressive controls.
    /// Participating scene drawings additionally share fixed native-complexity
    /// admission (64 MiB-weight on x64/ARM64, 32 MiB-weight on x86). This is a
    /// conservative structural weight, not measured native bytes or an RSS cap.
    /// Resource credit remains charged until native disposal actually succeeds,
    /// including failed-cleanup retirement. Capacity-deferred scenes retain a
    /// bounded readable semantic/source fallback and retry after capacity is
    /// released, without requiring another scroll or pointer event. A scene
    /// exceeding the single-scene weight ceiling keeps its accessible fallback;
    /// fallback is never reported as native-ready fidelity. Native residency
    /// follows the actual visible band rather than document-layout overscan.
    /// </summary>
    public bool DeferOffscreenScenes { get; init; }
}
