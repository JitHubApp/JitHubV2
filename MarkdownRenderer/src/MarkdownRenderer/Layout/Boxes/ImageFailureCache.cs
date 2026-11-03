using System.Collections.Concurrent;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Bounded negative caches for image sources that failed permanently. They
/// let a box created by the rebuild that followed a failure start in its
/// failed state instead of fetching and decoding the same source again.
/// </summary>
/// <remarks>
/// Negative caching is optional memoization, not an image admission policy.
/// Count bounds alone must not retain hundreds of multi-megabyte data URIs or
/// host-supplied keys after the owning document has retired, so long keys are
/// never remembered. The entries hold no disposable or native values;
/// eviction only forgets a prior failure and never changes ownership.
/// </remarks>
internal sealed class ImageFailureCache
{
    internal const int MaxEntries = 512;
    internal const int MaxKeyLength = 4096;

    // URLs that have permanently failed to load/parse. New ImageBox instances
    // for the same URL start in _loadFailed=true so the fatal state survives
    // the layout rebuild triggered by the original failure.
    private readonly ConcurrentDictionary<string, byte> _failedUrls = new();
    // Only typed portable UnsupportedContent failures are deterministic for an
    // exact source/provider pair. Untyped legacy decode errors and portable
    // resource/deadline/worker failures are retried because they may depend on
    // request geometry or transient state. Keeping this cache path-specific
    // lets a later provider retry a source that previously failed through WIC
    // or a different provider.
    private readonly ConcurrentDictionary<RasterDecodeFailureKey, byte> _failedRasterDecodes = new();
    private readonly ConcurrentDictionary<string, (MarkdownSvgFailureReason Reason, string Description)> _svgFailures = new();

    internal bool HasFailedUrl(string cacheKey) => _failedUrls.ContainsKey(cacheKey);

    internal void RememberFailedUrl(string cacheKey)
    {
        if (cacheKey.Length is 0 or > MaxKeyLength)
            return;
        _failedUrls.TryAdd(cacheKey, 0);
        Trim(_failedUrls, MaxEntries);
    }

    internal void ForgetFailedUrl(string cacheKey) => _failedUrls.TryRemove(cacheKey, out _);

    internal void RememberFailedRasterDecode(
        string cacheKey, string format, long decoderIdentity)
    {
        if (cacheKey.Length is 0 or > MaxKeyLength ||
            string.IsNullOrEmpty(format) || decoderIdentity < 0)
        {
            return;
        }

        _failedRasterDecodes.TryAdd(new RasterDecodeFailureKey(cacheKey, format, decoderIdentity), 0);
        Trim(_failedRasterDecodes, MaxEntries);
    }

    internal bool HasFailedRasterDecode(
        string cacheKey, string format, long decoderIdentity) =>
        cacheKey.Length is > 0 and <= MaxKeyLength &&
        !string.IsNullOrEmpty(format) &&
        _failedRasterDecodes.ContainsKey(new RasterDecodeFailureKey(cacheKey, format, decoderIdentity));

    internal bool TryGetSvgFailure(
        string cacheKey, out (MarkdownSvgFailureReason Reason, string Description) failure) =>
        _svgFailures.TryGetValue(cacheKey, out failure);

    /// <summary>Remembers an SVG failure; the caller bounds the key length.</summary>
    internal void RememberSvgFailure(string cacheKey, MarkdownSvgFailureReason reason, string description)
    {
        _svgFailures[cacheKey] = (reason, description);
        Trim(_svgFailures, MaxEntries);
    }

    internal void ForgetSvgFailure(string cacheKey) => _svgFailures.TryRemove(cacheKey, out _);

    private static void Trim<TKey, TValue>(ConcurrentDictionary<TKey, TValue> cache, int maxEntries)
        where TKey : notnull
    {
        while (cache.Count > maxEntries)
        {
            bool removed = false;
            foreach (TKey victim in cache.Keys)
            {
                if (!cache.TryRemove(victim, out _))
                    continue;
                removed = true;
                break;
            }

            if (!removed)
                break;
        }
    }

    private readonly record struct RasterDecodeFailureKey(
        string CacheKey, string Format, long DecoderIdentity);
}
