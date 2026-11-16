using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Diagnostics.Internal;

namespace MarkdownRenderer.Controls.Internal.Selection.Adorner;

/// <summary>
/// Owns where the selection adorner is presented: the inert overlay
/// placeholder, its composition child visual, the long-lived adorner surface
/// and the composition graphics device that draws it.
/// </summary>
/// <remarks>
/// A XAML Image showing Win2D's CanvasImageSource is unsafe: XAML's
/// reference to the aggregated inner SurfaceImageSource does not keep
/// Win2D's outer object alive, so the .NET finalizer could destroy it while
/// XAML still released the Image. Composition surfaces have no XAML peer and
/// are disposed deterministically. One long-lived, grow-only surface is kept
/// per placeholder; relayout, source replacement and resize reuse it.
/// </remarks>
internal sealed class SelectionAdornerCompositionTarget
{
    private readonly FrameworkElement _renderer;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly Action _repaintAfterDeviceReplaced;

    private Border? _placeholder;
    private SelectionAdornerVisualHost? _visualHost;
    private readonly SelectionAdornerSurfaceLifetime<SelectionAdornerCompositionSurface, CompositionGraphicsDevice> _surfaces =
        new(CreateSurface, ReportSurfaceReleaseFailure, DisposeSurface);

    // The composition graphics device that draws adorner surfaces, and the
    // Win2D device it currently renders with.
    private CompositionGraphicsDevice? _graphicsDevice;
    private CanvasDevice? _canvasDevice;
    private double _offsetY;

    /// <param name="renderer">The renderer element, for its XAML root and dispatcher.</param>
    /// <param name="surface">The renderer's overlay canvas.</param>
    /// <param name="lifetime">The renderer's unload and disposal state.</param>
    /// <param name="repaintAfterDeviceReplaced">
    /// Repaints the whole adorner, on the UI thread, after the rendering
    /// device was replaced underneath the current graphics device.
    /// </param>
    internal SelectionAdornerCompositionTarget(
        FrameworkElement renderer,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        Action repaintAfterDeviceReplaced)
    {
        _renderer = renderer;
        _surface = surface;
        _lifetime = lifetime;
        _repaintAfterDeviceReplaced = repaintAfterDeviceReplaced;
    }

    /// <summary>True once the placeholder element exists.</summary>
    internal bool IsCreated => _placeholder is not null;

    /// <summary>True while the adorner is shown.</summary>
    internal bool IsVisible => _placeholder?.Visibility == Visibility.Visible;

    /// <summary>The Win2D device the adorner surface currently renders with.</summary>
    internal CanvasDevice? SurfaceDevice => _canvasDevice;

    /// <summary>The document Y the placeholder was last positioned at.</summary>
    internal double OffsetY => _offsetY;

    /// <summary>Changes whenever the adorner surface is replaced or released.</summary>
    internal long SurfaceGeneration => _surfaces.Generation;

    /// <summary>The adorner surface currently attached, if any.</summary>
    internal SelectionAdornerCompositionSurface? Surface => _surfaces.Surface;

    /// <summary>Hides the adorner until its next paint.</summary>
    internal void Collapse()
    {
        if (_placeholder is not null)
            _placeholder.Visibility = Visibility.Collapsed;
    }

