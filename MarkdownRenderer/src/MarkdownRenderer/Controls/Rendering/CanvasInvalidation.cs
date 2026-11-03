using System;
using MarkdownRenderer.Diagnostics;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Controls.Rendering;

/// <summary>
/// Requests a repaint of the document canvas while the view is loaded. A
/// lost device during the request starts device recovery; a canvas that is
/// already shutting down is ignored.
/// </summary>
internal sealed class CanvasInvalidation
{
    private readonly FrameworkElement _renderer;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly MarkdownViewSurface _surface;
    private readonly CanvasDeviceRecovery _deviceRecovery;

    internal CanvasInvalidation(
        FrameworkElement renderer,
        MarkdownViewLifetime lifetime,
        MarkdownViewSurface surface,
        CanvasDeviceRecovery deviceRecovery)
    {
        _renderer = renderer;
        _lifetime = lifetime;
        _surface = surface;
        _deviceRecovery = deviceRecovery;
    }

    internal void Invalidate()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
            return;

        try
        {
            _surface.Canvas?.Invalidate();
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            _deviceRecovery.HandleDeviceLost(ex);
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsShutdownOrDisposed(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] ignored canvas invalidate during shutdown: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
        }
    }
}
