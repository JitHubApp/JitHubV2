using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using MarkdownRenderer.Diagnostics;
using Microsoft.Graphics.Canvas;
using MarkdownRenderer.Utilities;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Device-partitioned, byte-budgeted cache for decoded Win2D images.
/// Live layout boxes hold leases, so eviction and device-loss cleanup never
/// dispose a bitmap that is still being painted.
/// </summary>
internal static class SharedCanvasBitmapCache
{
    internal static readonly long DefaultBudgetBytes = Environment.Is64BitProcess
        ? 64L * 1024 * 1024
        : 32L * 1024 * 1024;
    private const int DecodeStripeCount = 64;
    private const long InvalidBitmapFallbackWeightBytes = 1L * 1024 * 1024;
    private static int _cacheDisposalFailureCount;
    private static int _lastCacheDisposalFailureHResult;
    private static int _bitmapOutputPressureTrimRequested;
    private static int _bitmapOutputPressureTrimQueued;
    // Numeric pixel-weight by unique owner lifetime, not driver heap size or
    // an admission cap. Cloning a lease never adds another bitmap charge.
    private static readonly LiveBitmapPixelWeightLedger BitmapWeightLedger = new();

    private static readonly WeightedLruCache<CacheKey, Entry> Cache = new(
        DefaultBudgetBytes,
        static entry => entry.WeightBytes,
        static entry => entry.ReleaseCacheReference(),
        keyWeightSelector: static key => EstimateCacheKeyWeightBytes(key.Identity));

    private static readonly SemaphoreSlim[] DecodeStripes = CreateDecodeStripes();

