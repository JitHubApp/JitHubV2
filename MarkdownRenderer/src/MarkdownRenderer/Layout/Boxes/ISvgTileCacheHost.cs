using System;
using MarkdownRenderer.Images;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The image box as seen by its <see cref="SvgTileCache"/>: the accepted and
/// parking plans and the parked state it owns under its image-work lock, the
/// SVG host's visual-demand epoch, and the box's publication and cleanup paths.
/// </summary>
internal interface ISvgTileCacheHost
{
    /// <summary>The accepted tile plan, read without a lock.</summary>
    SvgTileRenderPlan? AcceptedSvgTilePlan { get; }

    /// <summary>The plan a pressure park is detaching, read without a lock.</summary>
    SvgTileRenderPlan? SvgParkingPlan { get; }

    /// <summary>Whether the box's SVG document is parked under memory pressure.</summary>
    bool IsSvgPressureParked { get; }

    /// <summary>The box's latest visual-demand epoch.</summary>
    long SvgHostVisualDemandEpoch { get; }

    /// <summary>Advances the visual-demand epoch, saturating at its maximum, and returns it.</summary>
    long AdvanceSvgHostVisualDemandEpoch();

    /// <summary>Whether <paramref name="plan"/> is the live box's accepted plan for its current document.</summary>
    bool IsAcceptedSvgTilePlan(SvgTileRenderPlan plan);

    /// <summary>Queues <paramref name="publish"/> on the UI thread, or runs <paramref name="onDropped"/>.</summary>
    void PublishOnUiThread(Action publish, Action? onDropped);

    /// <summary>Runs a cleanup action, recording a failure for the retirement barrier.</summary>
    void TryImageCleanup(Action cleanup);

    /// <summary>Records a retirement failure for the box's retirement barrier.</summary>
    void RecordImageRetirementFailure(Exception exception);

    /// <summary>Paints a tile-grid hole that no fallback tile covers; the cache holds its tile lock.</summary>
    void FillSvgTileGapWithoutTiles(CanvasDrawingSession ds, Rect destination);
}
