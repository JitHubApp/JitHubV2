using System;
using System.Diagnostics.CodeAnalysis;
using MarkdownRenderer.Images;
using MarkdownRenderer.Utilities;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Retained-byte bounded cache of device-independent SVG source state, keyed
/// by exact content/theme/security identity and optionally by a built-in
/// source URL alias of the same entry.
/// </summary>
internal sealed class SvgSourceCache
{
    /// <summary>Default retained-byte budget.</summary>
    internal static readonly long DefaultBudgetBytes = Environment.Is64BitProcess
        ? 64L * 1024 * 1024
        : 32L * 1024 * 1024;

    private readonly WeightedLruCache<string, SvgCacheEntry> _entries;

    internal SvgSourceCache(long budgetBytes)
    {
        _entries = new WeightedLruCache<string, SvgCacheEntry>(
            budgetBytes,
            SvgCacheEntry.GetWeight,
            comparer: StringComparer.Ordinal,
            keyWeightSelector: static key => 48L + (long)key.Length * sizeof(char));
    }

    internal bool TryGetValue(string key, [NotNullWhen(true)] out SvgCacheEntry? entry)
    {
        if (_entries.TryGetValue(key, out SvgCacheEntry found))
        {
            entry = found;
            return true;
        }

        entry = null;
        return false;
    }

    internal void Set(string key, SvgCacheEntry entry) => _entries.Set(key, entry);

    internal bool Remove(string key) => _entries.Remove(key);

    internal int RemoveWhere(Predicate<string> predicate) => _entries.RemoveWhere(predicate);

    internal void Clear() => _entries.Clear();

    /// <summary>
    /// Records the bitmap a completed render produced on the exact cached
    /// source entry, and on its URL alias when one is given.
    /// </summary>
    /// <param name="builtInSourceAliasKey">The URL alias of the content entry, if any.</param>
    /// <param name="contentCacheKey">The exact preflight/content/theme/security key.</param>
    /// <param name="bitmapIdentity">The bitmap cache identity of the render.</param>
    /// <param name="width">The render's pixel width.</param>
    /// <param name="height">The render's pixel height.</param>
    /// <param name="physicalViewport">The render's physical viewport, if fractional.</param>
    internal void RecordLastBitmap(
        string? builtInSourceAliasKey,
        string contentCacheKey,
        string bitmapIdentity,
        int width,
        int height,
        MarkdownSvgPhysicalViewport? physicalViewport)
    {
        // Look up only the exact preflight/content/theme/security identity.
        // An evicted source is not resurrected through a weak pointer, and no
        // async continuation needs an extra entry wrapper or retained array.
        if (!_entries.TryGetValue(contentCacheKey, out SvgCacheEntry? sourceEntry))
            return;
        SvgCacheEntry completedEntry = sourceEntry.WithLastBitmap(bitmapIdentity, width, height, physicalViewport);
        if (string.IsNullOrEmpty(builtInSourceAliasKey))
        {
            _entries.TryReplaceIfReferenceMatches(contentCacheKey, sourceEntry, completedEntry);
            return;
        }
        // A newer resolution, theme/locale variant or eviction may have
        // replaced the URL alias while this image rendered. Memoization is
        // optional: update only the same complete entry, atomically, and never
        // restore an evicted alias or overwrite newer content with a late render.
        // The content and matching URL alias share one cache transaction so
        // concurrent completions cannot publish different bitmap metadata.
        _entries.TryReplaceWithAliasIfReferenceMatches(
            contentCacheKey,
            builtInSourceAliasKey,
            sourceEntry,
            completedEntry);
    }
}
