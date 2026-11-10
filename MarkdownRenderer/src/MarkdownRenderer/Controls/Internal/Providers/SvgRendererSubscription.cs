using System;
using System.Threading;
using MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;
using MarkdownRenderer.Controls.Internal.Rendering;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Images;
using Microsoft.UI.Xaml;
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Internal.Providers;

/// <summary>
/// The control's subscription to its SVG renderer's cache invalidation:
/// provider rasters are rebuilt and repainted when the shared provider
/// drops its cache, for the renderer currently assigned and loaded.
/// </summary>
/// <remarks>
/// The provider may raise CacheInvalidated from a worker or system-event
/// callback. The subscribed instance is kept outside DependencyObject
/// storage so the callback never reads a thread-affine dependency property.
/// </remarks>
internal sealed class SvgRendererSubscription
{
    private readonly MarkdownRendererControl _control;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly RebuildScheduler _rebuild;
    private readonly CanvasInvalidation _canvasInvalidation;
    private IMarkdownSvgRenderer? _subscribed;

    internal SvgRendererSubscription(
        MarkdownRendererControl control,
        MarkdownViewLifetime lifetime,
        RebuildScheduler rebuild,
        CanvasInvalidation canvasInvalidation)
    {
        _control = control;
        _lifetime = lifetime;
        _rebuild = rebuild;
        _canvasInvalidation = canvasInvalidation;
    }

    /// <summary>The SvgRenderer property changed.</summary>
    internal void OnChanged(DependencyPropertyChangedEventArgs args)
    {
        IMarkdownSvgRenderer? previous = Interlocked.Exchange(ref _subscribed, null);
        if (previous is not null)
            previous.CacheInvalidated -= OnCacheInvalidated;
        if (!_lifetime.IsUnloaded && args.NewValue is IMarkdownSvgRenderer current)
        {
            current.CacheInvalidated -= OnCacheInvalidated;
            Volatile.Write(ref _subscribed, current);
            current.CacheInvalidated += OnCacheInvalidated;
        }
        _rebuild.Request(RebuildReason.Full);
    }

    /// <summary>Subscribes to the assigned renderer when the view loads.</summary>
    internal void Attach()
    {
        if (_control.SvgRenderer is { } svgRenderer)
        {
            svgRenderer.CacheInvalidated -= OnCacheInvalidated;
            Volatile.Write(ref _subscribed, svgRenderer);
            svgRenderer.CacheInvalidated += OnCacheInvalidated;
        }
    }

    internal void Detach()
    {
        if (Interlocked.Exchange(ref _subscribed, null) is { } svgRenderer)
            svgRenderer.CacheInvalidated -= OnCacheInvalidated;
    }

    private void OnCacheInvalidated(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(sender, Volatile.Read(ref _subscribed)) ||
            _lifetime.IsDisposed ||
            _lifetime.IsUnloaded)
            return;

        void InvalidateProviderRasters()
        {
            if (!ReferenceEquals(sender, Volatile.Read(ref _subscribed)) ||
                _lifetime.IsDisposed ||
                _lifetime.IsUnloaded)
                return;
            _rebuild.Request(RebuildReason.Full);
            _canvasInvalidation.Invalidate();
        }

        if (_control.DispatcherQueue.HasThreadAccess)
            InvalidateProviderRasters();
        else
            _ = _control.DispatcherQueue.TryEnqueue(InvalidateProviderRasters);
    }
}
