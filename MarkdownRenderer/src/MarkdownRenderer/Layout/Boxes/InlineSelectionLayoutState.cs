using System;
using System.Runtime.ExceptionServices;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The neutral selection-foreground layout of one <see cref="InlineContainerBox"/>
/// and its reserved native-retirement owner. The box owns it by value, so a box
/// that never paints a selection carries no extra allocation; it is mutated in
/// place through the box's field and exchanged whole by measured-placement swaps.
/// </summary>
internal struct InlineSelectionLayoutState
{
    private CanvasTextLayout? _layout;
    private NativeResourceRetirementRecord? _retirementRecord;
    private Color _color;
    private float _width;

    internal readonly CanvasTextLayout? Layout => _layout;

    internal readonly NativeResourceRetirementRecord? RetirementRecord => _retirementRecord;

    internal readonly Color Color => _color;

    internal readonly float Width => _width;

    /// <summary>
    /// Forgets the published layout and its owner after the caller has taken
    /// custody of both. The last selection color is kept.
    /// </summary>
    internal void Detach()
    {
        _layout = null;
        _retirementRecord = null;
        _width = 0;
    }

    /// <summary>Restores a previously captured selection layout after a failed measure.</summary>
    /// <param name="layout">The captured layout.</param>
    /// <param name="retirementRecord">The captured native-retirement owner.</param>
    /// <param name="color">The captured selection color.</param>
    /// <param name="width">The captured layout width.</param>
    internal void Restore(
        CanvasTextLayout? layout,
        NativeResourceRetirementRecord? retirementRecord,
        Color color,
        float width)
    {
        _layout = layout;
        _retirementRecord = retirementRecord;
        _color = color;
        _width = width;
    }

    /// <summary>
    /// Returns the cached neutral selection layout for <paramref name="box"/>,
    /// replacing it when the selection color or measured width changed.
    /// </summary>
    /// <param name="box">The inline container that owns this state.</param>
    /// <param name="color">The selection foreground color.</param>
    /// <param name="style">The container's resolved style.</param>
    internal CanvasTextLayout Ensure(InlineContainerBox box, Color color, ElementStyle style)
    {
        if (!InlineMeasuredPlacementCapture.HasValidNativeLayoutCustody(box))
            throw new InvalidOperationException("Cannot replace an inline selection layout without its reserved native-retirement owner.");

        box.EnsureBuffer();
        // The neutral clone covers the full native layout. The current range
        // only supplies paint clips and decorations, so endpoint movement must
        // not recreate a native layout or change its retirement owner.
        if (_layout is not null &&
            _color == color &&
            Math.Abs(_width - box.LastMeasuredWidth) <= 0.5f)
        {
            return _layout;
        }

        using var formatLease = box.CreateTextFormatLease(style, box.WordWrapping, box.TextAlignment);
        var padding = box.GetEffectivePadding(style);
        float horizontalPadding = (float)(padding.Left + padding.Right);
        NativeResourceRetirementRecord candidateRetirementRecord = box.NativeRetirement.Reserve(
            resourceGroupCount: 0,
            temporarySlotCount: 1,
            disposer: box.NativeDisposer);
        CanvasTextLayout? candidateLayout = null;
        Exception? candidateFailure = null;
        try
        {
            candidateLayout = CanvasTextLayoutFactory.Create(
                box.Context.ResourceCreator,
                box.LayoutBuffer,
                formatLease.Format,
                Math.Max(1f, box.LastMeasuredWidth - horizontalPadding),
                float.MaxValue,
                box.Context.LayoutDiagnostics);
            candidateRetirementRecord.SetTemporary(0, candidateLayout);
            candidateLayout.Options = CanvasDrawTextOptions.EnableColorFont;
            // Selection is painted synchronously by a Win2D Draw callback against
            // an already-committed snapshot. A newer layout may have cancelled the
            // snapshot's build token, but cancellation must never escape Draw.
            InlineRunStyleApplier.ApplyRunStyles(box, candidateLayout, applyColors: false, observeCancellation: false);
            float selectionLayoutWidth = Math.Max(1f, box.LastMeasuredWidth - horizontalPadding);
            float inlineMetricScale = InlineAtomicObjectLayout.ApplyContentAwareLineSpacing(
                box,
                candidateLayout,
                style,
                selectionLayoutWidth);
            InlineAtomicObjectLayout.ApplyEmbedSpacing(
                box,
                candidateLayout,
                observeCancellation: false,
                inverseMetricScale: inlineMetricScale,
                maxInlineWidth: selectionLayoutWidth,
                computeInlineImageMetrics: false);
        }
        catch (Exception exception)
        {
            candidateFailure = exception;
        }

        if (candidateFailure is not null)
        {
            Exception? cleanupFailure = InlineNativeLayoutRetirement.RetireNativeLayout(
                ref candidateLayout,
                candidateRetirementRecord,
                "disposing a failed inline selection-layout candidate");
            Exception combined = InlineNativeLayoutRetirement.CombineFailures(
                "building an inline selection layout",
                candidateFailure,
                cleanupFailure)!;
            ExceptionDispatchInfo.Capture(combined).Throw();
        }

        CanvasTextLayout? previousSelectionLayout = _layout;
        NativeResourceRetirementRecord? previousSelectionRetirementRecord =
            _retirementRecord;
        if (previousSelectionLayout is not null)
        {
            try
            {
                if (previousSelectionRetirementRecord is null)
                    previousSelectionLayout.Dispose();
                else
                    previousSelectionRetirementRecord.Disposer(previousSelectionLayout);
            }
            catch (Exception previousCleanupFailure)
            {
                Exception? candidateCleanupFailure = InlineNativeLayoutRetirement.RetireNativeLayout(
                    ref candidateLayout,
                    candidateRetirementRecord,
                    "disposing a superseded inline selection-layout candidate");
                Exception combined = InlineNativeLayoutRetirement.CombineFailures(
                    "replacing an inline selection layout",
                    previousCleanupFailure,
                    candidateCleanupFailure)!;
                ExceptionDispatchInfo.Capture(combined).Throw();
            }
        }

        // The previous native owner was released successfully. Clear its box
        // reference before publishing the now-empty preallocated record, then
        // commit the fully styled candidate in one non-throwing field group.
        _layout = null;
        _retirementRecord = null;
        Exception? previousRecordFailure = InlineNativeLayoutRetirement.ReleaseEmptyNativeRecord(previousSelectionRetirementRecord);
        if (previousRecordFailure is not null)
        {
            Exception? candidateCleanupFailure = InlineNativeLayoutRetirement.RetireNativeLayout(
                ref candidateLayout,
                candidateRetirementRecord,
                "disposing an uncommitted inline selection-layout candidate");
            Exception combined = InlineNativeLayoutRetirement.CombineFailures(
                "retiring the previous inline selection layout",
                previousRecordFailure,
                candidateCleanupFailure)!;
            ExceptionDispatchInfo.Capture(combined).Throw();
        }

        _layout = candidateLayout;
        _retirementRecord = candidateRetirementRecord;
        _color = color;
        _width = box.LastMeasuredWidth;
        return candidateLayout!;
    }
}
