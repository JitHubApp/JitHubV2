using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal.Raster;

namespace MarkdownRenderer.Performance.Internal.Session.ImageSources;

/// <summary>
/// The session's byte- and metadata-budgeted LRU of resolved image sources,
/// shared across document scopes. Entries are exposed only after callers
/// revalidate a scalar identity snapshot, so eviction never waits on a
/// borrower.
/// </summary>
internal sealed class MarkdownImageSourceCache
{
    // Source bytes and the bookkeeping which owns them have separate limits.
    // Tiny/empty assets must not create an unbounded number of cache entries.
    private static readonly long SourceCacheMetadataBudgetBytes =
        (IntPtr.Size == 4 ? 2L : 4L) * 1024 * 1024;

    private readonly object _cacheGate = new();
    private readonly MarkdownPerformanceSessionLifetime _lifetime;
    private readonly long _budgetBytes;
    private readonly ConditionalWeakTable<IMarkdownImageResolver, object> _sourceResolverIdentities = new();
    private Dictionary<ImageSourceKey, CachedImageSource> _sourceCache = new(ImageSourceKeyComparer.Instance);
    private LinkedList<CachedImageSource> _sourceRecency = new();
    private Dictionary<MarkdownPerformanceDocumentScope, HashSet<ImageSourceKey>> _documentSourceKeys = new();
    private long _sourceCacheBytes;
    private long _sourceCacheMetadataBytes;
    private long _sourceCacheEntryIdentity;
    private long _sourceCacheHits;
    private long _sourceCacheEvictions;

    internal MarkdownImageSourceCache(MarkdownPerformanceSessionLifetime lifetime, long budgetBytes)
    {
        _lifetime = lifetime;
        _budgetBytes = budgetBytes;
    }

    internal long RetainedBytes
    {
        get
        {
            lock (_cacheGate)
                return _sourceCacheBytes;
        }
    }

    internal long Hits => Interlocked.Read(ref _sourceCacheHits);

    internal long Evictions => Interlocked.Read(ref _sourceCacheEvictions);

    /// <summary>Entries currently retained. Sampled by internal diagnostics/tests.</summary>
    internal int EntryCount
    {
        get
        {
            lock (_cacheGate)
                return _sourceCache.Count;
        }
    }

    /// <summary>
    /// Allocated entry capacity. A trim or final owner release swaps in fresh
    /// roots, so this returns to zero instead of keeping high-water arrays.
    /// </summary>
    internal int EntryCapacity
    {
        get
        {
            lock (_cacheGate)
                return _sourceCache.EnsureCapacity(0);
        }
    }

    /// <summary>Allocated capacity of the per-document key index; see <see cref="EntryCapacity"/>.</summary>
    internal int DocumentIndexCapacity
    {
        get
        {
            lock (_cacheGate)
                return _documentSourceKeys.EnsureCapacity(0);
        }
    }

    /// <summary>Charged bookkeeping weight. Sampled by internal diagnostics/tests.</summary>
    internal long MetadataBytes
    {
        get
        {
            lock (_cacheGate)
                return _sourceCacheMetadataBytes;
        }
    }

    /// <summary>
    /// Cache keys preserve resolver reference identity without owning
    /// arbitrary host/plugin state after its last document is closed.
    /// </summary>
    internal object GetResolverIdentity(IMarkdownImageResolver resolver) =>
        _sourceResolverIdentities.GetValue(resolver, static _ => new object());

    internal void ClearResolverIdentities() => _sourceResolverIdentities.Clear();

    internal void Trim()
    {
        lock (_cacheGate)
        {
            ResetSourceCacheNoLock();
        }
    }

    private void ResetSourceCacheNoLock()
    {
        // Swap private roots instead of clearing high-water backing arrays on
        // a memory-pressure/UI callback. No external borrower owns these roots.
        _sourceCache = new(ImageSourceKeyComparer.Instance);
        _sourceRecency = new();
        _documentSourceKeys = new();
        _sourceCacheBytes = 0;
        _sourceCacheMetadataBytes = 0;
    }

