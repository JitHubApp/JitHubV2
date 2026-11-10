using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// Pointer input on the document canvas: press, drag and release for text
/// selection, multi-click word and block selection, link hover and the
/// cursor, and the link activation a release produces.
/// </summary>
/// <remarks>
/// <para>
/// This class owns the captured pointer session, the drag anchor, the
/// multi-click sequence and the <see cref="ClickUnitSelection"/> that holds
/// the click mode. Other components end or reset them only through its
/// methods.
/// </para>
/// <para>
/// Every method runs while the renderer holds the snapshot interaction
/// lease. Calls that can reenter application code, such as moving focus,
/// are followed by a check that the snapshot, generation and layout
/// revision are still published.
/// </para>
/// <para>
/// This file is longer than 500 lines because press, move, release and
/// cancel share the session, anchor and click state, and each ordering rule
/// keeps its explanation next to the code it constrains.
/// </para>
/// </remarks>
internal sealed class DocumentPointerInteraction
{
    private readonly IPointerInteractionHost _host;
    private readonly MarkdownViewSurface _surface;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;
    private readonly SelectionController _selection;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;
    private readonly HorizontalOverflowPointerDrag _overflowDrag;
    private readonly SelectionHandlesPresenter _handles;
    private readonly SelectionDragAutoScroll _autoScroll;
    private readonly SelectionDragShield _dragShield;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly SelectionContextMenuPresenter _contextMenu;
    private readonly PointerHoverTracker _hoverTracker;
    private readonly PointerCursorPresenter _cursor;
    private readonly AbbreviationTooltipPresenter _abbreviationTooltip;

    // Multi-click tracking for double-click word and triple-click block selection.
    private readonly ClickSequenceTracker _clicks = new();
    // The captured primary-pointer gesture. Keeping the pointer id with the
    // state makes release, cancel and capture-lost handling idempotent when
    // events arrive out of order or are routed through the drag shield.
    private PointerSession _session;
    private DocumentPosition? _anchor;
    // The unit (character, word or block) the press selects and drags by.
    private readonly ClickUnitSelection _clickUnits;
    private readonly TouchSelectionHitTest _touchHitTest;

    internal DocumentPointerInteraction(
        IPointerInteractionHost host,
        MarkdownViewSurface surface,
        PublishedLayout published,
        MarkdownViewport viewport,
        SelectionController selection,
        MarkdownTouchSelectionSession touchSelection,
        TouchTapSuppressionPolicy touchTapSuppression,
        HorizontalOverflowPointerDrag overflowDrag,
        SelectionHandlesPresenter handles,
        SelectionDragAutoScroll autoScroll,
        SelectionDragShield dragShield,
        SelectionAdornerPresenter adorner,
        SelectionContextMenuPresenter contextMenu,
        PointerHoverTracker hoverTracker,
        PointerCursorPresenter cursor,
        AbbreviationTooltipPresenter abbreviationTooltip)
    {
        _host = host;
        _surface = surface;
        _published = published;
        _viewport = viewport;
        _selection = selection;
        _touchSelection = touchSelection;
        _touchTapSuppression = touchTapSuppression;
        _overflowDrag = overflowDrag;
        _handles = handles;
        _autoScroll = autoScroll;
        _dragShield = dragShield;
        _adorner = adorner;
        _contextMenu = contextMenu;
        _hoverTracker = hoverTracker;
        _cursor = cursor;
        _abbreviationTooltip = abbreviationTooltip;
        _clickUnits = new ClickUnitSelection(host, published, selection);
        _touchHitTest = new TouchSelectionHitTest(host);
    }

    /// <summary>True while a primary pointer session holds the canvas capture.</summary>
    internal bool HasActiveSession => _session.IsActive;

    /// <summary>True while a selection drag is in progress.</summary>
    internal bool IsDragSelecting => _anchor is not null;

    /// <summary>True when <paramref name="pointerId"/> owns the active session.</summary>
    internal bool IsSessionPointer(uint pointerId)
        => _session.IsActive && _session.PointerId == pointerId;

    /// <summary>Ends the pointer session and its drag without touching the selection.</summary>
    internal void EndSession()
    {
        _session = default;
        _anchor = null;
    }

    /// <summary>Forgets the pointer session.</summary>
    internal void ResetSession() => _session = default;

