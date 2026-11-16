using System;
using MarkdownRenderer.Controls;
using Microsoft.UI.Xaml;

namespace JitHub.WinUI.Services.Markdown.Hosting;

/// <summary>
/// Holds back a renderer's first Markdown assignment until the renderer is
/// loaded and has completed a measured layout, so the first pipeline request
/// runs at the real reading width.
/// </summary>
internal sealed class MarkdownRendererInitialLayoutGate
{
    private readonly IMarkdownRendererLayoutGateHost _host;
    private bool _loadedHandlerAttached;
    private bool _layoutHandlerAttached;
    private bool _initialLayoutReady;
    private EventHandler<object>? _layoutHandler;
    private long _layoutTicket;
    private MarkdownRendererControl? _layoutTarget;

    /// <param name="host">The host whose renderer this gate watches.</param>
    public MarkdownRendererInitialLayoutGate(IMarkdownRendererLayoutGateHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Gets whether the current renderer has completed its first measured layout.</summary>
    public bool IsReady => _initialLayoutReady;

    /// <summary>Starts watching a renderer that is about to be attached, or reattached.</summary>
    /// <param name="renderer">The renderer.</param>
    public void Prepare(MarkdownRendererControl renderer)
    {
        if (renderer.IsLoaded)
        {
            EnsureLayoutHandler(renderer);
            return;
        }

        _initialLayoutReady = false;
        if (_loadedHandlerAttached)
            return;

        // Settings can be applied as soon as the child is added, before its
        // own Loaded event has made the renderer able to accept a pipeline.
        // Install one handler before attachment so the first Loaded turn can
        // register for the completed initial layout before assignment.
        renderer.Loaded += OnRendererLoaded;
        _loadedHandlerAttached = true;
    }

    /// <summary>Stops watching a renderer that is being retired.</summary>
    /// <param name="renderer">The renderer.</param>
    public void Release(MarkdownRendererControl renderer)
    {
        renderer.Loaded -= OnRendererLoaded;
        ClearLayoutHandler();
        _loadedHandlerAttached = false;
        _initialLayoutReady = false;
    }

    private void OnRendererLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not MarkdownRendererControl renderer || !ReferenceEquals(renderer, _host.CurrentRenderer))
            return;

        renderer.Loaded -= OnRendererLoaded;
        _loadedHandlerAttached = false;
        if (!_host.IsActive)
            return;

        _initialLayoutReady = false;
        EnsureLayoutHandler(renderer);
    }

    private void EnsureLayoutHandler(MarkdownRendererControl renderer)
    {
        if (_initialLayoutReady || _layoutHandlerAttached)
            return;

        long ticket = ++_layoutTicket;
        EventHandler<object> handler = (_, _) => OnRendererLayoutUpdated(renderer, ticket);
        _layoutHandler = handler;
        _layoutTarget = renderer;
        renderer.LayoutUpdated += handler;
        _layoutHandlerAttached = true;
    }

    private void OnRendererLayoutUpdated(MarkdownRendererControl renderer, long ticket)
    {
        // WinUI raises LayoutUpdated with a null sender after all SizeChanged
        // events in a layout sequence, so use the renderer currently owned by
        // the host and validate its measured width directly. Capturing both
        // renderer and ticket prevents an already-queued old callback from
        // admitting a replacement renderer.
        if (ticket != _layoutTicket ||
            !ReferenceEquals(renderer, _layoutTarget) ||
            !ReferenceEquals(renderer, _host.CurrentRenderer) ||
            !_layoutHandlerAttached)
            return;

        if (!_host.IsActive)
        {
            ClearLayoutHandler();
            return;
        }

        if (!renderer.IsLoaded ||
            !double.IsFinite(renderer.ActualWidth) || renderer.ActualWidth <= 0 ||
            !double.IsFinite(renderer.ActualHeight) || renderer.ActualHeight <= 0)
            return;

        ClearLayoutHandler();
        _initialLayoutReady = true;
        _host.OnInitialLayoutReady();
    }

    private void ClearLayoutHandler()
    {
        MarkdownRendererControl? renderer = _layoutTarget;
        EventHandler<object>? handler = _layoutHandler;
        _layoutTarget = null;
        _layoutHandler = null;
        _layoutHandlerAttached = false;
        ++_layoutTicket;

        if (renderer is not null && handler is not null)
            renderer.LayoutUpdated -= handler;
    }
}
