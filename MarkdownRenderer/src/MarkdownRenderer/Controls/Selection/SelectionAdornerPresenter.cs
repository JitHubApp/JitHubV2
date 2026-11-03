using System;
using System.Collections.Generic;
using System.Numerics;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Selection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// Paints the selection highlight and the hovered and focused link states
/// into one viewport-bounded composition surface above the document canvas.
/// </summary>
/// <remarks>
/// <para>
/// Selection drags and hover changes repaint only this surface, never the
/// document's canvas tiles, and never change the overlay's child tree.
/// <see cref="SelectionAdornerRefreshScheduler"/> coalesces requests onto
/// the next rendering frame, where the refresh runs under the snapshot's
/// paint lease.
/// </para>
/// <para>
/// The adorner is an inert placeholder element hosting a composition child
/// visual; <see cref="SelectionAdornerCompositionTarget"/> owns the
/// placeholder, its long-lived surface and the graphics device, and explains
/// why a XAML Image is not used.
/// </para>
/// </remarks>
internal sealed class SelectionAdornerPresenter : ISelectionAdornerPainter
{
    private readonly ISelectionAdornerHost _host;
    private readonly FrameworkElement _renderer;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;
    private readonly SelectionController _selection;
    private readonly PointerHoverState _hover;

    // Selection rectangles are retained for viewport and diagnostics work.
    private List<Rect> _rects = new();
    private List<Rect> _rectScratch = new();
    private List<SelectionBackgroundGeometry> _backgrounds = new();
    private List<SelectionBackgroundGeometry> _backgroundScratch = new();

    private bool _geometryDirty = true;
    private bool _visualDirty = true;

    // The generation, layout revision, viewport and scale last painted.
    private long _paintedGeneration = -1;
    private long _paintedLayoutRevision = -1;
    private double _paintedViewportTop = double.NaN;
    private double _paintedViewportWidth = double.NaN;
    private double _paintedViewportHeight = double.NaN;
    private double _paintedRasterizationScale = double.NaN;

    private readonly SelectionAdornerCompositionTarget _target;
    private readonly SelectionAdornerRefreshScheduler _refresh;

    public SelectionAdornerPresenter(
        ISelectionAdornerHost host,
        FrameworkElement renderer,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewport viewport,
        SelectionController selection,
        PointerHoverState hover)
    {
        _host = host;
        _renderer = renderer;
        _lifetime = lifetime;
        _published = published;
        _viewport = viewport;
        _selection = selection;
        _hover = hover;
        _target = new SelectionAdornerCompositionTarget(renderer, surface, lifetime, RepaintAfterDeviceReplaced);
        _refresh = new SelectionAdornerRefreshScheduler(host, renderer, lifetime, published, this);
    }

    /// <summary>True once the placeholder element exists.</summary>
    public bool IsCreated => _target.IsCreated;

    /// <summary>True while the adorner is shown.</summary>
    public bool IsVisible => _target.IsVisible;

    /// <summary>True while a refresh is requested or running.</summary>
    public bool HasPendingRefresh => _refresh.HasPendingRefresh;

    /// <summary>The selection rectangles last painted, in document coordinates.</summary>
    public IReadOnlyList<Rect> Rectangles => _rects;

    /// <summary>The Win2D device the adorner surface currently renders with.</summary>
    public CanvasDevice? SurfaceDevice => _target.SurfaceDevice;

    /// <summary>Repaints on the next frame with the current geometry.</summary>
    public void Invalidate()
    {
        MarkVisualDirty();
        RequestRefresh();
    }

    /// <summary>Recomputes geometry and repaints on the next frame.</summary>
    public void InvalidateGeometry()
    {
        MarkGeometryDirty();
        Invalidate();
    }

    /// <summary>Marks geometry and pixels stale without requesting a frame.</summary>
    public void MarkGeometryDirty()
    {
        _geometryDirty = true;
        _visualDirty = true;
    }

    /// <summary>Marks pixels stale without requesting a frame.</summary>
    public void MarkVisualDirty() => _visualDirty = true;

    /// <summary>Requests a refresh on the next rendering frame.</summary>
    public void RequestRefresh() => _refresh.RequestRefresh();

    /// <summary>Marks the adorner for a full repaint without touching its surface.</summary>
    public void InvalidatePresentation()
    {
        _refresh.CancelRetry();
        _geometryDirty = true;
        _visualDirty = true;
        _paintedGeneration = -1;
        _paintedLayoutRevision = -1;
        _paintedViewportTop = double.NaN;
        _paintedViewportWidth = double.NaN;
        _paintedViewportHeight = double.NaN;
        _paintedRasterizationScale = double.NaN;
    }

    /// <summary>Hides the adorner until its next paint.</summary>
    public void Collapse() => _target.Collapse();

    /// <summary>Removes every overlay child except the adorner placeholder.</summary>
    public void RemoveOtherOverlayChildren() => _target.RemoveOtherOverlayChildren();

    /// <summary>Forgets the painted selection geometry.</summary>
    public void ClearGeometry()
    {
        _rects.Clear();
        _rectScratch.Clear();
        _backgrounds.Clear();
        _backgroundScratch.Clear();
    }

