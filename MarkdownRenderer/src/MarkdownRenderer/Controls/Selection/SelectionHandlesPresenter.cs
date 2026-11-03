using System;
using System.Collections.Generic;
using System.Diagnostics;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Selection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// The two touch selection handles: their placement at the selection's
/// endpoints, their keyboard nudges, and the pointer drag that moves one
/// endpoint while the other stays fixed.
/// </summary>
/// <remarks>
/// <para>
/// The handles are <see cref="SelectionHandleButtons"/> in the overlay above
/// the canvas. Their pointer events enter the renderer's ordered input path
/// through <see cref="IPointerInputSink"/>, which replays them here.
/// <see cref="SelectionHandlePlacement"/> places them during the adorner
/// paint and leaves the captured one where the drag holds it.
/// </para>
/// <para>
/// A drag is coalesced onto the compositor frame: pointer moves only record
/// the latest contact, and one hit-test runs per frame. The frame clock
/// keeps running while the contact is stationary, so edge auto-scroll
/// continues, and it retires a drag whose capture was lost without a
/// routed terminal.
/// </para>
/// <para>
/// This file is longer than 500 lines because placement and the drag
/// lifecycle (press, frame-coalesced move, release, cancel and silent
/// capture loss) read and reset the same drag fields; splitting them would
/// make one class reset another's state.
/// </para>
/// </remarks>
internal sealed class SelectionHandlesPresenter : ISelectionHandleDragView
{
    private readonly ISelectionHandlesHost _host;
    private readonly FrameworkElement _renderer;
    private readonly MarkdownViewSurface _surface;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly MarkdownViewport _viewport;
    private readonly SelectionController _selection;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private readonly TouchTapSuppressionPolicy _touchTapSuppression;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly SelectionDragAutoScroll _autoScroll;
    private readonly SelectionHandleButtons _buttons;
    private readonly SelectionHandlePlacement _placement;

    // The drag in progress. The fixed position is the endpoint that stays
    // put; the active endpoint can swap roles as the drag crosses it.
    private SelectionHandleEndpoint _activeEndpoint;
    private uint _dragPointerId;
    private Button? _captureTarget;
    private DocumentPosition _fixedPosition;
    private Point _latestPoint;
    private double _latestViewportY;
    private Point _caretOffset;
    private bool _movePending;
    private bool _frameSubscribed;
    private long _lastFrameTimestamp;

    internal SelectionHandlesPresenter(
        ISelectionHandlesHost host,
        IPointerInputSink input,
        FrameworkElement renderer,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewport viewport,
        SelectionController selection,
        MarkdownTouchSelectionSession touchSelection,
        TouchTapSuppressionPolicy touchTapSuppression,
        SelectionAdornerPresenter adorner,
        SelectionDragAutoScroll autoScroll)
    {
        _host = host;
        _renderer = renderer;
        _surface = surface;
        _lifetime = lifetime;
        _published = published;
        _viewport = viewport;
        _selection = selection;
        _touchSelection = touchSelection;
        _touchTapSuppression = touchTapSuppression;
        _adorner = adorner;
        _autoScroll = autoScroll;
        _buttons = new SelectionHandleButtons(host, input, renderer, surface, OnHandleKeyDown);
        _placement = new SelectionHandlePlacement(host, renderer, selection, touchSelection, _buttons, this);
    }

    /// <summary>The pointer that owns the drag in progress, or 0.</summary>
    internal uint DragPointerId => _dragPointerId;

    /// <summary>True while a handle Button holds a confirmed pointer capture.</summary>
    internal bool HasCaptureTarget => _captureTarget is not null;

    /// <summary>True when <paramref name="element"/> is inside either handle.</summary>
    internal bool ContainsElement(DependencyObject element) => _buttons.Contains(element);

    /// <summary>Refreshes the handles' accessible names after the string provider changed.</summary>
    internal void UpdateLocalization() => _buttons.UpdateLocalization();

    /// <summary>Sets both handles' visibility.</summary>
    internal void SetVisibility(Visibility visibility) => _buttons.SetVisibility(visibility);

