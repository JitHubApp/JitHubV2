using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using MarkdownRenderer.Controls.Internal.LazyLayout;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;
using MarkdownRenderer.Controls.Internal.Readiness;
using MarkdownRenderer.Controls.Internal.Rendering.Tiles;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Internal.Rendering;

/// <summary>
/// Paints the published snapshot into the document tiles around the
/// viewport, once per composition frame while there is work. Visible tiles
/// are painted every frame they are stale; tiles ahead of and behind the
/// viewport are painted within a per-frame budget, so a scroll arrives on
/// finished pixels. Owns the tile clear color published with each snapshot,
/// the one-shot first-viewport measurement, and the deferred scene re-probe
/// that waits for a painted band.
/// </summary>
/// <remarks>
/// The frame pump runs only while tiles need painting or the viewport is
/// moving, and stops itself once every planned tile is current. Stale tiles
/// keep their pixels on screen until repainted, so no invalidation exposes
/// blank area.
/// </remarks>
internal sealed class CanvasTilePainter
{
    /// <summary>UI-thread time per frame spent on tiles that are not yet visible.</summary>
    private const double PrefetchBudgetMilliseconds = 6;

    /// <summary>Viewports of tiles kept painted beyond the visible ones.</summary>
    private const int LookAheadViewports = 4;

    /// <summary>Upper bound on painted tiles, whatever the budget allows.</summary>
    private const int MaximumTiles = 128;

    /// <summary>Unmoving frames after which an idle pump stops.</summary>
    private const int StableFramesBeforeIdle = 2;

    /// <summary>A pause between viewport samples after which velocity restarts at zero.</summary>
    private const double VelocityResetSeconds = 0.25;

    /// <summary>GPU bytes the tiles of one renderer may hold.</summary>
    private static readonly long TileBudgetBytes = (IntPtr.Size == 4 ? 32L : 96L) * 1024 * 1024;

    private readonly ICanvasTilePainterHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewport _viewport;
    private readonly LazyLayoutRealization _lazyLayout;
    private readonly CanvasDeviceRecovery _deviceRecovery;
    private readonly RebuildScheduler _rebuild;
    private readonly DeferredSceneReadiness _deferredScenes;
    private readonly DocumentTilePlan _plan = new();
    // Shown under unpainted tiles. Transparent until the first snapshot
    // publishes its theme surface, so a dark host never flashes white.
    private readonly SolidColorBrush _canvasBackground = new(Colors.Transparent);
    // Background color used to clear each tile before painting. Published
    // atomically with the layout/theme snapshot so a tile never clears with
    // the next theme and repaints with the previous one.
    private Color _background = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private long _firstViewportPipelineStart;
    private long _firstViewportSourceUtf16Bytes;
    // Set when the visible deferred-scene probe could not verify its band
    // (busy or not yet measured). The next successful paint, which follows
    // band measurement or scene preparation, re-runs the probe once.
    private int _deferredSceneProbePending;
    private bool _framesAttached;
    private XamlRoot? _xamlRoot;
    private double _attachedScale = double.NaN;
    private double _lastViewportTop = double.NaN;
    private long _lastViewportTimestamp;
    private double _velocity;
    private int _stableFrames;

