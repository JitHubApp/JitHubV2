using System;
using System.Collections.Immutable;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Narrow sample/test-only access to observe and retire a real deferred touch
/// terminal through the control's normal failure callback. This deliberately
/// is not a public diagnostic or input-injection API.
/// </summary>
public partial class MarkdownRendererControl
{
    /// <summary>
    /// Reads the current real touch/lifecycle state for the opt-in sample
    /// regression host. This method observes only; it does not retire input,
    /// capture, or lifecycle state.
    /// </summary>
    internal TouchLifecycleProbeState CaptureTouchLifecycleProbeState(uint observedPointerId = 0)
    {
        if (DispatcherQueue?.HasThreadAccess != true)
            throw new InvalidOperationException("Touch lifecycle probe state must be read on the renderer's UI dispatcher.");

        uint contactPointerId = _touchSelection.ContactPointerId;
        uint handlePointerId = _selectionHandlePointerId;
        uint eventPointerId = observedPointerId != 0 ? observedPointerId : contactPointerId;
        return new TouchLifecycleProbeState(
            IsLoaded,
            _isUnloaded,
            _isDisposed,
            contactPointerId,
            contactPointerId == 0 ? 0 : _touchTapSuppression.GetGestureEpochForPointer(contactPointerId),
            eventPointerId,
            eventPointerId == 0 ? 0 : _touchTapSuppression.GetGestureEpochForPointerOrCompleted(eventPointerId),
            _touchSelection.State.ToString(),
            handlePointerId,
            handlePointerId == 0 ? 0 : _touchTapSuppression.GetGestureEpochForPointer(handlePointerId),
            _selectionHandleCaptureTarget is { } target
                ? AutomationProperties.GetAutomationId(target)
                : null,
            _selectionHandlePointerId != 0 || _selectionHandleCaptureTarget is not null ||
                _touchSelection.State is TouchSelectionState.DraggingStartHandle or TouchSelectionState.DraggingEndHandle,
            _selectionHandleFrameSubscribed,
            _selectionHandleLastFrameTimestamp,
            _deferredPointerInput?.Count ?? 0,
            _deferredPointerCaptures.Count,
            _pointerRetryTimer is not null,
            _contextMenuOpen,
            _selectionStartHandle?.Visibility == Visibility.Visible,
            _selectionEndHandle?.Visibility == Visibility.Visible,
            _selection.IsActive,
            CurrentSelectionRange);
    }

    /// <summary>Captures current deferred touch state. Must be called on the UI dispatcher.</summary>
    internal DeferredTouchProbeState CaptureDeferredTouchProbeState()
    {
        VerifyDeferredTouchProbeDispatcher();

        uint activePointerId = _touchSelection.ContactPointerId;
        long gestureEpoch = activePointerId == 0
            ? 0
            : _touchTapSuppression.GetGestureEpochForPointer(activePointerId);
        var terminals = ImmutableArray.CreateBuilder<DeferredTouchProbeTerminal>();
        int matchingTerminalCount = 0;
        int queuedCount = _deferredPointerInput?.Count ?? 0;
        if (_deferredPointerInput is { } queue)
        {
            for (int index = 0; index < queuedCount; index++)
            {
                if (!queue.TryGetAt(index, out PointerInput queued) || !IsDeferredTouchProbeTerminal(queued.Kind))
                    continue;

                Pointer? pointer = queued.Pointer;
                bool isTouch = pointer?.PointerDeviceType == PointerDeviceType.Touch;
                terminals.Add(new DeferredTouchProbeTerminal(
                    queued.Kind.ToString(),
                    pointer?.PointerId ?? 0,
                    queued.TouchTapGestureEpoch,
                    isTouch));
                if (isTouch && pointer?.PointerId == activePointerId &&
                    queued.TouchTapGestureEpoch == gestureEpoch &&
                    (queued.Kind is PointerInputKind.Release or PointerInputKind.Cancel))
                {
                    matchingTerminalCount++;
                }
            }
        }

        bool isDraggingHandle = _selectionHandlePointerId != 0 ||
            _selectionHandleCaptureTarget is not null ||
            _touchSelection.State is TouchSelectionState.DraggingStartHandle or
                TouchSelectionState.DraggingEndHandle;

        int visibleHandleCount =
            (_selectionStartHandle?.Visibility == Visibility.Visible ? 1 : 0) +
            (_selectionEndHandle?.Visibility == Visibility.Visible ? 1 : 0);

        return new DeferredTouchProbeState(
            activePointerId,
            gestureEpoch,
            _touchSelection.State.ToString(),
            queuedCount,
            terminals.ToImmutable(),
            matchingTerminalCount,
            _deferredPointerCaptures.Count,
            _pointerRetryTimer is not null,
            isDraggingHandle,
            visibleHandleCount,
            _contextMenuOpen,
            _selection.IsActive,
            CurrentSelectionRange);
    }

