using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The primary native text layout of one <see cref="InlineContainerBox"/>:
/// the layout and its reserved retirement record, the script-baseline
/// geometry and line metrics derived from it, and the width it was measured
/// at. <see cref="Measure"/> replaces it speculatively and restores it on
/// failure.
/// </summary>
/// <remarks>
/// The box owns this by value in a private field and mutates it in place;
/// the only whole-value use is the no-throw measured-placement swap, which
/// exchanges two boxes' fields. A copy kept anywhere else would hold a native
/// layout without its custody, so it is never copied out.
/// </remarks>
internal struct InlineTextLayoutState
{
    private CanvasTextLayout? _layout;
    private NativeResourceRetirementRecord? _layoutRetirementRecord;
    private ScriptBaselineGeometry _scriptBaselineGeometry;
    private CanvasLineMetrics[]? _cachedLineMetrics;
    private bool _lineMetricsRead;
    private float _lastWidth;

    public InlineTextLayoutState()
    {
        _scriptBaselineGeometry = ScriptBaselineGeometry.Empty;
    }

    internal readonly CanvasTextLayout? Layout => _layout;

    internal readonly NativeResourceRetirementRecord? RetirementRecord => _layoutRetirementRecord;

    internal readonly ScriptBaselineGeometry ScriptBaselineGeometry => _scriptBaselineGeometry;

    internal readonly CanvasLineMetrics[]? CachedLineMetrics => _cachedLineMetrics;

    internal readonly bool HasReadLineMetrics => _lineMetricsRead;

    internal readonly float LastWidth => _lastWidth;

    /// <summary>Copies a placement source's measured width into an unmeasured candidate.</summary>
    /// <param name="source">The placement source's layout state.</param>
    internal void CopyLastWidthFrom(in InlineTextLayoutState source) => _lastWidth = source._lastWidth;

    /// <summary>
    /// Clears the layout and its record on dispose. The caller has read both
    /// and retires them after detaching every other box-owned reference.
    /// </summary>
    internal void DetachLayout()
    {
        _layout = null;
        _layoutRetirementRecord = null;
    }

