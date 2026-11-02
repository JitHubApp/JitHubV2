using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Disposes the renderer when its top-level window is destroyed.
/// </summary>
/// <remarks>
/// WinUI does not guarantee a child UserControl receives Unloaded when its
/// top-level window is destroyed. Subscribing to the non-cancelable window
/// destruction event releases native Canvas resources while the WinUI
/// dispatcher and COM apartment are still valid. The window holds only a
/// weak reference back to this watch, so it cannot keep the renderer alive.
/// </remarks>
internal sealed class WindowDestroyingWatch
{
    private readonly IDisposable _owner;
    private readonly MarkdownViewLifetime _lifetime;
    private AppWindow? _appWindow;
    private WindowDestroyingSubscription? _subscription;

    internal WindowDestroyingWatch(IDisposable owner, MarkdownViewLifetime lifetime)
    {
        _owner = owner;
        _lifetime = lifetime;
    }

    /// <summary>Watches the window that hosts <paramref name="root"/>.</summary>
    internal void Attach(XamlRoot? root)
    {
        if (root?.ContentIslandEnvironment is not { } island)
            return;

        AppWindow appWindow;
        try
        {
            appWindow = AppWindow.GetFromWindowId(island.AppWindowId);
        }
        catch
        {
            // A host can transition between content islands while it is being
            // created or torn down. Its regular Unloaded path remains valid.
            return;
        }

        if (ReferenceEquals(_appWindow, appWindow))
            return;

        Detach();
        var subscription = new WindowDestroyingSubscription(this);
        _appWindow = appWindow;
        _subscription = subscription;
        appWindow.Destroying += subscription.OnDestroying;
    }

    /// <summary>Stops watching the current window.</summary>
    internal void Detach()
    {
        if (_appWindow is not { } appWindow)
            return;

        _appWindow = null;
        var subscription = _subscription;
        _subscription = null;
        if (subscription is null)
            return;

        try { appWindow.Destroying -= subscription.OnDestroying; }
        catch { /* The host may already be tearing down its WinRT event source. */ }
    }

    internal void OnDestroying(AppWindow sender)
    {
        if (ReferenceEquals(sender, _appWindow) && !_lifetime.IsDisposed)
            _owner.Dispose();
    }
}
