using System;
using System.Collections.Generic;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace MarkdownRenderer.Controls;

public partial class MarkdownRendererControl
{
    private DeferredInputQueue<PointerInput>? _deferredPointerInput;
    private LayoutSnapshot? _deferredPointerSnapshot;
    private DispatcherQueueTimer? _pointerRetryTimer;
    private bool _drainingPointerInput;
    private bool _resettingPointerInput;
    private readonly Dictionary<uint, (UIElement Target, Pointer Pointer)> _deferredPointerCaptures = new();

    private enum PointerInputKind { Press, Move, Release, Cancel, Exit, Wheel, HandlePress, HandleMove, HandleRelease, HandleCancel, Tap, DoubleTap, Hold, RightTap }

    // Capture point/modifiers/time at event delivery. Never retain routed event
    // arguments or re-query a moved pointer when deferred work is replayed.
    private struct PointerInput
    {
        internal PointerInputKind Kind;
        internal object Sender;
        internal Pointer Pointer;
        internal PointerPoint Point;
        internal VirtualKeyModifiers Modifiers;
        internal long TickCount;
        internal bool CapturedBeforeDeferral;
        internal bool Handled;
        internal Windows.Foundation.Point GesturePosition;
        internal PointerDeviceType GestureDevice;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Press, sender, e);
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Move, sender, e);
    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Release, sender, e);
    private void OnPointerCanceledOrCaptureLost(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Cancel, sender, e);
    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Exit, sender, e);
    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.Wheel, sender, e);
    private void OnSelectionHandlePointerPressed(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.HandlePress, sender, e);
    private void OnSelectionHandlePointerMoved(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.HandleMove, sender, e);
    private void OnSelectionHandlePointerReleased(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.HandleRelease, sender, e);
    private void OnSelectionHandlePointerCanceled(object sender, PointerRoutedEventArgs e) => DispatchPointerInput(PointerInputKind.HandleCancel, sender, e);

    private bool DispatchRecognizedGesture(PointerInputKind kind, Windows.Foundation.Point point, PointerDeviceType device)
    {
        if (_isDisposed || _isUnloaded || _canvas is null || _snapshot is not { } snapshot)
            return false;
        var input = new PointerInput { Kind = kind, GesturePosition = point, GestureDevice = device };
        if (!_drainingPointerInput && (_deferredPointerInput?.Count ?? 0) == 0 && snapshot.TryBeginInteraction())
        {
            try { ProcessPointerInput(ref input); return input.Handled; }
            finally { snapshot.EndInteraction(); }
        }
        if (_deferredPointerSnapshot is not null && !ReferenceEquals(snapshot, _deferredPointerSnapshot))
            ResetDeferredPointerInput();
        _deferredPointerSnapshot = snapshot;
        _deferredPointerInput ??= new DeferredInputQueue<PointerInput>();
        if (!_deferredPointerInput.TryEnqueue(input, 0, false))
        {
            CancelDeferredGesture();
            return false;
        }
        _pointerRetryTimer ??= CreatePointerRetryTimer();
        _pointerRetryTimer.Start();
        return true;
    }

    private void DispatchPointerInput(PointerInputKind kind, object sender, PointerRoutedEventArgs args)
    {
        if (_isDisposed || _isUnloaded || _resettingPointerInput || _canvas is null || _snapshot is not { } snapshot)
            return;

        var input = new PointerInput
        {
            Kind = kind,
            Sender = sender,
            Pointer = args.Pointer,
            Point = args.GetCurrentPoint(_canvas),
            Modifiers = args.KeyModifiers,
            TickCount = Environment.TickCount64,
            Handled = args.Handled,
        };
        // Vertical wheel/pan ownership remains with the page ScrollViewer.
        if (kind == PointerInputKind.Wheel && !input.Point.Properties.IsHorizontalMouseWheel &&
            (input.Modifiers & VirtualKeyModifiers.Shift) == 0)
            return;

        if (!_drainingPointerInput && (_deferredPointerInput?.Count ?? 0) == 0 && snapshot.TryBeginInteraction())
        {
            try { ProcessPointerInput(ref input); }
            finally { snapshot.EndInteraction(); }
            args.Handled = input.Handled;
            return;
        }

        if (_deferredPointerSnapshot is not null && !ReferenceEquals(snapshot, _deferredPointerSnapshot))
            ResetDeferredPointerInput();
        _deferredPointerSnapshot = snapshot;
        // Capture in the original press event so a release outside the canvas is
        // delivered even if layout is still busy. Never capture a pending touch
        // pan on the canvas; explicit selection handles own their touch gesture.
        bool primaryPress = input.Point.IsInContact &&
            (kind == PointerInputKind.HandlePress ||
             (kind == PointerInputKind.Press && IsPrecisePointer(input) &&
              input.Point.Properties.IsLeftButtonPressed && !input.Point.Properties.IsBarrelButtonPressed));
        if (primaryPress)
        {
            UIElement target = kind == PointerInputKind.HandlePress && sender is UIElement handle ? handle : _canvas;
            if (CapturePointerForInput(target, input))
            {
                input.CapturedBeforeDeferral = true;
                _deferredPointerCaptures[input.Pointer.PointerId] = (target, input.Pointer);
            }
        }
        input.CapturedBeforeDeferral |= _deferredPointerCaptures.ContainsKey(input.Pointer.PointerId);
        _deferredPointerInput ??= new DeferredInputQueue<PointerInput>();
        if (!_deferredPointerInput.TryEnqueue(input, input.Pointer.PointerId,
                kind is PointerInputKind.Move or PointerInputKind.HandleMove))
        {
            CancelDeferredGesture();
            return;
        }
        args.Handled |= input.CapturedBeforeDeferral || kind == PointerInputKind.Wheel;
        _pointerRetryTimer ??= CreatePointerRetryTimer();
        _pointerRetryTimer.Start();
    }

    private DispatcherQueueTimer CreatePointerRetryTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += OnPointerRetryTimer;
        return timer;
    }

    private void OnPointerRetryTimer(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed || _isUnloaded || !ReferenceEquals(_snapshot, _deferredPointerSnapshot))
        {
            ResetDeferredPointerInput();
            return;
        }
        if (_snapshot is not { } snapshot || !snapshot.TryBeginInteraction())
            return;
        _drainingPointerInput = true;
        try
        {
            while (_deferredPointerInput?.TryDequeue(out var input) == true)
            {
                if (_isDisposed || _isUnloaded || !ReferenceEquals(_snapshot, snapshot))
                    break;
                ProcessPointerInput(ref input);
            }
        }
        finally
        {
            _drainingPointerInput = false;
            snapshot.EndInteraction();
            sender.Stop();
            _deferredPointerSnapshot = null;
            _deferredPointerInput?.Clear();
            // A successfully started live gesture now owns its capture. Release
            // speculative captures only for misses or gestures already ended.
            var captures = new List<(UIElement Target, Pointer Pointer)>(_deferredPointerCaptures.Values);
            _deferredPointerCaptures.Clear();
            foreach (var capture in captures)
            {
                uint id = capture.Pointer.PointerId;
                if ((_pointerSession.IsActive && _pointerSession.PointerId == id) ||
                    (_horizontalOverflowCaptured && _horizontalOverflowPointerId == id) ||
                    _selectionHandlePointerId == id)
                    continue;
                capture.Target.ReleasePointerCapture(capture.Pointer);
            }
            ResumePendingRebuildAfterPointerInteraction();
        }
    }

    private void ProcessPointerInput(ref PointerInput input)
    {
        switch (input.Kind)
        {
            case PointerInputKind.Press: ProcessPointerPressed(ref input); break;
            case PointerInputKind.Move: ProcessPointerMoved(ref input); break;
            case PointerInputKind.Release: ProcessPointerReleased(ref input); break;
            case PointerInputKind.Cancel: ProcessPointerCanceledOrCaptureLost(ref input); break;
            case PointerInputKind.Exit: ProcessPointerExited(ref input); break;
            case PointerInputKind.Wheel: ProcessPointerWheelChanged(ref input); break;
            case PointerInputKind.HandlePress: ProcessSelectionHandlePointerPressed(ref input); break;
            case PointerInputKind.HandleMove: ProcessSelectionHandlePointerMoved(ref input); break;
            case PointerInputKind.HandleRelease: ProcessSelectionHandlePointerReleased(ref input); break;
            case PointerInputKind.HandleCancel: ProcessSelectionHandlePointerCanceled(ref input); break;
            case PointerInputKind.Tap: ProcessTapped(ref input); break;
            case PointerInputKind.DoubleTap: ProcessDoubleTapped(ref input); break;
            case PointerInputKind.Hold: ProcessHolding(ref input); break;
            case PointerInputKind.RightTap: ProcessRightTapped(ref input); break;
        }
    }

    private void CancelDeferredGesture()
    {
        ResetDeferredPointerInput();
        _pointerSession = default;
        _selectionAnchor = null;
        ClearHorizontalOverflowPointerState();
        ResetSelectionHandleDrag();
        _touchSelection.Reset();
        SetSelectionDragShieldActive(false);
        MarkdownDiagnostics.WriteLine("[MarkdownRendererControl] Cancelled an over-budget deferred pointer gesture.");
        ResumePendingRebuildAfterPointerInteraction();
    }

    private static bool CapturePointerForInput(UIElement target, PointerInput input)
    {
        // Pointer input can be replayed after a layout/image realization pass.
        // Do not ask WinUI for capture state once that target has left its tree.
        if (target.XamlRoot is null)
            return false;

        if (input.CapturedBeforeDeferral)
            return true;

        // ButtonBase can capture a touch contact in its class handler before our
        // handled-events-too selection-handle handler runs. Re-capturing the same
        // pointer may report false even though the intended target already owns it;
        // treat that existing capture as success so native Button behavior and the
        // renderer's drag state can cooperate.
        // Although the projected API is non-nullable, WinUI can surface a null
        // capture view while an element's input site is being initialized or
        // torn down. Treat it as the normal empty-capture state.
        IReadOnlyList<Pointer>? captures = target.PointerCaptures;
        if (ContainsPointerCapture(captures, input.Pointer.PointerId))
            return true;

        return target.CapturePointer(input.Pointer);
    }

    internal static bool ContainsPointerCapture(IReadOnlyList<Pointer>? captures, uint pointerId)
    {
        if (captures is null)
            return false;

        foreach (Pointer captured in captures)
        {
            if (captured.PointerId == pointerId)
                return true;
        }

        return false;
    }

    private void ResetDeferredPointerInput()
    {
        _resettingPointerInput = true;
        try
        {
            _deferredPointerInput?.Clear();
            _deferredPointerSnapshot = null;
            if (_pointerRetryTimer is { } timer)
            {
                timer.Stop();
                timer.Tick -= OnPointerRetryTimer;
                _pointerRetryTimer = null;
            }
            foreach (var capture in _deferredPointerCaptures.Values)
                capture.Target.ReleasePointerCapture(capture.Pointer);
            _deferredPointerCaptures.Clear();
        }
        finally { _resettingPointerInput = false; }
    }
}
