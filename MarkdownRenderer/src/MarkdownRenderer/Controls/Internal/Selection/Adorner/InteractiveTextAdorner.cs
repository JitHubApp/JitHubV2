using System.Collections.Generic;
using MarkdownRenderer.Controls.Internal.Focus;
using MarkdownRenderer.Controls.Internal.Input.Hover;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Rendering;
using MarkdownRenderer.Controls.Internal.Selection.TouchSelection;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Focus;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace MarkdownRenderer.Controls.Internal.Selection.Adorner;

/// <summary>
/// Decides when the interactive text adorner (selection highlight, hovered
/// and focused link state) needs new geometry or a repaint, and paints that
/// state into the adorner surface the <see cref="SelectionAdornerPresenter"/>
/// owns. Selection changes only touch the adorner, never the document canvas.
/// </summary>
internal sealed class InteractiveTextAdorner
{
    private readonly MarkdownViewSurface _surface;
    private readonly PublishedLayout _published;
    private readonly SelectionController _selection;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private readonly PointerHoverState _hover;
    private readonly KeyboardFocusModel _focus;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly GeometryRecovery _geometryRecovery;

    internal InteractiveTextAdorner(
        MarkdownViewSurface surface,
        PublishedLayout published,
        SelectionController selection,
        MarkdownTouchSelectionSession touchSelection,
        PointerHoverState hover,
        KeyboardFocusModel focus,
        SelectionAdornerPresenter adorner,
        GeometryRecovery geometryRecovery)
    {
        _surface = surface;
        _published = published;
        _selection = selection;
        _touchSelection = touchSelection;
        _hover = hover;
        _focus = focus;
        _adorner = adorner;
        _geometryRecovery = geometryRecovery;
    }

    internal void Invalidate()
    {
        if (_surface.Overlay is null)
            return;

        if (!_adorner.IsCreated && !_selection.IsActive &&
            _hover.Run is not LinkRun && !_focus.TryGetFocusedLink(out _, out _))
            return;

        _adorner.Invalidate();
    }

    internal void RefreshAfterLayoutChange()
    {
        // Lazy realization and image measurement can move text without replacing
        // the snapshot. Recompute selection geometry before redrawing the
        // viewport-bounded overlay; merely moving the surface would retain stale
        // highlight/text pixels at the old block positions.
        if (_selection.IsActive)
        {
            UpdateSelectionOverlay();
            return;
        }

        _adorner.InvalidateGeometry();
        _adorner.Invalidate();
    }

    /// <summary>
    /// Syncs the selection adorner geometry. The adorner is a single stable
    /// composition-surface child layered above the document text and below hosted controls.
    /// Selection changes only mutate this in-memory rect list and invalidate
    /// the adorner; they never invalidate the base document canvas or mutate
    /// the XAML child tree during a drag.
    /// </summary>
    internal void UpdateSelectionOverlay()
    {
        _adorner.MarkGeometryDirty();
        if (_geometryRecovery.RequestIfRequired(_published.Snapshot))
            return;
        _adorner.Invalidate();
    }

    internal bool HasContent()
        => (_selection.IsActive && _adorner.Rectangles.Count > 0) ||
           _hover.HasLink ||
           _focus.TryGetFocusedLink(out _, out _);

    internal bool ShouldRefreshForScroll()
    {
        bool hasHoveredLink = _hover.HasLink;
        IReadOnlyList<FocusableItem>? focusableItems = _focus.Items;
        bool hasFocusedLink = focusableItems is not null &&
            (uint)_focus.FocusedIndex < (uint)focusableItems.Count &&
            focusableItems[_focus.FocusedIndex].Kind == FocusableItemKind.Link;
        bool hasVisibleAdorner = _adorner.IsVisible;
        bool hasRetainedRectangles = _adorner.Rectangles.Count > 0;
        bool hasPendingRefresh = _adorner.HasPendingRefresh;

        return SelectionAdornerScrollRefreshPolicy.ShouldRefreshForScroll(
            hasActiveSelection: _selection.IsActive,
            hasHoveredLink: hasHoveredLink,
            hasFocusedLink: hasFocusedLink,
            hasVisibleAdorner: hasVisibleAdorner,
            hasRetainedRectangles: hasRetainedRectangles,
            hasTouchHandles: _touchSelection.ShouldShowHandles,
            hasPendingRefresh: hasPendingRefresh);
    }

    internal void PaintInteractiveDocumentState(
        LayoutSnapshot snapshot,
        CanvasDrawingSession drawingSession,
        Rect viewport,
        IReadOnlyList<SelectionBackgroundGeometry> selectionBackgrounds)
    {
        PaintLinkStateOverlay(drawingSession, viewport);
        if (!_selection.IsActive || selectionBackgrounds.Count == 0)
            return;

        Color selectedBackground = _published.Theme?.SelectionHighlightColor
            ?? Color.FromArgb(0xFF, 0x66, 0xAA, 0xE8);
        Color selectedForeground = _published.Theme?.SelectionForegroundColor
            ?? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        bool highContrast = _published.Theme?.IsHighContrast ?? false;
        foreach (SelectionBackgroundGeometry background in selectionBackgrounds)
        {
            Rect rectangle = background.Bounds;
            if (rectangle.Right < viewport.Left || rectangle.Left > viewport.Right ||
                rectangle.Bottom < viewport.Top || rectangle.Top > viewport.Bottom)
            {
                continue;
            }

            background.Paint(drawingSession, selectedBackground, highContrast);
        }
        snapshot.PaintSelectionForeground(
            drawingSession,
            _selection.Range.Normalized(),
            selectedForeground,
            viewport);
    }

    private void PaintLinkStateOverlay(CanvasDrawingSession drawingSession, Rect viewport)
    {
        if (_hover.TryGetLink(out var hover, out var hoverBox))
            hoverBox.PaintLinkStateForeground(drawingSession, hover, focused: false, viewport);

        if (_focus.TryGetFocusedLink(out var focusedBox, out var focusedLink))
            focusedBox.PaintLinkStateForeground(drawingSession, focusedLink, focused: true, viewport);
    }
}
