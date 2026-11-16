using System;
using JitHub.Core.Services.Telemetry;

namespace JitHub.Core.Services.Images.Caching;

/// <summary>
/// Owns one <see cref="GitHubImageService"/>'s subscription to app memory-pressure
/// notifications: Windows' <c>MemoryManager.AppMemoryUsageIncreased</c> by default,
/// or an injected subscribe/unsubscribe pair.
/// </summary>
internal sealed class GitHubImageMemoryPressureSubscription
{
    private readonly EventHandler<object> _handler;
    private readonly Action<EventHandler<object>>? _subscribe;
    private readonly Action<EventHandler<object>>? _unsubscribe;

    /// <summary>Creates an unsubscribed owner for <paramref name="handler"/>.</summary>
    /// <param name="handler">The handler to register.</param>
    /// <param name="subscribe">An injected subscribe callback, or <see langword="null"/> for Windows' event.</param>
    /// <param name="unsubscribe">The matching unsubscribe callback; supplied exactly when <paramref name="subscribe"/> is.</param>
    public GitHubImageMemoryPressureSubscription(
        EventHandler<object> handler,
        Action<EventHandler<object>>? subscribe,
        Action<EventHandler<object>>? unsubscribe)
    {
        if ((subscribe is null) != (unsubscribe is null))
            throw new ArgumentException("Memory-pressure subscription callbacks must be supplied as a pair.");

        _handler = handler;
        _subscribe = subscribe;
        _unsubscribe = unsubscribe;
    }

    /// <summary>Whether the handler is currently registered.</summary>
    public bool IsSubscribed { get; private set; }

    /// <summary>
    /// Registers the handler. When notifications are unavailable on this system the
    /// owner stays unsubscribed and the cache keeps its ordinary bounded LRU behavior.
    /// </summary>
    public void Subscribe()
    {
        try
        {
            if (_subscribe is not null)
                _subscribe(_handler);
            else
                Windows.System.MemoryManager.AppMemoryUsageIncreased += _handler;
            IsSubscribed = true;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            // An event add may partially register before the projection
            // reports that notifications are unavailable. Remove any such
            // registration, then retain the ordinary bounded LRU behavior.
            Exception? rollbackFailure = TryUnsubscribe();
            if (rollbackFailure is not null)
                HandledFailureReporter.Report(rollbackFailure, "github-image-memory-pressure-rollback");
        }
    }

    /// <summary>Removes the handler; afterwards the owner counts as unsubscribed even if removal failed.</summary>
    /// <returns>The removal failure, or <see langword="null"/>.</returns>
    public Exception? TryUnsubscribe()
    {
        try
        {
            if (_unsubscribe is not null)
                _unsubscribe(_handler);
            else
                Windows.System.MemoryManager.AppMemoryUsageIncreased -= _handler;
            IsSubscribed = false;
            return null;
        }
        catch (Exception exception)
        {
            IsSubscribed = false;
            return exception;
        }
    }

    /// <summary>Whether <paramref name="exception"/> means notifications are unavailable rather than a real failure.</summary>
    public static bool IsUnavailable(Exception exception) =>
        exception is System.Runtime.InteropServices.COMException or
            PlatformNotSupportedException or TypeInitializationException;
}