    /// <summary>
    /// Detaches and retires the adorner surface. Reserved for unload, device
    /// loss and geometry recovery; ordinary snapshot commits only invalidate.
    /// A failed XAML detach is reported and the next refresh rebinds
    /// explicitly.
    /// </summary>
    public void ReleaseSurface()
    {
        InvalidatePresentation();
        _target.ReleaseSurface();
    }

    /// <summary>
    /// Like <see cref="ReleaseSurface"/>, but reports a failure instead of
    /// throwing, so teardown that follows still runs.
    /// </summary>
    public bool TryReleaseSurface()
    {
        try
        {
            ReleaseSurface();
            return true;
        }
        catch (Exception failure)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] selection surface release failed: {failure.GetType().Name} {GraphicsDeviceErrors.FormatHResult(failure.HResult)}");
            return false;
        }
    }

    /// <summary>
    /// Disposes the detached surfaces, the child visual and the graphics
    /// device, and forgets the placeholder. No refresh frame follows an
    /// unload, so nothing else would release them.
    /// </summary>
    /// <remarks>Call after <see cref="TryReleaseSurface"/> and after the overlay is cleared.</remarks>
    public void DisposeForUnload()
    {
        _target.DisposeForUnload();
        ClearGeometry();
    }

    /// <summary>
    /// Releases the surface and graphics device on disposal. Unload has
    /// usually done this already; this covers a renderer disposed without
    /// ever unloading.
    /// </summary>
    public void ReleaseForDispose()
    {
        TryReleaseSurface();
        _target.ReleaseForDispose();
    }

    long ISelectionAdornerPainter.PaintedLayoutRevision => _paintedLayoutRevision;

    void ISelectionAdornerPainter.MarkGeometryDirty() => MarkGeometryDirty();

    SelectionAdornerRefreshOperationResult ISelectionAdornerPainter.RefreshUnderPaintLease(
        LayoutSnapshot snapshot,
        long generation)
    {
        if (!ReferenceEquals(snapshot, _published.Snapshot) || generation != _published.Generation ||
            snapshot.GeometryRecoveryRequired || snapshot.IsRetiringOrDisposed)
        {
            return SelectionAdornerRefreshOperationResult.Retry;
        }

        bool hasViewport = _viewport.TryGet(out double top, out double height, out double width);
        if (!hasViewport && (!double.IsFinite(height) || !double.IsFinite(width)))
            return SelectionAdornerRefreshOperationResult.AwaitLifecycle;

        if (!double.IsFinite(top)) top = 0;
        if (!double.IsFinite(height) || height <= 0) height = Math.Max(_renderer.ActualHeight, 1);
        if (!double.IsFinite(width) || width <= 0) width = Math.Max(_renderer.ActualWidth, 1);

        double scale = _target.GetRasterizationScale();

        long layoutRevision = snapshot.LayoutRevision;
        long requestVersion = _refresh.RequestVersion;
        bool viewportChanged = generation != _paintedGeneration ||
            layoutRevision != _paintedLayoutRevision ||
            Math.Abs(top - _paintedViewportTop) > 0.01 ||
            Math.Abs(width - _paintedViewportWidth) > 0.5 ||
            Math.Abs(height - _paintedViewportHeight) > 0.5 ||
            Math.Abs(scale - _paintedRasterizationScale) > 0.001;
        bool rebuildGeometry = _geometryDirty || viewportChanged;
        if (!_visualDirty && !rebuildGeometry)
            return SelectionAdornerRefreshOperationResult.Completed;

        Rect viewport = new(0, top, width, height);
        List<Rect> paintRects = _rects;
        List<SelectionBackgroundGeometry> paintBackgrounds = _backgrounds;
        if (rebuildGeometry)
        {
            paintRects = _rectScratch;
            paintRects.Clear();
            paintBackgrounds = _backgroundScratch;
            paintBackgrounds.Clear();
            if (_selection.IsActive)
            {
                _selection.AppendVisibleHighlightRects(
                    snapshot,
                    viewport,
                    paintRects,
                    paintBackgrounds);
            }
            SnapRectangles(paintRects, scale);
            SnapBackgroundRectangles(paintBackgrounds, scale);
        }

        bool hasContent = paintRects.Count > 0 ||
            _hover.HasLink ||
            _host.HasFocusedLink;
        if (!hasContent)
        {
            if (rebuildGeometry)
                CommitGeometry(paintRects, paintBackgrounds);
            Collapse();
            CommitViewport(generation, layoutRevision, top, width, height, scale);
            _host.UpdateSelectionHandlesUnderPaint(snapshot, new Rect(0, top, width, height));
            return SelectionAdornerRefreshOperationResult.Completed;
        }

        if (!_target.EnsurePlaceholder())
        {
            _host.UpdateSelectionHandlesUnderPaint(snapshot, viewport);
            return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
        }

        _target.AdvanceFrame();
        SelectionAdornerCompositionSurface? source = null;
        try
        {
            // Snapshot bitmaps belong to the device that built the snapshot.
            // After device loss the shared device is replaced; never draw the
            // old snapshot into a surface created on the new device.
            if (!ReferenceEquals(_published.LayoutDevice, CanvasDevice.GetSharedDevice()))
            {
                RequestDeviceRebuild();
                _host.UpdateSelectionHandlesUnderPaint(snapshot, viewport);
                return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
            }

            // Surface creation and attachment can report device loss too, so
            // they share the guarded region with drawing.
            if (!_target.ApplyViewportUnderPaint(width, height, top, out source))
            {
                _host.UpdateSelectionHandlesUnderPaint(snapshot, viewport);
                return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
            }

            long surfaceVersion = _target.SurfaceGeneration;
            // EndDraw can fail. Do not publish clean geometry or mutate XAML
            // handles until the explicitly owned drawing session has closed.
            using (var drawingSession = CanvasComposition.CreateDrawingSession(source.Surface, source.PixelBounds, source.Dpi))
            {
                drawingSession.Clear(Color.FromArgb(0, 0, 0, 0));
                drawingSession.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                drawingSession.Transform = Matrix3x2.CreateTranslation(0, -(float)_target.OffsetY);
                _host.PaintInteractiveDocumentState(
                    snapshot,
                    drawingSession,
                    viewport,
                    paintBackgrounds);
            }

            if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
                return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
            if (surfaceVersion != _target.SurfaceGeneration ||
                !ReferenceEquals(source, _target.Surface) ||
                requestVersion != _refresh.RequestVersion ||
                !ReferenceEquals(snapshot, _published.Snapshot) || generation != _published.Generation ||
                snapshot.LayoutRevision != layoutRevision)
            {
                return SelectionAdornerRefreshOperationResult.Retry;
            }

            if (rebuildGeometry)
                CommitGeometry(paintRects, paintBackgrounds);
            CommitViewport(generation, layoutRevision, top, width, height, scale);
            _host.UpdateSelectionHandlesUnderPaint(snapshot, new Rect(0, top, width, height));
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            // The host's device-lost handling releases the surface under its
            // own guard and always schedules recovery.
            _host.HandleDeviceLost(ex);
            return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsShutdownOrDisposed(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] ignored selection invalidate during shutdown: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
            return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
        }
        finally
        {
            // A reentrant lifecycle callback may detach the field while the
            // drawing session is unwinding. Its managed owner outlives EndDraw.
            GC.KeepAlive(source);
        }

        return SelectionAdornerRefreshOperationResult.Completed;
    }

    private static void SnapRectangles(List<Rect> rectangles, double scale)
    {
        for (int index = 0; index < rectangles.Count; index++)
        {
            Rect rect = rectangles[index];
            Rect snapped = SelectionController.SnapToRasterizationScale(rect, scale);
            if (snapped.Width <= 0 || snapped.Height <= 0)
            {
                rectangles.RemoveAt(index--);
                continue;
            }

            rectangles[index] = snapped;
        }
    }

    private static void SnapBackgroundRectangles(
        List<SelectionBackgroundGeometry> rectangles,
        double scale)
    {
        for (int index = 0; index < rectangles.Count; index++)
        {
            SelectionBackgroundGeometry background = rectangles[index];
            Rect snapped = SelectionController.SnapToRasterizationScale(background.Bounds, scale);
            if (snapped.Width <= 0 || snapped.Height <= 0)
            {
                rectangles.RemoveAt(index--);
                continue;
            }

            rectangles[index] = background with { Bounds = snapped };
        }
    }

    private void CommitGeometry(
        List<Rect> stagedRectangles,
        List<SelectionBackgroundGeometry> stagedBackgrounds)
    {
        if (ReferenceEquals(stagedRectangles, _rects))
            return;

        List<Rect> previousRectangles = _rects;
        _rects = stagedRectangles;
        _rectScratch = previousRectangles;
        previousRectangles.Clear();

        List<SelectionBackgroundGeometry> previousBackgrounds = _backgrounds;
        _backgrounds = stagedBackgrounds;
        _backgroundScratch = previousBackgrounds;
        previousBackgrounds.Clear();

        _rectScratch.Clear();
        _backgroundScratch.Clear();
    }

    private void CommitViewport(
        long generation,
        long layoutRevision,
        double top,
        double width,
        double height,
        double scale)
    {
        _paintedGeneration = generation;
        _paintedLayoutRevision = layoutRevision;
        _paintedViewportTop = top;
        _paintedViewportWidth = width;
        _paintedViewportHeight = height;
        _paintedRasterizationScale = scale;
        _geometryDirty = false;
        _visualDirty = false;
    }

    private void RequestDeviceRebuild()
    {
        // Device recovery already rebuilds against the replacement device.
        if (_host.IsDeviceRecoveryQueued || _lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        MarkdownDiagnostics.WriteLine(
            "[MarkdownRendererControl] snapshot layout device differs from the shared device; rebuilding.");
        _host.RequestRebuildForDeviceChange();
    }

    /// <summary>
    /// Repaints everything after the rendering device was replaced, which
    /// discards surface content without any paint failure.
    /// </summary>
    private void RepaintAfterDeviceReplaced()
    {
        InvalidatePresentation();
        Invalidate();
    }
}
