using System;
using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// The compositor frame clock that advances a selection handle drag: one
/// hit-test per frame, edge auto-scroll while the contact is stationary, and
/// retirement of a drag whose capture was silently lost.
/// </summary>
internal sealed class SelectionHandleFrameClock
{
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;
    private readonly ISelectionHandleFrameTarget _drag;
    private bool _frameSubscribed;
    private long _lastFrameTimestamp;

    /// <param name="lifetime">The renderer's unload and disposal state.</param>
    /// <param name="published">The published snapshot the drag hit-tests.</param>
    /// <param name="drag">The drag each frame advances.</param>
    internal SelectionHandleFrameClock(
        MarkdownViewLifetime lifetime,
        PublishedLayout published,
        ISelectionHandleFrameTarget drag)
    {
        _lifetime = lifetime;
        _published = published;
        _drag = drag;
    }

    /// <summary>Starts ticking on every compositor frame, if not already.</summary>
    internal void Start()
    {
        if (_frameSubscribed)
            return;

        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnFrame;
        _frameSubscribed = true;
    }

    /// <summary>Stops the drag's frame clock.</summary>
    internal void Stop()
    {
        if (!_frameSubscribed)
            return;

        CompositionTarget.Rendering -= OnFrame;
        _frameSubscribed = false;
        _lastFrameTimestamp = 0;
    }

    private void OnFrame(object? sender, object args)
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
            _drag.ActiveEndpoint == SelectionHandleEndpoint.None ||
            _published.Snapshot is not { } snapshot)
        {
            Stop();
            return;
        }

        if (!snapshot.TryBeginInteraction())
            return;

        try
        {
            // The clock runs for the whole drag, so a capture that
            // moved without a routed terminal is retired within a frame.
            if (_drag.RetireSilentlyLostCapture())
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
            _ = _drag.ApplyPendingMove(
                sustained: true,
                elapsedSeconds);
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }
}