    /// <summary>Forgets the drag anchor and returns to single-click selection.</summary>
    internal void ForgetDragAnchor()
    {
        _anchor = null;
        _clickUnits.ResetToSingle();
    }

    /// <summary>Starts the next press as a new click sequence.</summary>
    internal void ResetClickTracking() => _clicks.Reset();

    /// <summary>Forgets every gesture: the session, the drag, the click mode and the click sequence.</summary>
    internal void Reset()
    {
        _session = default;
        _anchor = null;
        _clickUnits.Reset();
        _clicks.Reset();
    }

    /// <summary>
    /// Begins a text selection, a multi-click selection or a link press, or
    /// lets the press through to a hosted embed or code block action.
    /// </summary>
    /// <remarks>
    /// Kept whole: the press is classified in priority order (touch contact,
    /// horizontal overflow drag, primary-button policy, selection handles,
    /// code block action, embed, then text or a linked vector), and the
    /// layout currency check must follow the focus move before any geometry
    /// is read.
    /// </remarks>
    internal void ProcessPressed(ref PointerInput e)
    {
        if (_published.Snapshot is not { } snapshot || _surface.Canvas is null)
            return;
        _host.CancelPendingTouchSelectionRestore();
        long snapshotGeneration = _published.Generation;
        long layoutRevision = snapshot.LayoutRevision;

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            BeginTouchContact(ref e);

        if (_overflowDrag.TryBegin(ref e))
        {
            if (e.Handled)
                return;
            if (!PointerInputClassification.IsPrecisePointer(e))
                return;
        }
        MarkdownPointerModality modality = PointerInputClassification.GetPointerModality(e.Pointer.PointerDeviceType);
        if (!MarkdownPointerGesturePolicy.BeginsImmediateSelection(modality))
            return;

        // Only process left (primary) button presses; right-clicks are handled by
        // OnRightTapped and must not affect the multi-click counter or selection anchor.
        var currentPoint = e.Point;
        if (!MarkdownPointerGesturePolicy.IsPrimarySelectionPress(
                modality,
                currentPoint.IsInContact,
                currentPoint.Properties.IsLeftButtonPressed,
                currentPoint.Properties.IsBarrelButtonPressed))
            return;
        var pt = currentPoint.Position;
        if (_handles.HasCaptureTarget)
            _handles.ResetDrag();
        _touchTapSuppression.Reset();
        _touchSelection.Reset();
        _handles.Update();
        _viewport.TryGet(out double pointerViewportTop, out _, out _);
        _autoScroll.TrackPointer(pt.Y - pointerViewportTop);
        _host.RememberFocusResumePoint(pt);

        // Pressing renderer chrome (code-copy action) must not create or clear
        // selection; let the native button handle the click.
        if (_host.IsPointOverCodeBlockAction(pt))
        {
            _clicks.Reset();
            return;
        }

        _abbreviationTooltip.Hide();

        // Pressing *on* a hosted inline embed must NOT start a selection.
        // The embed is a real WinUI element layered above the canvas — its
        // own pointer-pressed handler must run (Button click, TextBox focus,
        // …).  Returning here without setting the anchor or capturing the
        // pointer lets XAML's normal pointer routing deliver the event to
        // the embedded element.
        if (_host.IsPointOverEmbed(pt))
        {
            // A press on an embedded control breaks any ongoing double/triple-click
            // sequence; reset timing state so the next text press starts fresh.
            _clicks.Reset();
            // Clear any prior selection so the user gets visual feedback that
            // the click isn't a new selection start.
            if (!_selection.Range.IsEmpty)
            {
                _selection.Clear();
                // No canvas Invalidate() needed — overlay cleared by Changed handler.
            }
            // Also dismiss keyboard focus ring on click over embed.
            _host.ClearKeyboardFocusItem();
            return;
        }

        // Dismiss any keyboard focus ring — mouse clicks and keyboard nav are
        // separate modalities; clicking anywhere in the document should clear
        // the ring so the focus indicator doesn't linger after the user
        // switches from keyboard to mouse.
        _host.ClearKeyboardFocusItem();

        // Track consecutive clicks for word (double) and line (triple) selection.
        long nowMs = e.TickCount;
        int clickCount = _clicks.RegisterPress(pt, nowMs);

        _host.FocusRendererForPointerInteraction();
        // Focus can synchronously re-enter application code, including document
        // replacement or unload. Keep all geometry reads on the snapshot whose
        // interaction lease DispatchPointerInput acquired.
        if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return;

        if (snapshot.HitTest(pt, out var pos))
        {
            BeginTextPress(ref e, snapshot, pos, pt, nowMs, clickCount, snapshotGeneration, layoutRevision);
        }
        else if (_host.TryGetVectorPointerState(pt, out bool vectorLinked, out _) && vectorLinked)
        {
            // Linked and Selectable are independent scene semantics. A linked-only
            // item must still receive a complete press/release gesture, but it must
            // not become a text-selection anchor.
            _clicks.AcceptPress(pt, nowMs);
            _clickUnits.ResetToSingle();
            _session = new PointerSession(e.Pointer.PointerId, IsPrimary: true);
            _anchor = null;
            _selection.Clear();
            if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
                return;
            if (!PointerInputClassification.CapturePointerForInput(_surface.Canvas!, e))
                _session = default;
            else
                e.Handled = true;
        }
        else
        {
            // HitTest missed (pointer landed on a gap or embed area).
            // Reset click state so this miss isn't counted toward a future
            // double/triple-click; also reset timing so a miss→text sequence
            // never misclassifies as a double-click.
            _clicks.Reset();
            _anchor = null; // defensive: clear any stale anchor from a prior capture loss
            _autoScroll.Reset();
            _selection.Clear();
            // No canvas invalidate: selection clear is overlay-only.
        }
    }

