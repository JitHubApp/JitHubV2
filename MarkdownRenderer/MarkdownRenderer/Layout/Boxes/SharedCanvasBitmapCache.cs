using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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

    private static readonly WeightedLruCache<CacheKey, Entry> Cache = new(
        DefaultBudgetBytes,
        static entry => entry.WeightBytes,
        static entry => entry.ReleaseCacheReference());

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
        if (Cache.TryGetValue(key, out Entry? entry) && entry.TryAcquire())
        {
            lease = new Lease(entry);
            return true;
        }

        // An entry can reach zero between the LRU lookup and lease acquisition
        // if device-loss cleanup wins the race. The caller will decode and Set
        // a replacement; do not remove by key here because another thread may
        // already have installed a fresh entry for the same identity.
        lease = null;
        return false;
    }

    internal static bool TryAcquireBestRasterPreview(
        CanvasDevice device,
        string sourceIdentity,
        int targetWidth,
        int targetHeight,
        out Lease? lease)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        lease = null;
        if (sourceIdentity.Length == 0 || targetWidth <= 0 || targetHeight <= 0)
            return false;

        string variantPrefix = string.Concat(sourceIdentity, "\u001Fdisplay:");
        if (!Cache.TryGetBestMatch(
                (key, entry) => ReferenceEquals(key.Device, device) &&
                    IsExactDisplayVariantKey(key.Identity, variantPrefix) &&
                    entry.PixelWidth > 0 &&
                    entry.PixelHeight > 0 &&
                    entry.PixelWidth <= targetWidth &&
                    entry.PixelHeight <= targetHeight &&
                    (entry.PixelWidth < targetWidth || entry.PixelHeight < targetHeight) &&
                    entry.IntrinsicSize is not null,
                static entry => (long)entry.PixelWidth * entry.PixelHeight,
                out Entry? preview) ||
            preview is null || !preview.TryAcquire())
        {
            return false;
        }

        lease = new Lease(preview);
        return true;
    }

    private static bool IsExactDisplayVariantKey(string identity, string prefix)
    {
        if (!identity.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        ReadOnlySpan<char> dimensions = identity.AsSpan(prefix.Length);
        int separator = dimensions.IndexOf('x');
        if (separator is <= 0 || separator == dimensions.Length - 1)
            return false;
        foreach (char digit in dimensions[..separator])
        {
            if (!char.IsAsciiDigit(digit))
                return false;
        }
        foreach (char digit in dimensions[(separator + 1)..])
        {
            if (!char.IsAsciiDigit(digit))
                return false;
        }
        return true;
    }

    internal static Lease StoreAndAcquire(
        CanvasDevice device,
        string identity,
        CanvasBitmap bitmap,
        Size? intrinsicSize = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(bitmap);

        // The entry starts with one reference owned by the returned lease.
        // Add a distinct cache reference before insertion; an over-budget
        // insertion immediately releases only that cache reference.
        var entry = new Entry(bitmap, EstimateWeightBytes(bitmap), intrinsicSize);
        entry.AddCacheReference();
        Cache.Set(new CacheKey(device, identity), entry);
        return new Lease(entry);
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

    internal static int CountForTests => Cache.Count;

    private static void OnAppMemoryUsageIncreased(object? sender, object args)
    {
        Cache.Clear();
        ImageBox.TrimSharedCachesForMemoryPressure();
    }

    private static long EstimateWeightBytes(CanvasBitmap bitmap)
    {
        try
        {
            var size = bitmap.SizeInPixels;
            double bytes = Math.Max(1d, size.Width) * Math.Max(1d, size.Height) * 4d;
            return bytes >= long.MaxValue ? long.MaxValue : Math.Max(1L, (long)Math.Ceiling(bytes));
        }
        catch
        {
            // A queried resource may already be invalid after device loss. It
            // still needs a non-zero weight so it cannot evade the budget.
            return 1L * 1024 * 1024;
        }
    }

    private static SemaphoreSlim[] CreateDecodeStripes()
    {
        var stripes = new SemaphoreSlim[DecodeStripeCount];
        for (int i = 0; i < stripes.Length; i++)
            stripes[i] = new SemaphoreSlim(1, 1);
        return stripes;
    }

    private sealed class CacheKey : IEquatable<CacheKey>
    {
        internal CacheKey(CanvasDevice device, string identity)
        {
            Device = device;
            Identity = identity;
        }

        internal CanvasDevice Device { get; }

        internal string Identity { get; }

        public bool Equals(CacheKey? other) =>
            other is not null &&
            ReferenceEquals(Device, other.Device) &&
            StringComparer.Ordinal.Equals(Identity, other.Identity);

        public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            RuntimeHelpers.GetHashCode(Device),
            StringComparer.Ordinal.GetHashCode(Identity));
    }

    internal sealed class Entry
    {
        private int _referenceCount = 1;

        internal Entry(CanvasBitmap bitmap, long weightBytes, Size? intrinsicSize)
        {
            Bitmap = bitmap;
            WeightBytes = weightBytes;
            IntrinsicSize = intrinsicSize;
            try
            {
                var pixels = bitmap.SizeInPixels;
                PixelWidth = (int)pixels.Width;
                PixelHeight = (int)pixels.Height;
            }
            catch
            {
                // A device-lost entry may still need normal lease retirement,
                // but it must never be chosen as a preview candidate.
            }
        }

        internal CanvasBitmap Bitmap { get; }

        internal long WeightBytes { get; }

        internal Size? IntrinsicSize { get; }

        internal int PixelWidth { get; }

        internal int PixelHeight { get; }

        internal void AddCacheReference() => Interlocked.Increment(ref _referenceCount);

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

        internal void ReleaseCacheReference() => Release();

        internal void Release()
        {
            if (Interlocked.Decrement(ref _referenceCount) != 0)
                return;

            try
            {
                Bitmap.Dispose();
            }
            catch
            {
                // Cleanup must stay safe during device removal and shutdown.
            }
        }
    }

    internal sealed class Lease : IDisposable
    {
        private Entry? _entry;

        internal Lease(Entry entry) => _entry = entry;

        internal CanvasBitmap Bitmap =>
            Volatile.Read(ref _entry)?.Bitmap ??
            throw new ObjectDisposedException(nameof(Lease));

        internal Size? IntrinsicSize => Volatile.Read(ref _entry)?.IntrinsicSize;

        public void Dispose()
        {
            Interlocked.Exchange(ref _entry, null)?.Release();
        }
    }
}
