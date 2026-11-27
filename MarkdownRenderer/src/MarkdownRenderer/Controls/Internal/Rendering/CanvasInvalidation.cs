using Microsoft.UI.Xaml;
using MarkdownRenderer.Controls.Internal.View;

namespace MarkdownRenderer.Controls.Internal.Rendering;

/// <summary>
/// Requests a repaint of the document canvas while the view is loaded by
/// marking its tiles stale. Stale tiles keep their pixels until the painter
/// repaints them on the next composition frame.
/// </summary>
internal sealed class CanvasInvalidation
{
    private readonly FrameworkElement _renderer;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly MarkdownViewSurface _surface;

    internal CanvasInvalidation(
        FrameworkElement renderer,
        MarkdownViewLifetime lifetime,
        MarkdownViewSurface surface)
    {
        _renderer = renderer;
        _lifetime = lifetime;
        _surface = surface;
    }

    internal void Invalidate()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
            return;

        _surface.Tiles?.MarkAllStale();
    }

    /// <summary>
    /// Repaints the canvas from document Y <paramref name="top"/> to its
    /// bottom. Positive infinity repaints nothing; a non-finite or
    /// non-positive top repaints everything.
    /// </summary>
    internal void InvalidateFrom(double top)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
            return;

        _surface.Tiles?.MarkStaleFrom(top);
    }
}
