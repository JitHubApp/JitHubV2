using System;
using MarkdownRenderer.Layout.Internal.Inlines.Styling;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.Inlines.Placement;

/// <summary>
/// The root fences of an inline measured-placement transfer: which published
/// Body/Heading, table-cell and container-leaf roots may transfer at all,
/// whether a source is measured at the expected width, whether a prepared box
/// is still the unmeasured private clone of that source, and whether the two
/// roots carry the same layout inputs.
/// </summary>
internal static class InlineMeasuredPlacementRootAdmission
{
    internal static bool IsSupportedMeasuredPlacementRoot(
        InlineContainerBox box,
        bool allowMeasuredVectorPlacement = false) =>
        (box.ElementKey == MarkdownElementKeys.Body || InlineRunStyleResolver.IsHeadingElement(box.ElementKey)) &&
        box.CodeLanguage is null && box.StyleState is null &&
        box.CodeBlockTextOffset == 0 && box.CodeBlockTextLength == 0 &&
        box.SafeHtmlInlineFlowScopeId is null &&
        box.InlineEmbedEntries is null &&
        (allowMeasuredVectorPlacement || box.VectorSceneRunCount == 0) &&
        !box.HasDeferredSceneMarker &&
        (allowMeasuredVectorPlacement || box.PendingSceneGeometry.Length == 0) &&
        box.ForegroundSpans.Count == 0;

    internal static bool IsSupportedTableCellMeasuredPlacementRoot(
        InlineContainerBox box,
        bool allowMeasuredVectorPlacement = false) =>
        (box.ElementKey is MarkdownElementKeys.TableHeader or MarkdownElementKeys.TableCell) &&
        box.BlockIndex > 0 && box.CodeLanguage is null && box.StyleState is null &&
        box.CodeBlockTextOffset == 0 && box.CodeBlockTextLength == 0 &&
        box.SafeHtmlInlineFlowScopeId is null &&
        box.InlineEmbedEntries is null &&
        (allowMeasuredVectorPlacement || box.VectorSceneRunCount == 0) &&
        !box.HasDeferredSceneMarker &&
        (allowMeasuredVectorPlacement || box.PendingSceneGeometry.Length == 0) &&
        box.ForegroundSpans.Count == 0;