    internal bool TryGetMetadata(ImageSourceKey key, out CachedImageSourceIdentity identity)
    {
        identity = default;
        if (key.Source.Length > 2048)
            return false;
        lock (_cacheGate)
        {
            if (_lifetime.IsDisposed || !_sourceCache.TryGetValue(key, out CachedImageSource? cached))
                return false;
            // Do not return or retain the asset/entry while waiting for byte
            // admission. The identity is scalar so eviction can proceed safely.
            identity = new CachedImageSourceIdentity(cached.Identity, cached.Asset.Bytes.Length);
            return true;
        }
    }

    internal bool TryGetPrefetchedRasterIntrinsicMetadata(
        ImageSourceKey sharedKey,
        ImageSourceKey localKey,
        out RasterIntrinsicMetadata metadata)
    {
        metadata = default;
        lock (_cacheGate)
        {
            if (_lifetime.IsDisposed)
                return false;

            // Match the authoritative source lookup order. If a shared entry is
            // present without a hint, do not fall through to a potentially stale
            // document-local value for the same request.
            if (_sourceCache.TryGetValue(sharedKey, out CachedImageSource? shared))
            {
                if (shared.RasterIntrinsicMetadata is not { } sharedMetadata)
                    return false;
                metadata = sharedMetadata;
                return true;
            }
            if (_sourceCache.TryGetValue(localKey, out CachedImageSource? local))
            {
                if (local.RasterIntrinsicMetadata is not { } localMetadata)
                    return false;
                metadata = localMetadata;
                return true;
            }
            return false;
        }
    }

    internal bool TryGetIdentity(
        ImageSourceKey key,
        MarkdownImageAsset expectedAsset,
        out CachedImageSourceIdentity identity)
    {
        identity = default;
        if (key.Source.Length > 2048)
            return false;
        lock (_cacheGate)
        {
            if (_lifetime.IsDisposed || !_sourceCache.TryGetValue(key, out CachedImageSource? cached) ||
                !ReferenceEquals(cached.Asset, expectedAsset))
            {
                return false;
            }

            identity = new CachedImageSourceIdentity(cached.Identity, cached.Asset.Bytes.Length);
            return true;
        }
    }

    internal bool TryPublishRasterIntrinsicMetadata(
        ImageSourceKey key,
        CachedImageSourceIdentity expected,
        MarkdownImageAsset expectedAsset,
        RasterIntrinsicMetadata metadata)
    {
        lock (_cacheGate)
        {
            if (_lifetime.IsDisposed || !_sourceCache.TryGetValue(key, out CachedImageSource? cached) ||
                cached.Identity != expected.EntryIdentity ||
                cached.Asset.Bytes.Length != expected.ByteLength ||
                !ReferenceEquals(cached.Asset, expectedAsset))
            {
                return false;
            }

            if (cached.RasterIntrinsicMetadata is { } current)
                return current == metadata;
            if (_sourceCacheMetadataBytes > SourceCacheMetadataBudgetBytes -
                CachedImageSource.RasterIntrinsicMetadataWeightBytes)
            {
                return false;
            }

            cached.RasterIntrinsicMetadata = metadata;
            cached.MetadataWeight += CachedImageSource.RasterIntrinsicMetadataWeightBytes;
            _sourceCacheMetadataBytes += CachedImageSource.RasterIntrinsicMetadataWeightBytes;
            return true;
        }
    }

    internal bool TryExpose(
        ImageSourceKey key,
        CachedImageSourceIdentity expected,
        out MarkdownImageAsset? asset)
    {
        asset = null;
        lock (_cacheGate)
        {
            if (_lifetime.IsDisposed || !_sourceCache.TryGetValue(key, out CachedImageSource? cached) ||
                cached.Identity != expected.EntryIdentity ||
                cached.Asset.Bytes.Length != expected.ByteLength)
            {
                return false;
            }

            _sourceRecency.Remove(cached.Node);
            _sourceRecency.AddFirst(cached.Node);
            Interlocked.Increment(ref _sourceCacheHits);
            asset = cached.Asset;
            return true;
        }
    }