    /// <summary>
    /// Measures <paramref name="box"/> at <paramref name="availableWidth"/>,
    /// replacing this layout only when its runs, atomic metrics or width
    /// changed, and rolls every speculative change back if measuring fails.
    /// </summary>
    /// <param name="box">The box that owns this state in its field.</param>
    /// <param name="availableWidth">The width offered by the parent.</param>
    /// <returns>The measured height, including margin and padding.</returns>
    internal float Measure(InlineContainerBox box, float availableWidth)
    {
        box.ThrowIfCancellationRequested();
        if (!InlineMeasuredPlacementCapture.HasValidNativeLayoutCustody(box))
            throw new InvalidOperationException("An inline text layout is missing its reserved native-retirement owner.");
        var style = box.GetContainerStyle();
        var padding = box.GetEffectivePadding(style);
        CanvasTextLayout? previousLayout = _layout;
        NativeResourceRetirementRecord? previousLayoutRetirementRecord = _layoutRetirementRecord;
        CanvasLineMetrics[]? previousLineMetrics = _cachedLineMetrics;
        bool previousLineMetricsRead = _lineMetricsRead;
        CanvasTextLayout? previousSelectionLayout = box.SelectionLayout;
        NativeResourceRetirementRecord? previousSelectionLayoutRetirementRecord =
            box.SelectionLayoutRetirementRecord;
        Color previousSelectionLayoutColor = box.SelectionLayoutColor;
        float previousSelectionLayoutWidth = box.SelectionLayoutWidth;
        string previousBuffer = box.LayoutBuffer;
        bool previousBufferDirty = box.IsBufferDirty;
        float previousWidth = _lastWidth;
        Rect previousBounds = box.Bounds;
        List<InlineMeasureRollbackState>? changedInlineMeasures = null;
        InlineScenePlacementRollbackState[]? previousScenePlacements = null;
        CanvasTextLayout? replacementLayout = null;
        NativeResourceRetirementRecord? replacementLayoutRetirementRecord = null;
        ScriptBaselineGeometry? replacementScriptBaselineGeometry = null;
        InlineSceneGeometry[]? replacementSceneGeometry = null;
        float[]? replacementInlineImageLineBaselines = null;
        bool publishInlineImageLineBaselines = false;
        float measuredHeight;
        // Measure with the same effective margin that Arrange/paint will use.
        // Resolving from the current style keeps remeasurement correct when a
        // theme changes authored margins, without publishing state before the
        // measurement succeeds.
        Thickness margin = box.UseContainerMargin ? box.ResolvePlacementMargin(style.Margin) : box.Margin;
        float horizontalPadding = (float)(padding.Left + padding.Right);
        float layoutWidth = Math.Max(1f, availableWidth - horizontalPadding);

        try
        {
            bool atomicMetricsChanged = InlineAtomicObjectLayout.MeasureAtomicInlineRuns(
                box,
                layoutWidth,
                style.FontSize,
                out changedInlineMeasures);
            if (atomicMetricsChanged)
            {
                InvalidateLineMetrics();
            }

            if (_layout is null || atomicMetricsChanged || box.IsBufferDirty ||
                Math.Abs(_lastWidth - availableWidth) > 0.5f)
            {
                // Only rebuild the run-content string when runs changed; skip on pure
                // width-change reflow so repeated window resizes don't re-allocate.
                box.BuildLayoutBufferIfDirty();
                InvalidateLineMetrics();
                if (previousLayout is not null)
                    previousScenePlacements = InlineMeasureRollback.CaptureScenePlacements(box.RunList);

                using var formatLease = box.CreateTextFormatLease(style, box.WordWrapping, box.TextAlignment);
                NativeResourceRetirementRecord candidateRetirementRecord =
                    box.NativeRetirement.Reserve(
                        resourceGroupCount: 0,
                        temporarySlotCount: 1,
                        disposer: box.NativeDisposer);
                replacementLayoutRetirementRecord = candidateRetirementRecord;
                CanvasTextLayout candidateLayout = CanvasTextLayoutFactory.Create(
                    box.Context.ResourceCreator,
                    box.LayoutBuffer,
                    formatLease.Format,
                    layoutWidth,
                    float.MaxValue,
                    box.Context.LayoutDiagnostics);
                candidateRetirementRecord.SetTemporary(0, candidateLayout);
                replacementLayout = candidateLayout;
                _layout = candidateLayout;
                _layoutRetirementRecord = candidateRetirementRecord;
                // CanvasTextLayout construction is one native call and cannot
                // be interrupted safely. Observe a superseding lazy-band
                // request immediately afterward, before walking/styling runs.
                box.ThrowIfCancellationRequested();
                // Enable DirectWrite color font path (Segoe UI Emoji / COLR-CPAL glyphs).
                candidateLayout.Options = CanvasDrawTextOptions.EnableColorFont;
                InlineRunStyleApplier.ApplyRunStyles(box, candidateLayout, applyColors: true);
                InlineRunStyleApplier.ApplyForegroundSpans(box, candidateLayout);
                float inlineMetricScale = InlineAtomicObjectLayout.ApplyContentAwareLineSpacing(box, candidateLayout, style, layoutWidth);
                InlineAtomicObjectLayout.ApplyEmbedSpacing(
                    box,
                    candidateLayout,
                    inverseMetricScale: inlineMetricScale,
                    maxInlineWidth: layoutWidth);
                replacementScriptBaselineGeometry = InlineScriptBaselineLayout.BuildScriptBaselineGeometry(box, style.FontSize);
                if (box.Context.ThemeSnapshot.MiddleAlignInlineImages &&
                    box.InlineImageEntries is { Count: > 0 })
                {
                    replacementInlineImageLineBaselines =
                        InlineObjectGeometry.BuildInlineImageLineBaselines(box, TryGetLineMetrics());
                    publishInlineImageLineBaselines = true;
                }
                replacementSceneGeometry = InlineVectorScenePlacement.UpdatePlacements(
                    box.RunList,
                    candidateLayout,
                    TryGetLineMetrics(),
                    box.UsesProgressiveNativeSceneResidency,
                    box.VectorSceneStateHolder);
                _lastWidth = availableWidth;
            }

            // Keep defensive recovery for an absent/stale baseline snapshot
            // on the measurement path, never on paint.
            else if (box.Context.ThemeSnapshot.MiddleAlignInlineImages &&
                box.InlineImageEntries is { Count: > 0 } &&
                (box.InlineImageLineBaselineByRunIndex is null ||
                    box.InlineImageLineBaselineByRunIndex.Length != box.RunList.Count))
            {
                replacementInlineImageLineBaselines =
                    InlineObjectGeometry.BuildInlineImageLineBaselines(box, TryGetLineMetrics());
                publishInlineImageLineBaselines = true;
            }

            var bounds = _layout!.LayoutBounds;
            float top = (float)(margin.Top + padding.Top);
            float bottom = (float)(margin.Bottom + padding.Bottom);
            ScriptBaselineGeometry scriptGeometry =
                replacementScriptBaselineGeometry ?? _scriptBaselineGeometry;
            measuredHeight = (float)bounds.Height + top + bottom +
                scriptGeometry.TopInset + scriptGeometry.BottomInset;
            // Do not retire the previously published native layout if the
            // candidate was superseded while placement metrics were computed.
            box.ThrowIfCancellationRequested();
            box.SetMeasuredBounds(new Rect(0, 0, availableWidth, measuredHeight));
        }
        catch (Exception measureFailure)
        {
            _layout = previousLayout;
            _layoutRetirementRecord = previousLayoutRetirementRecord;
            _cachedLineMetrics = previousLineMetrics;
            _lineMetricsRead = previousLineMetricsRead;
            box.RestoreSelectionLayout(
                previousSelectionLayout,
                previousSelectionLayoutRetirementRecord,
                previousSelectionLayoutColor,
                previousSelectionLayoutWidth);
            box.RestoreLayoutBuffer(previousBuffer, previousBufferDirty);
            _lastWidth = previousWidth;
            box.SetMeasuredBounds(previousBounds);

            InlineMeasureRollback.RollBackSpeculativeMeasure(
                measureFailure,
                changedInlineMeasures,
                previousScenePlacements,
                replacementLayout,
                replacementLayoutRetirementRecord,
                previousLayout);
            throw;
        }

        if (changedInlineMeasures is not null)
        {
            foreach (InlineMeasureRollbackState state in changedInlineMeasures)
                state.Commit();
        }

        if (publishInlineImageLineBaselines)
            box.PublishInlineImageLineBaselines(replacementInlineImageLineBaselines);

        if (replacementScriptBaselineGeometry is not null)
            _scriptBaselineGeometry = replacementScriptBaselineGeometry;

        if (box.UseContainerMargin)
            box.Margin = style.Margin;

        if (!ReferenceEquals(previousLayout, _layout))
        {
            if (box.VectorSceneStateHolder is { } vectorState)
            {
                long nextGeneration = unchecked(vectorState.SceneGeometryGeneration + 1);
                vectorState.SceneGeometryGeneration = nextGeneration == 0 ? 1 : nextGeneration;
                vectorState.PendingSceneGeometry =
                    replacementSceneGeometry ?? Array.Empty<InlineSceneGeometry>();
                vectorState.PendingSceneGeometryGeneration = box.UsesProgressiveNativeSceneResidency
                    ? vectorState.SceneGeometryGeneration
                    : 0;
            }
            box.DetachSelectionLayout();

            Exception? supersededCleanupFailure = InlineMeasureRollback.RetireSupersededLayouts(
                previousLayout,
                previousLayoutRetirementRecord,
                previousSelectionLayout,
                previousSelectionLayoutRetirementRecord);
            if (supersededCleanupFailure is not null)
                box.Context.RegisterRetirement(Task.FromException(supersededCleanupFailure));
        }

        return measuredHeight;
    }

    /// <summary>
    /// Returns the current layout's line metrics, reading them from the native
    /// layout at most once per layout; null when unavailable.
    /// </summary>
    internal CanvasLineMetrics[]? TryGetLineMetrics()
    {
        if (_lineMetricsRead)
            return _cachedLineMetrics;

        _lineMetricsRead = true;
        try
        {
            _cachedLineMetrics = _layout?.LineMetrics;
            return _cachedLineMetrics;
        }
        catch (NotSupportedException)
        {
            // Some WinRT/Release projections cannot marshal CanvasLineMetrics.
            // Text still renders; decoration placement can use region bounds.
            return null;
        }
    }

    /// <summary>Drops the cached line metrics so the next read queries the layout.</summary>
    internal void InvalidateLineMetrics()
    {
        _cachedLineMetrics = null;
        _lineMetricsRead = false;
    }
}
