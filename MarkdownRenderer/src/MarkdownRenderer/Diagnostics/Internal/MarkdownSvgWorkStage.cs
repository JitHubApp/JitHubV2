using System.Diagnostics;
using System.Threading;

namespace MarkdownRenderer.Diagnostics.Internal;

/// <summary>
/// Numeric SVG work stages emitted while an SVG stage observer is listening.
/// The event contract contains no source identity or user-authored metadata.
/// </summary>
internal enum MarkdownSvgWorkStage
{
    SourcePreparation = 1,
    HostPreflight = 2,
    HostAdmissionWait = 3,
    ProviderOpen = 4,
    BitmapCacheWait = 5,
    ProviderRender = 6,
    BitmapUpload = 7,
    BitmapCacheStore = 8,
    UiDispatchDelay = 9,
    UiPublish = 10,
    SvgBitmapBufferCopy = 11,
    SvgCanvasBitmapCreateFromBytes = 12,
    SvgCacheWeightSizeQuery = 13,
    SvgCacheEntryPixelSizeQuery = 14,
    SvgCacheLruInsertAndEviction = 15,
    OuterDispatchQueueWait = 16,
}
