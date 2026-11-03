using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Places inline vector scene runs on a native text layout and captures their
/// scalar character-region geometry for progressive scene residency.
/// </summary>
internal static class InlineVectorScenePlacement
{
    /// <summary>
    /// Places every inline vector scene run on its laid-out baseline in
    /// <paramref name="layout"/> and, for progressive native residency, returns
    /// the scalar geometry captured while placing them.
    /// </summary>
    /// <param name="runs">The owning box's runs, in ordinal order.</param>
    /// <param name="layout">The candidate native layout being measured.</param>
    /// <param name="lineMetrics">The candidate layout's line metrics, if readable.</param>
    /// <param name="captureGeometry">Whether the box uses progressive native scene residency.</param>
    /// <param name="vectorState">The box's vector-scene state, which counts region queries.</param>
    internal static InlineSceneGeometry[] UpdatePlacements(
        SingletonMutableList<InlineRun> runs,
        CanvasTextLayout layout,
        CanvasLineMetrics[]? lineMetrics,
        bool captureGeometry,
        InlineVectorSceneState? vectorState)
    {
        List<InlineSceneGeometry>? sceneGeometry = null;
        int cumulative = 0;
        foreach (InlineRun run in runs)
        {
            int length = run.TextLength;
            if (run is InlineVectorSceneRun vector && length > 0)
            {
                CanvasTextLayoutRegion[]? regions = layout.GetCharacterRegions(cumulative, length);
                RecordRegionQuery(vectorState);
                if (captureGeometry)
                    (sceneGeometry ??= []).Add(new InlineSceneGeometry(vector, regions));

                if (regions is { Length: > 0 })
                {
                    CanvasTextLayoutRegion region = regions[0];
                    Rect bounds = region.LayoutBounds;
                    CanvasLineMetrics metric = InlineContainerBox.FindLineMetrics(lineMetrics, region);
                    float baseline = metric.Baseline > 0
                        ? metric.Baseline
                        : (float)bounds.Height * 0.8f;
                    double y = bounds.Y + baseline - vector.DesiredBaseline;
                    vector.SetLocalBounds(new Rect(
                        bounds.X,
                        y,
                        Math.Min(vector.DesiredWidth, bounds.Width),
                        vector.DesiredHeight));
                }
            }
            cumulative += length;
        }

        return sceneGeometry?.ToArray() ?? Array.Empty<InlineSceneGeometry>();
    }

    /// <summary>
    /// Captures the scalar character regions of every inline vector scene run
    /// in a current layout, without moving any run.
    /// </summary>
    /// <param name="runs">The owning box's runs, in ordinal order.</param>
    /// <param name="layout">The box's current native layout.</param>
    /// <param name="cancellationToken">Observed before each run.</param>
    /// <param name="vectorState">The box's vector-scene state, which counts region queries.</param>
    internal static InlineSceneGeometry[] CaptureGeometry(
        SingletonMutableList<InlineRun> runs,
        CanvasTextLayout layout,
        CancellationToken cancellationToken,
        InlineVectorSceneState? vectorState)
    {
        List<InlineSceneGeometry>? sceneGeometry = null;
        int cumulative = 0;
        foreach (InlineRun run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = run.TextLength;
            if (run is InlineVectorSceneRun vector && length > 0)
            {
                CanvasTextLayoutRegion[]? regions = layout.GetCharacterRegions(cumulative, length);
                RecordRegionQuery(vectorState);
                (sceneGeometry ??= []).Add(new InlineSceneGeometry(vector, regions));
            }

            if (length > int.MaxValue - cumulative)
                throw new InvalidOperationException("Inline text geometry exceeded the supported layout range.");
            cumulative += length;
        }

        return sceneGeometry?.ToArray() ?? Array.Empty<InlineSceneGeometry>();
    }

    /// <summary>
    /// Returns the pending geometry published for the current scene generation.
    /// A box without vector runs reports an empty but current result.
    /// </summary>
    /// <param name="vectorState">The box's vector-scene state, if any.</param>
    /// <param name="currentGeneration">The box's current scene generation, or 0.</param>
    /// <param name="generation">The current scene generation.</param>
    /// <param name="geometry">The pending geometry for that generation.</param>
    internal static bool TryGetCurrentGeometry(
        InlineVectorSceneState? vectorState,
        long currentGeneration,
        out long generation,
        out InlineSceneGeometry[] geometry)
    {
        generation = currentGeneration;
        geometry = Array.Empty<InlineSceneGeometry>();
        if (generation == 0)
            return false;
        if (vectorState is null || vectorState.VectorSceneRunCount == 0)
            return true;
        if (vectorState.PendingSceneGeometryGeneration != generation)
            return false;

        geometry = vectorState.PendingSceneGeometry;
        return true;
    }

    /// <summary>
    /// Captures scalar regions for the current layout, without moving any run.
    /// </summary>
    /// <param name="runs">The owning box's runs, in ordinal order.</param>
    /// <param name="layout">The box's current native layout, if measured.</param>
    /// <param name="vectorState">The box's vector-scene state, if any.</param>
    /// <param name="currentGeneration">The box's current scene generation, or 0.</param>
    /// <param name="cancellationToken">Observed before each run.</param>
    /// <param name="generation">The current scene generation.</param>
    /// <param name="geometry">The captured geometry.</param>
    internal static bool TryCaptureCurrentGeometry(
        SingletonMutableList<InlineRun> runs,
        CanvasTextLayout? layout,
        InlineVectorSceneState? vectorState,
        long currentGeneration,
        CancellationToken cancellationToken,
        out long generation,
        out InlineSceneGeometry[] geometry)
    {
        generation = currentGeneration;
        geometry = Array.Empty<InlineSceneGeometry>();
        if (generation == 0 || layout is null)
            return false;
        if (vectorState is null || vectorState.VectorSceneRunCount == 0)
            return true;

        geometry = CaptureGeometry(runs, layout, cancellationToken, vectorState);
        return true;
    }

    /// <summary>
    /// Reads the character regions of a buffer range in the box's current
    /// layout and the snapped text-origin Y used to place them.
    /// </summary>
    /// <param name="box">The measured inline container.</param>
    /// <param name="start">The buffer offset of the range.</param>
    /// <param name="length">The range length.</param>
    /// <param name="regions">The laid-out regions.</param>
    /// <param name="originY">The snapped text-origin Y.</param>
    internal static bool TryGetRunCharacterRegions(
        InlineContainerBox box,
        int start,
        int length,
        out CanvasTextLayoutRegion[]? regions,
        out double originY)
    {
        regions = null;
        originY = 0;
        CanvasTextLayout? layout = box.TextLayout;
        if (start < 0 || length <= 0 || layout is null || box.IsBufferDirty ||
            start > box.LayoutBuffer.Length - length)
        {
            return false;
        }

        CanvasTextLayoutRegion[]? currentRegions = layout.GetCharacterRegions(start, length);
        RecordRegionQuery(box.VectorSceneStateHolder);
        if (currentRegions is not { Length: > 0 })
            return false;

        var (_, snappedOriginY) = box.GetSnappedOrigin(box.GetContainerStyle());
        if (!double.IsFinite(snappedOriginY))
            return false;

        regions = currentRegions;
        originY = snappedOriginY;
        return true;
    }

    private static void RecordRegionQuery(InlineVectorSceneState? vectorState)
    {
        if (vectorState is not null)
            vectorState.SceneCharacterRegionQueryCount++;
    }
}