    static SharedCanvasBitmapCache()
    {
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
        }
        catch (Exception exception) when (
            exception is COMException or PlatformNotSupportedException or TypeInitializationException)
        {
            // MemoryManager is unavailable in some unpackaged and test hosts.
            // The hard LRU budget and explicit device-loss cleanup still apply.
        }
    }

    internal static bool TryAcquire(CanvasDevice device, string identity, out Lease? lease)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(identity);

        var key = new CacheKey(device, identity);
        if (Cache.TryGetValue(key, out Entry? entry))
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

    internal static bool TryAcquireBestRasterPreview(
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
        if (!Cache.TryGetBestMatch(
                (key, entry) => ReferenceEquals(key.Device, device) &&
                    IsExactDisplayVariantKey(key.Identity, variantPrefix) &&
                    IsCompatibleRasterPreview(entry, sizingRequest),
                static entry => (long)entry.PixelWidth * entry.PixelHeight,
                out Entry? preview) || preview is null)
            return false;
        var owner = new Lease();
        if (!preview.TryAcquire())
            return false;
        owner.Attach(preview);
        lease = owner;
        return true;
    }

    private static bool TryGetOrientedTarget(
        Entry entry, RasterImageSizingRequest sizingRequest,
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

    private static bool IsCompatibleRasterPreview(Entry entry, RasterImageSizingRequest sizingRequest)
    {
        return entry.PixelWidth > 0 && entry.PixelHeight > 0 &&
            (long)entry.PixelWidth * entry.PixelHeight <= sizingRequest.MaximumPixels &&
            TryGetOrientedTarget(entry, sizingRequest, out var target) &&
            entry.PixelWidth <= target.Width && entry.PixelHeight <= target.Height &&
            (entry.PixelWidth < target.Width || entry.PixelHeight < target.Height);
    }

    internal static Lease StoreAndAcquire(
        CanvasDevice device,
        string identity,
        CanvasBitmap bitmap,
        Size? intrinsicSize = null
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , long svgWorkId = 0,
        long svgSourceBytes = 0,
        long svgTargetPixels = 0
#endif
        , BitmapOutputAdmission.Reservation? bitmapReservation = null
    )
        => StoreAndAcquireCore(
            device,
            identity,
            bitmap,
            intrinsicSize,
            null,
            null
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            , svgWorkId,
            svgSourceBytes,
            svgTargetPixels
#endif
            , bitmapReservation
        );

    /// <summary>
    /// Stores an SVG bitmap using the raster dimensions that were validated
    /// before upload. This avoids querying Win2D metadata on the hot path.
    /// </summary>
    internal static Lease StoreSvgAndAcquire(
        CanvasDevice device,
        string identity,
        CanvasBitmap bitmap,
        Size? intrinsicSize,
        int pixelWidth,
        int pixelHeight
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , long svgWorkId,
        long svgSourceBytes,
        long svgTargetPixels
#endif
        , BitmapOutputAdmission.Reservation? bitmapReservation = null
    )
        => StoreAndAcquireCore(
            device,
            identity,
            bitmap,
            intrinsicSize,
            pixelWidth,
            pixelHeight
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            , svgWorkId,
            svgSourceBytes,
            svgTargetPixels
#endif
            , bitmapReservation
        );

    private static Lease StoreAndAcquireCore(
        CanvasDevice device,
        string identity,
        CanvasBitmap bitmap,
        Size? intrinsicSize,
        int? knownPixelWidth,
        int? knownPixelHeight
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , long svgWorkId,
        long svgSourceBytes,
        long svgTargetPixels
#endif
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
        Entry entry;
        if (bitmapReservation is null)
        {
            entry = new Entry(
                bitmap,
                intrinsicSize,
                knownPixelWidth,
                knownPixelHeight
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                , svgWorkId,
                svgSourceBytes,
                svgTargetPixels
#endif
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

                entry = new Entry(
                    bitmap,
                    intrinsicSize,
                    knownPixelWidth,
                    knownPixelHeight
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    , svgWorkId,
                    svgSourceBytes,
                    svgTargetPixels
#endif
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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            using (MarkdownSvgStageTrace.Measure(
                svgWorkId,
                MarkdownSvgWorkStage.SvgCacheLruInsertAndEviction,
                svgSourceBytes,
                svgTargetPixels))
#endif
            {
                // Set preallocates its mutation batch. This key's hashing and
                // the cache's contained eviction callback cannot throw during
                // commit; allocation failure therefore leaves it unpublished.
                Cache.Set(new CacheKey(device, identity), entry);
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

    internal static Lease CreateUncachedLease(
        CanvasBitmap bitmap,
        BitmapOutputAdmission.Reservation? bitmapReservation = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var lease = new Lease();
        if (bitmapReservation is null)
        {
            lease.Attach(new Entry(bitmap));
            return lease;
        }

        lock (bitmapReservation)
        {
            if (bitmapReservation.IsTransferred)
                throw new InvalidOperationException("The bitmap output reservation already belongs to an Entry.");
            var entry = new Entry(bitmap, bitmapReservation);
            lease.Attach(entry);
            if (!entry.TransferBitmapOutputReservationToEntry())
            {
                lease.AbandonUnpublishedOwnership();
                throw new InvalidOperationException("The bitmap output reservation could not be transferred to the uncached entry.");
            }
        }
        return lease;
    }

    internal static Lease CreateUncachedLease(
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
            lease.Attach(new Entry(bitmap, pixelWidth, pixelHeight, intrinsicSize));
            return lease;
        }

        lock (bitmapReservation)
        {
            if (bitmapReservation.IsTransferred)
                throw new InvalidOperationException("The bitmap output reservation already belongs to an Entry.");
            var entry = new Entry(bitmap, pixelWidth, pixelHeight, intrinsicSize, bitmapReservation);
            lease.Attach(entry);
            if (!entry.TransferBitmapOutputReservationToEntry())
            {
                lease.AbandonUnpublishedOwnership();
                throw new InvalidOperationException("The bitmap output reservation could not be transferred to the uncached entry.");
            }
        }
        return lease;
    }

    internal static void TrimForBitmapOutputPressure() => Cache.Clear();

    /// <summary>
    /// Requests a best-effort cache-only trim without making the rejection
    /// path synchronously dispose Win2D resources (which may run on the UI
    /// thread). The worker owns no ImageBox, lease, or bitmap references.
    /// </summary>
    internal static void RequestBitmapOutputPressureTrim()
    {
        Interlocked.Exchange(ref _bitmapOutputPressureTrimRequested, 1);
        QueueBitmapOutputPressureTrim();
    }

    private static void QueueBitmapOutputPressureTrim()
    {
        if (Interlocked.CompareExchange(ref _bitmapOutputPressureTrimQueued, 1, 0) != 0)
            return;

        try
        {
            // Reclamation has no document work or caller identity to inherit.
            // Do not retain ambient host AsyncLocal state for this source-free
            // maintenance job, and never run its native cleanup inline.
            if (ThreadPool.UnsafeQueueUserWorkItem(static _ => DrainBitmapOutputPressureTrim(), null))
                return;
        }
        catch (Exception)
        {
            // Cache reclamation is opportunistic. A later rejection can retry
            // queueing; never fall back to native disposal on this caller.
        }

        Interlocked.Exchange(ref _bitmapOutputPressureTrimQueued, 0);
    }

    private static void DrainBitmapOutputPressureTrim()
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _bitmapOutputPressureTrimRequested, 0);
                try
                {
                    Cache.Clear();
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

    internal static SemaphoreSlim GetDecodeGate(CanvasDevice device, string identity)
    {
        var key = new CacheKey(device, identity);
        int index = (key.GetHashCode() & int.MaxValue) % DecodeStripes.Length;
        return DecodeStripes[index];
    }

    internal static void ReleaseDevice(CanvasDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Cache.RemoveWhere(key => ReferenceEquals(key.Device, device));
    }

    internal static void Clear() => Cache.Clear();

    internal static long RetainedBytesForTests => Cache.RetainedBytes;

    internal static long BudgetBytesForTests => Cache.BudgetBytes;

    internal static long LiveBitmapPixelWeightBytesForTests => BitmapWeightLedger.LivePixelWeightBytes;

    internal static long UncertainBitmapDisposePixelWeightBytesForTests => BitmapWeightLedger.UncertainDisposePixelWeightBytes;

    internal static int CacheDisposalFailureCountForTests =>
        Volatile.Read(ref _cacheDisposalFailureCount);

    internal static int LastCacheDisposalFailureHResultForTests =>
        Volatile.Read(ref _lastCacheDisposalFailureHResult);

    internal static void SetBudgetForTests(long budgetBytes) => Cache.SetBudget(budgetBytes);

    // Lease release failures flow to ImageBox's retirement barrier. LRU
    // callbacks cannot throw: Set has already committed its replacement and
    // must finish releasing every evicted cache reference.
    internal static void ReleaseFinalReference(ref int referenceCount, IDisposable resource)
    {
        if (Interlocked.Decrement(ref referenceCount) == 0)
            resource.Dispose();
    }

    internal static void ReleaseFinalReference(ref int referenceCount, IDisposable resource, long pixelWeightBytes) =>
        BitmapWeightLedger.ReleaseFinalReference(ref referenceCount, resource, pixelWeightBytes);

    internal static void ReleaseCacheReference(ref int referenceCount, IDisposable resource)
    {
        try
        {
            ReleaseFinalReference(ref referenceCount, resource);
        }
        catch (Exception exception)
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
    }

    private static void OnAppMemoryUsageIncreased(object? sender, object args)
    {
        Cache.Clear();
        ImageBox.TrimSharedCachesForMemoryPressure();
    }

    internal static void ReleaseCacheReference(ref int referenceCount, IDisposable resource, long pixelWeightBytes)
    {
        try
        {
            ReleaseFinalReference(ref referenceCount, resource, pixelWeightBytes);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _lastCacheDisposalFailureHResult, exception.HResult);
            Interlocked.Increment(ref _cacheDisposalFailureCount);
            try { MarkdownPerformanceEventSource.Log.BitmapCacheCleanupFailed(exception.HResult); }
            catch (Exception) { /* Diagnostic listeners must not interrupt LRU eviction. */ }
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

    /// <summary>Numeric-only accounting; never retains a resource reference.</summary>
    internal sealed class LiveBitmapPixelWeightLedger
    {
        private long _livePixelWeightBytes;
        private long _uncertainDisposePixelWeightBytes;
        internal long LivePixelWeightBytes => Volatile.Read(ref _livePixelWeightBytes);
        internal long UncertainDisposePixelWeightBytes => Volatile.Read(ref _uncertainDisposePixelWeightBytes);

        internal void Register(long pixelWeightBytes)
        {
            if (pixelWeightBytes > 0)
                Interlocked.Add(ref _livePixelWeightBytes, pixelWeightBytes);
        }

        internal void AbandonRegistration(long pixelWeightBytes)
        {
            if (pixelWeightBytes > 0)
                Interlocked.Add(ref _livePixelWeightBytes, -pixelWeightBytes);
        }

        internal void ReleaseSuccessfulDispose(long pixelWeightBytes)
        {
            if (pixelWeightBytes > 0)
                Interlocked.Add(ref _livePixelWeightBytes, -pixelWeightBytes);
        }

        internal void RecordUncertainDispose(long pixelWeightBytes)
        {
            if (pixelWeightBytes > 0)
                Interlocked.Add(ref _uncertainDisposePixelWeightBytes, pixelWeightBytes);
        }

        internal void ReleaseFinalReference(ref int referenceCount, IDisposable resource, long pixelWeightBytes)
        {
            if (Interlocked.Decrement(ref referenceCount) != 0)
                return;
            try { resource.Dispose(); }
            catch
            {
                // Native disposal state is unknown: retain its conservative
                // charge without retaining the failed native object.
                RecordUncertainDispose(pixelWeightBytes);
                throw;
            }
            ReleaseSuccessfulDispose(pixelWeightBytes);
        }
    }

    internal readonly struct CacheKey : IEquatable<CacheKey>
    {
        internal CacheKey(CanvasDevice device, string identity)
        {
            Device = device;
            Identity = identity;
        }

        internal readonly CanvasDevice Device;

        internal readonly string Identity;

        public bool Equals(CacheKey other) =>
            ReferenceEquals(Device, other.Device) &&
            StringComparer.Ordinal.Equals(Identity, other.Identity);

        public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            RuntimeHelpers.GetHashCode(Device),
            Identity is null ? 0 : StringComparer.Ordinal.GetHashCode(Identity));
    }

    internal sealed class Entry
    {
        private int _referenceCount = 1;
        private readonly object _bitmapOutputAdmissionGate = new();
        private BitmapOutputAdmission.Reservation? _bitmapOutputReservation;
        private bool _bitmapOutputReservationTransferred;

        // Outside the LRU does not mean outside the unique-owner ledger.
        internal Entry(
            CanvasBitmap bitmap,
            BitmapOutputAdmission.Reservation? bitmapReservation = null)
        {
            long pixelWeightBytes = InvalidBitmapFallbackWeightBytes;
            int pixelWidth = 0;
            int pixelHeight = 0;
            try
            {
                var pixels = bitmap.SizeInPixels;
                if (pixels.Width > 0 && pixels.Height > 0 &&
                    pixels.Width <= int.MaxValue && pixels.Height <= int.MaxValue)
                {
                    pixelWidth = (int)pixels.Width;
                    pixelHeight = (int)pixels.Height;
                    pixelWeightBytes = EstimateWeightBytes(pixels.Width, pixels.Height);
                }
                else if (bitmapReservation is not null)
                {
                    throw new InvalidOperationException("Bitmap dimensions cannot be represented by the admitted output metadata.");
                }
            }
            catch when (bitmapReservation is null)
            {
                // Device loss must not change legacy ownership or retirement.
            }
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
            WeightBytes = pixelWeightBytes;
            PixelWeightBytes = pixelWeightBytes;
            ConfigureBitmapOutputReservation(bitmapReservation);
            BitmapWeightLedger.Register(PixelWeightBytes);
            Bitmap = bitmap;
        }

        internal Entry(
            CanvasBitmap bitmap,
            int pixelWidth,
            int pixelHeight,
            Size? intrinsicSize = null,
            BitmapOutputAdmission.Reservation? bitmapReservation = null)
        {
            IntrinsicSize = intrinsicSize;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
            PixelWeightBytes = EstimateWeightBytes(pixelWidth, pixelHeight);
            WeightBytes = PixelWeightBytes;
            ConfigureBitmapOutputReservation(bitmapReservation);
            BitmapWeightLedger.Register(PixelWeightBytes);
            Bitmap = bitmap;
        }

        internal Entry(
            CanvasBitmap bitmap,
            Size? intrinsicSize,
            int? knownPixelWidth,
            int? knownPixelHeight
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            , long svgWorkId,
            long svgSourceBytes,
            long svgTargetPixels
#endif
            , BitmapOutputAdmission.Reservation? bitmapReservation
        )
        {
            IntrinsicSize = intrinsicSize;
            if (knownPixelWidth.HasValue && knownPixelHeight.HasValue)
            {
                PixelWidth = knownPixelWidth.Value;
                PixelHeight = knownPixelHeight.Value;
                WeightBytes = EstimateWeightBytes(knownPixelWidth.Value, knownPixelHeight.Value);
                PixelWeightBytes = WeightBytes;
                ConfigureBitmapOutputReservation(bitmapReservation);
                BitmapWeightLedger.Register(PixelWeightBytes);
                Bitmap = bitmap;
                return;
            }
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            using (MarkdownSvgStageTrace.Measure(
                svgWorkId,
                MarkdownSvgWorkStage.SvgCacheWeightSizeQuery,
                svgSourceBytes,
                svgTargetPixels))
            {
#endif
            try
            {
                var pixels = bitmap.SizeInPixels;
                if (pixels.Width == 0 || pixels.Height == 0 ||
                    pixels.Width > int.MaxValue || pixels.Height > int.MaxValue)
                    throw new InvalidOperationException("Bitmap dimensions cannot be represented by the cache metadata.");
                PixelWidth = (int)pixels.Width;
                PixelHeight = (int)pixels.Height;
                WeightBytes = EstimateWeightBytes(pixels.Width, pixels.Height);
            }
            catch when (bitmapReservation is null)
            {
                // A device-lost entry may still need normal lease retirement,
                // but it must never be chosen as a preview candidate or evade
                // the bounded cache budget.
                WeightBytes = InvalidBitmapFallbackWeightBytes;
            }
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            }
#endif
            // Finish tracing before transferring ownership/accounting, so a
            // tracing failure cannot leave a phantom retained bitmap charge.
            PixelWeightBytes = WeightBytes;
            ConfigureBitmapOutputReservation(bitmapReservation);
            BitmapWeightLedger.Register(PixelWeightBytes);
            Bitmap = bitmap;
        }

        internal readonly CanvasBitmap Bitmap;

        internal readonly long WeightBytes;

        internal readonly long PixelWeightBytes;

        internal readonly Size? IntrinsicSize;

        internal readonly int PixelWidth;

        internal readonly int PixelHeight;

        internal bool IsBitmapOutputAdmitted
        {
            get
            {
                lock (_bitmapOutputAdmissionGate)
                    return _bitmapOutputReservationTransferred &&
                        _bitmapOutputReservation is { IsCommitted: true };
            }
        }

        internal long BitmapOutputAdmissionBytes
        {
            get
            {
                lock (_bitmapOutputAdmissionGate)
                    return _bitmapOutputReservationTransferred
                        ? _bitmapOutputReservation?.Bytes ?? 0
                        : 0;
            }
        }

        internal void AddCacheReference() => Interlocked.Increment(ref _referenceCount);

        private void ConfigureBitmapOutputReservation(
            BitmapOutputAdmission.Reservation? bitmapReservation)
        {
            if (bitmapReservation is null)
                return;

            long actualBytes = PixelWeightBytes;
            long reservedBytes = bitmapReservation.Bytes;
            if (actualBytes <= 0 || reservedBytes < actualBytes)
                throw new InvalidOperationException(
                    "The decoded bitmap exceeds its admitted output reservation.");
            if (!bitmapReservation.IsCommitted || bitmapReservation.IsTransferred)
                throw new InvalidOperationException(
                    "A bitmap output reservation must be committed and not already transferred before cache ownership.");

            // Do not shrink here: this token may be offered concurrently to
            // another factory, and only the winning Entry is allowed to alter
            // the charge. Shrink only after its one-way transfer succeeds.
            _bitmapOutputReservation = bitmapReservation;
        }

        internal bool TransferBitmapOutputReservationToEntry()
        {
            lock (_bitmapOutputAdmissionGate)
            {
                if (_bitmapOutputReservation is null)
                    return true;
                if (_bitmapOutputReservationTransferred)
                    return true;
                if (!_bitmapOutputReservation.TransferToEntry())
                    return false;
                _bitmapOutputReservationTransferred = true;
                if (!_bitmapOutputReservation.ResizeDown(PixelWeightBytes))
                {
                    // Transfer is irreversible. Preserve the conservative
                    // original charge rather than publishing an unaccounted
                    // bitmap or letting another Entry reuse this token.
                    _bitmapOutputReservation.MarkUncertain();
                }
                return true;
            }
        }

        internal bool TryAdmitBitmapOutput(
            Lease lease,
            bool isVisible,
            bool isReplacement,
            out long requiredBytes,
            out long capacityVersion)
        {
            requiredBytes = 0;
            capacityVersion = 0;
            lock (_bitmapOutputAdmissionGate)
            {
                // Serialize lazy admission with final release and verify the
                // requesting lease still owns this Entry at the linearization
                // point. A concurrent Dispose then waits to release the bitmap.
                if (!lease.IsAttachedTo(this) || Volatile.Read(ref _referenceCount) <= 0)
                    return false;

                if (_bitmapOutputReservationTransferred &&
                    _bitmapOutputReservation is { IsCommitted: true })
                    return true;

                // A factory's precommitted reservation is not visible to its
                // lease until the factory completes the one-way transfer.
                if (_bitmapOutputReservation is not null || PixelWeightBytes <= 0)
                {
                    requiredBytes = PixelWeightBytes;
                    return false;
                }

                if (PixelWeightBytes > BitmapOutputAdmission.Shared.MaximumSingleOutputBytes)
                {
                    requiredBytes = PixelWeightBytes;
                    capacityVersion = BitmapOutputAdmission.Shared.GetSnapshot().CapacityVersion;
                    return false;
                }

                if (!BitmapOutputAdmission.Shared.TryReserve(
                        PixelWeightBytes,
                        isVisible,
                        isReplacement,
                        out BitmapOutputAdmission.Reservation? reservation,
                        out capacityVersion))
                {
                    requiredBytes = PixelWeightBytes;
                    return false;
                }

                if (reservation is null)
                    return false;

                try
                {
                    if (!reservation.Commit())
                    {
                        reservation.Dispose();
                        requiredBytes = PixelWeightBytes;
                        return false;
                    }
                    if (!reservation.TransferToEntry())
                    {
                        reservation.MarkUncertain();
                        // Retain the terminal token on this Entry so another
                        // lease cannot charge the same still-live bitmap again.
                        _bitmapOutputReservation = reservation;
                        requiredBytes = PixelWeightBytes;
                        return false;
                    }

                    _bitmapOutputReservation = reservation;
                    _bitmapOutputReservationTransferred = true;
                    return true;
                }
                catch
                {
                    if (reservation.IsCommitted)
                    {
                        reservation.MarkUncertain();
                        // The native bitmap is still alive, so retain this
                        // terminal charge on the Entry and prevent a later
                        // lease from admitting the same pixels a second time.
                        _bitmapOutputReservation = reservation;
                    }
                    else
                        reservation.Dispose();
                    throw;
                }
            }
        }

        internal void AbandonUnpublishedOwnership()
        {
            // Only factory failure before cache commit can call this. No native
            // Dispose: the failed factory has not consumed the caller's bitmap.
            lock (_bitmapOutputAdmissionGate)
            {
                if (_bitmapOutputReservationTransferred)
                    throw new InvalidOperationException("Transferred bitmap admission cannot be abandoned.");
                _bitmapOutputReservation = null;
                Volatile.Write(ref _referenceCount, 0);
            }
            BitmapWeightLedger.AbandonRegistration(PixelWeightBytes);
        }

        internal bool TryAcquire()
        {
            int observed = Volatile.Read(ref _referenceCount);
            while (observed > 0)
            {
                int updated = Interlocked.CompareExchange(ref _referenceCount, observed + 1, observed);
                if (updated == observed)
                    return true;
                observed = updated;
            }

            return false;
        }

        internal void ReleaseCacheReference()
        {
            try
            {
                ReleaseReference();
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _lastCacheDisposalFailureHResult, exception.HResult);
                Interlocked.Increment(ref _cacheDisposalFailureCount);
                try { MarkdownPerformanceEventSource.Log.BitmapCacheCleanupFailed(exception.HResult); }
                catch (Exception) { /* Diagnostic listeners must not interrupt LRU eviction. */ }
            }
        }

        internal void Release() => ReleaseReference();

        private void ReleaseReference()
        {
            BitmapOutputAdmission.Reservation? reservation;
            lock (_bitmapOutputAdmissionGate)
            {
                if (Interlocked.Decrement(ref _referenceCount) != 0)
                    return;
                reservation = _bitmapOutputReservationTransferred
                    ? _bitmapOutputReservation
                    : null;
            }

            try
            {
                Bitmap.Dispose();
            }
            catch
            {
                BitmapWeightLedger.RecordUncertainDispose(PixelWeightBytes);
                reservation?.MarkUncertain();
                throw;
            }

            BitmapWeightLedger.ReleaseSuccessfulDispose(PixelWeightBytes);
            reservation?.ReleaseAfterSuccessfulCleanup();
        }
    }

    internal sealed class Lease : IDisposable
    {
        private Entry? _entry;

        internal Lease() { }

        internal Lease(Entry entry) => _entry = entry;

        // Used only by factories before their one-way ownership transfer.
        internal void Attach(Entry entry) => _entry = entry;

        internal bool IsAttachedTo(Entry entry) =>
            ReferenceEquals(Volatile.Read(ref _entry), entry);

        internal void AbandonUnpublishedOwnership() =>
            Interlocked.Exchange(ref _entry, null)?.AbandonUnpublishedOwnership();

        internal CanvasBitmap Bitmap =>
            Volatile.Read(ref _entry)?.Bitmap ??
            throw new ObjectDisposedException(nameof(Lease));

        internal Size? IntrinsicSize => Volatile.Read(ref _entry)?.IntrinsicSize;

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
            Entry? entry = Volatile.Read(ref _entry);
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
            Entry? entry = Volatile.Read(ref _entry);
            return entry is { PixelWidth: > 0, PixelHeight: > 0 } &&
                (long)entry.PixelWidth * entry.PixelHeight <= maximumPixels;
        }

        internal bool MatchesSizingRequest(RasterImageSizingRequest sizingRequest)
        {
            Entry? entry = Volatile.Read(ref _entry);
            return entry is not null &&
                TryGetOrientedTarget(entry, sizingRequest, out var target) &&
                entry.PixelWidth == target.Width && entry.PixelHeight == target.Height;
        }

        internal Lease? TryClone()
        {
            Entry? entry = Volatile.Read(ref _entry);
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
