using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Releases one bitmap cache's partition for a device when that device
/// reports that it was lost.
/// </summary>
/// <remarks>
/// <para>
/// Device loss must reach every cache holding bitmaps on the lost device,
/// not only the cache of the view whose paint failed. Each cache therefore
/// registers with the devices it caches for: the device's own
/// <see cref="CanvasDevice.DeviceLost"/> event is the registry, raised by
/// Win2D or by a view's recovery once it confirms the loss.
/// </para>
/// <para>
/// A device roots only the small per-device handler, which holds the cache
/// weakly; a collected cache's handler unsubscribes when it next fires.
/// </para>
/// </remarks>
internal sealed class SharedCanvasBitmapCacheDeviceWatch
{
    private readonly object _gate = new();
    private readonly WeakReference<SharedCanvasBitmapCache> _cache;
    private readonly Dictionary<CanvasDevice, DeviceLostHandler> _handlers =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Creates the watch of <paramref name="cache"/>.</summary>
    /// <param name="cache">The cache to release on device loss.</param>
    internal SharedCanvasBitmapCacheDeviceWatch(SharedCanvasBitmapCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = new WeakReference<SharedCanvasBitmapCache>(cache);
    }

    /// <summary>Starts watching <paramref name="device"/> if not already watched.</summary>
    /// <param name="device">A device the cache now holds bitmaps on.</param>
    internal void Watch(CanvasDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        DeviceLostHandler handler;
        lock (_gate)
        {
            if (_handlers.ContainsKey(device))
                return;
            handler = new DeviceLostHandler(this, device);
            _handlers.Add(device, handler);
        }

        try
        {
            device.DeviceLost += handler.OnDeviceLost;
        }
        catch (Exception)
        {
            // A device that cannot accept the registration is already
            // failing. The view that owns this cache still releases it on
            // its own device-loss recovery.
            lock (_gate)
            {
                if (_handlers.TryGetValue(device, out DeviceLostHandler? current) &&
                    ReferenceEquals(current, handler))
                {
                    _handlers.Remove(device);
                }
            }
        }
    }

    /// <summary>Stops watching <paramref name="device"/>.</summary>
    /// <param name="device">A device whose partition was released.</param>
    internal void Forget(CanvasDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        DeviceLostHandler? handler;
        lock (_gate)
        {
            if (!_handlers.Remove(device, out handler))
                return;
        }

        handler.Unsubscribe();
    }

    private void OnDeviceLost(CanvasDevice device, DeviceLostHandler handler)
    {
        if (_cache.TryGetTarget(out SharedCanvasBitmapCache? cache))
        {
            // ReleaseDevice forgets the device, which unsubscribes this handler.
            cache.ReleaseDevice(device);
            return;
        }

        handler.Unsubscribe();
    }

    private sealed class DeviceLostHandler(SharedCanvasBitmapCacheDeviceWatch owner, CanvasDevice device)
    {
        private readonly WeakReference<SharedCanvasBitmapCacheDeviceWatch> _owner = new(owner);

        internal void OnDeviceLost(CanvasDevice sender, object args)
        {
            if (_owner.TryGetTarget(out SharedCanvasBitmapCacheDeviceWatch? watch))
                watch.OnDeviceLost(device, this);
            else
                Unsubscribe();
        }

        internal void Unsubscribe()
        {
            try
            {
                device.DeviceLost -= OnDeviceLost;
            }
            catch (Exception)
            {
                // A lost device may refuse event changes. The handler holds
                // its cache weakly, so a failed removal retains nothing.
            }
        }
    }
}
