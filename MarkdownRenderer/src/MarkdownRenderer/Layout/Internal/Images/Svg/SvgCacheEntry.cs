using System;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Internal.Images.Svg;

/// <summary>Device-independent SVG source state retained across relayout.</summary>
/// <remarks>
/// CPU rasters are never retained here. The provider owns parsed trees and
/// resources; <see cref="SharedCanvasBitmapCache"/> owns uploaded,
/// device-scoped bitmaps.
/// </remarks>
internal sealed class SvgCacheEntry(
    byte[] RawBytes,
    Size Intrinsic,
    string? Title,
    string? Desc,
    string ContentHash,
    string SecurityPartitionHash,
    MarkdownSvgDocumentInfo Info,
    long RendererIdentity,
    long ProviderCacheGeneration,
    string? LastBitmapIdentity = null,
    int LastBitmapWidthPixels = 0,
    int LastBitmapHeightPixels = 0,
    MarkdownSvgPhysicalViewport? LastPhysicalViewport = null)
{
    internal readonly byte[] RawBytes = RawBytes;
    internal readonly Size Intrinsic = Intrinsic;
    internal readonly string? Title = Title;
    internal readonly string? Desc = Desc;
    internal readonly string ContentHash = ContentHash;
    internal readonly string SecurityPartitionHash = SecurityPartitionHash;
    internal readonly MarkdownSvgDocumentInfo Info = Info;
    internal readonly long RendererIdentity = RendererIdentity;
    internal readonly long ProviderCacheGeneration = ProviderCacheGeneration;
    internal readonly string? LastBitmapIdentity = LastBitmapIdentity;
    internal readonly int LastBitmapWidthPixels = LastBitmapWidthPixels;
    internal readonly int LastBitmapHeightPixels = LastBitmapHeightPixels;
    internal readonly MarkdownSvgPhysicalViewport? LastPhysicalViewport = LastPhysicalViewport;

    internal SvgCacheEntry WithLastBitmap(
        string bitmapIdentity,
        int bitmapWidthPixels,
        int bitmapHeightPixels,
        MarkdownSvgPhysicalViewport? physicalViewport) =>
        new(
            RawBytes,
            Intrinsic,
            Title,
            Desc,
            ContentHash,
            SecurityPartitionHash,
            Info,
            RendererIdentity,
            ProviderCacheGeneration,
            bitmapIdentity,
            bitmapWidthPixels,
            bitmapHeightPixels,
            physicalViewport);

    /// <summary>The retained bytes this entry charges to the SVG source cache.</summary>
    internal static long GetWeight(SvgCacheEntry entry)
    {
        long weight = 512L + entry.RawBytes.LongLength;
        weight += (entry.Title?.Length ?? 0) * sizeof(char);
        weight += (entry.Desc?.Length ?? 0) * sizeof(char);
        weight += entry.ContentHash.Length * sizeof(char);
        weight += entry.SecurityPartitionHash.Length * sizeof(char);
        weight += (entry.LastBitmapIdentity?.Length ?? 0) * sizeof(char);

        return Math.Max(1L, weight);
    }
}
