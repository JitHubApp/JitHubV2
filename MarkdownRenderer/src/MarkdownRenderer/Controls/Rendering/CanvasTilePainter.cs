using System;
using System.Diagnostics;
using System.Threading;
using MarkdownRenderer.Controls.LazyLayout;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Readiness;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace MarkdownRenderer.Controls.Rendering;

/// <summary>
/// Paints the published snapshot into the Win2D canvas tiles that request
/// it. Owns the tile clear color published with each snapshot, the one-shot
/// first-viewport measurement, and the deferred scene re-probe that waits
/// for a painted band.
/// </summary>
internal sealed class CanvasTilePainter
{
    private readonly ICanvasTilePainterHost _host;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewSurface _surface;
    private readonly LazyLayoutRealization _lazyLayout;
    private readonly CanvasDeviceRecovery _deviceRecovery;
    private readonly RebuildScheduler _rebuild;
    private readonly DeferredSceneReadiness _deferredScenes;
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

    internal CanvasTilePainter(
        ICanvasTilePainterHost host,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewSurface surface,
        LazyLayoutRealization lazyLayout,
        CanvasDeviceRecovery deviceRecovery,
        RebuildScheduler rebuild,
        DeferredSceneReadiness deferredScenes)
    {
        _host = host;
        _lifetime = lifetime;
        _published = published;
        _surface = surface;
        _lazyLayout = lazyLayout;
        _deviceRecovery = deviceRecovery;
        _rebuild = rebuild;
        _deferredScenes = deferredScenes;
    }

    /// <summary>Publishes the clear color of the snapshot just committed.</summary>
    internal void PublishBackground(Color background) => _background = background;

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
    /// Paints the canvas tiles Win2D invalidated from the published snapshot,
    /// measuring paint work when performance measurement is enabled.
    /// </summary>
    internal void OnRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded || !_host.IsLoaded || !ReferenceEquals(sender, _surface.Canvas))
            return;

        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long started = measure ? Stopwatch.GetTimestamp() : 0;
        long allocatedBefore = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        try
        {
            PaintInvalidatedRegions(sender, args);
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsShutdownOrDisposed(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] ignored canvas paint during shutdown: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
        }
        finally
        {
            if (measure)
            {
                long allocated = Math.Max(
                    0,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                performance.PaintWork(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    Stopwatch.GetTimestamp() - started,
                    allocated);
            }
        }
    }

    private void PaintInvalidatedRegions(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long allocationCheckpoint = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        _lazyLayout.QueueForViewport();
        long schedulingBytes = measure
            ? Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocationCheckpoint)
            : 0;
        long platformSessionBytes = 0;
        long snapshotPaintBytes = 0;
        long interactivePaintBytes = 0;

        if (_published.Snapshot is not { } snapshot) return;
        if (_host.RequestGeometryRecoveryIfRequired(snapshot))
            return; // No drawing session/clear: retain the last good tiles.
        bool refreshDeferredSceneStatusAfterPaint = _deferredScenes.IsRefreshAfterPaintPending;
        bool paintedRegion = false;
        foreach (var region in args.InvalidatedRegions)
        {
            try
            {
                paintedRegion |= TryPaintRegion(
                    sender,
                    snapshot,
                    region,
                    measure,
                    ref platformSessionBytes,
                    ref snapshotPaintBytes,
                    ref interactivePaintBytes);
            }
            catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
            {
                _deviceRecovery.HandleDeviceLost(ex);
                return;
            }
        }
        if (paintedRegion)
            OnRegionPainted(refreshDeferredSceneStatusAfterPaint);
        if (measure)
        {
            performance.PaintAllocationBreakdown(
                MarkdownPerformanceEventSource.GetLogicalFrameId(),
                schedulingBytes,
                platformSessionBytes,
                snapshotPaintBytes,
                interactivePaintBytes);
        }
    }

    /// <summary>
    /// Paints one invalidated tile of <paramref name="snapshot"/>, adding the
    /// drawing session's allocations to the running totals. Returns false,
    /// leaving the tile's pixels, while background realization owns the layout.
    /// </summary>
    private bool TryPaintRegion(
        CanvasVirtualControl sender,
        LayoutSnapshot snapshot,
        Rect region,
        bool measure,
        ref long platformSessionBytes,
        ref long snapshotPaintBytes,
        ref long interactivePaintBytes)
    {
        long regionStart = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        bool painted = false;
        // The tile itself is the authoritative paint band; the ScrollViewer
        // viewport can lag behind CanvasVirtualControl's region requests.
        _lazyLayout.QueueForPaintRegion(region);
        if (!snapshot.TryBeginPaint())
        {
            // Do not create or clear the tile while background realization
            // owns the layout. Completion invalidates the canvas, so the
            // existing pixels remain visible without blocking this frame.
            return false;
        }

        try
        {
            long afterSetup = regionStart;
            long afterSnapshot = regionStart;
            long afterInteractive = regionStart;
            using (var ds = sender.CreateDrawingSession(region))
            {
                // Clear to the theme-appropriate background color so that switching between
                // light and dark mode (or any theme change) fully overwrites old tile content.
                // We use an opaque theme color rather than Colors.Transparent because
                // CanvasVirtualControl may not alpha-composite with the XAML compositor
                // depending on the DirectX swap-chain configuration of the platform; on
                // such configurations transparent pixels show as black rather than letting
                // the XAML background show through.
                ds.Clear(_background);
                // Force grayscale text anti-aliasing. ClearType is colour-aware:
                // the same glyph rendered onto a white background versus an
                // alpha-blended selection-tinted background produces subtly
                // different sub-pixel RGB values. Switching to grayscale makes
                // glyph edges background-independent.
                ds.TextAntialiasing = Microsoft.Graphics.Canvas.Text.CanvasTextAntialiasing.Grayscale;
                if (measure)
                    afterSetup = GC.GetAllocatedBytesForCurrentThread();
                snapshot.Paint(ds, region);
                if (measure)
                    afterSnapshot = GC.GetAllocatedBytesForCurrentThread();
                // Selection/link state is rendered by the dedicated
                // viewport-bounded image-source adorner. Never bake it
                // into document tiles: changing a selection must not
                // invalidate and repaint immutable document content.
                afterInteractive = afterSnapshot;
                painted = true;
            }
            if (measure)
            {
                long afterDispose = GC.GetAllocatedBytesForCurrentThread();
                platformSessionBytes += Math.Max(0, afterSetup - regionStart) +
                                        Math.Max(0, afterDispose - afterInteractive);
                snapshotPaintBytes += Math.Max(0, afterSnapshot - afterSetup);
                interactivePaintBytes += Math.Max(0, afterInteractive - afterSnapshot);
            }
        }
        finally
        {
            snapshot.EndPaint();
        }
        return painted;
    }

    private void OnRegionPainted(bool refreshDeferredSceneStatus)
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
