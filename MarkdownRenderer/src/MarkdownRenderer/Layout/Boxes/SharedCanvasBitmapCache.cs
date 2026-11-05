using System;
using System.Globalization;
using System.Threading;
using MarkdownRenderer.Diagnostics;
using Microsoft.Graphics.Canvas;
using MarkdownRenderer.Utilities;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Device-partitioned, byte-budgeted cache for decoded Win2D images.
/// Live layout boxes hold leases, so eviction and device-loss cleanup never
/// dispose a bitmap that is still being painted. Each cache belongs to one
/// <see cref="ImageResourceScope"/> and charges lazy output admission to
/// that scope's <see cref="BitmapOutputAdmission"/>.
/// </summary>
internal sealed class SharedCanvasBitmapCache
{
    internal static readonly long DefaultBudgetBytes = Environment.Is64BitProcess
        ? 64L * 1024 * 1024
        : 32L * 1024 * 1024;
    private const int DecodeStripeCount = 64;
    internal const long InvalidBitmapFallbackWeightBytes = 1L * 1024 * 1024;
    private readonly BitmapOutputAdmission _outputAdmission;
    private int _cacheDisposalFailureCount;
    private int _lastCacheDisposalFailureHResult;
    private int _bitmapOutputPressureTrimRequested;
    private int _bitmapOutputPressureTrimQueued;
    // Numeric pixel-weight by unique owner lifetime, not driver heap size or
    // an admission cap. Cloning a lease never adds another bitmap charge.
    private readonly LiveBitmapPixelWeightLedger _bitmapWeightLedger = new();

    /// <summary>This cache's numeric live-pixel accounting.</summary>
    internal LiveBitmapPixelWeightLedger PixelWeightLedger => _bitmapWeightLedger;

    /// <summary>The bitmap output admission this cache's entries charge.</summary>
    internal BitmapOutputAdmission OutputAdmission => _outputAdmission;

    private readonly WeightedLruCache<SharedCanvasBitmapCacheKey, SharedCanvasBitmapCacheEntry> _cache;

    private readonly SemaphoreSlim[] _decodeStripes = CreateDecodeStripes();

    /// <summary>Creates a cache that charges lazy admission to <paramref name="outputAdmission"/>.</summary>
    /// <param name="outputAdmission">The scope's bitmap output admission.</param>
    /// <param name="budgetBytes">The retained-byte budget of the LRU.</param>
    /// <param name="weightSelector">
    /// Weighs an entry for the LRU; defaults to <see cref="SharedCanvasBitmapCacheEntry.WeightBytes"/>.
    /// A fault-injecting selector exercises a failed precommit insertion.
    /// </param>
    internal SharedCanvasBitmapCache(
        BitmapOutputAdmission outputAdmission,
        long budgetBytes,
        Func<SharedCanvasBitmapCacheEntry, long>? weightSelector = null)
    {
        ArgumentNullException.ThrowIfNull(outputAdmission);
        _outputAdmission = outputAdmission;
        _cache = new(
            budgetBytes,
            weightSelector ?? (static entry => entry.WeightBytes),
            static entry => entry.ReleaseCacheReference(),
            keyWeightSelector: static key => EstimateCacheKeyWeightBytes(key.Identity));
    }

    internal bool TryAcquire(CanvasDevice device, string identity, out Lease? lease)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(identity);

        var key = new SharedCanvasBitmapCacheKey(device, identity);
        if (_cache.TryGetValue(key, out SharedCanvasBitmapCacheEntry? entry))
        {
            var owner = new Lease();
            if (entry.TryAcquire())
            {
                owner.Attach(entry);
                lease = owner;
                return true;
            }
        }

