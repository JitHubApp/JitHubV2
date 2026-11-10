using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.Controls.Internal.Input;
using MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;
using MarkdownRenderer.Controls.Internal.View;

namespace MarkdownRenderer.Controls.Internal.Rendering;

/// <summary>
/// Creates the Win2D document canvas and subscribes its paint, resource and
/// pointer events. Win2D can keep queued native callbacks after XAML starts
/// removing the view, so unload detaches them before any snapshot or
/// DirectWrite resource they may reference is released.
/// </summary>
internal sealed class CanvasEvents
{
    private readonly MarkdownViewLifetime _lifetime;
    private readonly MarkdownViewSurface _surface;
    private readonly CanvasPointerEvents _pointerEvents;
    private readonly CanvasTilePainter _painter;
    private readonly RebuildScheduler _rebuild;
    private bool _renderHandlersAttached;

    internal CanvasEvents(
        MarkdownViewLifetime lifetime,
        MarkdownViewSurface surface,
        CanvasPointerEvents pointerEvents,
        CanvasTilePainter painter,
        RebuildScheduler rebuild)
    {
        _lifetime = lifetime;
        _surface = surface;
        _pointerEvents = pointerEvents;
        _painter = painter;
        _rebuild = rebuild;
    }

    /// <summary>Creates the canvas when the surface has none, and subscribes its events.</summary>
    internal void EnsureCanvas()
    {
        if (_lifetime.IsDisposed || _surface.TryCreateCanvas() is not { } canvas)
            return;
        _pointerEvents.Attach(canvas);
        AttachRenderHandlers();
    }

    internal void DetachInputHandlers()
    {
        if (_surface.Canvas is not null)
            _pointerEvents.Detach(_surface.Canvas);
    }

    internal void AttachRenderHandlers()
    {
        if (_surface.Canvas is null || _renderHandlersAttached)
            return;

        _surface.Canvas.RegionsInvalidated += _painter.OnRegionsInvalidated;
        _surface.Canvas.CreateResources += OnCreateResources;
        _renderHandlersAttached = true;
    }

    internal void DetachRenderHandlers()
    {
        if (_surface.Canvas is null || !_renderHandlersAttached)
            return;

        _surface.Canvas.RegionsInvalidated -= _painter.OnRegionsInvalidated;
        _surface.Canvas.CreateResources -= OnCreateResources;
        _renderHandlersAttached = false;
    }

    private void OnCreateResources(
        CanvasVirtualControl sender,
        CanvasCreateResourcesEventArgs args)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(sender, _surface.Canvas))
            return;

        // Initial resource creation can be deferred until the first input frame in
        // packaged WinUI. Rebuilding there resets pointer capture and selection
        // state, which makes the first click/drag flash and get swallowed. Layout
        // uses CanvasDevice.GetSharedDevice(), so initial creation needs no rebuild.
        // Later resource creation means device/DPI changed, so rebuild layouts.
        if (args.Reason != CanvasCreateResourcesReason.FirstTime)
            _rebuild.Request(RebuildReason.Full);
    }
}
