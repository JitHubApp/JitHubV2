using System;
using System.Collections.Generic;
using MarkdownRenderer.Controls.Selection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// The speculative pointer captures taken when a press is queued, so its
/// release is delivered even while layout is busy.
/// </summary>
/// <remarks>
/// <see cref="PointerInputRouter"/> takes each capture itself, in the
/// original press event, and records it here; this class owns only the
/// record and how the captures are let go.
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

    /// <summary>Records that <paramref name="target"/> captured <paramref name="pointer"/>.</summary>
    internal void Record(uint pointerId, UIElement target, Pointer pointer)
        => _captures[pointerId] = (target, pointer);

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