        // An entry can reach zero between the LRU lookup and lease acquisition
        // if device-loss cleanup wins the race. The caller will decode and Set
        // a replacement; do not remove by key here because another thread may
        // already have installed a fresh entry for the same identity.
        lease = null;
        return false;
    }

    private static bool IsExactDisplayVariantKey(string identity, string prefix)
    {
        if (!identity.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        ReadOnlySpan<char> dimensions = identity.AsSpan(prefix.Length);
        int requestSeparator = dimensions.IndexOf('\u001F');
        if (requestSeparator <= 0)
            return false;
        if (requestSeparator >= 0)
        {
            ReadOnlySpan<char> request = dimensions[(requestSeparator + 1)..];
            const string requestPrefix = "request:";
            if (!request.StartsWith(requestPrefix, StringComparison.Ordinal))
                return false;
            request = request[requestPrefix.Length..];
            int scaleSeparator = request.IndexOf('@');
            if (scaleSeparator <= 0 ||
                !double.TryParse(request[..scaleSeparator], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out double available) ||
                !double.TryParse(request[(scaleSeparator + 1)..], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out double scale) ||
                !double.IsFinite(available) || available < 1 ||
                !double.IsFinite(scale) || scale <= 0)
                return false;
            dimensions = dimensions[..requestSeparator];
        }
        int separator = dimensions.IndexOf('x');
        if (separator is <= 0 || separator == dimensions.Length - 1)
            return false;
        return int.TryParse(dimensions[..separator], NumberStyles.None,
                CultureInfo.InvariantCulture, out int width) && width > 0 &&
            int.TryParse(dimensions[(separator + 1)..], NumberStyles.None,
                CultureInfo.InvariantCulture, out int height) && height > 0;
    }

    internal bool TryAcquireBestRasterPreview(
        CanvasDevice device,
        string cacheFamily,
        RasterImageSizingRequest sizingRequest,
        out Lease? lease)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(cacheFamily);
        lease = null;
        if (cacheFamily.Length == 0)
            return false;

        string variantPrefix = string.Concat(cacheFamily, "\u001Fdisplay:");
        // The cache predicate reads only immutable entry metadata and a detached
        // sizing request. Never query Win2D or live layout under the LRU lock.
        if (!_cache.TryGetBestMatch(
                (key, entry) => ReferenceEquals(key.Device, device) &&
                    IsExactDisplayVariantKey(key.Identity, variantPrefix) &&
                    IsCompatibleRasterPreview(entry, sizingRequest),
                static entry => (long)entry.PixelWidth * entry.PixelHeight,
                out SharedCanvasBitmapCacheEntry? preview) || preview is null)
            return false;
        var owner = new Lease();
        if (!preview.TryAcquire())
            return false;
        owner.Attach(preview);
        lease = owner;
        return true;
    }

    private static bool TryGetOrientedTarget(
        SharedCanvasBitmapCacheEntry entry, RasterImageSizingRequest sizingRequest,
        out (int Width, int Height) target)
    {
        target = default;
        if (sizingRequest.MaximumPixels <= 0 ||
            entry.IntrinsicSize is not { } intrinsic ||
            !double.IsFinite(intrinsic.Width) || !double.IsFinite(intrinsic.Height) ||
            intrinsic.Width < 1 || intrinsic.Height < 1 ||
            intrinsic.Width > int.MaxValue || intrinsic.Height > int.MaxValue ||
            intrinsic.Width != Math.Truncate(intrinsic.Width) ||
            intrinsic.Height != Math.Truncate(intrinsic.Height))
            return false;
        target = sizingRequest.GetSize((int)intrinsic.Width, (int)intrinsic.Height);
        return true;
    }

    private static bool IsCompatibleRasterPreview(SharedCanvasBitmapCacheEntry entry, RasterImageSizingRequest sizingRequest)
    {
        return entry.PixelWidth > 0 && entry.PixelHeight > 0 &&
            (long)entry.PixelWidth * entry.PixelHeight <= sizingRequest.MaximumPixels &&
            TryGetOrientedTarget(entry, sizingRequest, out var target) &&
            entry.PixelWidth <= target.Width && entry.PixelHeight <= target.Height &&
            (entry.PixelWidth < target.Width || entry.PixelHeight < target.Height);
    }

    internal Lease StoreAndAcquire(
        CanvasDevice device,
        string identity,
        CanvasBitmap bitmap,
        Size? intrinsicSize = null
        , long svgWorkId = 0,
        long svgSourceBytes = 0,
        long svgTargetPixels = 0
        , BitmapOutputAdmission.Reservation? bitmapReservation = null
    )
        => StoreAndAcquireCore(
            device,
            identity,
            bitmap,
            intrinsicSize,
            null,
            null
            , svgWorkId,
            svgSourceBytes,
            svgTargetPixels
            , bitmapReservation
        );

    /// <summary>
    /// Stores an SVG bitmap using the raster dimensions that were validated
    /// before upload. This avoids querying Win2D metadata on the hot path.
    /// </summary>
    internal Lease StoreSvgAndAcquire(
        CanvasDevice device,
        string identity,
        CanvasBitmap bitmap,
        Size? intrinsicSize,
        int pixelWidth,
        int pixelHeight
        , long svgWorkId,
        long svgSourceBytes,
        long svgTargetPixels
        , BitmapOutputAdmission.Reservation? bitmapReservation = null
    )
        => StoreAndAcquireCore(
            device,
            identity,
            bitmap,
            intrinsicSize,
            pixelWidth,
            pixelHeight
            , svgWorkId,
            svgSourceBytes,
            svgTargetPixels
            , bitmapReservation
        );

    private Lease StoreAndAcquireCore(
        CanvasDevice device,
        string identity,
        CanvasBitmap bitmap,
        Size? intrinsicSize,
        int? knownPixelWidth,
        int? knownPixelHeight
        , long svgWorkId,
        long svgSourceBytes,
        long svgTargetPixels
        , BitmapOutputAdmission.Reservation? bitmapReservation
    )
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(bitmap);
        if (knownPixelWidth.HasValue != knownPixelHeight.HasValue)
            throw new ArgumentException("Both known pixel dimensions must be supplied together.");
        if (knownPixelWidth is <= 0 || knownPixelHeight is <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(knownPixelWidth),
                "Known pixel dimensions must be positive.");

        // The entry starts with one reference owned by the returned lease.
        // Add a distinct cache reference before insertion; an over-budget
        // insertion immediately releases only that cache reference.
        // Allocate the returned owner before Entry starts lifetime accounting.
        // A failed owner allocation leaves the caller's bitmap uncharged.
        var lease = new Lease();
        SharedCanvasBitmapCacheEntry entry;
        if (bitmapReservation is null)
        {
            entry = new SharedCanvasBitmapCacheEntry(
                this,
                bitmap,
                intrinsicSize,
                knownPixelWidth,
                knownPixelHeight
                , svgWorkId,
                svgSourceBytes,
                svgTargetPixels
                , bitmapReservation: null
            );
            lease.Attach(entry);
            entry.AddCacheReference();
        }
        else
        {
            // A committed reservation belongs to exactly one output. Serialize
            // its dimension shrink and one-way transfer so concurrent misuse
            // cannot attach the same token to two distinct Entries.
            lock (bitmapReservation)
            {
                if (bitmapReservation.IsTransferred)
                    throw new InvalidOperationException("The bitmap output reservation already belongs to an Entry.");

                entry = new SharedCanvasBitmapCacheEntry(
                    this,
                    bitmap,
                    intrinsicSize,
                    knownPixelWidth,
                    knownPixelHeight
                    , svgWorkId,
                    svgSourceBytes,
                    svgTargetPixels
                    , bitmapReservation
                );
                lease.Attach(entry);
                entry.AddCacheReference();
                if (!entry.TransferBitmapOutputReservationToEntry())
                {
                    lease.AbandonUnpublishedOwnership();
                    throw new InvalidOperationException("The bitmap output reservation could not be transferred to the cache entry.");
                }
            }
        }
        bool insertionCompleted = false;
        try
        {
            using (MarkdownSvgStageTrace.Measure(
                svgWorkId,
                MarkdownSvgWorkStage.SvgCacheLruInsertAndEviction,
                svgSourceBytes,
                svgTargetPixels))
            {
                // Set preallocates its mutation batch. This key's hashing and
                // the cache's contained eviction callback cannot throw during
                // commit; allocation failure therefore leaves it unpublished.
                _cache.Set(new SharedCanvasBitmapCacheKey(device, identity), entry);
                insertionCompleted = true;
            }
            return lease;
        }
        catch when (insertionCompleted)
        {
            // A tracing cleanup failure must not report failed ownership after
            // the cache has committed and the bitmap is already lease-owned.
            return lease;
        }
        catch
        {
            // The cache is an optimization. A failed pre-commit insertion
            // leaves the mapping unchanged; drop only its speculative reference
            // and return the still-valid lease as an uncached image. Keeping
            // the owner lease attached also keeps its live bitmap/admission
            // charge until native disposal actually succeeds.
            entry.ReleaseCacheReference();
            return lease;
        }
    }

    internal Lease CreateUncachedLease(
        CanvasBitmap bitmap,
        BitmapOutputAdmission.Reservation? bitmapReservation = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var lease = new Lease();
        if (bitmapReservation is null)
        {
            lease.Attach(new SharedCanvasBitmapCacheEntry(this, bitmap));
            return lease;
        }

        lock (bitmapReservation)
        {
            if (bitmapReservation.IsTransferred)
                throw new InvalidOperationException("The bitmap output reservation already belongs to an Entry.");
            var entry = new SharedCanvasBitmapCacheEntry(this, bitmap, bitmapReservation);
            lease.Attach(entry);
            if (!entry.TransferBitmapOutputReservationToEntry())
            {
                lease.AbandonUnpublishedOwnership();
                throw new InvalidOperationException("The bitmap output reservation could not be transferred to the uncached entry.");
            }
        }
        return lease;
    }

    internal Lease CreateUncachedLease(
        CanvasBitmap bitmap,
        int pixelWidth,
        int pixelHeight,
        Size? intrinsicSize = null,
        BitmapOutputAdmission.Reservation? bitmapReservation = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (pixelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        var lease = new Lease();
        if (bitmapReservation is null)
        {
            lease.Attach(new SharedCanvasBitmapCacheEntry(this, bitmap, pixelWidth, pixelHeight, intrinsicSize));
            return lease;
        }

        lock (bitmapReservation)
        {
            if (bitmapReservation.IsTransferred)
                throw new InvalidOperationException("The bitmap output reservation already belongs to an Entry.");
            var entry = new SharedCanvasBitmapCacheEntry(this, bitmap, pixelWidth, pixelHeight, intrinsicSize, bitmapReservation);
            lease.Attach(entry);
            if (!entry.TransferBitmapOutputReservationToEntry())
            {
                lease.AbandonUnpublishedOwnership();
                throw new InvalidOperationException("The bitmap output reservation could not be transferred to the uncached entry.");
            }
        }
        return lease;
    }

    internal void TrimForBitmapOutputPressure() => _cache.Clear();

    /// <summary>
    /// Requests a best-effort cache-only trim without making the rejection
    /// path synchronously dispose Win2D resources (which may run on the UI
    /// thread). The worker owns no ImageBox, lease, or bitmap references.
    /// </summary>
    internal void RequestBitmapOutputPressureTrim()
    {
        Interlocked.Exchange(ref _bitmapOutputPressureTrimRequested, 1);
        QueueBitmapOutputPressureTrim();
    }

    private void QueueBitmapOutputPressureTrim()
    {
        if (Interlocked.CompareExchange(ref _bitmapOutputPressureTrimQueued, 1, 0) != 0)
            return;

        try
        {
            // Reclamation has no document work or caller identity to inherit.
            // Do not retain ambient host AsyncLocal state for this source-free
            // maintenance job, and never run its native cleanup inline.
            if (ThreadPool.UnsafeQueueUserWorkItem(
                    static state => ((SharedCanvasBitmapCache)state!).DrainBitmapOutputPressureTrim(), this))
                return;
        }
        catch (Exception)
        {
            // Cache reclamation is opportunistic. A later rejection can retry
            // queueing; never fall back to native disposal on this caller.
        }

        Interlocked.Exchange(ref _bitmapOutputPressureTrimQueued, 0);
    }

    private void DrainBitmapOutputPressureTrim()
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _bitmapOutputPressureTrimRequested, 0);
                try
                {
                    _cache.Clear();
                }
                catch (Exception)
                {
                    // The trim is best-effort; a failed worker must not escape
                    // to the ThreadPool or alter the rejected bitmap path.
                }
            }
            while (Volatile.Read(ref _bitmapOutputPressureTrimRequested) != 0);
        }
        finally
        {
            Interlocked.Exchange(ref _bitmapOutputPressureTrimQueued, 0);
            if (Volatile.Read(ref _bitmapOutputPressureTrimRequested) != 0)
                QueueBitmapOutputPressureTrim();
        }
    }

    internal SemaphoreSlim GetDecodeGate(CanvasDevice device, string identity)
    {
        var key = new SharedCanvasBitmapCacheKey(device, identity);
        int index = (key.GetHashCode() & int.MaxValue) % _decodeStripes.Length;
        return _decodeStripes[index];
    }

    internal void ReleaseDevice(CanvasDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _cache.RemoveWhere(key => ReferenceEquals(key.Device, device));
    }

    internal void Clear() => _cache.Clear();

    /// <summary>Reads this cache's retained, live and failure accounting.</summary>
    internal SharedCanvasBitmapCacheSnapshot GetSnapshot() => new(
        _cache.RetainedBytes,
        _cache.BudgetBytes,
        _bitmapWeightLedger.LivePixelWeightBytes,
        _bitmapWeightLedger.UncertainDisposePixelWeightBytes,
        Volatile.Read(ref _cacheDisposalFailureCount),
        Volatile.Read(ref _lastCacheDisposalFailureHResult));

    // Lease release failures flow to ImageBox's retirement barrier. LRU
    // callbacks cannot throw: Set has already committed its replacement and
    // must finish releasing every evicted cache reference.
    internal static void ReleaseFinalReference(ref int referenceCount, IDisposable resource)
    {
        if (Interlocked.Decrement(ref referenceCount) == 0)
            resource.Dispose();
    }

    internal void ReleaseFinalReference(ref int referenceCount, IDisposable resource, long pixelWeightBytes) =>
        _bitmapWeightLedger.ReleaseFinalReference(ref referenceCount, resource, pixelWeightBytes);

    internal void ReleaseCacheReference(ref int referenceCount, IDisposable resource)
    {
        try
        {
            ReleaseFinalReference(ref referenceCount, resource);
        }
        catch (Exception exception)
        {
            RecordCacheDisposalFailure(exception);
        }
    }

    internal void ReleaseCacheReference(ref int referenceCount, IDisposable resource, long pixelWeightBytes)
    {
        try
        {
            ReleaseFinalReference(ref referenceCount, resource, pixelWeightBytes);
        }
        catch (Exception exception)
        {
            RecordCacheDisposalFailure(exception);
        }
    }

    internal void RecordCacheDisposalFailure(Exception exception)
    {
        Volatile.Write(ref _lastCacheDisposalFailureHResult, exception.HResult);
        Interlocked.Increment(ref _cacheDisposalFailureCount);
        try
        {
            MarkdownPerformanceEventSource.Log.BitmapCacheCleanupFailed(exception.HResult);
        }
        catch (Exception)
        {
            // Diagnostic listeners must not interrupt LRU eviction.
        }
    }

    internal static long EstimateWeightBytes(double pixelWidth, double pixelHeight)
    {
        double bytes = Math.Max(1d, pixelWidth) * Math.Max(1d, pixelHeight) * 4d;
        return bytes >= long.MaxValue ? long.MaxValue : Math.Max(1L, (long)Math.Ceiling(bytes));
    }

    internal static long EstimateCacheKeyWeightBytes(string identity) =>
        48L + (long)identity.Length * sizeof(char);

    private static SemaphoreSlim[] CreateDecodeStripes()
    {
        var stripes = new SemaphoreSlim[DecodeStripeCount];
        for (int i = 0; i < stripes.Length; i++)
            stripes[i] = new SemaphoreSlim(1, 1);
        return stripes;
    }

    internal sealed class Lease : IDisposable
    {
        private SharedCanvasBitmapCacheEntry? _entry;

        internal Lease() { }

        internal Lease(SharedCanvasBitmapCacheEntry entry) => _entry = entry;

        // Used only by factories before their one-way ownership transfer.
        internal void Attach(SharedCanvasBitmapCacheEntry entry) => _entry = entry;

        internal bool IsAttachedTo(SharedCanvasBitmapCacheEntry entry) =>
            ReferenceEquals(Volatile.Read(ref _entry), entry);

        internal void AbandonUnpublishedOwnership() =>
            Interlocked.Exchange(ref _entry, null)?.AbandonUnpublishedOwnership();

        internal CanvasBitmap Bitmap =>
            Volatile.Read(ref _entry)?.Bitmap ??
            throw new ObjectDisposedException(nameof(Lease));

        internal Size? IntrinsicSize => Volatile.Read(ref _entry)?.IntrinsicSize;

        /// <summary>The held entry's owner count, or null once this lease let go of it.</summary>
        internal int? EntryReferenceCount => Volatile.Read(ref _entry)?.ReferenceCount;

        internal bool IsBitmapOutputAdmitted =>
            Volatile.Read(ref _entry)?.IsBitmapOutputAdmitted ?? false;

        internal long BitmapOutputAdmissionBytes =>
            Volatile.Read(ref _entry)?.BitmapOutputAdmissionBytes ?? 0;

        internal long PixelWeightBytes =>
            Volatile.Read(ref _entry)?.PixelWeightBytes ?? 0;

        internal bool TryAdmitBitmapOutput(
            bool isVisible,
            bool isReplacement,
            out long requiredBytes,
            out long capacityVersion)
        {
            SharedCanvasBitmapCacheEntry? entry = Volatile.Read(ref _entry);
            if (entry is null)
            {
                requiredBytes = 0;
                capacityVersion = 0;
                return false;
            }

            return entry.TryAdmitBitmapOutput(
                this,
                isVisible,
                isReplacement,
                out requiredBytes,
                out capacityVersion);
        }

        internal bool IsWithinPixelBudget(long maximumPixels)
        {
            SharedCanvasBitmapCacheEntry? entry = Volatile.Read(ref _entry);
            return entry is { PixelWidth: > 0, PixelHeight: > 0 } &&
                (long)entry.PixelWidth * entry.PixelHeight <= maximumPixels;
        }

        internal bool MatchesSizingRequest(RasterImageSizingRequest sizingRequest)
        {
            SharedCanvasBitmapCacheEntry? entry = Volatile.Read(ref _entry);
            return entry is not null &&
                TryGetOrientedTarget(entry, sizingRequest, out var target) &&
                entry.PixelWidth == target.Width && entry.PixelHeight == target.Height;
        }

        internal Lease? TryClone()
        {
            SharedCanvasBitmapCacheEntry? entry = Volatile.Read(ref _entry);
            if (entry is null)
                return null;
            var clone = new Lease();
            if (!entry.TryAcquire())
                return null;
            clone.Attach(entry);
            return clone;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _entry, null)?.Release();
        }
    }
}
