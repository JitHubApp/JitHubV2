using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Measures the top-level roots that newly enter a lazy realization band,
/// publishes their measured bits, and restores them when the pass fails.
/// Every member runs under the snapshot's layout gate.
/// </summary>
internal sealed class LazyBandMeasurement
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly LayoutPlanCandidateIndex _planCandidates;

    internal LazyBandMeasurement(LayoutSnapshotGeometry geometry, LayoutPlanCandidateIndex planCandidates)
    {
        _geometry = geometry;
        _planCandidates = planCandidates;
    }

    /// <summary>
    /// Measures every unmeasured root in the band, reflowing after each batch
    /// until no further root enters it.
    /// </summary>
    internal void MeasureBandNoLock(LazyBandRealizationPass pass, LazyLayoutBand realizationBand)
    {
        float contentWidth = _geometry.GetContentWidthNoLock();
        while (true)
        {
            pass.CancellationToken.ThrowIfCancellationRequested();
            List<int>? newlyMeasured = MeasureNewlyVisibleRootsNoLock(
                pass, realizationBand, contentWidth, out int firstAffectedOrdinal, out bool geometryUnchanged);
            if (newlyMeasured is null)
                break;

            foreach (int ordinal in newlyMeasured)
            {
                // Keep rollback ownership before publishing the bit;
                // growing this list can fail without changing the mask.
                (pass.MeasuredOrdinals ??= []).Add(ordinal);
                _geometry.MeasuredTopLevelBlocks![ordinal] = true;
                _geometry.MeasuredTopLevelBlockCount++;
                _planCandidates.AddMeasuredLayoutPlanRootOrdinalNoLock(ordinal);
            }

            // Recompute all candidate ordinals after changed heights
            // shift downstream blocks. The next pass measures every
            // previously unmeasured block newly entering the band.
            if (!geometryUnchanged)
                _geometry.ReflowNoLock(incrementRevision: false, firstOrdinal: firstAffectedOrdinal);
        }
    }

    /// <summary>
    /// Records shared height estimates and advances the revision once every
    /// visible native resource has been prepared.
    /// </summary>
    internal void CommitMeasuredOrdinalsNoLock(LazyBandRealizationPass pass)
    {
        if (pass.MeasuredOrdinals is { Count: > 0 } measuredOrdinals)
        {
            foreach (int ordinal in measuredOrdinals)
            {
                _geometry.SharedLayoutMetrics?.RecordHeight(
                    ordinal,
                    (float)_geometry.Blocks[ordinal].Bounds.Height);
            }
            _geometry.LayoutRevision = pass.OldRevision + 1;
            _geometry.PreserveLazyReflowContinuationRevisionNoLock(pass.OldRevision, _geometry.LayoutRevision);
        }
    }

    /// <summary>Clears the measured bits and plan ordinals this pass published.</summary>
    internal void RestoreMeasuredOrdinalsNoLock(LazyBandRealizationPass pass)
    {
        if (pass.MeasuredOrdinals is { Count: > 0 } measuredOrdinals)
        {
            foreach (int ordinal in measuredOrdinals)
                _geometry.MeasuredTopLevelBlocks![ordinal] = false;
            _geometry.MeasuredTopLevelBlockCount = pass.OldMeasuredTopLevelBlockCount;
            _planCandidates.RemoveUnmeasuredLayoutPlanRootOrdinalsNoLock();
            _planCandidates.MeasuredRootOrdinalsAreSorted =
                pass.OldMeasuredLayoutPlanRootOrdinalsAreSorted;
        }
    }

    private List<int>? MeasureNewlyVisibleRootsNoLock(
        LazyBandRealizationPass pass,
        LazyLayoutBand realizationBand,
        float contentWidth,
        out int firstAffectedOrdinal,
        out bool geometryUnchanged)
    {
        var range = _geometry.FindViewportRangeNoLock(realizationBand.Top, realizationBand.Bottom);
        List<int>? newlyMeasured = null;
        firstAffectedOrdinal = _geometry.Blocks.Count;
        geometryUnchanged = _geometry.CurrentPlacementGenerationNoLock().IsDocumentOrder &&
            !pass.PriorPaintOnlyGeometryTainted;
        for (int i = range.Start; i < range.End; i++)
        {
            pass.CancellationToken.ThrowIfCancellationRequested();
            int ordinal = _geometry.CurrentPlacementGenerationNoLock().GetBlockOrdinal(i);
            BlockBox block = _geometry.Blocks[ordinal];
            LayoutRootPlacement committedPlacement = _geometry.CurrentPlacementGenerationNoLock().GetPlacement(ordinal);
            if (_geometry.MeasuredTopLevelBlocks![ordinal] ||
                !realizationBand.Intersects(committedPlacement.Top, committedPlacement.Bottom))
            {
                continue;
            }

            // Measure resets most block bounds to local (0,0).
            // Preserve the candidate's old top until reflow assigns
            // its authoritative position after new heights settle.
            Rect priorBounds = block.Bounds;
            Thickness priorMargin = block.Margin;
            bool priorFlowWithPrevious = ordinal > 0 &&
                StackBox.SharesSafeHtmlInlineFlow(_geometry.Blocks[ordinal - 1], block);
            bool priorFlowWithNext = ordinal < _geometry.Blocks.Count - 1 &&
                StackBox.SharesSafeHtmlInlineFlow(block, _geometry.Blocks[ordinal + 1]);
            // A factory/Arrange can mutate then throw before this
            // batch gets copied into measuredOrdinals. Rollback must
            // include that attempted root, not just completed ones.
            pass.FirstMeasureAttempted = Math.Min(pass.FirstMeasureAttempted, ordinal);
            _geometry.PaintOnlyGeometryTainted = true;
            block.Measure(contentWidth);
            block.Arrange(
                (float)_geometry.DocumentPadding.Left,
                (float)priorBounds.Top,
                contentWidth);
            bool flowWithPrevious = ordinal > 0 &&
                StackBox.SharesSafeHtmlInlineFlow(_geometry.Blocks[ordinal - 1], block);
            bool flowWithNext = ordinal < _geometry.Blocks.Count - 1 &&
                StackBox.SharesSafeHtmlInlineFlow(block, _geometry.Blocks[ordinal + 1]);
            geometryUnchanged &= LayoutSnapshotGeometry.IsFiniteBounds(priorBounds) &&
                double.IsFinite(priorBounds.Right) && double.IsFinite(priorBounds.Bottom) &&
                priorBounds.Width >= 0 && priorBounds.Height >= 0 &&
                block.Bounds.Equals(priorBounds) &&
                block.Margin.Equals(priorMargin) &&
                flowWithPrevious == priorFlowWithPrevious &&
                flowWithNext == priorFlowWithNext;
            firstAffectedOrdinal = Math.Min(firstAffectedOrdinal,
                flowWithPrevious != priorFlowWithPrevious ? Math.Max(0, ordinal - 1) : ordinal);
            (newlyMeasured ??= []).Add(ordinal);
        }
        return newlyMeasured;
    }
}
