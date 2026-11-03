using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Speculative-state capture, rollback and superseded-layout retirement for
/// <see cref="InlineContainerBox.Measure(float)"/>. These steps work only on
/// the values the measure captured; restoring the box's own published fields
/// stays with the box.
/// </summary>
internal static class InlineMeasureRollback
{
    /// <summary>
    /// Captures the local bounds of every inline vector scene run so a failed
    /// re-layout can put them back. Returns null when there is none.
    /// </summary>
    /// <param name="runs">The box's runs, in ordinal order.</param>
    internal static InlineScenePlacementRollbackState[]? CaptureScenePlacements(SingletonMutableList<InlineRun> runs)
    {
        InlineScenePlacementRollbackState[]? previousScenePlacements = null;
        int vectorCount = 0;
        foreach (InlineRun run in runs)
        {
            if (run is InlineVectorSceneRun)
                vectorCount++;
        }

        if (vectorCount > 0)
        {
            previousScenePlacements = new InlineScenePlacementRollbackState[vectorCount];
            int vectorIndex = 0;
            foreach (InlineRun run in runs)
            {
                if (run is InlineVectorSceneRun vector)
                {
                    previousScenePlacements[vectorIndex++] = new InlineScenePlacementRollbackState(
                        vector,
                        vector.LocalBounds);
                }
            }
        }

        return previousScenePlacements;
    }

    /// <summary>
    /// Rolls back speculative atomic-object measures and scene placements,
    /// then hands the unpublished native candidate to its reserved owner.
    /// Call only after the box has restored its published fields. Cleanup
    /// failures are attached to <paramref name="measureFailure"/> and never
    /// replace it.
    /// </summary>
    /// <param name="measureFailure">The exception that ended the measure.</param>
    /// <param name="changedInlineMeasures">Atomic-object measures changed by the attempt.</param>
    /// <param name="previousScenePlacements">Scene placements captured before the attempt.</param>
    /// <param name="replacementLayout">The unpublished native candidate, if one was created.</param>
    /// <param name="replacementLayoutRetirementRecord">The candidate's reserved native owner.</param>
    /// <param name="previousLayout">The layout that was published before the attempt.</param>
    internal static void RollBackSpeculativeMeasure(
        Exception measureFailure,
        List<InlineMeasureRollbackState>? changedInlineMeasures,
        InlineScenePlacementRollbackState[]? previousScenePlacements,
        CanvasTextLayout? replacementLayout,
        NativeResourceRetirementRecord? replacementLayoutRetirementRecord,
        CanvasTextLayout? previousLayout)
    {
        Exception? cleanupFailure = null;
        try
        {
            if (changedInlineMeasures is not null)
                RestoreInlineMeasures(changedInlineMeasures, ref cleanupFailure);

            if (previousScenePlacements is not null)
                RestoreScenePlacements(previousScenePlacements, ref cleanupFailure);
        }
        finally
        {
            // Restore the published state before releasing the unpublished
            // native candidate. This finally is deliberately around all
            // rollback/aggregation work: the pre-reserved native owner is
            // handed off even if collecting a rollback failure runs out of
            // memory after a native Dispose failure.
            if (replacementLayout is not null && !ReferenceEquals(replacementLayout, previousLayout))
            {
                Exception? layoutCleanupFailure = InlineNativeLayoutRetirement.RetireNativeLayout(
                    ref replacementLayout,
                    replacementLayoutRetirementRecord,
                    "disposing a failed inline-container measure candidate");
                cleanupFailure = InlineNativeLayoutRetirement.CombineFailures(
                    "rolling back a failed inline-container measure",
                    cleanupFailure,
                    layoutCleanupFailure);
            }
            else if (replacementLayoutRetirementRecord is not null)
            {
                replacementLayoutRetirementRecord.Publish();
            }
        }

        if (cleanupFailure is not null)
        {
            try
            {
                measureFailure.Data["MarkdownRenderer.InlineContainerBox.MeasureCleanupFailure"] = cleanupFailure;
            }
            catch
            {
                // Failure metadata must not replace the original exception.
            }
        }
    }

    // Each restore runs even when an earlier one fails; failures aggregate.
    private static void RestoreInlineMeasures(
        List<InlineMeasureRollbackState> changedInlineMeasures,
        ref Exception? cleanupFailure)
    {
        foreach (InlineMeasureRollbackState state in changedInlineMeasures)
        {
            try
            {
                state.Restore();
            }
            catch (Exception exception)
            {
                cleanupFailure = InlineNativeLayoutRetirement.CombineFailures(
                    "rolling back speculative inline measurements",
                    cleanupFailure,
                    exception);
            }
        }
    }

    private static void RestoreScenePlacements(
        InlineScenePlacementRollbackState[] previousScenePlacements,
        ref Exception? cleanupFailure)
    {
        foreach (InlineScenePlacementRollbackState state in previousScenePlacements)
        {
            try
            {
                state.Run.SetLocalBounds(state.LocalBounds);
            }
            catch (Exception exception)
            {
                cleanupFailure = InlineNativeLayoutRetirement.CombineFailures(
                    "rolling back inline scene placements",
                    cleanupFailure,
                    exception);
            }
        }
    }

    /// <summary>
    /// Retires the primary and selection layouts superseded by a published
    /// measure. A selection layout that aliases the primary layout shares its
    /// native release; only a distinct empty owner is released separately.
    /// </summary>
    /// <param name="previousLayout">The superseded primary layout.</param>
    /// <param name="previousLayoutRetirementRecord">Its reserved native owner.</param>
    /// <param name="previousSelectionLayout">The superseded selection layout.</param>
    /// <param name="previousSelectionLayoutRetirementRecord">Its reserved native owner.</param>
    /// <returns>The combined cleanup failure, or null.</returns>
    internal static Exception? RetireSupersededLayouts(
        CanvasTextLayout? previousLayout,
        NativeResourceRetirementRecord? previousLayoutRetirementRecord,
        CanvasTextLayout? previousSelectionLayout,
        NativeResourceRetirementRecord? previousSelectionLayoutRetirementRecord)
    {
        bool previousLayoutsAlias = ReferenceEquals(previousLayout, previousSelectionLayout);
        Exception? layoutCleanupFailure = InlineNativeLayoutRetirement.RetireNativeLayout(
            ref previousLayout,
            previousLayoutRetirementRecord,
            "retiring a superseded inline layout");
        Exception? selectionCleanupFailure = previousLayoutsAlias
            ? ReferenceEquals(previousLayoutRetirementRecord, previousSelectionLayoutRetirementRecord)
                ? null
                : InlineNativeLayoutRetirement.ReleaseEmptyNativeRecord(previousSelectionLayoutRetirementRecord)
            : InlineNativeLayoutRetirement.RetireNativeLayout(
                ref previousSelectionLayout,
                previousSelectionLayoutRetirementRecord,
                "retiring a superseded inline selection layout");
        return InlineNativeLayoutRetirement.CombineFailures(
            "retiring superseded inline layouts",
            layoutCleanupFailure,
            selectionCleanupFailure);
    }
}