    internal void Store(ImageSourceKey sharedKey, ImageSourceKey localKey, MarkdownImageResolution resolution)
    {
        MarkdownImageAsset? asset = resolution.Asset;
        if (asset is null || _budgetBytes == 0 || sharedKey.Source.Length > 2048 ||
            asset.Bytes.Length > _budgetBytes)
            return;

        ImageSourceKey key = string.IsNullOrEmpty(asset.CacheKey) ? localKey : sharedKey;
        long metadataWeight = EstimateSourceMetadataWeight(key, asset);
        if (metadataWeight > SourceCacheMetadataBudgetBytes)
            return; // A cache miss never changes the authoritative resolution.

        lock (_cacheGate)
        {
            if (_lifetime.IsDisposed || key.DocumentScope?.IsDisposed == true)
                return;
            if (_sourceCache.TryGetValue(key, out CachedImageSource? old))
            {
                _sourceRecency.Remove(old.Node);
                _sourceCache.Remove(key);
                _sourceCacheBytes -= old.Asset.Bytes.Length;
                _sourceCacheMetadataBytes -= old.MetadataWeight;
            }
            var entry = new CachedImageSource(
                key, asset, metadataWeight, unchecked(++_sourceCacheEntryIdentity));
            entry.Node = _sourceRecency.AddFirst(entry);
            _sourceCache.Add(key, entry);
            _sourceCacheBytes += asset.Bytes.Length;
            _sourceCacheMetadataBytes += metadataWeight;
            if (key.DocumentScope is { } scope)
            {
                if (!_documentSourceKeys.TryGetValue(scope, out HashSet<ImageSourceKey>? keys))
                    _documentSourceKeys.Add(scope, keys = new HashSet<ImageSourceKey>(ImageSourceKeyComparer.Instance));
                keys.Add(key);
            }
            while ((_sourceCacheBytes > _budgetBytes ||
                    _sourceCacheMetadataBytes > SourceCacheMetadataBudgetBytes) &&
                   _sourceRecency.Last is { } last)
            {
                _sourceRecency.RemoveLast();
                _sourceCache.Remove(last.Value.Key);
                _sourceCacheBytes -= last.Value.Asset.Bytes.Length;
                _sourceCacheMetadataBytes -= last.Value.MetadataWeight;
                if (last.Value.Key.DocumentScope is { } evictedScope &&
                    _documentSourceKeys.TryGetValue(evictedScope, out HashSet<ImageSourceKey>? evictedKeys))
                {
                    evictedKeys.Remove(last.Value.Key);
                    if (evictedKeys.Count == 0)
                        _documentSourceKeys.Remove(evictedScope);
                }
                Interlocked.Increment(ref _sourceCacheEvictions);
            }
        }
    }

    private static long EstimateSourceMetadataWeight(ImageSourceKey key, MarkdownImageAsset asset)
    {
        // Includes entry/node/hash-table amortization, local-owner key indexing,
        // asset/array headers and context carriers. Count borrowed strings too:
        // overestimating shared context is safer than uncharged giant cache keys.
        static long TextWeight(string? text) => text is null ? 0 : 32L + 2L * text.Length;
        MarkdownImageResolveContext context = key.Context;
        MarkdownDocumentSource? document = context.DocumentSource;
        return 512L + TextWeight(key.Source) + TextWeight(asset.CacheKey) +
            TextWeight(asset.ContentType) + TextWeight(asset.ResolvedUri?.OriginalString) +
            TextWeight(context.BaseUri?.OriginalString) + TextWeight(context.DocumentPath) +
            TextWeight(document?.DocumentId) + TextWeight(document?.Owner) +
            TextWeight(document?.Repository) + TextWeight(document?.Ref) + TextWeight(document?.Path);
    }

    internal void RemoveDocumentEntries(MarkdownPerformanceDocumentScope scope)
    {
        lock (_cacheGate)
        {
            if (!_documentSourceKeys.Remove(scope, out HashSet<ImageSourceKey>? keys))
                return;
            foreach (ImageSourceKey key in keys)
            {
                if (!_sourceCache.Remove(key, out CachedImageSource? cached))
                    continue;
                _sourceRecency.Remove(cached.Node);
                _sourceCacheBytes -= cached.Asset.Bytes.Length;
                _sourceCacheMetadataBytes -= cached.MetadataWeight;
            }
            if (_sourceCache.Count == 0)
                ResetSourceCacheNoLock();
        }
    }
}