    /// <summary>
    /// Extends a drag selection to the pointer, or updates link hover and
    /// the cursor when no drag is in progress.
    /// </summary>
    internal void ProcessMoved(ref PointerInput e)
    {
        if (_published.Snapshot is null || _surface.Canvas is null) return;
        var pt = e.Point.Position;

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            Rect contact = e.Point.Properties.ContactRect;
            _touchSelection.UpdateContact(
                e.Pointer.PointerId,
                new TouchContactGeometry(contact.X, contact.Y, contact.Width, contact.Height));
        }

        if (_overflowDrag.TryHandleMoved(ref e, pt))
            return;

        // Drag-select.
        if (_anchor is not null)
        {
            if (!IsActiveSessionEvent(e))
                return;

            e.Handled = true;

            _abbreviationTooltip.Hide();
            _clickUnits.ExtendDragSelection(_autoScroll.PrepareDragPoint(pt));
            // Do NOT update hover state during an active selection drag.
            // Calling SetColor on the CanvasTextLayout (which ApplyHoverColor
            // does whenever HoveredRun changes) causes DirectWrite to
            // invalidate cached glyph metrics, which in turn produces
            // sub-pixel vertical jitter ("selection shake") whenever the
            // pointer crosses run boundaries — most visibly on body text
            // that contains links, because every time the pointer moves
            // between the link run and the surrounding body run the
            // hovered-run identity flips.  Suppressing the toggle here
            // costs nothing because the IBeam cursor and link-hover color
            // aren't relevant while the user is mid-drag selecting.
            return;
        }

        // Touch contacts should pan the containing ScrollViewer. Treating touch
        // like a mouse press can capture the pointer, start text selection, and
        // drive the Win2D selection overlay at touch-scroll frequency.
        if (!PointerInputClassification.IsPrecisePointer(e))
        {
            _abbreviationTooltip.Hide();
            _cursor.SetShape(null);
            return;
        }

        // Pointer hovering an inline embed: do nothing.  The embed is its
        // own pointer-event target and its ProtectedCursor (Hand / IBeam /
        // arrow / …) takes effect via XAML's normal pointer routing.  We
        // reset our own ProtectedCursor to null so child elements are not
        // overridden by an IBeam that this UserControl last set.  null
        // means "no cursor override" — XAML walks up the tree and finds
        // nothing, so the system default (Arrow) applies, allowing each
        // embed to set its own cursor if desired.
        if (_host.IsPointOverEmbed(pt))
        {
            _abbreviationTooltip.Hide();
            // Clear our own link-hover so when the pointer leaves the embed
            // the previous hovered link doesn't appear stuck-on.
            _hoverTracker.Clear();
            _cursor.SetShape(null);
            return;
        }

