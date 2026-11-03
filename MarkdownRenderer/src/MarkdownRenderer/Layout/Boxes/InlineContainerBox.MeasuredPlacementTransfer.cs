using System;
using System.Collections.Generic;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

internal sealed partial class InlineContainerBox
{

    /// <summary>
    /// Captures a measured Body/Heading source and the exact private clone
    /// created from it. The clone must be measured and arranged outside the
    /// source gate before <see cref="InlineMeasuredPlacementTransfer.IsCurrentAndPrepared"/>
    /// succeeds. Unsupported roots fail closed.
    /// </summary>
    internal bool TryCaptureMeasuredPlacementTransfer(
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedAvailableWidth, isTableCellTransfer: false, out transfer);

    /// <summary>
    /// Root-local-only variant for the explicit measurement-only vector-run
    /// recipe. Do not call from whole-tree COW; the candidate has no painter.
    /// </summary>
    internal bool TryCaptureMeasuredPlacementTransferWithMeasuredVectorPlacement(
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedAvailableWidth, isTableCellTransfer: false, out transfer,
            allowMeasuredVectorPlacement: true);

    /// <summary>
    /// Root-local image-completion variant for a table cell. The candidate is
    /// measurement-only and its vector geometry is remapped to stable source
    /// runs before the placement transaction commits.
    /// </summary>
    internal bool TryCaptureTableCellMeasuredPlacementTransferWithMeasuredVectorPlacement(
        InlineContainerBox prepared,
        float expectedSourceWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedSourceWidth, isTableCellTransfer: true, out transfer,
            allowMeasuredVectorPlacement: true);

    /// <summary>
    /// Captures a table-cell source and its private clone before the candidate
    /// table is measured. The source remains fenced at its exact current cell
    /// width; the prepared cell may later be measured at a different width as
    /// the candidate table rebalances its columns.
    /// </summary>
    internal bool TryCaptureTableCellMeasuredPlacementTransfer(
        InlineContainerBox prepared,
        float expectedSourceWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedSourceWidth, isTableCellTransfer: true, out transfer);

    /// <summary>
    /// Captures a measured image-free Body, Heading, or ListMarker leaf inside
    /// an ordinary StackBox/ListItemBox placement tree. Both the published
    /// source and the completed private leaf remain fenced at the same exact
    /// width; unlike a table cell this mode never permits column rebalancing.
    /// </summary>
    internal bool TryCaptureContainerLeafMeasuredPlacementTransfer(
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedAvailableWidth, isTableCellTransfer: false, out transfer,
            isContainerLeafTransfer: true);

    /// <summary>
    /// Root-local image-completion variant for a vector-bearing leaf in an
    /// ordinary stack/list placement tree. It never publishes its measured-only
    /// candidate runs.
    /// </summary>
    internal bool TryCaptureContainerLeafMeasuredPlacementTransferWithMeasuredVectorPlacement(
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        out InlineMeasuredPlacementTransfer? transfer)
        => TryCaptureMeasuredPlacementTransferCore(
            prepared, expectedAvailableWidth, isTableCellTransfer: false, out transfer,
            isContainerLeafTransfer: true, allowMeasuredVectorPlacement: true);

