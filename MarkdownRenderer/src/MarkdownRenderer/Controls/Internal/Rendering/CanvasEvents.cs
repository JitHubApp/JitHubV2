using MarkdownRenderer.Controls.Internal.Input;
using MarkdownRenderer.Controls.Internal.Rendering.Tiles;
using MarkdownRenderer.Controls.Internal.View;

namespace MarkdownRenderer.Controls.Internal.Rendering;

/// <summary>
/// Creates the document canvas and connects it to painting and pointer
/// input. Unload disconnects the painter before any snapshot or DirectWrite
/// resource it may draw with is released.
/// </summary>
internal sealed class CanvasEvents
{
    private readonly MarkdownViewLifetime _lifetime;
    private readonly MarkdownViewSurface _surface;
    private readonly CanvasPointerEvents _pointerEvents;
    private readonly CanvasTilePainter _painter;
    private DocumentTileLayer? _paintedLayer;

    internal CanvasEvents(
        MarkdownViewLifetime lifetime,
        MarkdownViewSurface surface,
        CanvasPointerEvents pointerEvents,
        CanvasTilePainter painter)
    {
        _lifetime = lifetime;
        _surface = surface;
        _pointerEvents = pointerEvents;
        _painter = painter;
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

    /// <summary>
    /// Connects the painter to the surface's tiles. Safe to repeat: a load
    /// after construction refreshes the window the painter follows.
    /// </summary>
    internal void AttachRenderHandlers()
    {
        if (_surface.Tiles is not { } layer)
            return;

        if (!ReferenceEquals(layer, _paintedLayer))
        {
            DetachRenderHandlers();
            layer.PaintRequested += _painter.RequestFrame;
            _paintedLayer = layer;
        }

        _painter.Attach();
    }

    internal void DetachRenderHandlers()
    {
        if (_paintedLayer is null)
            return;

        _paintedLayer.PaintRequested -= _painter.RequestFrame;
        _paintedLayer = null;
        _painter.Detach();
    }
}