    /// <summary>
    /// Drives the production layout-failure callback for an already queued,
    /// exact-owner Release or Cancel. It never creates input or changes queue
    /// contents before invoking production behavior.
    /// </summary>
    internal DeferredTouchProbeState FailDeferredTouchTerminalForProbe(
        LayoutSnapshot snapshot,
        uint pointerId,
        long gestureEpoch,
        bool canceled)
    {
        VerifyDeferredTouchProbeDispatcher();
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_isDisposed || _isUnloaded || !IsLoaded)
            throw new InvalidOperationException("The renderer must be alive and loaded for the deferred touch probe.");
        if (!ReferenceEquals(snapshot, _snapshot) ||
            !ReferenceEquals(snapshot, _deferredPointerSnapshot))
        {
            throw new InvalidOperationException("The probe snapshot is not both current and deferred.");
        }
        if (pointerId == 0 || gestureEpoch <= 0 ||
            _touchSelection.ContactPointerId != pointerId ||
            _touchTapSuppression.GetGestureEpochForPointer(pointerId) != gestureEpoch)
        {
            throw new InvalidOperationException("The requested pointer and gesture epoch do not own the active touch contact.");
        }
        if (_deferredPointerInput is not { Count: > 0 } queue)
            throw new InvalidOperationException("There is no queued deferred input to retire.");

        PointerInputKind expectedKind = canceled ? PointerInputKind.Cancel : PointerInputKind.Release;
        int matchingTerminalCount = 0;
        int queuedCount = queue.Count;
        for (int index = 0; index < queuedCount; index++)
        {
            if (!queue.TryGetAt(index, out PointerInput queued))
                throw new InvalidOperationException("The deferred queue changed while the probe was inspecting it.");
            if (!IsDeferredTouchProbeTerminal(queued.Kind))
                continue;

            Pointer? pointer = queued.Pointer;
            if (pointer?.PointerId != pointerId)
                continue;

            // A pointer ID can be reused; reject every same-ID terminal that
            // is not the requested touch terminal from the current epoch.
            if (pointer.PointerDeviceType != PointerDeviceType.Touch ||
                queued.TouchTapGestureEpoch != gestureEpoch ||
                queued.Kind != expectedKind)
            {
                throw new InvalidOperationException("The queued terminal is stale, from another device, or conflicts with the requested terminal.");
            }

            matchingTerminalCount++;
        }

        if (matchingTerminalCount == 0)
            throw new InvalidOperationException("The exact requested touch terminal is not queued.");
        if (!canceled && matchingTerminalCount != 1)
            throw new InvalidOperationException("A deferred touch release must have exactly one matching terminal.");

        // Multiple same-owner Cancel notifications are legitimate when a
        // canceled contact also reports capture loss. The production reset
        // handles those as one canceled gesture, so retain them in FIFO order.
        OnDeferredGestureLayoutCompletion(snapshot, succeeded: false);

        DeferredTouchProbeState after = CaptureDeferredTouchProbeState();
        if (after.ActivePointerId != 0 || after.GestureEpoch != 0 ||
            after.QueuedCount != 0 || after.RetryTimerExists || after.CaptureCount != 0 ||
            (canceled && after.SelectionContextMenuOpen))
        {
            throw new InvalidOperationException(
                "The production failure callback did not retire the deferred touch owner, queue, retry timer, and captures.");
        }

        return after;
    }

    private void VerifyDeferredTouchProbeDispatcher()
    {
        if (DispatcherQueue?.HasThreadAccess != true)
            throw new InvalidOperationException("Deferred touch probe methods must run on the renderer's UI dispatcher.");
    }

    private static bool IsDeferredTouchProbeTerminal(PointerInputKind kind) => kind is
        PointerInputKind.Release or PointerInputKind.Cancel or
        PointerInputKind.HandleRelease or PointerInputKind.HandleCancel;
}

internal readonly record struct DeferredTouchProbeTerminal(
    string Kind,
    uint PointerId,
    long GestureEpoch,
    bool IsTouch);

internal sealed record DeferredTouchProbeState(
    uint ActivePointerId,
    long GestureEpoch,
    string TouchState,
    int QueuedCount,
    ImmutableArray<DeferredTouchProbeTerminal> QueuedTerminals,
    int MatchingTerminalCount,
    int CaptureCount,
    bool RetryTimerExists,
    bool IsHandleDragging,
    int VisibleHandleCount,
    bool SelectionContextMenuOpen,
    bool SelectionIsActive,
    DocumentRange SelectionRange);

internal sealed record TouchLifecycleProbeState(
    bool IsLoaded,
    bool IsUnloaded,
    bool IsDisposed,
    uint ContactPointerId,
    long ContactGestureEpoch,
    uint ObservedPointerId,
    long ObservedGestureEpoch,
    string TouchState,
    uint HandlePointerId,
    long HandleGestureEpoch,
    string? HandleCaptureTargetAutomationId,
    bool IsHandleDragging,
    bool HandleFrameClockSubscribed,
    long HandleFrameTimestamp,
    int QueuedPointerInputCount,
    int DeferredPointerCaptureCount,
    bool PointerRetryTimerExists,
    bool IsContextMenuOpen,
    bool IsStartHandleVisible,
    bool IsEndHandleVisible,
    bool SelectionIsActive,
    DocumentRange SelectionRange);