    /// <summary>Adds the automation peers of the visible handles to <paramref name="peers"/>.</summary>
    internal void AppendVisibleAutomationPeers(IList<AutomationPeer> peers)
        => _buttons.AppendVisibleAutomationPeers(peers);

    /// <summary>Detaches every input handler from both handles.</summary>
    internal void DetachHandlers() => _buttons.DetachHandlers();

    /// <summary>
    /// Marks both endpoints unplaced, so a handle press waits for the next
    /// paint to place the handles on the new layout.
    /// </summary>
    internal void MarkEndpointsUnavailable() => _placement.MarkEndpointsUnavailable();

    /// <summary>
    /// Detaches the handles and forgets them on unload. The overlay's children
    /// were already cleared; they are re-created on the next selection.
    /// </summary>
    internal void ReleaseForUnload() => _buttons.Release();

    /// <summary>Requests a coalesced paint that places or hides the handles.</summary>
    internal void Update()
    {
        if (!_touchSelection.ShouldShowHandles || !_selection.IsActive)
        {
            _placement.HideUncapturedHandles();
            _adorner.MarkVisualDirty();
            _adorner.RequestRefresh();
            return;
        }

        // Geometry is refreshed only by the frame coordinator while holding
        // the snapshot paint lease. This direct state change merely requests
        // that coalesced frame; it never walks boxes on the input callback.
        _adorner.MarkVisualDirty();
        _adorner.RequestRefresh();
    }

    /// <summary>Places the handles at the selection's endpoints while the snapshot paint lease is held.</summary>
    internal void UpdateUnderPaint(LayoutSnapshot snapshot, Rect viewport)
        => _placement.UpdateUnderPaint(snapshot, viewport);

    private bool IsCaptured(SelectionHandleEndpoint endpoint)
    {
        Button? endpointButton = _buttons.Get(endpoint);

        return endpointButton is not null &&
            ReferenceEquals(endpointButton, _captureTarget) &&
            IsPointerDragActive();
    }

    private bool IsPointerDragActive()
        => _captureTarget is not null &&
           _activeEndpoint != SelectionHandleEndpoint.None &&
           _dragPointerId != 0 &&
           _touchSelection.ContactPointerId == _dragPointerId &&
           _touchSelection.State is TouchSelectionState.DraggingStartHandle or
               TouchSelectionState.DraggingEndHandle;

    /// <summary>Starts a drag when a primary contact presses a placed handle.</summary>
    /// <remarks>
    /// Kept whole: the drag state is set before the touch session, the tap
    /// policy and the capture accept it, and each failed step resets every
    /// field set before it.
    /// </remarks>
    internal void ProcessPointerPressed(ref PointerInput e)
    {
        if (e.Sender is not Button { Tag: SelectionHandleEndpoint endpoint } handle ||
            endpoint == SelectionHandleEndpoint.None ||
            _published.Snapshot is null || !_selection.IsActive || _surface.Canvas is null ||
            _captureTarget is not null ||
            _dragPointerId != 0 ||
            _activeEndpoint != SelectionHandleEndpoint.None)
        {
            return;
        }

        _host.CancelPendingTouchSelectionRestore();

        var point = e.Point;
        MarkdownPointerModality modality = PointerInputClassification.GetPointerModality(e.Pointer.PointerDeviceType);
        bool primary = modality == MarkdownPointerModality.Touch
            ? point.IsInContact
            : MarkdownPointerGesturePolicy.IsPrimarySelectionPress(
                modality,
                point.IsInContact,
                point.Properties.IsLeftButtonPressed,
                point.Properties.IsBarrelButtonPressed);
        if (!primary)
            return;

        bool endpointAvailable = endpoint == SelectionHandleEndpoint.Start
            ? _placement.StartEndpointAvailable
            : _placement.EndEndpointAvailable;
        if (!endpointAvailable)
            return;

        DocumentRange range = _selection.Range.Normalized();
        _activeEndpoint = endpoint;
        _dragPointerId = e.Pointer.PointerId;
        _fixedPosition = endpoint == SelectionHandleEndpoint.Start
            ? range.End
            : range.Start;
        _viewport.TryGet(out double viewportTop, out _, out _);
        _autoScroll.TrackPointer(point.Position.Y - viewportTop);
        Point endpointPoint = _placement.GetEndpointPoint(endpoint);
        _caretOffset = new Point(
            endpointPoint.X - point.Position.X,
            endpointPoint.Y - point.Position.Y);
        _latestPoint = point.Position;
        _latestViewportY = point.Position.Y - viewportTop;
        _movePending = false;
        if (!_touchSelection.BeginHandleDrag(
                e.Pointer.PointerId,
                endpoint == SelectionHandleEndpoint.Start))
        {
            ResetDrag();
            return;
        }
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch &&
            !_touchTapSuppression.TryBeginContact(e.Pointer.PointerId, out e.TouchTapGestureEpoch))
        {
            ResetDrag();
            _touchSelection.Cancel(e.Pointer.PointerId);
            return;
        }
        if (!PointerInputClassification.CapturePointerForInput(handle, e))
        {
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
                _touchTapSuppression.CancelContact(e.Pointer.PointerId);
            ResetDrag();
            _touchSelection.Cancel(e.Pointer.PointerId);
            return;
        }

