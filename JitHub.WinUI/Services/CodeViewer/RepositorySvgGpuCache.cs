using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;

namespace JitHub.Services.CodeViewer;

/// <summary>
/// Process-wide, device-partitioned cache for standalone SVG preview tiles.
/// Reservations include in-flight uploads and transition leases in the same
/// architecture-specific budget.
/// </summary>
internal static partial class RepositorySvgGpuCache
{
    internal static readonly long MaximumBytes =
        RuntimeInformation.ProcessArchitecture == Architecture.X86
            ? 32L * 1024 * 1024
            : 64L * 1024 * 1024;

    private static readonly StrictResourceLeaseCache<DeviceTileKey, CanvasBitmap> Cache =
        new(MaximumBytes);
    private static readonly bool MemoryPressureSubscribed;

    static RepositorySvgGpuCache()
    {
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
            MemoryPressureSubscribed = true;
        }
        catch (COMException)
        {
            // This WinRT event is unavailable in some unpackaged/test hosts.
            // The app runtime still trims the cache explicitly on shutdown.
        }
    }

    internal static bool TryAcquire(
        CanvasDevice device,
        string identity,
        out Lease? lease)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(identity);
        if (Cache.TryAcquire(new DeviceTileKey(device, identity), out var inner))
        {
            lease = new Lease(inner!);
            return true;
        }

        lease = null;
        return false;
    }

    internal static bool TryReserve(long byteCount, out Reservation? reservation)
    {
        if (Cache.TryReserve(byteCount, out var inner))
        {
            reservation = new Reservation(inner!);
            return true;
        }

        reservation = null;
        return false;
    }

    internal static Lease StoreAndAcquire(
        CanvasDevice device,
        string identity,
        CanvasBitmap bitmap,
        long byteCount,
        Reservation reservation)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(identity);
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(reservation);
        return new Lease(Cache.StoreAndAcquire(
            new DeviceTileKey(device, identity),
            bitmap,
            byteCount,
            reservation.Inner));
    }

    internal static void ReleaseDevice(CanvasDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Cache.RemoveWhere(key => ReferenceEquals(key.Device, device));
    }

    internal static void Trim() => Cache.Trim();

    internal static void Shutdown()
    {
        if (MemoryPressureSubscribed)
        {
            try
            {
                Windows.System.MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased;
            }
            catch (COMException)
            {
            }
        }

        Cache.Dispose();
    }

    private static void OnAppMemoryUsageIncreased(object? sender, object args)
    {
        try
        {
            Cache.Trim();
        }
        catch
        {
            // Memory-pressure notifications are advisory and must not escape
            // into the WinRT event source.
        }
    }

    internal readonly struct DeviceTileKey : IEquatable<DeviceTileKey>
    {
        internal DeviceTileKey(CanvasDevice device, string identity)
        {
            Device = device;
            Identity = identity;
        }

        internal CanvasDevice Device { get; }

        private string Identity { get; }

        public bool Equals(DeviceTileKey other) =>
            ReferenceEquals(Device, other.Device) &&
            StringComparer.Ordinal.Equals(Identity, other.Identity);

        public override bool Equals(object? obj) =>
            obj is DeviceTileKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(Device), StringComparer.Ordinal.GetHashCode(Identity));
    }

    internal sealed partial class Lease : IDisposable
    {
        private StrictResourceLeaseCache<DeviceTileKey, CanvasBitmap>.Lease? _inner;

        internal Lease(StrictResourceLeaseCache<DeviceTileKey, CanvasBitmap>.Lease inner) =>
            _inner = inner;

        internal CanvasBitmap Bitmap =>
            _inner?.Resource ?? throw new ObjectDisposedException(nameof(Lease));

        public void Dispose()
        {
            _inner?.Dispose();
            _inner = null;
        }
    }

    internal sealed partial class Reservation : IDisposable
    {
        private StrictResourceLeaseCache<DeviceTileKey, CanvasBitmap>.Reservation? _inner;

        internal Reservation(
            StrictResourceLeaseCache<DeviceTileKey, CanvasBitmap>.Reservation inner) =>
            _inner = inner;

        internal StrictResourceLeaseCache<DeviceTileKey, CanvasBitmap>.Reservation Inner =>
            _inner ?? throw new ObjectDisposedException(nameof(Reservation));

        public void Dispose()
        {
            _inner?.Dispose();
            _inner = null;
        }
    }
}
