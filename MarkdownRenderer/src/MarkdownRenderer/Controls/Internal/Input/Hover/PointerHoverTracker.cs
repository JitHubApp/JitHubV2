using Windows.Foundation;
using MarkdownRenderer.Controls.Internal.Input.Links;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Input.Hover;

/// <summary>
/// Link hover on the document canvas: which run the pointer is over, the
/// link-state repaint when the hovered link changes, the cursor shape and
/// the abbreviation tooltip.
/// </summary>
/// <remarks>
/// Hover never invalidates the canvas. Only the link-state overlay above the
/// text is repainted, because a partial canvas repaint shows DirectWrite's
/// sub-pixel glyph variance at tile edges as visible text shake.
/// </remarks>
internal sealed class PointerHoverTracker
{
    private readonly IPointerInteractionHost _host;
    private readonly PublishedLayout _published;
    private readonly PointerHoverState _hover;
    private readonly PointerCursorPresenter _cursor;
    private readonly AbbreviationTooltipPresenter _abbreviationTooltip;

    internal PointerHoverTracker(
        IPointerInteractionHost host,
        PublishedLayout published,
        PointerHoverState hover,
        PointerCursorPresenter cursor,
        AbbreviationTooltipPresenter abbreviationTooltip)
    {
        _host = host;
        _published = published;
        _hover = hover;
        _cursor = cursor;
        _abbreviationTooltip = abbreviationTooltip;
    }

    /// <summary>
    /// Records the run under <paramref name="pt"/>, repaints link state only
    /// when the hovered link changes, and sets the cursor and abbreviation tooltip.
    /// </summary>
    internal void Update(LayoutSnapshot snapshot, LayoutSnapshot? interactionSnapshot, Point pt)
    {
        // Hover effect for links + cursor change.
        // Only respond to transitions that *actually affect rendering or the
        // cursor shape*.  Earlier this method invalidated the canvas and
        // re-set ProtectedCursor on every transition between any two runs
        // (including TextRun → TextRun within the same paragraph as the
        // pointer moved character-by-character).  That produced visible
        // text "shake" on plain hover because ProtectedCursor reassignment
        // and a full Canvas.Invalidate per pointer-move event nudges the
        // visual tree by a sub-pixel.  Now we only react when the link
        // we're hovering changes (which mutates run color → must repaint)
        // or when the cursor shape needs to flip Hand↔IBeam.
        InlineRun? hovered = null;
        InlineContainerBox? hoveredBox = null;
        Point hoveredPhysicalPoint = pt;
        foreach (var b in snapshot.Blocks)
        {
            Point physicalPoint = interactionSnapshot is { } leased
                ? leased.ToPhysicalPointForTopLevelBlockUnderPaint(b, pt)
                : pt;
            using LayoutPlacementTransformContext.Scope placementScope =
                interactionSnapshot?.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(b) ?? default;
            if (DocumentHitTesting.FindInlineHover(b, physicalPoint) is var h && h.Run is not null)
            {
                hovered = h.Run;
                hoveredBox = h.Box;
                hoveredPhysicalPoint = physicalPoint;
                break;
            }
        }

        if (LinkTargets.IsLinkedRun(hovered) && hoveredBox is { } linkedBox)
        {
            using LayoutPlacementTransformContext.Scope placementScope =
                interactionSnapshot?.EnterPlacementTransformScopeForBlockIndexUnderPaint(linkedBox.BlockIndex) ?? default;
            if (linkedBox.IsPointInsideRunBounds(hovered!, hoveredPhysicalPoint) != true)
            {
                hovered = null;
                hoveredBox = null;
            }
        }

        var hoveredLinkedRun = LinkTargets.IsLinkedRun(hovered) ? hovered : null;
        var lastLinkedRun = LinkTargets.IsLinkedRun(_hover.Run) ? _hover.Run : null;
        bool linkChanged = !ReferenceEquals(hoveredLinkedRun, lastLinkedRun);
        bool overVectorScene = _host.TryGetVectorPointerState(
            pt,
            out bool hoveredVectorLink,
            out bool hoveredVectorSelectable);
        var wantedShape = hoveredLinkedRun is not null || hoveredVectorLink
            ? Microsoft.UI.Input.InputSystemCursorShape.Hand
            : _host.IsSelectionEnabled && (!overVectorScene || hoveredVectorSelectable)
                ? Microsoft.UI.Input.InputSystemCursorShape.IBeam
                : (Microsoft.UI.Input.InputSystemCursorShape?)null; // Arrow when selection is off

        if (linkChanged)
        {
            // Update hover bookkeeping for click routing, but do NOT invalidate
            // the canvas — hover causes no visual change to the text any more
            // (see InlineContainerBox.HoveredRun docs).  Eliminating these
            // partial-region invalidates is what finally killed the long-
            // standing text-shake bug: even with grayscale AA + device-pixel
            // origin snapping, repainting a partial canvas region forced
            // DirectWrite to re-tile glyphs at sub-pixel-different positions.
            foreach (var b in snapshot.Blocks) DocumentHitTesting.ClearHover(b);
            if (hoveredBox is not null && hoveredLinkedRun is LinkRun hoveredLink)
                hoveredBox.HoveredRun = hoveredLink;
            _host.InvalidateInteractiveTextAdorner();
        }
        _hover.Set(hovered, hoveredBox);

        _abbreviationTooltip.Update(hovered, hoveredBox, pt);
        _cursor.SetShape(wantedShape);
    }

    /// <summary>
    /// Clears hover from every block. Only a hovered link has painted state,
    /// its link-state overlay, so only that case refreshes the adorner.
    /// </summary>
    /// <remarks>
    /// Never invalidates the canvas: hover does not change the text layout,
    /// and a partial CanvasVirtualControl repaint exposes DirectWrite
    /// sub-pixel glyph variance at tile edges as visible text shake.
    /// </remarks>
    internal void Clear()
    {
        if (_hover.Run is null || _published.Snapshot is null)
            return;

        bool wasLink = _hover.Run is LinkRun;
        foreach (var b in _published.Snapshot.Blocks) DocumentHitTesting.ClearHover(b);
        _hover.Clear();
        if (wasLink)
            _host.InvalidateInteractiveTextAdorner();
    }
}
