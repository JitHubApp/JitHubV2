using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Selection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// The single entry point for pointer input and recognized gestures on the
/// document. Input runs at once when the published snapshot's interaction
/// lease is free; otherwise it is queued against that exact snapshot, with
/// speculative pointer captures, and replayed in FIFO order by a retry timer.
/// </summary>
/// <remarks>
/// This class builds the owners and answers for them as one router.
/// <see cref="RecognizedGestureRouting"/> and <see cref="RoutedPointerInputRouting"/>
/// run the immediate paths; <see cref="DeferredInputReplay"/> holds the queue,
/// its retry drain and post-interaction completion;
/// <see cref="DeferredPointerCaptures"/> takes and releases speculative captures;
/// <see cref="DeferredGestureAdmission"/> decides when a queued gesture may run;
/// and <see cref="GestureContextMenuCompletion"/> opens a gesture's context menu
/// after its lease ends.
/// </remarks>
internal sealed class PointerInputRouter
{
    private readonly DocumentPointerInteraction _document;
    private readonly HorizontalOverflowPointerDrag _horizontalOverflowDrag;
    private readonly SelectionHandlesPresenter _handles;
    private readonly GestureContextMenuCompletion _contextMenuCompletion;
    private readonly DeferredPointerCaptures _deferredCaptures;
    private readonly DeferredInputReplay _replay;
    private readonly RecognizedGestureRouting _gestures;
    private readonly RoutedPointerInputRouting _pointers;

    internal PointerInputRouter(
        IPointerInputRouterHost host,
        IDeferredGestureLayoutHost layout,
        DispatcherQueue dispatcherQueue,
        MarkdownViewSurface surface,
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        MarkdownViewport viewport,
        SelectionController selection,
        MarkdownTouchSelectionSession touchSelection,
        TouchTapSuppressionPolicy touchTapSuppression,
        SelectionHandlesPresenter handles,
        DocumentPointerInteraction document,
        HorizontalOverflowPointerDrag horizontalOverflowDrag,
        TouchGestureInteraction touchGestures,
        SelectionContextMenuPresenter contextMenu,
        SelectionAdornerPresenter adorner,
        SelectionDragShield dragShield)
    {
        _document = document;
        _horizontalOverflowDrag = horizontalOverflowDrag;
        _handles = handles;
        var admission = new DeferredGestureAdmission(layout, surface, lifetime, published, viewport, touchTapSuppression, document);
        var attribution = new RecognizedGestureAttribution(viewport, touchSelection, touchTapSuppression);
        var dispatcher = new PointerInputDispatcher(attribution, touchTapSuppression, document, horizontalOverflowDrag, handles, touchGestures);
        var droppedTerminals = new DroppedTouchTerminalRetirement(host, selection, touchSelection, touchTapSuppression, handles, contextMenu);
        var postInteraction = new PointerPostInteraction(host, surface, lifetime, published, adorner);
        _contextMenuCompletion = new GestureContextMenuCompletion(lifetime, published, contextMenu);
        _deferredCaptures = new DeferredPointerCaptures(document, horizontalOverflowDrag, handles);
        _replay = new DeferredInputReplay(
            host, dispatcherQueue, lifetime, published, document, horizontalOverflowDrag, admission,
            attribution, dispatcher, droppedTerminals, postInteraction, _contextMenuCompletion, _deferredCaptures);
        _gestures = new RecognizedGestureRouting(
            host, layout, surface, lifetime, published, viewport, admission, attribution, dispatcher, _contextMenuCompletion, _replay);
        _pointers = new RoutedPointerInputRouting(
            host, surface, lifetime, published, touchSelection, touchTapSuppression, handles, document,
            horizontalOverflowDrag, dragShield, dispatcher, postInteraction, _deferredCaptures, _replay);
    }

    /// <summary>
    /// True while any pointer owner holds the published snapshot's identity.
    /// </summary>
    /// <remarks>
    /// A pending horizontal touch contact is not captured until it becomes a
    /// drag, but it already owns a table/snapshot identity. Selection handles
    /// and queued input likewise must finish against their original geometry.
    /// Read only on the UI dispatcher, including the final publication fence.
    /// </remarks>
    internal bool HasSnapshotInteraction =>
        _document.HasActiveSession || _horizontalOverflowDrag.PointerId != 0 ||
        _handles.DragPointerId != 0 || _replay.IsDraining ||
        _deferredCaptures.Count != 0 ||
        (_replay.Queue?.HasBlockingSnapshotInteractions ?? false);

    /// <summary>The snapshot the queued input was hit-tested against, if any.</summary>
    internal LayoutSnapshot? DeferredSnapshot => _replay.Snapshot;

    /// <summary>
    /// True when <paramref name="pointerId"/> holds a speculative capture or
    /// still has queued input.
    /// </summary>
    internal bool HasDeferredInputFor(uint pointerId)
    {
        if (_deferredCaptures.Contains(pointerId))
            return true;
        if (_replay.Queue is not { Count: > 0 } queue)
            return false;
        for (int index = 0; index < queue.Count; index++)
        {
            if (queue.TryGetAt(index, out PointerInput queued) && queued.Pointer?.PointerId == pointerId)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Dispatches a gesture WinUI recognized on the canvas. Returns whether
    /// the gesture was handled or queued.
    /// </summary>
    internal bool DispatchRecognizedGesture(PointerInputKind kind, Windows.Foundation.Point point, PointerDeviceType device)
        => _gestures.Dispatch(kind, point, device);

    /// <summary>
    /// Dispatches a routed pointer event from the canvas, a selection handle
    /// or the drag shield.
    /// </summary>
    internal void DispatchPointerInput(PointerInputKind kind, object sender, PointerRoutedEventArgs args)
        => _pointers.Dispatch(kind, sender, args);

    /// <summary>
    /// Reports the end of a lazy layout request so a gesture waiting on
    /// <paramref name="snapshot"/> can retry or be dropped.
    /// </summary>
    internal void OnLazyLayoutCompletion(LayoutSnapshot snapshot, bool succeeded)
        => _gestures.OnLazyLayoutCompletion(snapshot, succeeded);

    /// <summary>
    /// Drops all queued input, its retry timer and its speculative captures.
    /// Retires a touch owner whose terminal event was still queued.
    /// </summary>
    internal void Reset() => _replay.Reset();
}