    internal static bool IsSupportedContainerLeafMeasuredPlacementRoot(
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
        box.InlineEmbedEntries is null &&
        (allowMeasuredVectorPlacement || box.VectorSceneRunCount == 0) &&
        !box.HasDeferredSceneMarker &&
        (allowMeasuredVectorPlacement || box.PendingSceneGeometry.Length == 0) &&
        box.ForegroundSpans.Count == 0;

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
        InlinePrivatePlacementPreflight.ArePrivateRasterPlacementContextsCompatible(source.Context, prepared.Context) &&
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
        source.UsesProgressiveNativeSceneResidency == prepared.UsesProgressiveNativeSceneResidency &&
        source.HasAtomicInlineObject == prepared.HasAtomicInlineObject &&
        source.IsBufferDirty == prepared.IsBufferDirty &&
        source.RunTextLength == prepared.RunTextLength &&
        ReferenceEquals(source.LayoutBuffer, prepared.LayoutBuffer) &&
        ReferenceEquals(source.ResolvedContainerStyle, prepared.ResolvedContainerStyle) &&
        InlineMeasuredPlacementCapture.MatchesKeys(source.StyleContextKeys, prepared.StyleContextKeys) &&
        InlineMeasuredPlacementCapture.MatchesKeys(source.StyleAliasKeys, prepared.StyleAliasKeys);

    internal static bool IsMeasuredSourceForTransfer(
        InlineContainerBox box,
        float expectedAvailableWidth,
        bool allowMeasuredVectorPlacement = false)
    {
        bool expectedAtomic = (box.InlineImageEntries?.Count ?? 0) > 0 ||
            allowMeasuredVectorPlacement && box.VectorSceneRunCount > 0;
        if (box.IsDirty || box.TextLayout is null || box.IsBufferDirty || box.ResolvedContainerStyle is null ||
            !float.IsFinite(box.LastMeasuredWidth) || box.LastMeasuredWidth <= 0f ||
            Math.Abs(box.LastMeasuredWidth - expectedAvailableWidth) > 0.5f ||
            !InlineMeasuredPlacementCapture.IsFiniteNonnegative(box.Bounds) || box.Bounds.Width != expectedAvailableWidth ||
            box.RunList.Count == 0 || box.RunTextLength != box.LayoutBuffer.Length ||
            box.RunTextLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength ||
            !box.HasWellFormedEffectiveRunAliases || box.ResolvedRunStyleCount != box.RunList.Count ||
            box.HasAtomicInlineObject != expectedAtomic ||
            !InlineMeasuredPlacementCapture.IsFiniteThickness(box.Margin) || !double.IsFinite(box.CollapsedTopMargin))
        {
            return false;
        }

        return true;
    }

    internal static bool IsMeasuredSourceForTableCellTransfer(
        InlineContainerBox box,
        float expectedSourceWidth,
        bool allowMeasuredVectorPlacement = false)
    {
        int imageCount = box.InlineImageEntries?.Count ?? 0;
        bool expectedAtomic = imageCount > 0 ||
            allowMeasuredVectorPlacement && box.VectorSceneRunCount > 0;
        if (box.IsDirty || box.TextLayout is null || box.IsBufferDirty || box.ResolvedContainerStyle is null ||
            !float.IsFinite(expectedSourceWidth) || expectedSourceWidth <= 0f ||
            !float.IsFinite(box.LastMeasuredWidth) || box.LastMeasuredWidth != expectedSourceWidth ||
            !InlineMeasuredPlacementCapture.IsFiniteNonnegative(box.Bounds) || box.Bounds.Width != expectedSourceWidth ||
            box.RunTextLength != box.LayoutBuffer.Length ||
            box.RunTextLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength ||
            !box.HasWellFormedEffectiveRunAliases || box.ResolvedRunStyleCount != box.RunList.Count ||
            imageCount > InlineContainerBox.MaximumPrivateRasterPlacementImages ||
            box.HasAtomicInlineObject != expectedAtomic ||
            !InlineMeasuredPlacementCapture.IsFiniteThickness(box.Margin) || !double.IsFinite(box.CollapsedTopMargin))
        {
            return false;
        }

        return true;
    }

    internal static bool IsMeasuredSourceForContainerLeafTransfer(
        InlineContainerBox box,
        float expectedAvailableWidth,
        bool allowMeasuredVectorPlacement = false)
    {
        int imageCount = box.InlineImageEntries?.Count ?? 0;
        bool expectedAtomic = allowMeasuredVectorPlacement && box.VectorSceneRunCount > 0;
        if (box.IsDirty || box.TextLayout is null || box.IsBufferDirty || box.ResolvedContainerStyle is null ||
            !float.IsFinite(expectedAvailableWidth) || expectedAvailableWidth <= 0f ||
            !float.IsFinite(box.LastMeasuredWidth) || box.LastMeasuredWidth != expectedAvailableWidth ||
            !InlineMeasuredPlacementCapture.IsFiniteNonnegative(box.Bounds) || box.Bounds.Width != expectedAvailableWidth ||
            box.RunTextLength != box.LayoutBuffer.Length ||
            box.RunTextLength > InlineContainerBox.MaximumPrivateRasterPlacementTextLength ||
            !box.HasWellFormedEffectiveRunAliases || box.ResolvedRunStyleCount != box.RunList.Count ||
            imageCount != 0 || box.InlineImageEntries is not null || box.HasAtomicInlineObject != expectedAtomic ||
            !InlineMeasuredPlacementCapture.IsFiniteThickness(box.RawPlacementMargin) ||
            !InlineMeasuredPlacementCapture.IsFiniteThickness(box.Margin) ||
            !double.IsFinite(box.CollapsedTopMargin))
        {
            return false;
        }

        // Empty paragraphs may have no runs, but only the established clean
        // empty native-layout shape is admitted. Zero-length runs remain
        // restricted to the existing table-cell path.
        return box.RunList.Count != 0 ||
            (box.LayoutBuffer.Length == 0 && box.RunTextLength == 0 && box.InlineEmbedEntries is null &&
             box.VectorSceneRunCount == 0 && !box.HasDeferredSceneMarker &&
             box.PendingSceneGeometry.Length == 0 && box.ForegroundSpans.Count == 0);
    }

    internal static bool IsUnmeasuredPrivateCloneOf(
        InlineContainerBox source,
        InlineContainerBox prepared,
        float expectedAvailableWidth,
        bool allowImageFree = false,
        bool allowMeasuredVectorPlacement = false)
    {
        bool imageIndexShapeMatches = allowImageFree
            ? (prepared.InlineImageEntries?.Count ?? 0) == (source.InlineImageEntries?.Count ?? 0) &&
                ((prepared.InlineImageEntries is null) == (source.InlineImageEntries is null))
            : prepared.InlineImageEntries is { Count: > 0 } &&
                prepared.InlineImageEntries.Count == source.InlineImageEntries?.Count;
        return prepared.IsDirty && prepared.TextLayout is null && prepared.SelectionLayout is null &&
            prepared.TextLayoutRetirementRecord is null &&
            prepared.SelectionLayoutRetirementRecord is null &&
            prepared.CachedLineMetrics is null && !prepared.HasReadLineMetrics &&
            !prepared.IsBufferDirty && ReferenceEquals(prepared.LayoutBuffer, source.LayoutBuffer) &&
            prepared.RunTextLength == source.RunTextLength &&
            prepared.RunList.Count == source.RunList.Count && imageIndexShapeMatches &&
            prepared.VectorSceneRunCount == source.VectorSceneRunCount &&
            (allowMeasuredVectorPlacement || source.VectorSceneRunCount == 0) &&
            !source.HasDeferredSceneMarker && !prepared.HasDeferredSceneMarker &&
            prepared.CurrentSceneGeometryGeneration == 0 &&
            prepared.PendingSceneGeometryGeneration == 0 &&
            prepared.PendingSceneGeometry.Length == 0 &&
            prepared.LastMeasuredWidth == source.LastMeasuredWidth &&
            ReferenceEquals(prepared.ResolvedContainerStyle, source.ResolvedContainerStyle) &&
            prepared.Bounds.Width == 0 && prepared.Bounds.Height == 0 &&
            float.IsFinite(expectedAvailableWidth);
    }
}
