using System;
using Microsoft.UI.Windowing;

namespace MarkdownRenderer.Controls.Internal.View;

/// <summary>
/// The handler a window's Destroying event holds. It reaches its
/// <see cref="WindowDestroyingWatch"/> through a weak reference only.
/// </summary>
internal sealed class WindowDestroyingSubscription
{
    private readonly WeakReference<WindowDestroyingWatch> _watch;

    internal WindowDestroyingSubscription(WindowDestroyingWatch watch)
        => _watch = new WeakReference<WindowDestroyingWatch>(watch);

    internal void OnDestroying(AppWindow sender, object args)
    {
        if (_watch.TryGetTarget(out WindowDestroyingWatch? watch))
            watch.OnDestroying(sender);
    }
}
