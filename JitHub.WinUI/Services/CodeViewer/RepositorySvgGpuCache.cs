using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using JitHub.Markdown.Svg;
using Microsoft.Graphics.Canvas;

namespace JitHub.Services.CodeViewer;

/// <summary>
/// Device-partitioned cache for standalone SVG preview tiles.
/// Reservations include in-flight uploads and transition leases in the same
/// architecture-specific budget.
/// </summary>
/// <remarks>
/// <c>JitHubMarkdownRuntime</c> owns the app's one instance, hands it to
/// every SVG preview, and disposes it when it retires the SVG renderer.
/// </remarks>
internal sealed partial class RepositorySvgGpuCache : IDisposable
{
    internal static readonly long MaximumBytes =
        RuntimeInformation.ProcessArchitecture == Architecture.X86
            ? 32L * 1024 * 1024
            : 64L * 1024 * 1024;

    private readonly StrictResourceLeaseCache<DeviceTileKey, CanvasBitmap> _cache =
        new(MaximumBytes);
    private readonly bool _memoryPressureSubscribed;
    private int _disposed;

    internal RepositorySvgGpuCache()
    {
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += OnAppMemoryUsageIncreased;
            _memoryPressureSubscribed = true;
        }
        catch (COMException)
        {
            // This WinRT event is unavailable in some unpackaged/test hosts.
            // The app runtime still disposes the cache explicitly on shutdown.
        }
    }

    internal bool TryAcquire(
        CanvasDevice device,
        string identity,
        out Lease? lease)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(identity);
        if (_cache.TryAcquire(new DeviceTileKey(device, identity), out var inner))
        {
            lease = new Lease(inner!);
            return true;
        }

        lease = null;
        return false;
    }

    internal bool TryReserve(long byteCount, out Reservation? reservation)
    {
        if (_cache.TryReserve(byteCount, out var inner))
        {
            reservation = new Reservation(inner!);
            return true;
        }

        reservation = null;
        return false;
    }

    internal Lease StoreAndAcquire(
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
        return new Lease(_cache.StoreAndAcquire(
            new DeviceTileKey(device, identity),
            bitmap,
            byteCount,
            reservation.Inner));
    }

    internal void ReleaseDevice(CanvasDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _cache.RemoveWhere(key => ReferenceEquals(key.Device, device));
    }

    internal void Trim() => _cache.Trim();

    /// <summary>Stops trimming on memory pressure and releases every cached tile.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_memoryPressureSubscribed)
        {
            try
            {
                Windows.System.MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased;
            }
            catch (COMException)
            {
            }
        }

        _cache.Dispose();
    }

    private void OnAppMemoryUsageIncreased(object? sender, object args)
    {
        try
        {
            _cache.Trim();
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
        private StrictResourceLease<DeviceTileKey, CanvasBitmap>? _inner;

        internal Lease(StrictResourceLease<DeviceTileKey, CanvasBitmap> inner) =>
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
        private StrictResourceReservation<DeviceTileKey, CanvasBitmap>? _inner;

        internal Reservation(
            StrictResourceReservation<DeviceTileKey, CanvasBitmap> inner) =>
            _inner = inner;

        internal StrictResourceReservation<DeviceTileKey, CanvasBitmap> Inner =>
            _inner ?? throw new ObjectDisposedException(nameof(Reservation));

        public void Dispose()
        {
            _inner?.Dispose();
            _inner = null;
        }
    }
}
