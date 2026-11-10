using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using MarkdownRenderer.Controls.Internal.Input.HorizontalOverflow;
using MarkdownRenderer.Controls.Internal.Selection.TouchSelection;

namespace MarkdownRenderer.Controls.Internal.Input.Deferred;

/// <summary>
/// The speculative pointer captures taken when a press is queued, so its
/// release is delivered even while layout is busy.
/// </summary>
/// <remarks>
/// Each capture is taken in the original press event, while the input is
/// being queued, and only for a primary press.
/// </remarks>
internal sealed class DeferredPointerCaptures
{
    private readonly DocumentPointerInteraction _document;
    private readonly HorizontalOverflowPointerDrag _horizontalOverflowDrag;
    private readonly SelectionHandlesPresenter _handles;
    private readonly Dictionary<uint, (UIElement Target, Pointer Pointer)> _captures = new();

    /// <param name="document">The canvas pointer session a replayed press may have started.</param>
    /// <param name="horizontalOverflowDrag">The horizontal overflow drag a replayed press may have started.</param>
    /// <param name="handles">The selection handle drag a replayed press may have started.</param>
    internal DeferredPointerCaptures(
        DocumentPointerInteraction document,
        HorizontalOverflowPointerDrag horizontalOverflowDrag,
        SelectionHandlesPresenter handles)
    {
        _document = document;
        _horizontalOverflowDrag = horizontalOverflowDrag;
        _handles = handles;
    }

    /// <summary>The number of speculative captures held.</summary>
    internal int Count => _captures.Count;

    /// <summary>True when <paramref name="pointerId"/> holds a speculative capture.</summary>
    internal bool Contains(uint pointerId) => _captures.ContainsKey(pointerId);

    /// <summary>
    /// Captures the pointer of a primary press being queued, so its release
    /// is delivered even while layout is busy, and marks
    /// <paramref name="input"/> as captured when its pointer holds a capture.
    /// </summary>
    /// <param name="kind">The normalized kind of the queued input.</param>
    /// <param name="sender">The element that raised the input.</param>
    /// <param name="input">The input being queued.</param>
    /// <param name="canvas">The document canvas that captures a canvas press.</param>
    internal void CaptureForDeferral(PointerInputKind kind, object sender, ref PointerInput input, UIElement? canvas)
    {
        // Capture in the original press event so a release outside the canvas is
        // delivered even if layout is still busy. Never capture a pending touch
        // pan on the canvas; explicit selection handles own their touch gesture.
        bool primaryPress = input.Point.IsInContact &&
            (kind == PointerInputKind.HandlePress ||
             (kind == PointerInputKind.Press && PointerInputClassification.IsPrecisePointer(input) &&
              input.Point.Properties.IsLeftButtonPressed && !input.Point.Properties.IsBarrelButtonPressed));
        if (primaryPress)
        {
            UIElement target = kind == PointerInputKind.HandlePress && sender is UIElement handle ? handle : canvas!;
            if (PointerInputClassification.CapturePointerForInput(target, input))
            {
                input.CapturedBeforeDeferral = true;
                _captures[input.Pointer.PointerId] = (target, input.Pointer);
            }
        }
        input.CapturedBeforeDeferral |= Contains(input.Pointer.PointerId);
    }

    /// <summary>
    /// Releases every capture, continuing past a failed release, and forgets
    /// them.
    /// </summary>
    internal void ReleaseAll()
    {
        foreach (var capture in _captures.Values)
        {
            try { capture.Target.ReleasePointerCapture(capture.Pointer); }
            catch (Exception)
            {
                // A native input site may already be closed. Still release
                // every other capture and clear the geometry-bound queue.
            }
        }
        _captures.Clear();
    }

    /// <summary>
    /// Forgets every capture and releases those no live gesture took over
    /// when the queue drained.
    /// </summary>
    internal void ReleaseUnowned()
    {
        // A successfully started live gesture now owns its capture. Release
        // speculative captures only for misses or gestures already ended.
        var captures = new List<(UIElement Target, Pointer Pointer)>(_captures.Values);
        _captures.Clear();
        foreach (var capture in captures)
        {
            uint id = capture.Pointer.PointerId;
            if (_document.IsSessionPointer(id) ||
                (_horizontalOverflowDrag.IsCaptured && _horizontalOverflowDrag.PointerId == id) ||
                _handles.DragPointerId == id)
                continue;
            capture.Target.ReleasePointerCapture(capture.Pointer);
        }
    }
}