        _hoverTracker.Update(_published.Snapshot, e.InteractionSnapshot, pt);
    }

    /// <summary>
    /// Commits the drag at the release point and ends the session. A single
    /// click that selected nothing records the activation of the link under
    /// it, which runs after the snapshot lease ends.
    /// </summary>
    internal void ProcessReleased(ref PointerInput e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            _touchTapSuppression.EndContact(e.Pointer.PointerId);
            _touchSelection.EndContact(e.Pointer.PointerId);
        }

        if (_surface.Canvas is null) return;
        if (_overflowDrag.TryHandleReleased(ref e))
            return;
        if (!IsActiveSessionEvent(e))
            return;

        e.Handled = true;

        // PointerMoved is coalescible, including while the UI dispatcher is
        // busy. Commit the release location before ending capture so a fast
        // press/drag/release cannot leave selection at the last delivered move.
        if (_anchor is not null)
            ProcessMoved(ref e);

        // Retire the gesture state while the leased snapshot is stable. Capture
        // release is staged below because WinUI can synchronously route
        // PointerCaptureLost from ReleasePointerCapture.
        bool wasLeft = _session.IsActive &&
            _session.PointerId == e.Pointer.PointerId &&
            _session.IsPrimary;
        _session = default;
        _anchor = null;
        _autoScroll.Reset();
        _dragShield.SetActive(false);
        e.ReleaseCanvasCaptureAfterInteraction = true;
        e.ResumeRebuildAfterInteraction = true;
        if (!wasLeft) return;

        if (!_selection.Range.Normalized().IsEmpty)
        {
            e.RefreshSelectionAdornerAfterInteraction = true;
        }

        // Click handling for links: if no real selection occurred, raise LinkClick
        // when the click lands on a LinkRun.
        if (e.InteractionSnapshot is not { } snapshot) return;
        if (_clickUnits.Mode != ClickSelectionMode.Single) return; // double/triple-click: selection intent, not link-click
        if (!_selection.Range.Normalized().IsEmpty) return; // text was dragged — not a click
        var pt = e.Point.Position;
        if (LinkTargets.TryCreateActivationIntent(
            snapshot,
            pt,
            LinkTargets.GetLinkInputKind(e.Pointer.PointerDeviceType),
            e.LinkModifiers,
            out LinkActivationIntent intent))
        {
            e.SetLinkActivation(intent);
        }
    }

    /// <summary>
    /// Tears down the drag after a system cancel or a capture taken by
    /// another element, keeping the committed selection.
    /// </summary>
    internal void ProcessCanceledOrCaptureLost(ref PointerInput e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            _touchTapSuppression.CancelContact(e.Pointer.PointerId);
            TouchSelectionCancelResult cancelResult = _touchSelection.CancelContact(e.Pointer.PointerId);
            if (cancelResult != TouchSelectionCancelResult.NotOwner)
            {
                // A canceled unrelated contact should not dismiss a committed
                // selection, but it must not leave the selection flyout alive.
                _contextMenu.Close();
                _handles.Update();
                if (cancelResult == TouchSelectionCancelResult.ClearSelection && _selection.IsActive)
                    _host.ClearSelectionForExternalInteraction();
            }
        }

        if (_overflowDrag.TryHandleCanceled(ref e))
            return;

        if (!IsActiveSessionEvent(e))
            return;

        e.Handled = true;

        // True interruption (system-level cancel or capture taken by another element):
        // tear down drag state so a phantom anchor doesn't survive into the next gesture.
        // We deliberately do NOT do this on plain PointerExited — with capture, the
        // pointer can briefly leave canvas bounds during a normal drag, and clearing
        // the anchor there would kill drag-select on the very first vertical move
        // when the pointer crosses into a sibling overlay region (e.g. over a hosted
        // inline embed). PointerReleased handles the normal end-of-drag cleanup.
        _session = default;
        _anchor = null;
        _autoScroll.Reset();
        _clickUnits.ResetToSingle();
        _dragShield.SetActive(false);
        if (!_selection.Range.Normalized().IsEmpty)
            _adorner.Invalidate();
        ProcessExited(ref e);
        _host.ResumePendingRebuildAfterPointerInteraction();
    }

    /// <summary>Clears link hover and the cursor when the pointer leaves the canvas.</summary>
    internal void ProcessExited(ref PointerInput e)
    {
        _abbreviationTooltip.ScheduleHide();

        // PointerExited fires when the pointer leaves canvas bounds. During an
        // active captured drag this is expected (drag through hosted embeds /
        // adjacent areas) so we MUST NOT clear the anchor here — that killed
        // drag-select on the embeds page. Capture-loss / cancel are routed to
        // ProcessCanceledOrCaptureLost, which does the real cleanup.

        // ROOT-CAUSE FIX for text-selection shake:
        // During an active drag (anchor set), PointerExited fires whenever the
        // captured pointer physically leaves the canvas bounds (e.g. the user
        // drags toward the window edge). Calling InvalidateLinkHoverRegion
        // here issues a partial canvas Invalidate(), which triggers a tile repaint
        // that calls InlineContainerBox.Paint() → ApplyHoverColor() → CanvasTextLayout
        // .SetColor() → DirectWrite invalidates cached glyph-run metrics → character
        // region coordinates shift by sub-pixel amounts → visible text shake on every
        // drag frame. The canvas was already fully invalidated at drag-start
        // (ProcessPressed) so no intermediate repaint is needed during the drag.
        // ProcessCanceledOrCaptureLost clears the anchor *before* calling us,
        // so real cleanup (cancel / capture-loss) still runs normally here.
        if (_anchor is not null)
        {
            // Cursor reset is still safe and needed: if the pointer crosses from
            // the canvas into a hosted embed during drag, the embed must see the
            // default cursor (not an inherited IBeam).
            _cursor.SetShape(null);
            return;
        }

        // Clear hover state when the pointer leaves the canvas (or capture is
        // lost).  Without this, a link's hover colour and the hand cursor
        // persist even when the pointer is no longer over the control.
        if (_published.Snapshot is null || _surface.Canvas is null) return;
        // Capture loss routes through here too, with the anchor already
        // cleared, so this also runs on click-to-dismiss.
        _hoverTracker.Clear();
        // Always reset cursor to null (system default) on exit — not just
        // when a link was hovered.  PointerExited fires whenever the pointer
        // moves from the canvas to any sibling element — most importantly the
        // overlay that hosts embedded WinUI elements.  With ProtectedCursor =
        // null on this UserControl, XAML finds no cursor override anywhere in
        // the tree above the embed, so the embed (Button, CheckBox, …) can
        // show its own cursor instead of inheriting IBeam from us.
        _cursor.SetShape(null);
    }

    /// <summary>
    /// Ends a captured pen or mouse selection when a hold or double-tap takes
    /// over as a context gesture. The capture itself is released after the
    /// snapshot lease ends, because WinUI can route CaptureLost synchronously.
    /// </summary>
    internal void EndCapturedSessionForContextGesture(ref PointerInput input)
    {
        if (input.ContextCaptureEnded)
            return;
        input.ContextCaptureEnded = true;

        if (!_session.IsActive || _surface.Canvas is null)
            return;

        // Retire the live logical session under the snapshot lease, but defer
        // the native/XAML release: WinUI can synchronously route CaptureLost.
        uint pointerId = _session.PointerId;
        _session = default;
        _anchor = null;
        _autoScroll.Reset();
        _dragShield.SetActive(false);
        input.InteractionCanvas = _surface.Canvas;
        input.ReleasePointerIdAfterInteraction = pointerId;
        input.ReleaseCanvasCaptureAfterInteraction = pointerId != 0;
    }

    /// <summary>
    /// Selects the word under a touch or pen context gesture, probing the
    /// contact rectangle when the gesture point itself misses text. Returns
    /// true when a word is selected on the still-current layout.
    /// </summary>
    internal bool TrySelectWordForTouchContext(
        Point point,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        TouchContactGeometry? capturedContact = null)
    {
        if (!_host.IsSelectionEnabled ||
            !_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision) ||
            !_touchHitTest.TryHitTestTouchSelection(point, snapshot, capturedContact, out DocumentPosition position))
        {
            return false;
        }

        _host.FocusRendererForPointerInteraction();
        if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return false;

        _clicks.Reset();
        _anchor = null;
        _clickUnits.SelectUnit(
            snapshot, position, ClickSelectionMode.Word, snapshotGeneration, layoutRevision);
        if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return false;

        if (_selection.IsActive)
            _touchSelection.Select();
        _host.UpdateSelectionOverlay();
        return _host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision) && _selection.IsActive;
    }

    private void BeginTouchContact(ref PointerInput e)
    {
        Rect contact = e.Point.Properties.ContactRect;
        bool contactAccepted = _touchSelection.BeginContact(
            e.Pointer.PointerId,
            new TouchContactGeometry(contact.X, contact.Y, contact.Width, contact.Height),
            selectionIsActive: _selection.IsActive);
        if (contactAccepted &&
            _touchTapSuppression.TryBeginContact(e.Pointer.PointerId, out long gestureEpoch))
        {
            e.TouchTapGestureEpoch = gestureEpoch;
        }
        else
        {
            // Holding/Tapped routed events do not expose a pointer ID. Keep
            // a secondary contact's origin so a recognized event at that
            // location cannot be mistaken for the primary contact's event.
            _touchTapSuppression.TrackUnownedContact(
                e.Pointer.PointerId,
                e.Point.Position);
            if (contactAccepted)
                _touchSelection.Cancel(e.Pointer.PointerId);
        }
    }

    private void BeginTextPress(
        ref PointerInput e,
        LayoutSnapshot snapshot,
        DocumentPosition pos,
        Point pt,
        long nowMs,
        int clickCount,
        long snapshotGeneration,
        long layoutRevision)
    {
        // Advance clock/position only on successful text hits so a miss in the
        // same spot doesn't corrupt the double/triple-click timing window.
        _clicks.AcceptPress(pt, nowMs);
        _session = new PointerSession(e.Pointer.PointerId, IsPrimary: true);

        if (!_host.IsSelectionEnabled)
        {
            // Selection is disabled but links must still work: capture the pointer
            // so the release arrives and can raise LinkClick.
            // Also ensure the click mode is reset so the link-click guard in
            // ProcessReleased is not stale from a previous double/triple-click
            // sequence made while IsSelectionEnabled was true.
            _clickUnits.ResetToSingle();
            if (!PointerInputClassification.CapturePointerForInput(_surface.Canvas!, e)) _session = default;
            else e.Handled = true;
            return;
        }

        // Always arm the anchor first: this suppresses hover processing
        // in ProcessMoved during any captured drag (single, double, or triple-click)
        // and prevents a stale anchor from an earlier interaction being reused.
        _anchor = pos;

        if (clickCount == 3)
        {
            // Triple-click: select the entire block (line).
            SelectClickUnit(ref e, snapshot, pos, ClickSelectionMode.Block, snapshotGeneration, layoutRevision);
            return;
        }
        if (clickCount == 2)
        {
            // Double-click: select the word under the cursor.
            SelectClickUnit(ref e, snapshot, pos, ClickSelectionMode.Word, snapshotGeneration, layoutRevision);
            return;
        }
        _clickUnits.ResetToSingle();
        _selection.SetAnchor(pos);
        if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return;
        // Selection is rendered by the XAML overlay; do not dirty canvas text
        // for the empty anchor state.
        bool captured = PointerInputClassification.CapturePointerForInput(_surface.Canvas!, e);
        if (!captured) { _session = default; _anchor = null; }
        else e.Handled = true;
    }

    private void SelectClickUnit(
        ref PointerInput e,
        LayoutSnapshot snapshot,
        DocumentPosition pos,
        ClickSelectionMode mode,
        long snapshotGeneration,
        long layoutRevision)
    {
        _clickUnits.SelectUnit(snapshot, pos, mode, snapshotGeneration, layoutRevision);
        if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return;
        _selection.SetAnchor(_clickUnits.AnchorStart);
        if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return;
        _selection.ExtendTo(_clickUnits.AnchorEnd);
        if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return;
        // Selection is rendered by the XAML overlay; do not dirty canvas
        // text during mouse-down. Repainting DirectWrite text here causes
        // visible shake on selection starts, especially on the embeds page.
        if (!PointerInputClassification.CapturePointerForInput(_surface.Canvas!, e)) { _session = default; _anchor = null; }
        else e.Handled = true;
    }

    private bool IsActiveSessionEvent(PointerInput e)
        => _session.IsActive && _session.PointerId == e.Pointer.PointerId;

    /// <summary>
    /// True when <paramref name="point"/> hits selectable text that is not
    /// under a hosted embed or a code block action.
    /// </summary>
    internal bool IsSelectableTouchPoint(Point point, LayoutSnapshot snapshot, out DocumentPosition position)
        => _touchHitTest.IsSelectableTouchPoint(point, snapshot, out position);
}