    internal CanvasTilePainter(
        ICanvasTilePainterHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewSurface surface,
        MarkdownViewport viewport,
        LazyLayoutRealization lazyLayout,
        CanvasDeviceRecovery deviceRecovery,
        RebuildScheduler rebuild,
        DeferredSceneReadiness deferredScenes)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _surface = surface;
        _viewport = viewport;
        _lazyLayout = lazyLayout;
        _deviceRecovery = deviceRecovery;
        _rebuild = rebuild;
        _deferredScenes = deferredScenes;
    }

    /// <summary>Publishes the clear color of the snapshot just committed.</summary>
    internal void PublishBackground(Color background)
    {
        _background = background;
        _canvasBackground.Color = background;
    }

    /// <summary>
    /// Reports the first painted viewport of the snapshot committed by the
    /// pipeline that started at <paramref name="pipelineStartTimestamp"/>.
    /// </summary>
    internal void ArmFirstViewportMeasurement(long pipelineStartTimestamp, long sourceUtf16Bytes)
    {
        _firstViewportPipelineStart = pipelineStartTimestamp;
        _firstViewportSourceUtf16Bytes = sourceUtf16Bytes;
    }

    internal void CancelFirstViewportMeasurement() => _firstViewportPipelineStart = 0;

    /// <summary>Re-runs the visible deferred-scene probe after the next successful paint.</summary>
    internal void RequestDeferredSceneProbe() => Interlocked.Exchange(ref _deferredSceneProbePending, 1);

    internal void CancelDeferredSceneProbe() => Interlocked.Exchange(ref _deferredSceneProbePending, 0);

    /// <summary>
    /// Starts painting the document canvas the surface just created: shows
    /// the theme background under unpainted tiles and follows the window's
    /// rasterization scale.
    /// </summary>
    internal void Attach()
    {
        if (_surface.Canvas is { } canvas && !ReferenceEquals(canvas.Background, _canvasBackground))
            canvas.Background = _canvasBackground;

        XamlRoot? xamlRoot = _host.XamlRoot;
        if (!ReferenceEquals(xamlRoot, _xamlRoot))
        {
            if (_xamlRoot is not null)
                _xamlRoot.Changed -= OnXamlRootChanged;
            _xamlRoot = xamlRoot;
            if (xamlRoot is not null)
            {
                xamlRoot.Changed += OnXamlRootChanged;
                _attachedScale = xamlRoot.RasterizationScale;
            }
        }

        RequestFrame();
    }

    /// <summary>Stops painting at the view's unload boundary.</summary>
    internal void Detach()
    {
        StopFrames();
        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed -= OnXamlRootChanged;
            _xamlRoot = null;
        }

        _attachedScale = double.NaN;
        _lastViewportTop = double.NaN;
        _velocity = 0;
    }

    /// <summary>
    /// Paints on the next composition frame. Called when tiles went stale,
    /// the viewport moved or the document extent changed.
    /// </summary>
    internal void RequestFrame()
    {
        if (_framesAttached || _lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        _stableFrames = 0;
        CompositionTarget.Rendering += OnRendering;
        _framesAttached = true;
    }

    /// <summary>
    /// Paints the stale visible tiles now, from the view change that moved
    /// the viewport, so a jump presents painted tiles in the frame that
    /// shows it rather than one frame later. Look-ahead painting and
    /// velocity sampling stay on the frame pump.
    /// </summary>
    internal void PaintVisibleNow()
    {
        RequestFrame();
        if (_framesAttached && _host.IsLoaded)
            _ = Paint(visibleOnly: true);
    }

    private void StopFrames()
    {
        if (!_framesAttached)
            return;

        CompositionTarget.Rendering -= OnRendering;
        _framesAttached = false;
    }

    private void OnRendering(object? sender, object args)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_host.IsLoaded)
        {
            StopFrames();
            return;
        }

        if (!Paint(visibleOnly: false))
        {
            StopFrames();
            // Spares only pay off while the viewport moves.
            _surface.Tiles?.ReleaseSpares();
        }
    }

    /// <summary>
    /// Paints one frame's tiles, or only the visible ones. Returns true while
    /// another frame is needed. A lost device stops the pump and starts
    /// recovery.
    /// </summary>
    private bool Paint(bool visibleOnly)
    {
        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long started = measure ? Stopwatch.GetTimestamp() : 0;
        long allocatedBefore = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        try
        {
            return PaintFrame(visibleOnly);
        }
        catch (Exception exception) when (GraphicsDeviceErrors.IsDeviceLost(exception))
        {
            StopFrames();
            _deviceRecovery.HandleDeviceLost(exception);
            return false;
        }
        catch (Exception exception) when (GraphicsDeviceErrors.IsShutdownOrDisposed(exception))
        {
            StopFrames();
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] ignored canvas paint during shutdown: {exception.GetType().Name} " +
                $"{GraphicsDeviceErrors.FormatHResult(exception.HResult)}");
            return false;
        }
        finally
        {
            if (measure)
            {
                performance.PaintWork(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    Stopwatch.GetTimestamp() - started,
                    Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
            }
        }
    }

    /// <summary>
    /// Plans the tiles around the current viewport and paints those that are
    /// stale, or only the visible ones. Returns true while another frame is
    /// needed.
    /// </summary>
    private bool PaintFrame(bool visibleOnly)
    {
        if (_surface.Tiles is not { } layer ||
            _surface.Canvas is not { } canvas ||
            _published.Snapshot is not { } snapshot)
        {
            return false;
        }

        // Before the first viewport is known the estimate paints the top.
        _ = _viewport.TryGetUnclamped(out double viewportTop, out double viewportHeight);

        // No drawing session or clear: retain the last good tiles.
        if (_host.RequestGeometryRecoveryIfRequired(snapshot))
            return false;

        double documentHeight = canvas.Height;
        double scale = _host.RasterizationScale;
        if (!double.IsFinite(documentHeight) || documentHeight <= 0 ||
            !DocumentTileGrid.TryCreate(
                canvas.Width,
                scale,
                out DocumentTileGrid grid,
                DocumentTileGrid.PixelHeightFor(documentHeight, scale)))
        {
            return false;
        }

        layer.Prepare(grid, documentHeight);
        // Velocity is sampled once per composition frame, never from a view
        // change inside one, so it measures whole frames.
        bool moving = !visibleOnly && TrackViewport(viewportTop);
        _plan.Update(grid, viewportTop, viewportHeight, documentHeight, _velocity,
            PlannedTileCount(grid, viewportHeight), LookAheadViewports * viewportHeight);
        layer.Evict(_plan);
        bool morePaintable = PaintPlannedTiles(layer, snapshot, grid, visibleOnly);
        return morePaintable || moving || _stableFrames < StableFramesBeforeIdle;
    }

    /// <summary>
    /// Paints the plan's stale tiles in order: every visible tile, then,
    /// unless <paramref name="visibleOnly"/>, measured look-ahead tiles until
    /// the frame budget is spent. Returns true when paintable work remains
    /// for a later frame.
    /// </summary>
    private bool PaintPlannedTiles(
        DocumentTileLayer layer,
        LayoutSnapshot snapshot,
        DocumentTileGrid grid,
        bool visibleOnly)
    {
        bool refreshDeferredSceneStatusAfterPaint = _deferredScenes.IsRefreshAfterPaintPending;
        long started = Stopwatch.GetTimestamp();
        long budget = (long)(PrefetchBudgetMilliseconds * Stopwatch.Frequency / 1000);
        bool queuedViewport = false;
        bool painted = false;
        bool morePaintable = false;
        for (int position = 0; position < _plan.Order.Count; position++)
        {
            int index = _plan.Order[position];
            if (!layer.NeedsPaint(index))
                continue;

            double tileTop = grid.TopOf(index);
            bool visible = _plan.IsVisible(index);
            if (visible)
            {
                if (!queuedViewport)
                {
                    _lazyLayout.QueueForViewport();
                    queuedViewport = true;
                }
            }
            else
            {
                // Visible tiles lead the plan's order, so the rest is look-ahead.
                if (visibleOnly)
                {
                    morePaintable = true;
                    break;
                }

                if (Stopwatch.GetTimestamp() - started > budget)
                {
                    morePaintable = true;
                    break;
                }

                // An unmeasured look-ahead tile would paint holes; its layout
                // commit marks it stale and requests a frame once measured.
                if (!snapshot.IsRealizationBandMeasured(new LazyLayoutBand(tileTop, tileTop + grid.TileHeight)))
                    continue;
            }

            if (!TryPaintTile(layer, snapshot, grid, index, tileTop))
            {
                // Background realization holds the layout. A visible tile
                // outranks look-ahead measurement: ask it to stop early.
                if (visible)
                    _lazyLayout.YieldToPaint();
                morePaintable = true;
                break;
            }

            painted = true;
        }

        if (painted)
            OnTilePainted(refreshDeferredSceneStatusAfterPaint);
        return morePaintable;
    }

    /// <summary>
    /// Paints one tile of <paramref name="snapshot"/>. Returns false, leaving
    /// the tile's pixels, while background realization owns the layout.
    /// </summary>
    private bool TryPaintTile(
        DocumentTileLayer layer,
        LayoutSnapshot snapshot,
        DocumentTileGrid grid,
        int index,
        double tileTop)
    {
        DocumentTileSurface tile = layer.Acquire(index);
        if (!snapshot.TryBeginPaint())
            return false;

        try
        {
            using CanvasDrawingSession ds = CanvasComposition.CreateDrawingSession(
                tile.Surface, tile.PixelBounds, (float)(96.0 * grid.Scale));
            // Clear to the opaque theme color so a theme change fully
            // overwrites old tile content.
            ds.Clear(_background);
            // Force grayscale text anti-aliasing. ClearType is colour-aware:
            // the same glyph rendered onto a white background versus an
            // alpha-blended selection-tinted background produces subtly
            // different sub-pixel RGB values. Switching to grayscale makes
            // glyph edges background-independent.
            ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
            ds.Transform = Matrix3x2.CreateTranslation(0, -(float)tileTop);
            // Selection/link state is rendered by the dedicated
            // viewport-bounded adorner. Never bake it into document tiles:
            // changing a selection must not repaint immutable content.
            snapshot.Paint(ds, new Rect(0, tileTop, grid.Width, grid.TileHeight));
        }
        finally
        {
            snapshot.EndPaint();
        }

        layer.MarkPainted(index, tile);
        return true;
    }

    /// <summary>
    /// Samples the viewport once per frame. Returns true while it moves, and
    /// keeps an exponentially smoothed velocity for directional look-ahead.
    /// </summary>
    private bool TrackViewport(double viewportTop)
    {
        long now = Stopwatch.GetTimestamp();
        bool moved = false;
        if (!double.IsNaN(_lastViewportTop))
        {
            double elapsed = (now - _lastViewportTimestamp) / (double)Stopwatch.Frequency;
            moved = Math.Abs(viewportTop - _lastViewportTop) > 0.5;
            if (elapsed > VelocityResetSeconds)
                _velocity = 0;
            else if (elapsed > 0)
                _velocity = (_velocity + (viewportTop - _lastViewportTop) / elapsed) / 2;
        }

        _lastViewportTop = viewportTop;
        _lastViewportTimestamp = now;
        _stableFrames = moved ? 0 : _stableFrames + 1;
        return moved;
    }

    private static int PlannedTileCount(DocumentTileGrid grid, double viewportHeight)
    {
        int visible = (int)Math.Ceiling(viewportHeight / grid.TileHeight) + 1;
        long affordable = TileBudgetBytes / Math.Max(1, grid.TileBytes);
        long wanted = (long)visible * (1 + LookAheadViewports);
        return (int)Math.Clamp(Math.Min(affordable, wanted), visible, MaximumTiles);
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded)
            return;

        double scale = sender.RasterizationScale;
        if (!double.IsNaN(_attachedScale) && Math.Abs(scale - _attachedScale) > 1e-6)
        {
            // Layout snaps text and images to device pixels, so a new scale
            // needs new layout as well as new tiles.
            _rebuild.Request(RebuildReason.Full);
        }

        _attachedScale = scale;
        RequestFrame();
    }

    private void OnTilePainted(bool refreshDeferredSceneStatus)
    {
        if (refreshDeferredSceneStatus)
            _deferredScenes.RefreshAfterSuccessfulPaintIfNeeded();
        if (Interlocked.Exchange(ref _deferredSceneProbePending, 0) != 0 &&
            !_lifetime.IsDisposed && !_lifetime.IsUnloaded)
        {
            // A Mermaid or Math scene that scrolled into a band that was
            // still being measured was never scheduled; its source fallback
            // stayed "Loading" forever. Re-probe once the band has painted.
            _host.DispatcherQueue?.TryEnqueue(_host.ScheduleVisibleDeferredScenes);
        }
        _deviceRecovery.NotePainted();
        if (_published.Generation == _rebuild.Generation &&
            _firstViewportPipelineStart != 0)
        {
            long pipelineStartTimestamp = _firstViewportPipelineStart;
            long sourceUtf16Bytes = _firstViewportSourceUtf16Bytes;
            long generation = _published.Generation;
            _firstViewportPipelineStart = 0;
            MarkdownPerformanceEventSource.Log.FirstViewportPresented(
                generation,
                sourceUtf16Bytes,
                Stopwatch.GetTimestamp() - pipelineStartTimestamp);
        }
    }
}
