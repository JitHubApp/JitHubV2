using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Selection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
/// Requests are coalesced onto the next rendering frame, where the refresh
/// runs under the snapshot's paint lease.
/// </para>
/// <para>
/// The adorner is an inert placeholder element hosting a composition child
/// visual. A XAML Image showing Win2D's CanvasImageSource is unsafe: XAML's
/// reference to the aggregated inner SurfaceImageSource does not keep
/// Win2D's outer object alive, so the .NET finalizer could destroy it while
/// XAML still released the Image. Composition surfaces have no XAML peer and
/// are disposed deterministically. One long-lived, grow-only surface is kept
/// per placeholder; relayout, source replacement and resize reuse it.
/// </para>
/// </remarks>
internal sealed class SelectionAdornerPresenter
{
    private readonly ISelectionAdornerHost _host;
    private readonly FrameworkElement _renderer;
    private readonly MarkdownViewSurface _surface;
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

    private readonly SelectionAdornerRetryState _retryState = new();
    private bool _retryFrameSubscribed;
    private EventHandler<object>? _retryFrameHandler;
    private bool _refreshInProgress;
    private long _requestVersion;

    private bool _geometryDirty = true;
    private bool _visualDirty = true;

    // The generation, layout revision, viewport and scale last painted.
    private long _paintedGeneration = -1;
    private long _paintedLayoutRevision = -1;
    private double _paintedViewportTop = double.NaN;
    private double _paintedViewportWidth = double.NaN;
    private double _paintedViewportHeight = double.NaN;
    private double _paintedRasterizationScale = double.NaN;

    private Border? _placeholder;
    private SelectionAdornerVisualHost? _visualHost;
    private readonly SelectionAdornerSurfaceLifetime<SelectionAdornerCompositionSurface, CompositionGraphicsDevice> _surfaces =
        new(CreateSurface, ReportSurfaceReleaseFailure, DisposeSurface);