        // Capture belongs to this physical Button even if logical start/end
        // roles later cross while extending the selection. Only publish the
        // target after WinUI (or the deferred-input path) confirms capture.
        if (_dragPointerId != e.Pointer.PointerId ||
            _touchSelection.ContactPointerId != e.Pointer.PointerId ||
            _activeEndpoint == SelectionHandleEndpoint.None ||
            _touchSelection.State is not TouchSelectionState.DraggingStartHandle and
                not TouchSelectionState.DraggingEndHandle)
        {
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
                _touchTapSuppression.CancelContact(e.Pointer.PointerId);
            ResetDrag();
            _touchSelection.Cancel(e.Pointer.PointerId);
            return;
        }
        _captureTarget = handle;

        _buttons.SetGlyphVisibility(visible: false);
        StartFrameClock();
        e.Handled = true;
    }

    /// <summary>Records the drag's latest contact for the next frame.</summary>
    internal void ProcessPointerMoved(ref PointerInput e)
    {
        if (_activeEndpoint == SelectionHandleEndpoint.None ||
            _dragPointerId != e.Pointer.PointerId ||
            _published.Snapshot is null || _surface.Canvas is null)
        {
            return;
        }

        var pointerPoint = e.Point;
        if (!pointerPoint.IsInContact)
            return;

        _latestPoint = pointerPoint.Position;
        _viewport.TryGet(out double viewportTop, out _, out _);
        _latestViewportY = pointerPoint.Position.Y - viewportTop;
        _movePending = true;
        StartFrameClock();
        e.Handled = true;
    }

    /// <summary>
    /// Commits the drag's final position and ends it. Returns false when the
    /// released pointer does not own the drag.
    /// </summary>
    /// <remarks>
    /// A handle can appear under a finger that is still down, such as the
    /// second tap of a word-selecting double-tap. That contact belongs to the
    /// canvas, so the caller routes it to the canvas release instead.
    /// </remarks>
    internal bool TryProcessPointerReleased(ref PointerInput e)
    {
        if (_dragPointerId != e.Pointer.PointerId)
            return false;

        // Commit the release coordinate even when the dispatcher coalesced the
        // final PointerMoved event. This mirrors the main text-drag path and
        // prevents quick flick-and-release gestures from stopping one frame early.
        _latestPoint = e.Point.Position;
        _viewport.TryGet(out double viewportTop, out _, out _);
        _latestViewportY = e.Point.Position.Y - viewportTop;
        _movePending = true;
        ApplyPendingMove(sustained: false);
        if (_surface.Canvas is not null && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            _touchTapSuppression.SuppressTouchTap(
                e.TouchTapGestureEpoch,
                e.Point.Position,
                Environment.TickCount64);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            _touchTapSuppression.EndContact(e.Pointer.PointerId);
        _captureTarget = null;
        _touchSelection.CompleteHandleDrag(e.Pointer.PointerId);
        _activeEndpoint = SelectionHandleEndpoint.None;
        _dragPointerId = 0;
        e.ResumeRebuildAfterInteraction = true;
        _autoScroll.Reset();
        StopFrameClock();
        _placement.CollapseForPendingRefresh();
        _buttons.SetGlyphVisibility(visible: true);
        if (e.Sender is UIElement element)
            element.ReleasePointerCapture(e.Pointer);
        Update();
        e.Handled = true;
        return true;
    }

    /// <summary>
    /// Cancels the drag and keeps the committed selection. Returns false when
    /// the canceled pointer does not own the drag; see
    /// <see cref="TryProcessPointerReleased"/>.
    /// </summary>
    internal bool TryProcessPointerCanceled(ref PointerInput e)
    {
        if (_dragPointerId != e.Pointer.PointerId)
            return false;

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            _touchTapSuppression.CancelContact(e.Pointer.PointerId);
        ResetDrag();
        _touchSelection.Cancel(e.Pointer.PointerId);
        e.ResumeRebuildAfterInteraction = true;
        Update();
        e.Handled = true;
        return true;
    }

    /// <summary>
    /// Decides whether an active handle drag lost its pointer capture
    /// without a routed terminal. WinUI raises no PointerCaptureLost when a
    /// touch capture is released by code or by tree changes (it keeps a replay
    /// pointer point only for mouse), and the contact's later release is then
    /// routed away from the handle. An unknown capture view, a deferred capture
    /// record or queued input for the pointer never counts as lost: queued
    /// terminals must still replay and commit their final position.
    /// </summary>
    internal static bool IsCaptureSilentlyLost(
        uint dragPointerId,
        bool hasCaptureTarget,
        bool captureViewAvailable,
        bool captureTargetHoldsPointer,
        bool hasDeferredCaptureOrQueuedInput)
        => dragPointerId != 0 && hasCaptureTarget && captureViewAvailable &&
           !captureTargetHoldsPointer && !hasDeferredCaptureOrQueuedInput;

    /// <summary>
    /// Retires a handle drag whose capture was silently lost exactly
    /// as a routed handle cancel does: the committed selection is kept and the
    /// touch owner, gesture epoch, capture target and frame clock are cleared.
    /// </summary>
    internal bool RetireSilentlyLostCapture()
    {
        uint pointerId = _dragPointerId;
        UIElement? target = _captureTarget;
        IReadOnlyList<Pointer>? captures = target?.XamlRoot is null ? null : target.PointerCaptures;
        if (!IsCaptureSilentlyLost(
                pointerId,
                target is not null,
                captures is not null,
                PointerInputClassification.ContainsPointerCapture(captures, pointerId),
                _host.HasDeferredInputFor(pointerId)))
        {
            return false;
        }

        _touchTapSuppression.CancelContact(pointerId);
        ResetDrag();
        _touchSelection.Cancel(pointerId);
        _host.ResumePendingRebuildAfterPointerInteraction();
        Update();
        MarkdownDiagnostics.WriteLine("[MarkdownRendererControl] Retired a selection-handle drag whose pointer capture was lost without a routed terminal.");
        return true;
    }

    private void StartFrameClock()
    {
        if (_frameSubscribed)
            return;

        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnFrame;
        _frameSubscribed = true;
    }

    private void OnFrame(object? sender, object args)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
            _activeEndpoint == SelectionHandleEndpoint.None ||
            _published.Snapshot is not { } snapshot)
        {
            StopFrameClock();
            return;
        }

        if (!snapshot.TryBeginInteraction())
            return;

        try
        {
            // The clock runs for the whole drag, so a capture that
            // moved without a routed terminal is retired within a frame.
            if (RetireSilentlyLostCapture())
                return;

            long timestamp = Stopwatch.GetTimestamp();
            double elapsedSeconds = Math.Max(
                0,
                (timestamp - _lastFrameTimestamp) / (double)Stopwatch.Frequency);
            _lastFrameTimestamp = timestamp;

            // Rendering is the coalescing boundary: at most one hit-test and
            // overlay update is performed per compositor frame. The latest
            // contact remains active without PointerMoved so edge scrolling
            // continues while the finger is stationary.
            // The return value only reports whether movement or edge scrolling
            // continues; the clock itself stays subscribed until the drag ends
            // (ResetDrag or a routed release stops it).
            _ = ApplyPendingMove(
                sustained: true,
                elapsedSeconds);
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private bool ApplyPendingMove(
        bool sustained,
        double elapsedSeconds = 0)
    {
        if (_activeEndpoint == SelectionHandleEndpoint.None ||
            _published.Snapshot is null || _surface.Canvas is null ||
            (!sustained && !_movePending))
        {
            return false;
        }

        _viewport.TryGet(out double viewportTop, out _, out _);
        Point contact = new(
            _latestPoint.X,
            viewportTop + _latestViewportY);
        Point caretPoint = new(
            contact.X + _caretOffset.X,
            contact.Y + _caretOffset.Y);
        if (sustained && !_movePending)
        {
            _viewport.TryGet(out double edgeViewportTop, out double edgeViewportHeight, out _);
            if (Math.Abs(SelectionAutoScroll.ComputeVelocity(
                    caretPoint.Y,
                    edgeViewportTop,
                    edgeViewportHeight)) < 0.5)
            {
                return false;
            }
        }
        Point point = _autoScroll.PrepareDragPoint(caretPoint, sustained, elapsedSeconds);
        _movePending = false;

        DocumentPosition candidate;
        if (_host.TryHitTestEmbed(point, out DocumentPosition embedPosition))
            candidate = embedPosition;
        else if (!_published.Snapshot.HitTestSelectionEndpoint(point, out candidate))
            return IsInAutoScrollBand(caretPoint);

        AdjustEndpoint(_activeEndpoint, candidate);
        return IsInAutoScrollBand(caretPoint);
    }

    private bool IsInAutoScrollBand(Point caretPoint)
    {
        if (_surface.Canvas is null)
            return false;

        _viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
        double velocity = SelectionAutoScroll.ComputeVelocity(
            caretPoint.Y,
            viewportTop,
            viewportHeight);
        if (Math.Abs(velocity) < 0.5)
            return false;

        double maxOffset = Math.Max(0, _surface.Canvas.ActualHeight - viewportHeight);
        return velocity < 0
            ? viewportTop > 0.0001
            : viewportTop < maxOffset - 0.0001;
    }

    /// <summary>Stops the drag's frame clock.</summary>
    internal void StopFrameClock()
    {
        if (!_frameSubscribed)
            return;

        CompositionTarget.Rendering -= OnFrame;
        _frameSubscribed = false;
        _lastFrameTimestamp = 0;
    }

    private void OnHandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_host.RequestGeometryRecoveryIfRequired(_published.Snapshot))
            return;
        if (sender is not Button { Tag: SelectionHandleEndpoint endpoint } ||
            !_selection.IsActive)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            _host.FocusRendererForPointerInteraction();
            e.Handled = true;
            return;
        }

        int delta = e.Key switch
        {
            VirtualKey.Left => _renderer.FlowDirection == FlowDirection.RightToLeft ? 1 : -1,
            VirtualKey.Right => _renderer.FlowDirection == FlowDirection.RightToLeft ? -1 : 1,
            VirtualKey.Up => -1,
            VirtualKey.Down => 1,
            _ => 0,
        };
        if (delta == 0)
            return;

        MoveByTextOffset(endpoint, delta);
        e.Handled = true;
    }

    private void MoveByTextOffset(
        SelectionHandleEndpoint endpoint,
        int delta)
    {
        if (_published.Snapshot is null || !_selection.IsActive)
            return;

        DocumentRange range = _selection.Range.Normalized();
        DocumentPosition current = endpoint == SelectionHandleEndpoint.Start
            ? range.Start
            : range.End;
        int currentOffset = _published.Snapshot.SemanticDocument.TextOffsetFromDocumentPosition(current);
        int requestedOffset = _published.Snapshot.SemanticDocument.TextElementBoundaries.Move(
            currentOffset,
            delta,
            out _);
        AdjustEndpointAtTextOffset(
            endpoint,
            requestedOffset,
            candidateIsBoundary: true);
    }

    private void AdjustEndpoint(
        SelectionHandleEndpoint endpoint,
        DocumentPosition candidate)
    {
        if (_published.Snapshot is null || !_selection.IsActive)
            return;

        var semantic = _published.Snapshot.SemanticDocument;
        int rawCandidateOffset = semantic.TextOffsetFromDocumentPosition(candidate);
        AdjustEndpointAtTextOffset(
            endpoint,
            rawCandidateOffset,
            candidateIsBoundary: false);
    }

    private void AdjustEndpointAtTextOffset(
        SelectionHandleEndpoint endpoint,
        int rawCandidateOffset,
        bool candidateIsBoundary)
    {
        if (_published.Snapshot is null || !_selection.IsActive)
            return;

        var semantic = _published.Snapshot.SemanticDocument;
        int fixedOffset = semantic.TextOffsetFromDocumentPosition(_dragPointerId != 0
            ? _fixedPosition
            : endpoint == SelectionHandleEndpoint.Start
                ? _selection.Range.Normalized().End
                : _selection.Range.Normalized().Start);
        bool pointerDrag = _dragPointerId != 0;
        TouchSelectionEndpointAdjustment adjustment = TouchSelectionEndpointPolicy.Resolve(
            semantic.TextElementBoundaries,
            fixedOffset,
            rawCandidateOffset,
            endpoint == SelectionHandleEndpoint.Start,
            pointerDrag,
            candidateIsBoundary);
        int candidateOffset = adjustment.Offset;
        if (candidateOffset == fixedOffset)
            return;
        DocumentPosition? adjusted = semantic.PositionFromTextOffset(candidateOffset);
        if (adjusted is null)
            return;

        DocumentPosition fixedPosition = semantic.PositionFromTextOffset(fixedOffset)
            ?? _fixedPosition;
        _selection.SetAnchor(fixedPosition);
        _selection.ExtendTo(adjusted.Value);
        if (pointerDrag)
            _activeEndpoint = adjustment.IsStartEndpoint
                ? SelectionHandleEndpoint.Start
                : SelectionHandleEndpoint.End;
        _touchSelection.Select();
    }

    /// <summary>
    /// Ends any drag without moving the selection: clears its state, stops the
    /// frame clock and releases the handles' pointer captures.
    /// </summary>
    internal void ResetDrag()
    {
        bool hadCapturedButton = _captureTarget is not null;
        _captureTarget = null;
        if (hadCapturedButton)
        {
            _adorner.MarkVisualDirty();
        }
        _activeEndpoint = SelectionHandleEndpoint.None;
        _dragPointerId = 0;
        _fixedPosition = default;
        _latestPoint = default;
        _latestViewportY = double.NaN;
        _caretOffset = default;
        _movePending = false;
        _autoScroll.Reset();
        try
        {
            StopFrameClock();
            if (hadCapturedButton)
                _placement.CollapseForPendingRefresh();
            _buttons.SetGlyphVisibility(visible: true);
        }
        finally
        {
            _buttons.ReleasePointerCaptures();
        }

        if (hadCapturedButton && !_lifetime.IsDisposed && !_lifetime.IsUnloaded && _renderer.IsLoaded)
            _adorner.RequestRefresh();
    }

    SelectionHandleEndpoint ISelectionHandleDragView.ActiveEndpoint => _activeEndpoint;

    bool ISelectionHandleDragView.IsPointerDragActive => IsPointerDragActive();

    bool ISelectionHandleDragView.IsCaptured(SelectionHandleEndpoint endpoint) => IsCaptured(endpoint);
}
