using MarkdownRenderer.Controls.Focus;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// The cross-feature resets of pointer, touch and selection interaction:
/// before a rebuild or document replacement, and when another surface takes
/// the user's attention. Each step calls the owning feature's own reset, in
/// a fixed order: logical capture ownership is cleared before XAML is touched.
/// </summary>
internal sealed class InteractionReset
{
    private readonly MarkdownViewSurface _surface;
    private readonly SelectionController _selection;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private readonly TouchSelectionRestore _touchRestore;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;
    private readonly PointerInputRouter _pointerInput;
    private readonly DocumentPointerInteraction _document;
    private readonly SelectionHandlesPresenter _handles;
    private readonly SelectionDragAutoScroll _autoScroll;
    private readonly SelectionDragShield _dragShield;
    private readonly SelectionContextMenuPresenter _contextMenu;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly PointerHoverState _hover;
    private readonly AbbreviationTooltipPresenter _abbreviationTooltip;
    private readonly PointerCursorPresenter _cursor;
    private readonly KeyboardFocusModel _focus;
    private readonly FocusRingPresenter _focusRing;

    internal InteractionReset(
        MarkdownViewSurface surface,
        SelectionController selection,
        MarkdownTouchSelectionSession touchSelection,
        TouchSelectionRestore touchRestore,
        TouchTapSuppressionPolicy touchTapSuppression,
        PointerInputRouter pointerInput,
        DocumentPointerInteraction document,
        SelectionHandlesPresenter handles,
        SelectionDragAutoScroll autoScroll,
        SelectionDragShield dragShield,
        SelectionContextMenuPresenter contextMenu,
        SelectionAdornerPresenter adorner,
        PointerHoverState hover,
        AbbreviationTooltipPresenter abbreviationTooltip,
        PointerCursorPresenter cursor,
        KeyboardFocusModel focus,
        FocusRingPresenter focusRing)
    {
        _surface = surface;
        _selection = selection;
        _touchSelection = touchSelection;
        _touchRestore = touchRestore;
        _touchTapSuppression = touchTapSuppression;
        _pointerInput = pointerInput;
        _document = document;
        _handles = handles;
        _autoScroll = autoScroll;
        _dragShield = dragShield;
        _contextMenu = contextMenu;
        _adorner = adorner;
        _hover = hover;
        _abbreviationTooltip = abbreviationTooltip;
        _cursor = cursor;
        _focus = focus;
        _focusRing = focusRing;
    }

    /// <summary>
    /// Releases every in-progress gesture, hover, flyout and capture, and
    /// clears the selection too when <paramref name="clearSelection"/> is set.
    /// </summary>
    internal void ResetTransientState(bool clearSelection)
    {
        // Clear logical capture ownership before touching XAML. A disconnected
        // input site must not keep a replacement blocked by a stale gesture.
        _touchTapSuppression.Reset();
        _handles.ResetDrag();
        _touchSelection.Reset();
        _document.Reset();
        _autoScroll.Reset();
        _hover.Clear();
        _abbreviationTooltip.ForgetTarget();
        _contextMenu.Close();
        _dragShield.SetActive(false);
        _abbreviationTooltip.Hide();

        _cursor.SetShape(null);

        try { _surface.Canvas?.ReleasePointerCaptures(); } catch { }

        if (!clearSelection)
            return;

        _selection.Clear();
        _adorner.InvalidateGeometry();
    }

    /// <summary>
    /// Discards selection, touch ownership and keyboard focus because another
    /// surface or the host took over interaction.
    /// </summary>
    internal void ClearSelectionForExternalInteraction(
        bool resetClickTracking = true,
        bool resetDeferredPointerInput = true)
    {
        _touchRestore.Cancel();
        _touchTapSuppression.Reset();
        _contextMenu.Close();
        if (resetDeferredPointerInput)
            _pointerInput.Reset();
        _document.ResetSession();
        _handles.ResetDrag();
        _touchSelection.Reset();
        _document.ForgetDragAnchor();
        _dragShield.SetActive(false);
        if (resetClickTracking)
        {
            _document.ResetClickTracking();
        }

        if (_selection.IsActive || !_selection.Range.IsEmpty)
            _selection.Clear();

        if (_focus.FocusedIndex >= 0)
        {
            _focus.ClearFocus();
            _focusRing.Update();
        }
    }

    /// <summary>Resets touch ownership, handles, flyouts and pending input before a document replacement.</summary>
    internal void ResetInputForDocumentReplacement()
    {
        // Prevent a queued terminal from applying old-document cancellation
        // semantics while its input queue is being discarded. The committed
        // document selection remains governed by the normal rebuild commit;
        // only touch ownership, handles, flyouts, and pending input are reset.
        _touchRestore.Cancel();
        _touchTapSuppression.Reset();
        _pointerInput.Reset();
        ResetTransientState(clearSelection: false);
        _touchTapSuppression.RequireAcceptedContactForRecognizedGesture();
        _handles.Update();
    }
}