    /// <summary>Removes every overlay child except the adorner placeholder.</summary>
    internal void RemoveOtherOverlayChildren()
    {
        if (_surface.Overlay is null)
            return;

        var children = _surface.Overlay.Children;
        for (int index = children.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(children[index], _placeholder))
                children.RemoveAt(index);
        }
    }

    /// <summary>Detaches and retires the adorner surface.</summary>
    internal void ReleaseSurface() => _surfaces.Release(_visualHost);

    /// <summary>
    /// Disposes the detached surfaces, the child visual and the graphics
    /// device, and forgets the placeholder.
    /// </summary>
    internal void DisposeForUnload()
    {
        _surfaces.DropRetired();
        _visualHost?.Dispose();
        ReleaseGraphicsDevice();
        if (_placeholder is not null)
        {
            _placeholder = null;
            _visualHost = null;
        }
    }

    /// <summary>Disposes the detached surfaces and the graphics device.</summary>
    internal void ReleaseForDispose()
    {
        _surfaces.DropRetired();
        ReleaseGraphicsDevice();
    }

    /// <summary>Ages retired surfaces by one painted frame.</summary>
    internal void AdvanceFrame() => _surfaces.AdvanceFrame();

    /// <summary>
    /// Sizes, clips and positions the placeholder for the viewport and
    /// returns the surface to draw into.
    /// </summary>
    internal bool ApplyViewportUnderPaint(
        double width,
        double height,
        double top,
        [NotNullWhen(true)] out SelectionAdornerCompositionSurface? source)
    {
        source = null;
        if (_placeholder is null)
            return false;

        source = EnsureSurface(width, height);
        if (source is null || _placeholder is null)
        {
            source = null;
            Collapse();
            return false;
        }

        // The placeholder and its visual span the whole bucketed surface at 1:1
        // so a reused, larger surface is never stretched; the clips bound it to
        // the viewport.
        SelectionAdornerSurfaceAllocation allocation = _surfaces.Allocation;
        _visualHost?.SetViewport(allocation, width, height);
        if (double.IsNaN(_placeholder.Width) || Math.Abs(_placeholder.Width - allocation.Width) > 0.01)
            _placeholder.Width = allocation.Width;
        if (double.IsNaN(_placeholder.Height) || Math.Abs(_placeholder.Height - allocation.Height) > 0.01)
            _placeholder.Height = allocation.Height;

        Rect visible = new(0, 0, width, height);
        if (_placeholder.Clip is not RectangleGeometry clip)
        {
            clip = new RectangleGeometry();
            _placeholder.Clip = clip;
        }
        if (clip.Rect != visible)
            clip.Rect = visible;

        double currentLeft = Canvas.GetLeft(_placeholder);
        if (double.IsNaN(currentLeft) || Math.Abs(currentLeft) > 0.1)
            Canvas.SetLeft(_placeholder, 0);

        double currentTop = Canvas.GetTop(_placeholder);
        if (double.IsNaN(currentTop) || Math.Abs(currentTop - top) > 0.1)
            Canvas.SetTop(_placeholder, top);

        _offsetY = top;
        _placeholder.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>
    /// Creates the placeholder on first use and attaches it to the overlay.
    /// Returns false when there is no overlay or the attach failed.
    /// </summary>
    internal bool EnsurePlaceholder()
    {
        if (_surface.Overlay is null)
            return false;

        if (_placeholder is null)
            CreatePlaceholder();

        try
        {
            if (VisualTreeHelper.GetParent(_placeholder) is null)
                _surface.Overlay.Children.Add(_placeholder);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] selection adorner attach failed: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>The renderer's rasterization scale, or 1 when unknown.</summary>
    internal double GetRasterizationScale()
    {
        double scale = _renderer.XamlRoot?.RasterizationScale ?? 1.0;
        return double.IsFinite(scale) && scale > 0 ? scale : 1.0;
    }

    [MemberNotNull(nameof(_placeholder))]
    private void CreatePlaceholder()
    {
        var placeholder = new Border
        {
            IsHitTestVisible = false,
            UseLayoutRounding = true,
            // Keep the overlay inert until selection gives it viewport bounds.
            Width = 1,
            Height = 1,
            Visibility = Visibility.Collapsed,
        };
        Canvas.SetZIndex(placeholder, 0);
        _placeholder = placeholder;
        _visualHost = new SelectionAdornerVisualHost(placeholder);
    }

    /// <summary>
    /// Returns the long-lived adorner surface, attached to the current adorner
    /// visual. Device-loss and shutdown failures propagate to the guarded
    /// refresh, which owns recovery.
    /// </summary>
    private SelectionAdornerCompositionSurface? EnsureSurface(double width, double height)
    {
        SelectionAdornerVisualHost? visualHost = _visualHost;
        if (visualHost is null || _placeholder is null || _lifetime.IsDisposed || _lifetime.IsUnloaded ||
            !ReferenceEquals(visualHost.Placeholder, _placeholder))
        {
            return null;
        }

        float dpi = (float)(96.0 * GetRasterizationScale());

        SelectionAdornerCompositionSurface? surface = _surfaces.Ensure(
            visualHost,
            width,
            height,
            dpi,
            EnsureGraphicsDevice(visualHost.Compositor));
        // Attachment can reenter unload or replace the adorner placeholder.
        if (surface is null || _lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(visualHost, _visualHost))
            return null;

        return surface;
    }

    /// <summary>
    /// Returns the composition graphics device for the adorner, rendering with
    /// the current shared Win2D device. A replaced Win2D device is switched in
    /// place: surfaces survive with lost content, and every refresh repaints.
    /// </summary>
    private CompositionGraphicsDevice EnsureGraphicsDevice(Compositor compositor)
    {
        CanvasDevice canvasDevice = CanvasDevice.GetSharedDevice();
        if (_graphicsDevice is { } graphicsDevice &&
            ReferenceEquals(graphicsDevice.Compositor, compositor))
        {
            if (!ReferenceEquals(_canvasDevice, canvasDevice))
            {
                CanvasComposition.SetCanvasDevice(graphicsDevice, canvasDevice);
                _canvasDevice = canvasDevice;
            }

            return graphicsDevice;
        }

        ReleaseGraphicsDevice();
        graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, canvasDevice);
        graphicsDevice.RenderingDeviceReplaced += OnRenderingDeviceReplaced;
        _graphicsDevice = graphicsDevice;
        _canvasDevice = canvasDevice;
        return graphicsDevice;
    }

    /// <summary>
    /// A replaced rendering device discards surface content without any
    /// paint failure, so repaint the adorner on the UI thread.
    /// </summary>
    private void OnRenderingDeviceReplaced(
        CompositionGraphicsDevice sender,
        RenderingDeviceReplacedEventArgs args)
        => _renderer.DispatcherQueue?.TryEnqueue(() =>
        {
            if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !ReferenceEquals(sender, _graphicsDevice))
                return;
            _repaintAfterDeviceReplaced();
        });

    /// <summary>
    /// Disposes the adorner's composition graphics device. The surface owner
    /// recreates its surface for a new device, so this is safe at any time.
    /// </summary>
    private void ReleaseGraphicsDevice()
    {
        CompositionGraphicsDevice? graphicsDevice = _graphicsDevice;
        _graphicsDevice = null;
        _canvasDevice = null;
        if (graphicsDevice is null)
            return;
        try
        {
            graphicsDevice.RenderingDeviceReplaced -= OnRenderingDeviceReplaced;
            graphicsDevice.Dispose();
        }
        catch (Exception failure)
        {
            ReportSurfaceReleaseFailure(failure);
        }
    }

    private static SelectionAdornerCompositionSurface CreateSurface(
        CompositionGraphicsDevice device,
        SelectionAdornerSurfaceAllocation allocation)
        => new(device, allocation);

    private static void DisposeSurface(SelectionAdornerCompositionSurface surface)
        => surface.Dispose();

    private static void ReportSurfaceReleaseFailure(Exception failure)
        => MarkdownDiagnostics.WriteLine(
            $"[MarkdownRendererControl] selection surface detach failed: {failure.GetType().Name} {GraphicsDeviceErrors.FormatHResult(failure.HResult)}");
}