    // The composition graphics device that draws adorner surfaces, and the
    // Win2D device it currently renders with.
    private CompositionGraphicsDevice? _graphicsDevice;
    private CanvasDevice? _canvasDevice;
    private double _offsetY;

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
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _viewport = viewport;
        _selection = selection;
        _hover = hover;
    }

    /// <summary>True once the placeholder element exists.</summary>
    public bool IsCreated => _placeholder is not null;

    /// <summary>True while the adorner is shown.</summary>
    public bool IsVisible => _placeholder?.Visibility == Visibility.Visible;

    /// <summary>True while a refresh is requested or running.</summary>
    public bool HasPendingRefresh => _retryState.IsPending || _refreshInProgress;

    /// <summary>The selection rectangles last painted, in document coordinates.</summary>
    public IReadOnlyList<Rect> Rectangles => _rects;

    /// <summary>The Win2D device the adorner surface currently renders with.</summary>
    public CanvasDevice? SurfaceDevice => _canvasDevice;

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
    public void RequestRefresh()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
        {
            CancelRetry();
            return;
        }

        // Device recovery owns its bounded backoff. Keep the geometry/visual
        // dirty flags intact, but do not race the replacement surface every
        // Rendering frame while the recovery timer is waiting.
        if (_host.IsDeviceRecoveryQueued)
        {
            CancelRetry();
            return;
        }

        _retryState.Request();
        _requestVersion = unchecked(_requestVersion + 1);
        ScheduleRetry();
    }

    /// <summary>Marks the adorner for a full repaint without touching its surface.</summary>
    public void InvalidatePresentation()
    {
        CancelRetry();
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
    public void Collapse()
    {
        if (_placeholder is not null)
            _placeholder.Visibility = Visibility.Collapsed;
    }

    /// <summary>Removes every overlay child except the adorner placeholder.</summary>
    public void RemoveOtherOverlayChildren()
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
        _surfaces.Release(_visualHost);
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
        _surfaces.DropRetired();
        _visualHost?.Dispose();
        ReleaseGraphicsDevice();
        ClearGeometry();
        if (_placeholder is not null)
        {
            _placeholder = null;
            _visualHost = null;
        }
    }

    /// <summary>
    /// Releases the surface and graphics device on disposal. Unload has
    /// usually done this already; this covers a renderer disposed without
    /// ever unloading.
    /// </summary>
    public void ReleaseForDispose()
    {
        TryReleaseSurface();
        _surfaces.DropRetired();
        ReleaseGraphicsDevice();
    }

    internal SelectionAdornerRefreshOperationResult RefreshUnderPaintLease(
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

        double scale = GetRasterizationScale();

        long layoutRevision = snapshot.LayoutRevision;
        long requestVersion = _requestVersion;
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

        if (!EnsurePlaceholder())
        {
            _host.UpdateSelectionHandlesUnderPaint(snapshot, viewport);
            return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
        }

        _surfaces.AdvanceFrame();
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
            if (!ApplyViewportUnderPaint(width, height, top, out source))
            {
                _host.UpdateSelectionHandlesUnderPaint(snapshot, viewport);
                return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
            }

            long surfaceVersion = _surfaces.Generation;
            // EndDraw can fail. Do not publish clean geometry or mutate XAML
            // handles until the explicitly owned drawing session has closed.
            using (var drawingSession = CanvasComposition.CreateDrawingSession(source.Surface, source.PixelBounds, source.Dpi))
            {
                drawingSession.Clear(Color.FromArgb(0, 0, 0, 0));
                drawingSession.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                drawingSession.Transform = Matrix3x2.CreateTranslation(0, -(float)_offsetY);
                _host.PaintInteractiveDocumentState(
                    snapshot,
                    drawingSession,
                    viewport,
                    paintBackgrounds);
            }

            if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
                return SelectionAdornerRefreshOperationResult.AwaitLifecycle;
            if (surfaceVersion != _surfaces.Generation ||
                !ReferenceEquals(source, _surfaces.Surface) ||
                requestVersion != _requestVersion ||
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

    private void TryRefreshAttempt()
    {
        if (_refreshInProgress)
        {
            // A synchronous XAML/native callback can re-enter the frame path.
            // Preserve that new request for the next rendering frame instead
            // of nesting a second snapshot lease or letting the outer attempt
            // clear its dirtiness.
            _retryState.Request();
            _requestVersion = unchecked(_requestVersion + 1);
            ScheduleRetry();
            return;
        }

        if (_host.IsDeviceRecoveryQueued)
        {
            CancelRetry();
            return;
        }

        if (!_retryState.TryBeginFrame())
            return;

        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
        {
            CancelRetry();
            return;
        }

        long requestVersion = _requestVersion;
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null)
        {
            CompleteRetry();
            return;
        }

        if (snapshot.GeometryRecoveryRequired)
        {
            _host.RequestGeometryRecoveryIfRequired(snapshot);
            CancelRetry();
            return;
        }
        if (snapshot.IsRetiringOrDisposed)
        {
            CancelRetry();
            return;
        }

        long generation = _published.Generation;
        var operation = new SelectionAdornerRefreshOperation(this, generation);
        SelectionAdornerRefreshResult result;
        _refreshInProgress = true;
        try
        {
            result = SelectionAdornerRefreshCoordinator.TryRefresh(snapshot, ref operation);
        }
        finally
        {
            _refreshInProgress = false;
        }

        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
        {
            CancelRetry();
            return;
        }

        if (_host.IsDeviceRecoveryQueued)
        {
            // The native operation may have discovered device loss and queued
            // the normal backoff while it held the paint lease. Do not let the
            // stale revision created by surface release bypass that backoff.
            CancelRetry();
            return;
        }

        if (result != SelectionAdornerRefreshResult.Completed)
        {
            if (result == SelectionAdornerRefreshResult.AwaitLifecycle)
            {
                // Surface attachment/shutdown failures have no useful
                // per-frame retry. Keep dirty state and wait for the next
                // lifecycle/resource request. Restore the dirty state even if
                // a shutdown exception occurred after the visual commit.
                _geometryDirty = true;
                _visualDirty = true;
                if (requestVersion != _requestVersion)
                    ScheduleRetry();
                else
                    CancelRetry();
            }
            else
            {
                ScheduleRetry();
            }
            return;
        }

        if (requestVersion != _requestVersion ||
            !ReferenceEquals(snapshot, _published.Snapshot) || generation != _published.Generation ||
            snapshot.LayoutRevision != _paintedLayoutRevision)
        {
            _geometryDirty = true;
            _visualDirty = true;
            _retryState.Request();
            ScheduleRetry();
            return;
        }

        CompleteRetry();
    }

    private void OnRetryFrame(object? sender, object args)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
        {
            CancelRetry();
            return;
        }

        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = HasPendingRefresh &&
            !_host.IsDeviceRecoveryQueued &&
            performance.IsMeasurementEnabled();
        // The harness changes this thread-static ID at its Rendering sample;
        // it labels the interval currently open. Work before that sample uses
        // the prior ID, while work after it uses the new ID, in either handler
        // subscription order.
        long logicalFrameId = measure
            ? MarkdownPerformanceEventSource.GetLogicalFrameId()
            : 0;
        measure = logicalFrameId > 0;
        if (!measure)
        {
            // Keep idle, uncorrelated, and device-recovery callbacks on the
            // normal path without adding timing/allocation probes to them.
            TryRefreshAttempt();
            return;
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        try
        {
            TryRefreshAttempt();
        }
        finally
        {
            performance.PaintWork(
                logicalFrameId,
                Stopwatch.GetTimestamp() - started,
                Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
        }
    }

    private void ScheduleRetry()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_renderer.IsLoaded)
        {
            CancelRetry();
            return;
        }

        _retryState.Retry();
        if (_retryFrameSubscribed)
            return;

        _retryFrameHandler ??= OnRetryFrame;
        CompositionTarget.Rendering += _retryFrameHandler;
        _retryFrameSubscribed = true;
    }

    private void CompleteRetry()
    {
        _retryState.Complete();
        UnsubscribeRetryFrame();
    }

    private void CancelRetry()
    {
        _retryState.Cancel();
        UnsubscribeRetryFrame();
    }

    private void UnsubscribeRetryFrame()
    {
        if (!_retryFrameSubscribed)
            return;

        if (_retryFrameHandler is { } handler)
            CompositionTarget.Rendering -= handler;
        _retryFrameSubscribed = false;
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

    private bool ApplyViewportUnderPaint(
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

    private bool EnsurePlaceholder()
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
            InvalidatePresentation();
            Invalidate();
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

    private void RequestDeviceRebuild()
    {
        // Device recovery already rebuilds against the replacement device.
        if (_host.IsDeviceRecoveryQueued || _lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        MarkdownDiagnostics.WriteLine(
            "[MarkdownRendererControl] snapshot layout device differs from the shared device; rebuilding.");
        _host.RequestRebuildForDeviceChange();
    }

    private double GetRasterizationScale()
    {
        double scale = _renderer.XamlRoot?.RasterizationScale ?? 1.0;
        return double.IsFinite(scale) && scale > 0 ? scale : 1.0;
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
