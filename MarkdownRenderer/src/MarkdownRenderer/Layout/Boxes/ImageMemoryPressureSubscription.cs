using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Trims one image scope when the system reports rising app memory use.
/// </summary>
/// <remarks>
/// The system event roots only this small subscription. It holds its scope
/// weakly, so an engine that is never disposed still lets its caches be
/// collected; the next notification after that removes the subscription.
/// </remarks>
internal sealed class ImageMemoryPressureSubscription : IDisposable
{
    private readonly WeakReference<ImageResourceScope> _scope;
    private int _disposed;

    private ImageMemoryPressureSubscription(ImageResourceScope scope) => _scope = new(scope);

    /// <summary>
    /// Subscribes <paramref name="scope"/> to memory-pressure trimming, or
    /// returns null where the system memory manager is unavailable.
    /// </summary>
    /// <param name="scope">The scope to trim.</param>
    internal static ImageMemoryPressureSubscription? TrySubscribe(ImageResourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var subscription = new ImageMemoryPressureSubscription(scope);
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased += subscription.OnAppMemoryUsageIncreased;
            return subscription;
        }
        catch (Exception exception) when (
            exception is COMException or PlatformNotSupportedException or TypeInitializationException)
        {
            // MemoryManager is unavailable in some unpackaged and test hosts.
            // The hard LRU budgets and explicit device-loss cleanup still apply.
            return null;
        }
    }

    /// <summary>Stops trimming.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            Windows.System.MemoryManager.AppMemoryUsageIncreased -= OnAppMemoryUsageIncreased;
        }
        catch (Exception exception) when (
            exception is COMException or PlatformNotSupportedException or TypeInitializationException)
        {
            // The subscription succeeded, so the manager existed; a host that
            // is shutting down may refuse the removal. Nothing is retained.
        }
    }

    private void OnAppMemoryUsageIncreased(object? sender, object args)
    {
        if (_scope.TryGetTarget(out ImageResourceScope? scope))
            scope.TrimForMemoryPressure();
        else
            Dispose();
    }
}