    private bool TryCaptureMeasuredPlacementTransferCore(
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        bool isTableCellTransfer,
        out InlineMeasuredPlacementTransfer? transfer,
        bool isContainerLeafTransfer = false,
        bool allowMeasuredVectorPlacement = false)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        transfer = null;
        bool rootsSupported = isTableCellTransfer
            ? IsSupportedTableCellMeasuredPlacementRoot(this, allowMeasuredVectorPlacement) &&
                IsSupportedTableCellMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement)
            : isContainerLeafTransfer
            ? IsSupportedContainerLeafMeasuredPlacementRoot(this, allowMeasuredVectorPlacement) &&
                IsSupportedContainerLeafMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement)
            : IsSupportedMeasuredPlacementRoot(this, allowMeasuredVectorPlacement) &&
                IsSupportedMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement);
        bool measuredSource = isTableCellTransfer
            ? IsMeasuredSourceForTableCellTransfer(expectedAvailableWidth, allowMeasuredVectorPlacement)
            : isContainerLeafTransfer
            ? IsMeasuredSourceForContainerLeafTransfer(expectedAvailableWidth, allowMeasuredVectorPlacement)
            : IsMeasuredSourceForTransfer(expectedAvailableWidth, allowMeasuredVectorPlacement);
        int runCount = 0;
        int imageCount = 0;
        bool placementCountsValid = allowMeasuredVectorPlacement
            ? TryGetPrivateRasterPlacementCountsWithMeasuredVectorPlacement(
                prepared.Context, expectedAvailableWidth, out runCount,
                out imageCount, out _)
            : TryGetPrivateRasterPlacementCounts(
                prepared.Context, expectedAvailableWidth, out runCount,
                out imageCount, out _, allowUnmeasuredTextOnly: false,
                allowIdleImagePlacements: false, allowCodeForegroundSpans: false);
        if (ReferenceEquals(this, prepared) ||
            !float.IsFinite(expectedAvailableWidth) || expectedAvailableWidth <= 0f ||
            !rootsSupported ||
            !ArePrivateRasterPlacementContextsCompatible(Context, prepared.Context) ||
            !RawPlacementMargin.Equals(prepared.RawPlacementMargin) ||
            !placementCountsValid ||
            (!isTableCellTransfer && !isContainerLeafTransfer && imageCount == 0) ||
            (isContainerLeafTransfer && imageCount != 0) ||
            imageCount > MaximumPrivateRasterPlacementImages ||
            (!isTableCellTransfer && !isContainerLeafTransfer && runCount == 0) ||
            runCount > MaximumPrivateRasterPlacementRuns ||
            !measuredSource ||
            !IsUnmeasuredPrivateCloneOf(
                this, prepared, expectedAvailableWidth,
                allowImageFree: isTableCellTransfer || isContainerLeafTransfer,
                allowMeasuredVectorPlacement: allowMeasuredVectorPlacement) ||
            !NativeLayoutsAreDistinct(this) || !NativeLayoutsAreDistinct(prepared))
        {
            return false;
        }

        var runStates = new InlineMeasuredRunState[runCount];
        int offset = 0;
        int imageOrdinal = 0;
        var seenSourceImages = new ImageBox[imageCount];
        var seenPreparedImages = new ImageBox[imageCount];
        for (int index = 0; index < runCount; index++)
        {
            InlineRun sourceRun = _runs[index];
            InlineRun preparedRun = prepared._runs[index];
            if (!InlineMeasuredRunState.TryCreate(
                    sourceRun, preparedRun, index, offset,
                    allowMeasuredVectorPlacement, _resolvedContainerStyle!.FontSize,
                    out runStates[index]))
                return false;

            if (runStates[index].SourceImageRun is { } sourceImageRun &&
                runStates[index].PreparedImageRun is { } preparedImageRun)
            {
                for (int prior = 0; prior < imageOrdinal; prior++)
                {
                    if (ReferenceEquals(seenSourceImages[prior], sourceImageRun.Image) ||
                        ReferenceEquals(seenPreparedImages[prior], preparedImageRun.Image) ||
                        ReferenceEquals(seenSourceImages[prior], preparedImageRun.Image) ||
                        ReferenceEquals(seenPreparedImages[prior], sourceImageRun.Image))
                    {
                        return false;
                    }
                }
                seenSourceImages[imageOrdinal] = sourceImageRun.Image;
                seenPreparedImages[imageOrdinal] = preparedImageRun.Image;
                imageOrdinal++;
            }

            offset += sourceRun.TextLength;
        }

        if (imageOrdinal != imageCount || offset != _runTextLength ||
            (_inlineImageRuns?.Count ?? 0) != imageCount ||
            (prepared._inlineImageRuns?.Count ?? 0) != imageCount ||
            ((_inlineImageRuns is null) != (prepared._inlineImageRuns is null)))
        {
            return false;
        }

        imageOrdinal = 0;
        foreach (InlineMeasuredRunState run in runStates)
        {
            if (run.SourceImageRun is null || run.PreparedImageRun is null)
                continue;
            if (_inlineImageRuns is null || prepared._inlineImageRuns is null)
                return false;
            if (!run.MatchesImageEntries(
                    _inlineImageRuns[imageOrdinal], prepared._inlineImageRuns[imageOrdinal]))
            {
                return false;
            }
            imageOrdinal++;
        }

        var effectiveAliases = new IReadOnlyList<string>[runCount];
        var resolvedStyles = new ElementStyle?[runCount];
        for (int index = 0; index < runCount; index++)
        {
            string[] sourceAliases = GetEffectiveRunAliases(index);
            string[] preparedAliases = prepared.GetEffectiveRunAliases(index);
            if (ReferenceEquals(sourceAliases, preparedAliases) ||
                !MatchesKeys(sourceAliases, preparedAliases) ||
                !ReferenceEquals(GetCachedRunStyle(index), prepared.GetCachedRunStyle(index)))
            {
                return false;
            }
            effectiveAliases[index] = preparedAliases;
            resolvedStyles[index] = GetCachedRunStyle(index);
        }

        if (ReferenceEquals(_styleContextKeys, prepared._styleContextKeys) ||
            ReferenceEquals(_styleAliasKeys, prepared._styleAliasKeys) ||
            !MatchesKeys(_styleContextKeys, prepared._styleContextKeys) ||
            !MatchesKeys(_styleAliasKeys, prepared._styleAliasKeys))
        {
            return false;
        }

        transfer = new InlineMeasuredPlacementTransfer(
            this,
            prepared,
            expectedAvailableWidth,
            runStates,
            prepared._styleContextKeys,
            prepared._styleAliasKeys,
            effectiveAliases,
            resolvedStyles,
            isTableCellTransfer,
            isContainerLeafTransfer,
            allowMeasuredVectorPlacement);
        return true;
    }

    private static bool IsSupportedMeasuredPlacementRoot(
        InlineContainerBox box,
        bool allowMeasuredVectorPlacement = false) =>
        (box.ElementKey == MarkdownElementKeys.Body || InlineRunStyleResolver.IsHeadingElement(box.ElementKey)) &&
        box.CodeLanguage is null && box.StyleState is null &&
        box.CodeBlockTextOffset == 0 && box.CodeBlockTextLength == 0 &&
        box.SafeHtmlInlineFlowScopeId is null &&
        box._inlineEmbedRuns is null &&
        (allowMeasuredVectorPlacement || box._vectorSceneRunCount == 0) &&
        !box._hasDeferredSceneMarker &&
        (allowMeasuredVectorPlacement || box._pendingSceneGeometry.Length == 0) &&
        box._foregroundSpans.Count == 0;

    private static bool IsSupportedTableCellMeasuredPlacementRoot(
        InlineContainerBox box,
        bool allowMeasuredVectorPlacement = false) =>
        (box.ElementKey is MarkdownElementKeys.TableHeader or MarkdownElementKeys.TableCell) &&
        box.BlockIndex > 0 && box.CodeLanguage is null && box.StyleState is null &&
        box.CodeBlockTextOffset == 0 && box.CodeBlockTextLength == 0 &&
        box.SafeHtmlInlineFlowScopeId is null &&
        box._inlineEmbedRuns is null &&
        (allowMeasuredVectorPlacement || box._vectorSceneRunCount == 0) &&
        !box._hasDeferredSceneMarker &&
        (allowMeasuredVectorPlacement || box._pendingSceneGeometry.Length == 0) &&
        box._foregroundSpans.Count == 0;

    private static bool IsSupportedContainerLeafMeasuredPlacementRoot(
        InlineContainerBox box,
        bool allowMeasuredVectorPlacement = false) =>
        (box.ElementKey is MarkdownElementKeys.Body or
            MarkdownElementKeys.Heading1 or MarkdownElementKeys.Heading2 or
            MarkdownElementKeys.Heading3 or MarkdownElementKeys.Heading4 or
            MarkdownElementKeys.Heading5 or MarkdownElementKeys.Heading6 or
            MarkdownElementKeys.ListMarker) &&
        box.BlockIndex > 0 && box.CodeLanguage is null && box.StyleState is null &&
        box.CodeBlockTextOffset == 0 && box.CodeBlockTextLength == 0 &&
        box.SafeHtmlInlineFlowScopeId is null &&
        box._inlineEmbedRuns is null &&
        (allowMeasuredVectorPlacement || box._vectorSceneRunCount == 0) &&
        !box._hasDeferredSceneMarker &&
        (allowMeasuredVectorPlacement || box._pendingSceneGeometry.Length == 0) &&
        box._foregroundSpans.Count == 0;

    internal static bool AreCompatibleMeasuredRootInputs(
        InlineContainerBox source,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        bool allowMeasuredVectorPlacement = false) =>
        float.IsFinite(expectedAvailableWidth) && expectedAvailableWidth > 0f &&
        IsSupportedMeasuredPlacementRoot(source, allowMeasuredVectorPlacement) &&
        IsSupportedMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement) &&
        AreCompatiblePlacementRootInputs(source, prepared);

    internal static bool AreCompatibleTableCellRootInputs(
        InlineContainerBox source,
        InlineContainerBox prepared,
        bool allowMeasuredVectorPlacement = false) =>
        IsSupportedTableCellMeasuredPlacementRoot(source, allowMeasuredVectorPlacement) &&
        IsSupportedTableCellMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement) &&
        AreCompatiblePlacementRootInputs(source, prepared);

    internal static bool AreCompatibleContainerLeafRootInputs(
        InlineContainerBox source,
        InlineContainerBox prepared,
        bool allowMeasuredVectorPlacement = false) =>
        IsSupportedContainerLeafMeasuredPlacementRoot(source, allowMeasuredVectorPlacement) &&
        IsSupportedContainerLeafMeasuredPlacementRoot(prepared, allowMeasuredVectorPlacement) &&
        AreCompatiblePlacementRootInputs(source, prepared);

    private static bool AreCompatiblePlacementRootInputs(
        InlineContainerBox source,
        InlineContainerBox prepared) =>
        ArePrivateRasterPlacementContextsCompatible(source.Context, prepared.Context) &&
        source.BlockIndex == prepared.BlockIndex &&
        string.Equals(source.ElementKey, prepared.ElementKey, StringComparison.Ordinal) &&
        string.Equals(source.CodeLanguage, prepared.CodeLanguage, StringComparison.Ordinal) &&
        string.Equals(source.StyleState, prepared.StyleState, StringComparison.Ordinal) &&
        source.CodeBlockTextOffset == prepared.CodeBlockTextOffset &&
        source.CodeBlockTextLength == prepared.CodeBlockTextLength &&
        source.WordWrapping == prepared.WordWrapping &&
        source.TextAlignment == prepared.TextAlignment &&
        source.DrawContainerChrome == prepared.DrawContainerChrome &&
        source.UseContainerPadding == prepared.UseContainerPadding &&
        source.UseContainerMargin == prepared.UseContainerMargin &&
        source.SafeHtmlInlineFlowScopeId == prepared.SafeHtmlInlineFlowScopeId &&
        source.Margin.Equals(prepared.Margin) &&
        source.CollapsedTopMargin == prepared.CollapsedTopMargin &&
        source.SuppressTopMargin == prepared.SuppressTopMargin &&
        source.SuppressBottomMargin == prepared.SuppressBottomMargin &&
        source._usesProgressiveNativeSceneResidency == prepared._usesProgressiveNativeSceneResidency &&
        source._hasAtomicInlineObject == prepared._hasAtomicInlineObject &&
        source._bufferDirty == prepared._bufferDirty &&
        source._runTextLength == prepared._runTextLength &&
        ReferenceEquals(source._buffer, prepared._buffer) &&
        ReferenceEquals(source._resolvedContainerStyle, prepared._resolvedContainerStyle) &&
        MatchesKeys(source._styleContextKeys, prepared._styleContextKeys) &&
        MatchesKeys(source._styleAliasKeys, prepared._styleAliasKeys);

    private bool IsMeasuredSourceForTransfer(
        float expectedAvailableWidth,
        bool allowMeasuredVectorPlacement = false)
    {
        bool expectedAtomic = (_inlineImageRuns?.Count ?? 0) > 0 ||
            allowMeasuredVectorPlacement && _vectorSceneRunCount > 0;
        if (IsDirty || _layout is null || _bufferDirty || _resolvedContainerStyle is null ||
            !float.IsFinite(_lastWidth) || _lastWidth <= 0f ||
            Math.Abs(_lastWidth - expectedAvailableWidth) > 0.5f ||
            !IsFiniteNonnegative(Bounds) || Bounds.Width != expectedAvailableWidth ||
            _runs.Count == 0 || _runTextLength != _buffer.Length ||
            _runTextLength > MaximumPrivateRasterPlacementTextLength ||
            !HasWellFormedEffectiveRunAliases || ResolvedRunStyleCount != _runs.Count ||
            _hasAtomicInlineObject != expectedAtomic ||
            !IsFiniteThickness(Margin) || !double.IsFinite(CollapsedTopMargin))
        {
            return false;
        }

        return true;
    }

    private bool IsMeasuredSourceForTableCellTransfer(
        float expectedSourceWidth,
        bool allowMeasuredVectorPlacement = false)
    {
        int imageCount = _inlineImageRuns?.Count ?? 0;
        bool expectedAtomic = imageCount > 0 ||
            allowMeasuredVectorPlacement && _vectorSceneRunCount > 0;
        if (IsDirty || _layout is null || _bufferDirty || _resolvedContainerStyle is null ||
            !float.IsFinite(expectedSourceWidth) || expectedSourceWidth <= 0f ||
            !float.IsFinite(_lastWidth) || _lastWidth != expectedSourceWidth ||
            !IsFiniteNonnegative(Bounds) || Bounds.Width != expectedSourceWidth ||
            _runTextLength != _buffer.Length ||
            _runTextLength > MaximumPrivateRasterPlacementTextLength ||
            !HasWellFormedEffectiveRunAliases || ResolvedRunStyleCount != _runs.Count ||
            imageCount > MaximumPrivateRasterPlacementImages ||
            _hasAtomicInlineObject != expectedAtomic ||
            !IsFiniteThickness(Margin) || !double.IsFinite(CollapsedTopMargin))
        {
            return false;
        }

        return true;
    }

    private bool IsMeasuredSourceForContainerLeafTransfer(
        float expectedAvailableWidth,
        bool allowMeasuredVectorPlacement = false)
    {
        int imageCount = _inlineImageRuns?.Count ?? 0;
        bool expectedAtomic = allowMeasuredVectorPlacement && _vectorSceneRunCount > 0;
        if (IsDirty || _layout is null || _bufferDirty || _resolvedContainerStyle is null ||
            !float.IsFinite(expectedAvailableWidth) || expectedAvailableWidth <= 0f ||
            !float.IsFinite(_lastWidth) || _lastWidth != expectedAvailableWidth ||
            !IsFiniteNonnegative(Bounds) || Bounds.Width != expectedAvailableWidth ||
            _runTextLength != _buffer.Length ||
            _runTextLength > MaximumPrivateRasterPlacementTextLength ||
            !HasWellFormedEffectiveRunAliases || ResolvedRunStyleCount != _runs.Count ||
            imageCount != 0 || _inlineImageRuns is not null || _hasAtomicInlineObject != expectedAtomic ||
            !IsFiniteThickness(RawPlacementMargin) || !IsFiniteThickness(Margin) ||
            !double.IsFinite(CollapsedTopMargin))
        {
            return false;
        }

        // Empty paragraphs may have no runs, but only the established clean
        // empty native-layout shape is admitted. Zero-length runs remain
        // restricted to the existing table-cell path.
        return _runs.Count != 0 ||
            (_buffer.Length == 0 && _runTextLength == 0 && _inlineEmbedRuns is null &&
             _vectorSceneRunCount == 0 && !_hasDeferredSceneMarker &&
             _pendingSceneGeometry.Length == 0 && _foregroundSpans.Count == 0);
    }

    private static bool IsUnmeasuredPrivateCloneOf(
        InlineContainerBox source,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        bool allowImageFree = false,
        bool allowMeasuredVectorPlacement = false)
    {
        bool imageIndexShapeMatches = allowImageFree
            ? (prepared._inlineImageRuns?.Count ?? 0) == (source._inlineImageRuns?.Count ?? 0) &&
                ((prepared._inlineImageRuns is null) == (source._inlineImageRuns is null))
            : prepared._inlineImageRuns is { Count: > 0 } &&
                prepared._inlineImageRuns.Count == source._inlineImageRuns?.Count;
        return prepared.IsDirty && prepared._layout is null && prepared._selectionLayout is null &&
            prepared._layoutRetirementRecord is null &&
            prepared._selectionLayoutRetirementRecord is null &&
            prepared._cachedLineMetrics is null && !prepared._lineMetricsRead &&
            !prepared._bufferDirty && ReferenceEquals(prepared._buffer, source._buffer) &&
            prepared._runTextLength == source._runTextLength &&
            prepared._runs.Count == source._runs.Count && imageIndexShapeMatches &&
            prepared._vectorSceneRunCount == source._vectorSceneRunCount &&
            (allowMeasuredVectorPlacement || source._vectorSceneRunCount == 0) &&
            !source._hasDeferredSceneMarker && !prepared._hasDeferredSceneMarker &&
            prepared._sceneGeometryGeneration == 0 &&
            prepared._pendingSceneGeometryGeneration == 0 &&
            prepared._pendingSceneGeometry.Length == 0 &&
            prepared._lastWidth == source._lastWidth &&
            ReferenceEquals(prepared._resolvedContainerStyle, source._resolvedContainerStyle) &&
            prepared.Bounds.Width == 0 && prepared.Bounds.Height == 0 &&
            float.IsFinite(expectedAvailableWidth);
    }

    internal static bool MatchesRuns(
        InlineContainerBox source,
        InlineContainerBox prepared,
        InlineMeasuredRunState[] runs,
        IReadOnlyList<string>[] effectiveAliases,
        ElementStyle?[] sourceResolvedStyles,
        bool allowMeasuredVectorPlacement)
    {
        int sourceImageCount = source._inlineImageRuns?.Count ?? 0;
        int preparedImageCount = prepared._inlineImageRuns?.Count ?? 0;
        int vectorCount = 0;
        foreach (InlineMeasuredRunState run in runs)
            if (run.SourceVectorRun is not null)
                vectorCount++;
        if (source._runs.Count != runs.Length || prepared._runs.Count != runs.Length ||
            source._runTextLength != prepared._runTextLength ||
            sourceImageCount != preparedImageCount ||
            ((source._inlineImageRuns is null) != (prepared._inlineImageRuns is null)) ||
            !source.HasWellFormedEffectiveRunAliases ||
            !prepared.HasWellFormedEffectiveRunAliases ||
            source.ResolvedRunStyleCount != runs.Length ||
            prepared.ResolvedRunStyleCount != runs.Length ||
            source._foregroundSpans.Count != 0 || prepared._foregroundSpans.Count != 0 ||
            source._inlineEmbedRuns is not null || prepared._inlineEmbedRuns is not null ||
            source._hasDeferredSceneMarker || prepared._hasDeferredSceneMarker ||
            source._vectorSceneRunCount != vectorCount || prepared._vectorSceneRunCount != vectorCount ||
            (!allowMeasuredVectorPlacement && vectorCount != 0) ||
            source._hasAtomicInlineObject != prepared._hasAtomicInlineObject)
        {
            return false;
        }

        for (int index = 0; index < runs.Length; index++)
        {
            InlineMeasuredRunState run = runs[index];
            if (!ReferenceEquals(source._runs[index], run.SourceRun) ||
                !ReferenceEquals(prepared._runs[index], run.PreparedRun) ||
                !run.MatchesSourceAndPrepared() ||
                !MatchesKeys(source.GetEffectiveRunAliases(index), effectiveAliases[index]) ||
                !MatchesKeys(prepared.GetEffectiveRunAliases(index), effectiveAliases[index]) ||
                !ReferenceEquals(source.GetCachedRunStyle(index), sourceResolvedStyles[index]))
            {
                return false;
            }
        }

        int imageOrdinal = 0;
        foreach (InlineMeasuredRunState run in runs)
        {
            if (run.SourceImageRun is null || run.PreparedImageRun is null)
                continue;
            if (source._inlineImageRuns is null || prepared._inlineImageRuns is null ||
                imageOrdinal >= sourceImageCount ||
                !run.MatchesImageEntries(
                    source._inlineImageRuns[imageOrdinal],
                    prepared._inlineImageRuns[imageOrdinal]))
            {
                return false;
            }
            imageOrdinal++;
        }
        return imageOrdinal == sourceImageCount;
    }

    internal static bool MatchesContainerKeys(
        InlineContainerBox box,
        IReadOnlyList<string> contextKeys,
        IReadOnlyList<string> aliasKeys) =>
        MatchesKeys(box._styleContextKeys, contextKeys) &&
        MatchesKeys(box._styleAliasKeys, aliasKeys);

    private static bool MatchesKeys(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null || left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
                return false;
        return true;
    }

    private static bool NativeLayoutsAreDistinct(InlineContainerBox box) =>
        HasValidNativeLayoutCustody(box);

    internal static bool HasValidNativeLayoutCustody(InlineContainerBox box) =>
        (box._layout is null) == (box._layoutRetirementRecord is null) &&
        (box._selectionLayout is null) == (box._selectionLayoutRetirementRecord is null) &&
        (box._layout is null || box._selectionLayout is null ||
            !ReferenceEquals(box._layout, box._selectionLayout)) &&
        (box._layoutRetirementRecord is null || box._selectionLayoutRetirementRecord is null ||
            !ReferenceEquals(box._layoutRetirementRecord, box._selectionLayoutRetirementRecord));

    internal static bool Aliases(CanvasTextLayout? first, CanvasTextLayout? second) =>
        first is not null && ReferenceEquals(first, second);

    internal static bool Aliases(
        NativeResourceRetirementRecord? first,
        NativeResourceRetirementRecord? second) =>
        first is not null && ReferenceEquals(first, second);

    internal static bool SameRect(Rect first, Rect second) =>
        first.X == second.X && first.Y == second.Y &&
        first.Width == second.Width && first.Height == second.Height;

    internal static bool IsFiniteNonnegative(Rect bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && bounds.Width >= 0 &&
        double.IsFinite(bounds.Height) && bounds.Height >= 0;

    internal static bool HasUsableMeasuredVectorRegions(in InlineSceneGeometry geometry)
    {
        if (geometry.RegionCount <= 0)
            return false;

        for (int index = 0; index < geometry.RegionCount; index++)
        {
            Rect region = geometry.GetRegionBounds(index);
            if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) ||
                !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
                !double.IsFinite(region.Right) || !double.IsFinite(region.Bottom) ||
                region.Width < 0d || region.Height < 0d)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFiniteThickness(Thickness thickness) =>
        double.IsFinite(thickness.Left) && double.IsFinite(thickness.Top) &&
        double.IsFinite(thickness.Right) && double.IsFinite(thickness.Bottom);
}
